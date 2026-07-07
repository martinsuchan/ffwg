import { LuaFactory } from "wasmoon";

import { Shape } from "../game/Shape";

/** legacy/src/level/View.h: View::SCALE - pixels per grid cell. */
export const GRID_SCALE = 15;

export interface LevelModel {
  kind: string;
  x: number;
  y: number;
  /** Raw ASCII-art shape string passed to addModel(), needed by the physics port (see web/src/game/Shape.ts). */
  shape: string;
  /** "goal_no" | "goal_out" | "goal_escape" | "goal_alive" - see web/src/game/Goal.ts. */
  goal: string;
  /** Lua-style picture path ("images/<level>/name.png") of the model's
   *  current anim/phase, or null if the script never set one. */
  picture: string | null;
}

export interface LevelData {
  levelName: string;
  roomWidth: number;
  roomHeight: number;
  bgPicture: string;
  models: LevelModel[];
}

interface HostModel {
  kind: string;
  x: number;
  y: number;
  shape: string;
  /** Bounding-box size of the shape's "X" marks (Shape.ts) - answers model_getW/model_getH,
   *  which some levels' code.lua use to pick out specific models (e.g. grail's "every 2x2 item"). */
  w: number;
  h: number;
  goal: string;
  /** anim name -> per-side ordered list of picture paths, mirroring Anim's
   *  m_animPack[SIDE_LEFT]/m_animPack[SIDE_RIGHT] split (see Anim.cpp). */
  anims: Map<string, { left: string[]; right: string[] }>;
  currentAnim: string | null;
  currentPhase: number;
  isLeft: boolean;
}

async function fetchText(url: string | URL): Promise<string> {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`failed to fetch ${url}: ${response.status}`);
  }
  return response.text();
}

// A *static* new URL(literal, import.meta.url) is what Vite's dev server
// specially rewrites into an /@fs/<absolute-path> URL (server.fs.allow in
// vite.config.ts) - resolving it once here means every other legacy/ fetch
// below can just do plain WHATWG URL joining against it at runtime
// (new URL(relativePath, LEGACY_ROOT)), instead of needing its own literal
// new URL(..., import.meta.url) call that Vite would otherwise have to
// glob-match (which is what made every level's models.lua end up bundled
// into `vite build` output - see docs/006's "Open for next time").
// Vite's rewrite of the literal above doesn't reliably preserve the
// trailing slash, and WHATWG URL resolution treats a base with no
// trailing slash as "replace the last segment" rather than "append to
// it" - normalize explicitly so new URL(relativePath, LEGACY_ROOT) below
// actually appends instead of silently landing one directory up.
const LEGACY_ROOT = new URL(
  new URL("../../../legacy/", import.meta.url).href.replace(/\/?$/, "/"),
);

function fetchLegacyFile(relativePath: string): Promise<string> {
  return fetchText(new URL(relativePath, LEGACY_ROOT));
}

/**
 * Finds file_include(...) calls in a level's code.lua so their targets can
 * be fetched+run *before* code.lua itself (see loadLevelModels). Handles
 * the two shapes actually used across legacy/script/*\/code.lua: a plain
 * string literal, and "script/"..codename.."/name.lua" concatenation.
 */
function extractFileIncludes(source: string, levelName: string): string[] {
  const paths: string[] = [];

  const literalRe = /file_include\(\s*['"]([^'"]+)['"]\s*\)/g;
  for (const m of source.matchAll(literalRe)) {
    paths.push(m[1]);
  }

  const codenameRe =
    /file_include\(\s*["']([^"']*)["']\s*\.\.\s*codename\s*\.\.\s*["']([^"']*)["']\s*\)/g;
  for (const m of source.matchAll(codenameRe)) {
    paths.push(m[1] + levelName + m[2]);
  }

  return paths;
}

