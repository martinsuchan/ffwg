import { LuaFactory, LuaMultiReturn, type LuaEngine } from "wasmoon";

import { Shape } from "../game/Shape";
import type { RenderModel } from "../game/GameEngine";
import { fetchText, fetchLegacyFile, extractFileIncludes } from "./levelLoader";

/** One level's Lua-driven animation override for a single model this round. */
export interface ScriptAnim {
  name: string;
  phase: number;
}

/** A registered dialogId()'s font+text, keyed by dialog name - see docs/015. */
interface DialogEntry {
  font: string;
  subtitle: string;
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
}

interface ScriptModel {
  w: number;
  h: number;
}

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
}

function isDialogActive(state: LevelScriptState): boolean {
  return state.activeDialog !== null && state.cycles < state.activeDialog.endCycle;
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
   *  (deliberate simplification, see docs/015). */
  getActiveSubtitle(): { text: string; font: string } | null {
    if (!isDialogActive(this.state)) return null;
    const dialog = this.state.activeDialog as ActiveDialog;
    return { text: dialog.text, font: dialog.font };
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
  const borejokesSource = await fetchLegacyFile("script/share/borejokes.lua");
  const blackjokesSource = await fetchLegacyFile("script/share/blackjokes.lua");
  const bublesSource = await fetchLegacyFile("script/share/bubles.lua");
  const bordershoutSource = await fetchLegacyFile("script/share/bordershout.lua");
  const levelStartSource = await fetchLegacyFile("script/share/level_start.lua");
  // English-only dialog content (docs/015): dialogLoad() would enumerate
  // ~15 languages via select_lang.lua and call file_include per match -
  // more work than needed and the same wasmoon reentrancy risk docs/008
  // found. Fetching these 4 files directly and running them ourselves
  // (order-independent - see docs/015) sidesteps dialogLoad() entirely.
  const shoutDialogsSource = await fetchLegacyFile("script/share/shout_dialogs_en.lua");
  const boreDialogsSource = await fetchLegacyFile("script/share/bore_dialogs_en.lua");
  const blackDialogsSource = await fetchLegacyFile("script/share/black_dialogs_en.lua");
  const levelDialogsSource = await fetchLegacyFile(`script/${levelName}/dialogs_en.lua`);
  const modelsSource = await fetchLegacyFile(`script/${levelName}/models.lua`);
  const codeSource = await fetchLegacyFile(`script/${levelName}/code.lua`);
  const includedSources = await Promise.all(
    extractFileIncludes(codeSource, levelName).map((path) => fetchLegacyFile(path)),
  );

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
  };
  const modelShapes: ScriptModel[] = [];

  function setScriptAnim(index: number, name: string, phase: number): void {
    state.scriptAnims.set(index, { name, phase });
  }

  try {
    // Room/sound/font registration - none of this is read back; the
    // static LevelData from levelLoader.ts already has everything needed
    // to render the room and resolve anim frame paths.
    lua.global.set("level_createRoom", () => {});
    lua.global.set("game_setRoomWaves", () => {});
    lua.global.set("sound_addSound", () => {});
    lua.global.set("sound_playMusic", () => {});
    lua.global.set("dialog_addFont", () => {});
    // Frame discovery doesn't matter in this engine (addItemAnim's/
    // addBodyAnim's imgList() loop is only used to build the frame lists
    // levelLoader.ts's static pass already captured) - always "not found"
    // keeps models.lua's setup fast and side-effect-free here.
    lua.global.set("file_exists", () => false);

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

    // Dialog text (docs/015). dialog_addDialog registers name -> {font,
    // subtitle} the moment a dialog file runs (dialogId -> dialogStr ->
    // dialog_addDialog, all pure Lua in level_dialog.lua). lang/soundPath
    // are ignored - English-only, no audio.
    lua.global.set(
      "dialog_addDialog",
      (name: string, _lang: string, _soundPath: string, font: string, subtitle: string) => {
        state.dialogRegistry.set(name, { font, subtitle });
      },
    );
    // model_talk's real 5th "immediate" arg (dialogFlag in the original)
    // isn't distinguished here - every call becomes the one active
    // subtitle slot, matching the addm/addv/planDialog path airplane
    // actually uses (always immediate=true) - see docs/015's "Deliberate
    // simplifications". Duration is Dialog::getMinTime()'s own no-sound
    // fallback formula (min(180, textLength) cycles), not an invented
    // heuristic - the original uses exactly this when no audio is playing.
    lua.global.set(
      "model_talk",
      (index: number, dialogName: string, _volume?: number, loops?: number | null) => {
        const entry = state.dialogRegistry.get(dialogName);
        if (!entry) return;
        const minTime = Math.min(180, entry.subtitle.length);
        const repeats = (loops ?? 0) + 1;
        state.activeDialog = {
          actorIndex: index,
          text: entry.subtitle,
          font: entry.font,
          endCycle: state.cycles + minTime * repeats,
        };
      },
    );
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
    lua.global.set("sound_playSound", () => {});

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
    await lua.doString(borejokesSource);
    await lua.doString(blackjokesSource);
    await lua.doString(bublesSource);
    await lua.doString(bordershoutSource);
    await lua.doString(levelStartSource);
    await lua.doString(shoutDialogsSource);
    await lua.doString(boreDialogsSource);
    await lua.doString(blackDialogsSource);
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
    return new LevelScript(lua, scriptUpdate, state);
  } catch (error) {
    lua.global.close();
    throw error;
  }
}
