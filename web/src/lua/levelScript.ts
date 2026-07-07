import { LuaFactory, LuaMultiReturn, type LuaEngine } from "wasmoon";

import { Shape } from "../game/Shape";
import type { RenderModel } from "../game/GameEngine";
import { fetchText, fetchLegacyFile, extractFileIncludes } from "./levelLoader";

/** One level's Lua-driven animation override for a single model this round. */
export interface ScriptAnim {
  name: string;
  phase: number;
}

interface ScriptModel {
  w: number;
  h: number;
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
 * instead of dozens of separate translate-and-verify passes.
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
    private readonly state: {
      renderModels: RenderModel[];
      scriptAnims: Map<number, ScriptAnim>;
    },
  ) {}

  /** Called once per physics round with the latest render state. */
  tick(renderModels: RenderModel[]): void {
    this.state.renderModels = renderModels;
    this.scriptUpdate();
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
 */
export async function createLevelScript(
  levelName: string,
  initialRenderModels: RenderModel[],
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
  const modelsSource = await fetchLegacyFile(`script/${levelName}/models.lua`);
  const codeSource = await fetchLegacyFile(`script/${levelName}/code.lua`);
  const includedSources = await Promise.all(
    extractFileIncludes(codeSource, levelName).map((path) => fetchLegacyFile(path)),
  );

  const factory = new LuaFactory();
  const lua = await factory.createEngine();

  const state = {
    // Seeded with the real starting positions rather than left empty:
    // code.lua's own top-level prog_init() calls initModels() (level_start.lua),
    // which calls model:getLoc() for every model *during* this setup, well
    // before the first real tick() - an empty snapshot would crash that.
    renderModels: initialRenderModels,
    scriptAnims: new Map<number, ScriptAnim>(),
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

    // No dialog/talk/sound system yet (phase 3) - model_isTalking always
    // false and model_talk a no-op are enough for every level's code to
    // run its animation logic; nothing here is read back.
    lua.global.set("model_isTalking", () => false);
    lua.global.set("model_talk", () => {});
    lua.global.set("sound_playSound", () => {});

    // game_isPlanning() = true deliberately makes no_dialog() ("not
    // dialog_isDialog() and not game_isPlanning()") always false, cleanly
    // and centrally disabling every level's ambient-dialog/banter branches
    // without needing a real game_planAction scheduler - see docs/014.
    lua.global.set("dialog_isDialog", () => false);
    lua.global.set("game_isPlanning", () => true);
    lua.global.set("game_killPlan", () => {});
    // stdBlackJoke's death-reaction path is NOT gated by no_dialog() and
    // calls this via planDialog/planTimeAction once a fish dies (already a
    // real mechanic - docs/011/013) - a no-op stub (never invoking the
    // planned callback) is confirmed safe via the docs/014 spike.
    lua.global.set("game_planAction", () => {});

    let cycles = 0;
    lua.global.set("game_getCycles", () => cycles);
    // script_update() is called exactly once per physics round, always -
    // there's no separate render-vs-logic distinction to detect here.
    lua.global.set("level_isNewRound", () => true);
    lua.global.set("level_isSolved", () => false);
    lua.global.set("level_getDepth", () => 0);
    // level_getRestartCounter() backs prog_compatible.lua's real
    // getRestartCount() (function getRestartCount() return
    // level_getRestartCounter() end) - no separate stub needed for
    // getRestartCount/initModels themselves, since level_start.lua/
    // prog_compatible.lua define the real globals before code.lua runs.
    lua.global.set("level_getRestartCounter", () => 1);

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
    const wrappedUpdate = () => {
      cycles += 1;
      return scriptUpdate();
    };
    return new LevelScript(lua, wrappedUpdate, state);
  } catch (error) {
    lua.global.close();
    throw error;
  }
}