/**
 * Runs the real legacy/script/<levelName>/models.lua and code.lua - plus
 * the shared level_creation.lua/level_plan.lua glue they depend on
 * (createRoom/addModel/addFishAnim/random/createArray/...) - through
 * wasmoon, using a minimal host API that just records room/model state
 * instead of doing physics, rendering, sound or dialogs.
 *
 * code.lua matters even for this "no gameplay yet" loader because roughly
 * a dozen levels (the "final level" of each world - grail, barrel, floppy,
 * atlantis, gods, linux, map, propulsion, turtle) override the fish's
 * default goal_escape there: both fish get goal_alive (stay alive, no need
 * to leave) and one or more items get goal_out (push *them* out instead) -
 * see docs/008. That override only runs once at code.lua's top level
 * (inside its `local update_table = prog_init()`), so this loader only
 * needs code.lua's *synchronous* init to complete - its returned per-round
 * update closures (dialogs/hints/ambient animation) are never called,
 * since we never run the level's own script_update() loop. initModels()
 * (the real one pulls in fonts/jokes/border-shout sound content) is
 * stubbed as a no-op for the same reason - nothing in a goal-setting
 * prefix depends on its side effects. dialogs.lua is still not run (only
 * needed for translated text), and this loader will throw if a level's
 * code.lua needs host bindings beyond this stub set - that's a deliberate
 * "fail loud" so gaps are obvious rather than silently wrong goals.
 *
 * legacy/ files are fetched straight off disk via Vite's dev-only /@fs/
 * route (server.fs.allow in vite.config.ts) so this proves the port against
 * the actual unmodified legacy content, not a copy. That only works against
 * the dev server - packaging Lua content for a production build is still
 * open (see docs/005).
 */
