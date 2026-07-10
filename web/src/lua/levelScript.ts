import { LuaFactory, LuaMultiReturn, type LuaEngine } from "wasmoon";

import { Shape } from "../game/Shape";
import type { RenderModel } from "../game/GameEngine";
import { ROUND_MS } from "../game/timing";
import { fetchText, fetchLegacyFile, extractFileIncludes, getAudioManifest } from "./levelLoader";

/** Dialog language for both text and voice audio - see docs/018 (switched
 *  from English per docs/015 to Czech, the original's home language with by
 *  far the most complete translation+voice-over coverage across levels). */
const DIALOG_LANG = "cs";

/** Own glue, not legacy content - wraps legacy/script/share/Pickle.lua's
 *  pickle()/loadstring() and prog_save.lua's script_loadState() (both
 *  loaded verbatim below) into two single-call primitives, so LevelScript
 *  can capture/restore Lua-side model state via a plain synchronous
 *  function reference - exactly like the existing scriptUpdate call
 *  (proven-safe, docs/014) - instead of an async lua.doString() per call,
 *  which could race a same-tick tick() into the same live engine (the
 *  class of corruption docs/008 already hit once). See docs/026. */
const SAVE_STATE_GLUE_SOURCE = `
function ffwg_captureModelState()
    return pickle(getModelsTable())
end
function ffwg_restoreModelState(serialized)
    saved_models = loadstring("return " .. serialized)()
    script_loadState()
end
`;

/** A resolved playable sound: which built sprite file, and which region
 *  inside it. Works uniformly for built-in sounds (impact/death), Lua-
 *  driven one-shots (sound_playSound), and dialog/NPC voice (model_talk) -
 *  see docs/018. */
export interface ResolvedSound {
  spriteDir: string;
  region: string;
}

/** "sound/<dir>/<name>.ogg" -> {spriteDir: dir, region: name} - the same
 *  transform for every sound category, since sound_addSound()'s registered
 *  file paths and dataPathSound()'s derived dialog paths are both already
 *  in this exact form (see legacy/script/share/level_creation.lua and
 *  level_dialog.lua). Returns null for anything not shaped like that
 *  (empty string - no sound available). */
