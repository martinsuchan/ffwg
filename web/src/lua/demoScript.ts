import { LuaFactory, type LuaEngine } from "wasmoon";

import { fetchText, fetchLegacyFile, getAudioManifest } from "./levelLoader";
import {
  resolveSoundPath,
  fetchSoundDurations,
  splitDialogName,
  formatSubtitle,
  type ResolvedSound,
} from "./dialogSound";
import { loadSettings } from "../storage/settingsStorage";

/** One movie cycle - the rate DemoScene ticks demoScript at, matching the
 *  original's ~100ms TimerAgent cadence the movie's planDelay() counts were
 *  tuned to. Exported so DemoScene's timer and this file's model_talk duration
 *  math use the SAME value: a subtitle/voice line lasts ceil(clipMs /
 *  DEMO_CYCLE_MS) cycles, so waitForTalker() (model_isTalking) waits exactly as
 *  long as the audio actually plays. (Using ROUND_MS here while the demo ticked
 *  at 100ms made model_isTalking report "done" ~23% early, cropping each line's
 *  tail - see docs/031.) */
export const DEMO_CYCLE_MS = 100;

/** One `demo_display(path, x, y)` draw. The original accumulates pictures on a
 *  *persistent surface buffer* it never clears (DemoMode::drawOn), so each
 *  call is a layer drawn on top at its own (x,y) - a later frame with
 *  transparency reveals earlier ones underneath. DemoScene replays these onto
 *  a RenderTexture in order, so they must be a draw *log* (not deduped by
 *  position) to reproduce that layering. See docs/031. */
export interface DemoPicture {
  path: string;
  x: number;
  y: number;
}

/** sound_playMusic/sound_stopMusic - only the latest command in a cycle is
 *  kept, same as levelScript.ts's MusicCommand. */
export type MusicCommand = { type: "play"; track: string } | { type: "stop" };

interface DialogEntry {
  font: string;
  subtitle: string;
  soundPath: string;
}

interface ActiveDialog {
  actorIndex: number;
  text: string;
  endCycle: number;
  sound: ResolvedSound | null;
  volume: number;
}

interface DemoState {
  /** Planner::m_plan - one command's worth of work per cycle (same FIFO shape
   *  as levelScript.ts's pendingActions). */
  plan: Array<(count: number) => boolean>;
  planCount: number;
  cycles: number;
  /** demo_display() draws not yet consumed by DemoScene - drained each cycle
   *  and blitted onto its RenderTexture in order (append-only, not deduped). */
  pendingDraws: DemoPicture[];
  dialogRegistry: Map<string, DialogEntry>;
  activeDialog: ActiveDialog | null;
  pendingMusic: MusicCommand | null;
  soundDurations: Map<string, number>;
}

function isDialogActive(state: DemoState): boolean {
  return state.activeDialog !== null && state.cycles < state.activeDialog.endCycle;
}

/**
 * Runs a level's fullscreen "movie" demo script (only briefcase's
 * demo_briefcase.lua today) in a persistent wasmoon engine - the port's
 * equivalent of the C++ DemoMode/demo-script.cpp. Unlike levelScript.ts it has
 * no physics/models: the demo is a plan-driven slideshow (game_planAction) of
 * demo_display() pictures with model_talk() subtitles/voice and background
 * music. See docs/031.
 */
export class DemoScript {
  constructor(
    private readonly lua: LuaEngine,
    private readonly state: DemoState,
  ) {}

  /** Runs one plan command (Planner::executeFirst) - returns true once the
   *  whole plan is drained (the movie is over). */
  tick(): boolean {
    this.state.cycles += 1;
    const { plan } = this.state;
    if (plan.length > 0) {
      const done = plan[0](this.state.planCount);
      if (done) {
        plan.shift();
        this.state.planCount = 0;
      } else {
        this.state.planCount += 1;
      }
    }
    return this.state.plan.length === 0;
  }