export async function loadLevelModels(levelName: string): Promise<LevelData> {
  const compatSource = await fetchText("/lua/lua50-compat.lua");
  const levelCreationSource = await fetchLegacyFile(
    "script/share/level_creation.lua",
  );
  const levelPlanSource = await fetchLegacyFile("script/share/level_plan.lua");
  // Pure Lua (setanim/resetanim/goanim/endanim), no host bindings - several
  // final levels' code.lua call setanim() synchronously at init time (e.g.
  // barrel's decorative barrel-wobble sequence) to queue a scripted anim
  // string; since we never call goanim() (the per-round advance - that's
  // animation, out of scope), loading the real file just lets that init
  // call succeed instead of crashing, with no actual animation happening.
  const goanimSource = await fetchLegacyFile("script/share/prog_goanim.lua");
  const modelsSource = await fetchLegacyFile(`script/${levelName}/models.lua`);
  const codeSource = await fetchLegacyFile(`script/${levelName}/code.lua`);
  // code.lua itself calls file_include(...) at its own top level (e.g. every
  // "final level" - see docs/008 - does file_include('script/share/
  // prog_border.lua'); gods also does its own prog_ships.lua). Resolve and
  // fetch those *before* running code.lua rather than implementing
  // file_include as a host function that calls back into lua.doString() -
  // that reentrant call (running Lua from inside a host callback invoked by
  // an in-progress Lua run) corrupted wasmoon's WASM engine state
  // (surfaced as an uncaught "function signature mismatch"), even though
  // the result value still came back correct - not something to rely on.
  const includedSources = await Promise.all(
    extractFileIncludes(codeSource, levelName).map((path) =>
      fetchLegacyFile(path),
    ),
  );

  const factory = new LuaFactory();
  const lua = await factory.createEngine();

  const hostModels: HostModel[] = [];
  let room: { width: number; height: number; picture: string } | null = null;

  try {
    lua.global.set(
      "level_createRoom",
      (width: number, height: number, picture: string) => {
        room = { width, height, picture };
      },
    );
    lua.global.set("game_setRoomWaves", () => {});
    lua.global.set("sound_addSound", () => {});
    // imgList()'s file_exists probe only ever discovers extra anim *phases*
    // beyond phase 0 (see level_creation.lua) - since this POC always
    // displays phase 0, always-false is safe and keeps this loader free of
    // an asset-existence side channel. Revisit once real animation lands.
    lua.global.set("file_exists", () => false);

    lua.global.set(
      "game_addModel",
      (kind: string, x: number, y: number, shape: string) => {
        const parsedShape = new Shape(shape);
        hostModels.push({
          kind,
          x,
          y,
          shape,
          w: parsedShape.w,
          h: parsedShape.h,
          goal: "goal_no", // Cube::Cube() default (Cube.cpp: m_goal(Goal::noGoal()))
          anims: new Map(),
          currentAnim: null,
          currentPhase: 0,
          isLeft: true, // Cube::Cube() default (Cube.cpp)
        });
        return hostModels.length - 1;
      },
    );
    lua.global.set("model_getW", (modelIndex: number) => hostModels[modelIndex].w);
    lua.global.set("model_getH", (modelIndex: number) => hostModels[modelIndex].h);
    lua.global.set(
      "model_addAnim",
      (
        modelIndex: number,
        animName: string,
        picture: string,
        lookDir?: number,
      ) => {
        const model = hostModels[modelIndex];
        let anim = model.anims.get(animName);
        if (!anim) {
          anim = { left: [], right: [] };
          model.anims.set(animName, anim);
        }
        (lookDir === 1 ? anim.right : anim.left).push(picture);
      },
    );
    lua.global.set(
      "model_setAnim",
      (modelIndex: number, animName: string, phase: number) => {
        const model = hostModels[modelIndex];
        model.currentAnim = animName;
        model.currentPhase = phase;
      },
    );
    lua.global.set(
      "model_runAnim",
      (modelIndex: number, animName: string, phase?: number | null) => {
        // wasmoon marshals an omitted/nil Lua arg as null, not undefined, so
        // a `phase = 0` default parameter would never kick in - coalesce
        // explicitly instead.
        const model = hostModels[modelIndex];
        model.currentAnim = animName;
        model.currentPhase = phase ?? 0;
      },
    );
    lua.global.set(
      "model_isLeft",
      (modelIndex: number) => hostModels[modelIndex].isLeft,
    );
    lua.global.set("model_setGoal", (modelIndex: number, goal: string) => {
      hostModels[modelIndex].goal = goal;
    });
    lua.global.set("model_change_turnSide", (modelIndex: number) => {
      const model = hostModels[modelIndex];
      model.isLeft = !model.isLeft;
    });
    lua.global.set("model_setBusy", () => {});
    lua.global.set("model_setEffect", () => {});

    // code.lua's top-level init prefix (initModels/sound_playMusic/
    // getRestartCount, occasionally dialog_addFont - see linux) - all
    // no-ops or fixed values, since we only care about the goal-setting
    // statements that follow them, not fonts/sound/restart-flavored text.
    lua.global.set("initModels", () => {});
    lua.global.set("sound_playMusic", () => {});
    lua.global.set("getRestartCount", () => 1);
    lua.global.set("dialog_addFont", () => {});
    // No-op: code.lua's own file_include(...) targets are pre-fetched and
    // run just before codeSource below (extractFileIncludes) instead of
    // being handled reentrantly here - see the comment by that call.
    lua.global.set("file_include", () => {});

    lua.global.set("codename", levelName);

    await lua.doString(compatSource);
    // Defensive scratch table: a few levels (e.g. linux) stash ad-hoc
    // per-level flags on a global "text" table normally populated by
    // dialogs.lua (translated strings), which this loader doesn't run.
    await lua.doString("text = {}");
    await lua.doString(levelCreationSource);
    await lua.doString(levelPlanSource);
    await lua.doString(goanimSource);
    await lua.doString(modelsSource);
    for (const includedSource of includedSources) {
      await lua.doString(includedSource);
    }
    await lua.doString(codeSource);
  } finally {
    lua.global.close();
  }

  if (!room) {
    throw new Error(`level "${levelName}" never called createRoom()`);
  }
  const loadedRoom: { width: number; height: number; picture: string } = room;

  return {
    levelName,
    roomWidth: loadedRoom.width,
    roomHeight: loadedRoom.height,
    bgPicture: loadedRoom.picture,
    models: hostModels.map((model) => {
      const anim = model.currentAnim ? model.anims.get(model.currentAnim) : undefined;
      const frames = anim ? (model.isLeft ? anim.left : anim.right) : undefined;
      // Anim::setAnim (Anim.cpp) wraps an out-of-range phase via modulo
      // rather than dropping it - matters here because file_exists always
      // reporting false (see above) means some models only ever get frame
      // 0 loaded even though code.lua asks for a later phase (e.g. grail's
      // "light" piece: setAnim("default", 1)); without the same wrap this
      // would silently resolve to no picture at all.
      const frameCount = frames?.length ?? 0;
      const picture =
        frameCount > 0 ? frames![model.currentPhase % frameCount] : null;
      return {
        kind: model.kind,
        x: model.x,
        y: model.y,
        shape: model.shape,
        goal: model.goal,
        picture,
      };
    }),
  };
}