function resolveSoundPath(soundPath: string): ResolvedSound | null {
  if (!soundPath) return null;
  const withoutPrefix = soundPath.replace(/^sound\//, "");
  const lastSlash = withoutPrefix.lastIndexOf("/");
  if (lastSlash === -1) return null;
  return {
    spriteDir: withoutPrefix.slice(0, lastSlash),
    region: withoutPrefix.slice(lastSlash + 1).replace(/\.ogg$/, ""),
  };
}

/** Fetches each sprite's spritemap (JSON only, not the audio itself) and
 *  flattens every region's clip length into one "sound/<dir>/<name>.ogg" ->
 *  seconds map - tolerant of 404s (spriteDir not in our converted set),
 *  since callers already have a text-length fallback for missing durations
 *  (docs/018). */
async function fetchSoundDurations(spriteDirs: string[]): Promise<Map<string, number>> {
  const durations = new Map<string, number>();
  await Promise.all(
    spriteDirs.map(async (spriteDir) => {
      let json: string;
      try {
        json = await fetchText(`/assets/sound/${spriteDir}/sprite.json`);
      } catch {
        return;
      }
      const { spritemap } = JSON.parse(json) as {
        spritemap: Record<string, { start: number; end: number }>;
      };
      for (const [region, { start, end }] of Object.entries(spritemap)) {
        durations.set(`sound/${spriteDir}/${region}.ogg`, end - start);
      }
    }),
  );
  return durations;
}

/** One level's Lua-driven animation override for a single model this round. */
export interface ScriptAnim {
  name: string;
  phase: number;
}

/** A registered dialogId()'s font+text+derived sound path, keyed by dialog
 *  name - see docs/015, docs/018. */
interface DialogEntry {
  font: string;
  subtitle: string;
  /** dataPathSound()'s result - "" when no voice file exists for this
   *  dialog in DIALOG_LANG (level_dialog.lua's own file_exists() gate). */
  soundPath: string;
}

/** The single currently-showing subtitle (docs/015's simplification: one
 *  active slot, not the original's 5-line stacking deque). */
interface ActiveDialog {
  actorIndex: number;
  text: string;
  font: string;
  /** cycles value (LevelScriptState.cycles) at which this subtitle should
   *  stop counting as active - computed once when model_talk() fires, not
   *  re-derived, matching Dialog::getMinTime()'s one-shot duration calc. */
  endCycle: number;
  /** Resolved voice clip to play once, if this dialog has real audio
   *  (docs/018) - null falls back to the silent/text-only case. */
  sound: ResolvedSound | null;
  /** model_talk()'s volume arg, legacy default 75 (docs/018). */
  volume: number;
}

interface ScriptModel {
  w: number;
  h: number;
}

/** One sound_playSound() call this round, queued for LevelScene to
 *  actually play - see docs/018. */
interface PendingSoundEffect {
  sound: ResolvedSound;
  volume: number;
}

/** sound_playMusic(track)/sound_stopMusic() - only the *latest* command
 *  issued in a round is kept (matches SDLMusicLooper: one track at a time,
 *  always stop-before-start), null means "no new command this round, leave
 *  whatever's playing alone" - see docs/018. */
type MusicCommand = { type: "play"; track: string } | { type: "stop" };

interface LevelScriptState {
  renderModels: RenderModel[];
  scriptAnims: Map<number, ScriptAnim>;
  cycles: number;
  dialogRegistry: Map<string, DialogEntry>;
  activeDialog: ActiveDialog | null;
  /** Planner::m_plan (docs/015): a single FIFO, one command's worth of work
   *  processed per round - not one independent timer per queued action. */
  pendingActions: Array<(count: number) => boolean>;
  /** Rounds the current queue front has been waiting - resets to 0 whenever
   *  it finishes and the next one becomes the front (CommandQueue::m_count). */
  frontCount: number;
  /** name -> registered file paths, multiple sound_addSound() calls under
   *  the same name accumulate (ResourcePack's multimap - random variant
   *  selection, e.g. two impact takes) - see docs/018. */
  soundRegistry: Map<string, string[]>;
  pendingSoundEffects: PendingSoundEffect[];
  pendingMusicCommand: MusicCommand | null;
  /** "sound/<dir>/<name>.ogg" -> clip length in seconds, from pre-fetched
   *  sprite JSON spritemaps (docs/018) - backs model_talk()'s real-duration
   *  subtitle timing when a voice clip exists. */
  soundDurations: Map<string, number>;
}

function isDialogActive(state: LevelScriptState): boolean {
  return state.activeDialog !== null && state.cycles < state.activeDialog.endCycle;
}

/** Picks a random registered variant for `name` and resolves it - shared by
 *  the sound_playSound() host binding and LevelScript.resolveSound() (see
 *  docs/018). */
function resolveSoundName(state: LevelScriptState, name: string): ResolvedSound | null {
  const variants = state.soundRegistry.get(name);
  if (!variants || variants.length === 0) return null;
  const chosen = variants[Math.floor(Math.random() * variants.length)];
  return resolveSoundPath(chosen);
}

/**
 * Runs a level's real per-round Lua update loop (script_update(), defined
 * in legacy/script/share/level_start.lua) against a *persistent* wasmoon
 * engine - unlike levelLoader.ts's one-shot static extraction, this engine
 * stays alive for the life of a play session. See docs/014 for why running
 * the original Lua live (rather than porting each level's code.lua
 * item-animation logic to TypeScript) is the right call: item animation is
 * ~66 levels' worth of one-off hand-written state machines, not one shared
 * algorithm like physics/fish-animation - running the original gets every
 * level's content verbatim-correct for one bounded host-API effort,
 * instead of dozens of separate translate-and-verify passes. docs/015
 * extends this the same way for dialog text (English only, no audio).
 *
 * Deliberately does NOT touch GameEngine/Room/Rules - physics stays
 * entirely Lua-free (docs/007). Host bindings read live model state from
 * whatever RenderModel[] snapshot the caller passes to tick() each round -
 * the exact same array LevelScene already computes for animators.
 */
export class LevelScript {
  constructor(
    private readonly lua: LuaEngine,
    private readonly scriptUpdate: () => unknown,
    private readonly state: LevelScriptState,
    private readonly captureModelStateFn: () => string,
    private readonly restoreModelStateFn: (serialized: string) => void,
  ) {}

  /** Called once per physics round with the latest render state. */
  tick(renderModels: RenderModel[]): void {
    this.state.renderModels = renderModels;
    this.state.cycles += 1;
    this.scriptUpdate();
    this.processPlan();
  }

  /** The latest Lua-driven (animName, phase) override for a model this
   *  round, if any. Callers should only consult this for non-fish models -
   *  the real script_update() also drives fish anim internally (via
   *  animateUnits()), but fish stay entirely TS-owned (docs/009/013); see
   *  docs/014's "Fish vs item anim ownership" for why that's safe even
   *  though those calls still land in this same map. */
  getScriptAnim(index: number): ScriptAnim | null {
    return this.state.scriptAnims.get(index) ?? null;
  }

  /** The currently-showing subtitle, if any and not yet expired - see
   *  docs/015. Font is tracked but not yet used to pick a real typeface
   *  (deliberate simplification, see docs/015). `sound` carries the voice
   *  clip to play, if this dialog has one (docs/018). */
  getActiveSubtitle(): {
    text: string;
    font: string;
    sound: ResolvedSound | null;
    volume: number;
  } | null {
    if (!isDialogActive(this.state)) return null;
    const dialog = this.state.activeDialog as ActiveDialog;
    return { text: dialog.text, font: dialog.font, sound: dialog.sound, volume: dialog.volume };
  }

  /** Cuts a model's current sound/subtitle short - legacy's Dialogs::
   *  killSound(), called from TS for built-in death sounds (Room::playDead
   *  does this before its own death sound - docs/018), mirroring the
   *  model_killSound Lua binding below for Lua-driven calls. */
  killSound(index: number): void {
    if (this.state.activeDialog?.actorIndex === index) {
      this.state.activeDialog = null;
    }
  }

  /** Identity of the currently-active dialog (actor+start), or null - lets
   *  LevelScene detect when a *new* dialog has started (vs. the same one
   *  still showing) without re-playing its sound every round - docs/018. */
  getActiveDialogId(): string | null {
    if (!isDialogActive(this.state)) return null;
    const dialog = this.state.activeDialog as ActiveDialog;
    return `${dialog.actorIndex}@${dialog.endCycle}`;
  }

  /** Every sound_playSound() call since the last read, drained on read
   *  (not reset at the start of tick()) so a call made during the async
   *  bootstrap itself - before any tick() ever runs, e.g. a level's
   *  code.lua calling sound_playMusic() from its top-level prog_init() -
   *  is never silently discarded. See docs/018. */
  getPendingSoundEffects(): PendingSoundEffect[] {
    const effects = this.state.pendingSoundEffects;
    this.state.pendingSoundEffects = [];
    return effects;
  }

  /** The latest sound_playMusic()/sound_stopMusic() call since the last
   *  read, drained on read (null = no new command since the last read,
   *  leave whatever's playing alone) - see getPendingSoundEffects()'s
   *  bootstrap-timing note, same reasoning applies here. */
  getMusicCommand(): MusicCommand | null {
    const command = this.state.pendingMusicCommand;
    this.state.pendingMusicCommand = null;
    return command;
  }

  /** Resolves a registered sound name to a playable clip, picking a random
   *  variant if several were registered (ResourcePack::getRandomRes) - used
   *  both by sound_playSound() internally and by TS-triggered built-in
   *  sounds (impact/death) which have no Lua call site at all but share the
   *  same name->file registry, populated by sound_addSound() either way -
   *  see docs/018. */
  resolveSound(name: string): ResolvedSound | null {
    return resolveSoundName(this.state, name);
  }

  /** Every plain-data field a level's own code.lua has stashed onto a
   *  model (dialogue-progress counters, "have I shown this yet" flags,
   *  decoration/texture state it tracks itself) - legacy's saved_models,
   *  `pickle(getModelsTable())`. Never includes physics position, which
   *  model tables never cache (always fetched live via getLoc()) - see
   *  docs/026. Used by LevelScene's mid-level save (F2/dot-click). */
  captureModelState(): string {
    return this.captureModelStateFn();
  }

  /** Restores a captureModelState() snapshot onto the current (freshly
   *  bootstrapped) models - legacy's script_loadState()/
   *  assignModelAttributes(). Call once, after this LevelScript's own
   *  bootstrap has finished and physics has already been fast-forwarded
   *  to the matching position, before any tick(). Throws if `serialized`
   *  doesn't parse as a valid pickled table (e.g. saved by an older,
   *  incompatible version of this level's code.lua). */
  restoreModelState(serialized: string): void {
    this.restoreModelStateFn(serialized);
  }

  /** Planner::executeFirst() (docs/015): only the queue's front command
   *  runs each round: matches the original's serialized, single-command
   *  planning queue rather than resolving every pending action in parallel. */
  private processPlan(): void {
    const { pendingActions } = this.state;
    if (pendingActions.length === 0) return;
    const done = pendingActions[0](this.state.frontCount);
    if (done) {
      pendingActions.shift();
      this.state.frontCount = 0;
    } else {
      this.state.frontCount += 1;
    }
  }

  destroy(): void {
    this.lua.global.close();
  }
}

/**
 * Fetches and runs the full real bootstrap chain a level's script_update()
 * needs - hand-traced against legacy/script/share/level_funcs.lua's real
 * load order, then empirically verified via a spike that ran script_update()
 * up to 500 times (including a forced fish death) with zero Lua errors -
 * see docs/014. Order matters: prog_finder.lua defines dir_no/dir_up/...
 * that level_start.lua reads directly, level_dialog.lua's dialogLoad() is
 * needed by initModels()'s xLoad() calls, etc.
 *
 * `restartCount` backs level_getRestartCounter() for real (docs/015) -
 * pass the same 1-based counter LevelScene already increments once per
 * startEngine() call (Level::m_restartCounter starts at 1 and increments
 * once per real restart - an exact match already sitting in LevelScene).
 */
export async function createLevelScript(
  levelName: string,
  initialRenderModels: RenderModel[],
  restartCount: number,
): Promise<LevelScript> {
  const compatSource = await fetchText("/lua/lua50-compat.lua");
  const levelCreationSource = await fetchLegacyFile("script/share/level_creation.lua");
  const levelPlanSource = await fetchLegacyFile("script/share/level_plan.lua");
  const levelUpdateSource = await fetchLegacyFile("script/share/level_update.lua");
  const levelFontsSource = await fetchLegacyFile("script/share/level_fonts.lua");
  const levelDialogSource = await fetchLegacyFile("script/share/level_dialog.lua");
  const goanimSource = await fetchLegacyFile("script/share/prog_goanim.lua");
  const finderSource = await fetchLegacyFile("script/share/prog_finder.lua");
  const compatibleSource = await fetchLegacyFile("script/share/prog_compatible.lua");
  // pickle()/unpickle_string()/script_loadState() (docs/026) - loaded
  // verbatim, not reimplemented, so mid-level save/load reuses the
  // original's exact serialization. prog_save.lua also defines undo/redo,
  // out of scope here - confirmed safe to load anyway, since those are
  // only ever *called* by a separate C++ path (Level::saveUndo()) this
  // port has no equivalent of; its one top-level side effect (seeding an
  // unused `undo` table) needs getRestartCount() (prog_compatible.lua,
  // just fetched above) to already exist by the time it runs below.
  const pickleSource = await fetchLegacyFile("script/share/Pickle.lua");
  const progSaveSource = await fetchLegacyFile("script/share/prog_save.lua");
  const borejokesSource = await fetchLegacyFile("script/share/borejokes.lua");
  const blackjokesSource = await fetchLegacyFile("script/share/blackjokes.lua");
  const bublesSource = await fetchLegacyFile("script/share/bubles.lua");
  const bordershoutSource = await fetchLegacyFile("script/share/bordershout.lua");
  const levelStartSource = await fetchLegacyFile("script/share/level_start.lua");
  // DIALOG_LANG-only dialog content (docs/015, switched en->cs per
  // docs/018): dialogLoad() would enumerate ~15 languages via
  // select_lang.lua and call file_include per match - more work than
  // needed and the same wasmoon reentrancy risk docs/008 found. Fetching
  // these 4 files directly and running them ourselves (order-independent -
  // see docs/015) sidesteps dialogLoad() entirely.
  const shoutDialogsSource = await fetchLegacyFile(
    `script/share/shout_dialogs_${DIALOG_LANG}.lua`,
  );
  const boreDialogsSource = await fetchLegacyFile(
    `script/share/bore_dialogs_${DIALOG_LANG}.lua`,
  );
  const blackDialogsSource = await fetchLegacyFile(
    `script/share/black_dialogs_${DIALOG_LANG}.lua`,
  );
  const levelDialogsSource = await fetchLegacyFile(
    `script/${levelName}/dialogs_${DIALOG_LANG}.lua`,
  );
  const modelsSource = await fetchLegacyFile(`script/${levelName}/models.lua`);
  const codeSource = await fetchLegacyFile(`script/${levelName}/code.lua`);
  const includedSources = await Promise.all(
    extractFileIncludes(codeSource, levelName).map((path) => fetchLegacyFile(path)),
  );
  const audioManifest = await getAudioManifest();
  // Real clip lengths for the dialog sound pools this level actually loads
  // (docs/018) - tolerant of 404s (pool/level not in our converted set),
  // since model_talk() falls back to the text-length formula either way.
  const soundDurations = await fetchSoundDurations([
    `${levelName}/${DIALOG_LANG}`,
    `share/border/${DIALOG_LANG}`,
    `share/borejokes/${DIALOG_LANG}`,
    `share/blackjokes/${DIALOG_LANG}`,
  ]);

  const factory = new LuaFactory();
  const lua = await factory.createEngine();

  const state: LevelScriptState = {
    // Seeded with the real starting positions rather than left empty:
    // code.lua's own top-level prog_init() calls initModels() (level_start.lua),
    // which calls model:getLoc() for every model *during* this setup, well
    // before the first real tick() - an empty snapshot would crash that.
    renderModels: initialRenderModels,
    scriptAnims: new Map<number, ScriptAnim>(),
    cycles: 0,
    dialogRegistry: new Map<string, DialogEntry>(),
    activeDialog: null,
    pendingActions: [],
    frontCount: 0,
    soundRegistry: new Map<string, string[]>(),
    pendingSoundEffects: [],
    pendingMusicCommand: null,
    soundDurations,
  };
  const modelShapes: ScriptModel[] = [];
  // level_dialog.lua's own DialogState.lang is never actually set to
  // DIALOG_LANG (dialogLoad() - the only function that sets it - is
  // deliberately bypassed, same reentrancy reasoning as docs/015), so
  // dataPathSound()'s own soundPath computation is unusable (it resolves
  // against lang="" - a malformed path that never exists). We already know
  // the exact soundPrefix each of the 4 dialog files uses (matching each
  // one's real dialogLoad(prefix, soundPrefix) call - see
  // legacy/script/share/{bordershout,borejokes,blackjokes}.lua), so
  // dialog_addDialog recomputes the real path itself from this instead of
  // trusting whatever Lua passed in - see docs/018.
  let currentSoundPrefix = "";

  function setScriptAnim(index: number, name: string, phase: number): void {
    state.scriptAnims.set(index, { name, phase });
  }

  try {
    // Room/font registration - none of this is read back; the static
    // LevelData from levelLoader.ts already has everything needed to
    // render the room and resolve anim frame paths.
    lua.global.set("level_createRoom", () => {});
    lua.global.set("game_setRoomWaves", () => {});
    lua.global.set("dialog_addFont", () => {});
    // Real for "sound/..." (docs/018 - unlocks dataPathSound()'s dialog
    // voice resolution and level_creation.lua's sound_addSound() calls).
    // Still always "not found" for "images/..." - frame discovery doesn't
    // matter in this engine (addItemAnim's/addBodyAnim's imgList() loop
    // only builds frame lists levelLoader.ts's static pass already
    // captured), and there's no reason to risk changing that established,
    // unrelated behavior here.
    lua.global.set("file_exists", (path: string) =>
      path.startsWith("sound/") ? audioManifest.has(path) : false,
    );

    // Sound (docs/018). sound_addSound accumulates variants per name
    // (ResourcePack's multimap - e.g. two impact takes); sound_playSound
    // picks a random one (getRandomRes) and queues it for LevelScene to
    // actually play this round.
    lua.global.set("sound_addSound", (name: string, file: string) => {
      const variants = state.soundRegistry.get(name);
      if (variants) variants.push(file);
      else state.soundRegistry.set(name, [file]);
    });
    lua.global.set("sound_playSound", (name: string, volume?: number | null) => {
      const sound = resolveSoundName(state, name);
      if (sound) state.pendingSoundEffects.push({ sound, volume: volume ?? 100 });
    });
    // Only one track at a time, always stop-before-start (SDLMusicLooper) -
    // the *last* call in a round wins; LevelScene diffs this against
    // whatever's currently playing each round. track arrives as the raw
    // legacy path (e.g. "music/rybky14.ogg", matching Path::dataReadPath -
    // relative to legacy/) - strip it down to the bare basename
    // convert-music.ps1 names its output files with ("rybky14").
    lua.global.set("sound_playMusic", (track: string) => {
      const basename = track.replace(/^music\//, "").replace(/\.ogg$/, "");
      state.pendingMusicCommand = { type: "play", track: basename };
    });
    lua.global.set("sound_stopMusic", () => {
      state.pendingMusicCommand = { type: "stop" };
    });

    lua.global.set(
      "game_addModel",
      (_kind: string, _x: number, _y: number, shape: string) => {
        const parsedShape = new Shape(shape);
        modelShapes.push({ w: parsedShape.w, h: parsedShape.h });
        return modelShapes.length - 1;
      },
    );
    lua.global.set("model_getW", (index: number) => modelShapes[index].w);
    lua.global.set("model_getH", (index: number) => modelShapes[index].h);
    // addAnim itself is a no-op (see file_exists above); setAnim/runAnim/
    // useSpecialAnim are the real per-round item-animation primitive this
    // whole file exists for.
    lua.global.set("model_addAnim", () => {});
    lua.global.set("model_setAnim", (index: number, name: string, phase: number) =>
      setScriptAnim(index, name, phase),
    );
    lua.global.set(
      "model_runAnim",
      (index: number, name: string, phase?: number | null) =>
        // wasmoon marshals an omitted/nil Lua arg as null, not undefined.
        setScriptAnim(index, name, phase ?? 0),
    );
    lua.global.set(
      "model_useSpecialAnim",
      (index: number, name: string, phase: number) => setScriptAnim(index, name, phase),
    );

    lua.global.set("model_getLoc", (index: number) => {
      const model = state.renderModels[index];
      return LuaMultiReturn.of(model.x, model.y);
    });
    lua.global.set("model_getAction", (index: number) => state.renderModels[index].action);
    lua.global.set("model_getState", (index: number) => state.renderModels[index].state);
    lua.global.set("model_isAlive", (index: number) => state.renderModels[index].isAlive);
    lua.global.set("model_isOut", (index: number) => state.renderModels[index].isOut);
    lua.global.set(
      "model_isLeft",
      (index: number) => state.renderModels[index].isLeft,
    );

    // Dynamically changing goal/turn-side/busy/effect during play isn't a
    // scenario any level's per-round update closure exercises today
    // (verified via the docs/014 spike) - safe no-ops, matching
    // levelLoader.ts's same-named stubs for the static pass.
    lua.global.set("model_setGoal", () => {});
    lua.global.set("model_change_turnSide", () => {});
    lua.global.set("model_setBusy", () => {});
    lua.global.set("model_setEffect", () => {});

    // Dialog text+sound (docs/015, docs/018). dialog_addDialog registers
    // name -> {font, subtitle, soundPath} the moment a dialog file runs
    // (dialogId -> dialogStr -> dialog_addDialog, all pure Lua in
    // level_dialog.lua) - Lua's own computed soundPath is unusable (see
    // currentSoundPrefix's comment above), so it's ignored and recomputed
    // from currentSoundPrefix + the real audio manifest instead.
    lua.global.set(
      "dialog_addDialog",
      (name: string, _lang: string, _soundPath: string, font: string, subtitle: string) => {
        const candidate = `${currentSoundPrefix}${name}.ogg`;
        const soundPath = audioManifest.has(candidate) ? candidate : "";
        state.dialogRegistry.set(name, { font, subtitle, soundPath });
      },
    );
    // model_talk's real 5th "immediate" arg (dialogFlag in the original)
    // isn't distinguished here - every call becomes the one active
    // subtitle slot, matching the addm/addv/planDialog path airplane
    // actually uses (always immediate=true) - see docs/015's "Deliberate
    // simplifications". Duration uses the real voice clip's length when one
    // resolved (docs/018) - matching the original's actual behavior, where
    // real playback drives subtitle duration whenever audio is available -
    // falling back to Dialog::getMinTime()'s own no-sound formula
    // (min(180, textLength) cycles) otherwise, not an invented heuristic.
    lua.global.set(
      "model_talk",
      (
        index: number,
        dialogName: string,
        volume?: number | null,
        loops?: number | null,
      ) => {
        const entry = state.dialogRegistry.get(dialogName);
        if (!entry) return;
        const sound = entry.soundPath ? resolveSoundPath(entry.soundPath) : null;
        const durationSeconds = entry.soundPath
          ? state.soundDurations.get(entry.soundPath)
          : undefined;
        const minTime =
          durationSeconds !== undefined
            ? Math.ceil((durationSeconds * 1000) / ROUND_MS)
            : Math.min(180, entry.subtitle.length);
        const repeats = (loops ?? 0) + 1;
        state.activeDialog = {
          actorIndex: index,
          text: entry.subtitle,
          font: entry.font,
          endCycle: state.cycles + minTime * repeats,
          sound,
          volume: volume ?? 75,
        };
      },
    );
    // Cuts a model's current sound/subtitle short - legacy's Dialogs::
    // killSound(), needed by e.g. viking1's instrument-swapping NPCs
    // (melodak1/piskac/basak cut the previous note before playing the
    // next) - see docs/018.
    lua.global.set("model_killSound", (index: number) => {
      if (state.activeDialog?.actorIndex === index) {
        state.activeDialog = null;
      }
    });
    lua.global.set(
      "model_isTalking",
      (index: number) => isDialogActive(state) && state.activeDialog?.actorIndex === index,
    );
    lua.global.set("dialog_isDialog", () => isDialogActive(state));
    // Kills the plan queue outright (Planner::killPlan) - cheap and
    // correct now that the queue is real, rather than leaving stale
    // actions to run once game_planAction stopped being a no-op.
    lua.global.set("game_killPlan", () => {
      state.pendingActions.length = 0;
      state.frontCount = 0;
    });
    lua.global.set("game_planAction", (callback: (count: number) => unknown) => {
      state.pendingActions.push((count) => Boolean(callback(count)));
    });
    lua.global.set("game_isPlanning", () => state.pendingActions.length > 0);

    lua.global.set("game_getCycles", () => state.cycles);
    // script_update() is called exactly once per physics round, always -
    // there's no separate render-vs-logic distinction to detect here.
    lua.global.set("level_isNewRound", () => true);
    lua.global.set("level_isSolved", () => false);
    // The level's static position in the world-map campaign tree
    // (LevelNode::m_depth), not a stuck/death counter - we have no
    // world-map system, so this stays a constant (docs/015).
    lua.global.set("level_getDepth", () => 0);
    // Real now (docs/015): Level::m_restartCounter starts at 1 and
    // increments once per restart, nothing fancier - restartCount is
    // LevelScene's own matching counter, passed straight through.
    lua.global.set("level_getRestartCounter", () => restartCount);

    lua.global.set("file_include", () => {});
    lua.global.set("codename", levelName);

    await lua.doString(compatSource);
    await lua.doString("text = {}");
    await lua.doString(levelCreationSource);
    await lua.doString(levelPlanSource);
    await lua.doString(levelUpdateSource);
    await lua.doString(levelFontsSource);
    await lua.doString(levelDialogSource);
    await lua.doString(goanimSource);
    await lua.doString(finderSource);
    await lua.doString(compatibleSource);
    await lua.doString(pickleSource);
    await lua.doString(progSaveSource);
    await lua.doString(SAVE_STATE_GLUE_SOURCE);
    await lua.doString(borejokesSource);
    await lua.doString(blackjokesSource);
    await lua.doString(bublesSource);
    await lua.doString(bordershoutSource);
    await lua.doString(levelStartSource);
    // Matches each file's own real dialogLoad(prefix, soundPrefix) call -
    // legacy/script/share/bordershout.lua, borejokes.lua, blackjokes.lua -
    // and level_dialog.lua's own default soundPrefix ("sound/"..codename..
    // "/") for a level's own dialogs file.
    currentSoundPrefix = `sound/share/border/${DIALOG_LANG}/`;
    await lua.doString(shoutDialogsSource);
    currentSoundPrefix = `sound/share/borejokes/${DIALOG_LANG}/`;
    await lua.doString(boreDialogsSource);
    currentSoundPrefix = `sound/share/blackjokes/${DIALOG_LANG}/`;
    await lua.doString(blackDialogsSource);
    currentSoundPrefix = `sound/${levelName}/${DIALOG_LANG}/`;
    await lua.doString(levelDialogsSource);
    await lua.doString(modelsSource);
    for (const includedSource of includedSources) {
      await lua.doString(includedSource);
    }
    // codeSource's own top-level prog_init() already calls initModels()
    // itself (confirmed for airplane; level_start.lua's own comment says
    // "Run this function in you init" - every level's code.lua is expected
    // to call it, not the driver) - calling it again here would duplicate
    // borderShoutLoad()/stdBoreJokeLoad()/etc.'s side effects.
    await lua.doString(codeSource);

    const scriptUpdate = lua.global.get("script_update") as () => unknown;
    const captureModelStateFn = lua.global.get("ffwg_captureModelState") as () => string;
    const restoreModelStateFn = lua.global.get("ffwg_restoreModelState") as (
      serialized: string,
    ) => void;
    return new LevelScript(
      lua,
      scriptUpdate,
      state,
      captureModelStateFn,
      restoreModelStateFn,
    );
  } catch (error) {
    lua.global.close();
    throw error;
  }
}