  /** The demo_display() draws since the last call, in order - drained so
   *  DemoScene can blit each onto its accumulating RenderTexture exactly once. */
  takePendingDraws(): DemoPicture[] {
    const draws = this.state.pendingDraws;
    this.state.pendingDraws = [];
    return draws;
  }

  getActiveSubtitle(): { text: string; sound: ResolvedSound | null; volume: number } | null {
    if (!isDialogActive(this.state)) return null;
    const d = this.state.activeDialog as ActiveDialog;
    return { text: d.text, sound: d.sound, volume: d.volume };
  }

  /** Identity of the active dialog (so DemoScene plays its voice once), or null. */
  getActiveDialogId(): string | null {
    if (!isDialogActive(this.state)) return null;
    const d = this.state.activeDialog as ActiveDialog;
    return `${d.actorIndex}@${d.endCycle}`;
  }

  getMusicCommand(): MusicCommand | null {
    const command = this.state.pendingMusic;
    this.state.pendingMusic = null;
    return command;
  }

  destroy(): void {
    this.lua.global.close();
  }
}

/**
 * Boots a demo movie script. Mirrors levelScript.ts's persistent-engine
 * pattern but with a minimal binding set (no models/physics). `demoFile` is a
 * legacy-relative path like "script/briefcase/demo_briefcase.lua"; `levelName`
 * ("briefcase") locates its dialog file and voice pool.
 *
 * `opts` distinguishes the two DemoMode uses (docs/031, docs/050):
 * - briefcase movie: `dialogPrefix "brief_"`, no prog_demo.
 * - final-level/ending poster: `dialogPrefix "demo_"` (its
 *   `demo_dialogs_<lang>.lua`), and `includeProgDemo` since a `demo_poster.lua`
 *   `file_include`s `prog_demo.lua` (which defines planTalk/planStop/planDelay
 *   over the game_planAction/model_talk this engine already binds).
 */
export async function createDemoScript(
  demoFile: string,
  levelName: string,
  opts: { dialogPrefix?: string; includeProgDemo?: boolean } = {},
): Promise<DemoScript> {
  const { dialogPrefix = "brief_", includeProgDemo = false } = opts;
  // The demo movie's voice/subtitle language follows the player's setting
  // (cs/nl), same as levelScript.ts (docs/038).
  const DIALOG_LANG = loadSettings().lang;
  const compatSource = await fetchText("/lua/lua50-compat.lua");
  const levelDialogSource = await fetchLegacyFile("script/share/level_dialog.lua");
  const progDemoSource = includeProgDemo
    ? await fetchLegacyFile("script/share/prog_demo.lua")
    : null;
  // dialogLoad() is bypassed (would enumerate ~15 languages via select_lang and
  // risk the docs/008 reentrancy bug) - the demo's own <prefix>dialogs_<lang>.lua
  // is fetched and run directly, exactly like levelScript.ts does for a level's
  // dialogs_<lang>.lua (docs/015).
  const demoDialogsSource = await fetchLegacyFile(
    `script/${levelName}/${dialogPrefix}dialogs_${DIALOG_LANG}.lua`,
  );
  const demoSource = await fetchLegacyFile(demoFile);
  const audioManifest = await getAudioManifest();
  const soundDurations = await fetchSoundDurations([`${levelName}/${DIALOG_LANG}`]);

  const factory = new LuaFactory();
  const lua = await factory.createEngine();

  const state: DemoState = {
    plan: [],
    planCount: 0,
    cycles: 0,
    pendingDraws: [],
    dialogRegistry: new Map<string, DialogEntry>(),
    activeDialog: null,
    pendingMusic: null,
    soundDurations,
  };
  // See levelScript.ts: Lua's own dataPathSound() resolves against lang="" (a
  // malformed path) because dialogLoad() - the only thing that sets the lang -
  // is bypassed, so dialog_addDialog recomputes the real voice path itself.
  const soundPrefix = `sound/${levelName}/${DIALOG_LANG}/`;

  try {
    lua.global.set("codename", levelName);
    lua.global.set("file_include", () => {});
    lua.global.set("dialog_addFont", () => {});
    lua.global.set("file_exists", (path: string) =>
      path.startsWith("sound/") ? audioManifest.has(path) : false,
    );
    lua.global.set("options_getParam", (name: string) => (name === "lang" ? DIALOG_LANG : ""));

    lua.global.set("sound_playMusic", (track: string) => {
      const basename = track.replace(/^music\//, "").replace(/\.ogg$/, "");
      state.pendingMusic = { type: "play", track: basename };
    });
    lua.global.set("sound_stopMusic", () => {
      state.pendingMusic = { type: "stop" };
    });

    // Log each draw in order - DemoScene blits them onto a persistent
    // RenderTexture (never cleared), matching DemoMode's surface buffer where
    // a transparent frame layers over what's already there. See docs/031.
    lua.global.set("demo_display", (path: string, x: number, y: number) => {
      state.pendingDraws.push({ path, x, y });
    });

    lua.global.set(
      "dialog_addDialog",
      (name: string, _lang: string, _soundPath: string, font: string, subtitle: string) => {
        const candidate = `${soundPrefix}${name}.ogg`;
        const soundPath = audioManifest.has(candidate) ? candidate : "";
        state.dialogRegistry.set(name, { font, subtitle, soundPath });
      },
    );
    lua.global.set(
      "model_talk",
      (index: number, dialogName: string, volume?: number | null, loops?: number | null) => {
        // Same '@' name/args split as levelScript.ts (DialogStack::actorTalk) -
        // one shared rule for every model_talk in the port. See docs/052.
        const { name: baseName, args } = splitDialogName(dialogName);
        const entry = state.dialogRegistry.get(baseName);
        if (!entry) return;
        const sound = entry.soundPath ? resolveSoundPath(entry.soundPath) : null;
        const durationSeconds = entry.soundPath
          ? state.soundDurations.get(entry.soundPath)
          : undefined;
        // Same as levelScript.ts: real clip length when known (movie voice is
        // long), else Dialog::getMinTime()'s no-sound formula.
        const minTime =
          durationSeconds !== undefined
            ? Math.ceil((durationSeconds * 1000) / DEMO_CYCLE_MS)
            : Math.min(180, entry.subtitle.length);
        const repeats = (loops ?? 0) + 1;
        state.activeDialog = {
          actorIndex: index,
          text: formatSubtitle(entry.subtitle, args),
          endCycle: state.cycles + minTime * repeats,
          sound,
          volume: volume ?? 75,
        };
      },
    );
    lua.global.set(
      "model_isTalking",
      (index: number) => isDialogActive(state) && state.activeDialog?.actorIndex === index,
    );

    lua.global.set("game_planAction", (callback: (count: number) => unknown) => {
      state.plan.push((count) => Boolean(callback(count)));
    });
    lua.global.set("game_isPlanning", () => state.plan.length > 0);
    lua.global.set("game_getCycles", () => state.cycles);

    await lua.doString(compatSource);
    await lua.doString("text = {}");
    await lua.doString(levelDialogSource);
    // prog_demo.lua defines planTalk/planStop/planDelay (used by demo_poster.lua)
    // in terms of the game_planAction/model_talk bindings above, and registers a
    // "dlg-x-SPACE" filler dialog + poster fonts - all safe to run here.
    if (progDemoSource) await lua.doString(progDemoSource);
    // Override the just-defined Lua dialogLoad() with a no-op - the demo calls
    // it, but we've pre-loaded the dialogs ourselves (see demoDialogsSource).
    lua.global.set("dialogLoad", () => {});
    await lua.doString(demoDialogsSource);
    // Running the demo script queues its whole plan (game_planAction) up front;
    // DemoScript.tick() then drains one command per cycle.
    await lua.doString(demoSource);

    return new DemoScript(lua, state);
  } catch (error) {
    lua.global.close();
    throw error;
  }
}
