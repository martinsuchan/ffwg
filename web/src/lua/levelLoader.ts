import { LuaFactory, LuaMultiReturn } from "wasmoon";

import { Shape } from "../game/Shape";

/** legacy/src/level/View.h: View::SCALE - pixels per grid cell. */
export const GRID_SCALE = 15;

/** One named animation's frames, Lua-style picture paths ("images/<level>/name.png"), per facing side. */
export interface AnimFrames {
  left: string[];
  right: string[];
}

export interface LevelModel {
  kind: string;
  x: number;
  y: number;
  /** Raw ASCII-art shape string passed to addModel(), needed by the physics port (see web/src/game/Shape.ts). */
  shape: string;
  /** "goal_no" | "goal_out" | "goal_escape" | "goal_alive" - see web/src/game/Goal.ts. */
  goal: string;
  /** Every anim name -> its frames, both sides - see web/src/game/UnitAnimator.ts for how these get selected at runtime. */
  anims: Record<string, AnimFrames>;
  /** Anim/phase/facing the script left this model in at load time (e.g. addItemAnim's "default" phase 0) - the animator's starting point. */
  initialAnim: string | null;
  initialPhase: number;
  isLeft: boolean;
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

export async function fetchText(url: string | URL): Promise<string> {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`failed to fetch ${url}: ${response.status}`);
  }
  return response.text();
}

// Where the runtime fetches the legacy `.lua` content (level scripts under
// legacy/script/**, reference solutions under legacy/solution/**). The same
// "/legacy/" URL path works in both modes, so no import.meta.url / @vite-ignore
// trickery is needed (an earlier version used that and broke dev - see docs/042):
//   - Dev: a small Vite middleware serves the repo-root legacy/ tree at
//     "/legacy/..." (serveLegacyDev in vite.config.ts), so it's read off disk
//     uncopied.
//   - Prod (`vite build`): scripts/publish.ps1 copies legacy/script +
//     legacy/solution into the deployed site under /legacy/ (see docs/041).
// BASE_URL (default "/") keeps it correct if the site is ever hosted on a
// sub-path. The trailing slash matters: WHATWG URL resolution treats a base
// without one as "replace the last segment" rather than "append", so
// new URL(relativePath, LEGACY_ROOT) below appends instead of landing a dir up.
export const LEGACY_ROOT = new URL(`${import.meta.env.BASE_URL}legacy/`, window.location.origin);

export function fetchLegacyFile(relativePath: string): Promise<string> {
  return fetchText(new URL(relativePath, LEGACY_ROOT));
}

/**
 * Finds file_include(...) calls in a level's code.lua so their targets can
 * be fetched+run *before* code.lua itself (see loadLevelModels). Handles
 * the two shapes actually used across legacy/script/*\/code.lua: a plain
 * string literal, and "script/"..codename.."/name.lua" concatenation.
 */
export function extractFileIncludes(source: string, levelName: string): string[] {
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
 * The subset of a code.lua's file_include() targets that are called at
 * *runtime* (inside a function/closure body, so indented) rather than at the
 * file's top level - only `briefcase`'s `demo_help.lua` (its auto-play
 * tutorial) is one across the whole game (every other code.lua include is a
 * top-level `prog_border`/`prog_ships`/etc. helper that must load at bootstrap).
 * These must be wrapped and run only when the trigger fires, not pre-run at
 * bootstrap (which would queue the whole show immediately). Classified by
 * leading indentation - a top-level include sits at column 0. See docs/031.
 */
export function extractRuntimeIncludes(source: string, levelName: string): Set<string> {
  const runtime = new Set<string>();
  for (const line of source.split("\n")) {
    if (!/^\s+\S/.test(line)) continue; // not indented -> top-level, runs at bootstrap
    const literalRe = /file_include\(\s*['"]([^'"]+)['"]\s*\)/g;
    for (const m of line.matchAll(literalRe)) runtime.add(m[1]);
    const codenameRe =
      /file_include\(\s*["']([^"']*)["']\s*\.\.\s*codename\s*\.\.\s*["']([^"']*)["']\s*\)/g;
    for (const m of line.matchAll(codenameRe)) runtime.add(m[1] + levelName + m[2]);
  }
  return runtime;
}

/**
 * Extracts the `saved_moves = '...'` string from a `legacy/solution/
 * <level>.lua` file (docs/021's move-symbol format, docs/022-024's
 * validator) - used by ReplayScene (docs/025) to source a watchable
 * replay's move data. Returns null if the file doesn't define one.
 */
export function extractSavedMoves(source: string): string | null {
  const match = source.match(/saved_moves\s*=\s*'([^']*)'/);
  return match ? match[1] : null;
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
 * since we never run the level's own script_update() loop.
 *
 * initModels() was originally stubbed as a no-op here, on the assumption
 * that nothing in a goal-setting prefix depends on its side effects - true
 * for most levels, but wrong for several (alibaba/city/gems/...) whose
 * code.lua calls a model's :updateAnim() *synchronously* during
 * prog_init() (not deferred into a per-round closure), which only exists
 * because the real initModels() attaches it. See docs/024: this loader
 * now runs a faithful subset of the real initModels() (see
 * level_start.lua) via INIT_MODELS_SOURCE below - the per-model
 * .afaze/.updateAnim/.X/.Y/.dir/.anim setup - deliberately excluding its
 * trailing borderShoutLoad()/stdBoreJokeLoad()/stdBlackJokeLoad()/
 * stdBublesLoad()/loadFonts() calls, which pull in sound/font content
 * this goal-extraction-only loader still doesn't need.
 *
 * dialogs.lua is still not run (only needed for translated text), and
 * this loader will throw if a level's code.lua needs host bindings beyond
 * this stub set - that's a deliberate "fail loud" so gaps are obvious
 * rather than silently wrong goals.
 *
 * legacy/ files are fetched straight off disk via Vite's dev-only /@fs/
 * route (server.fs.allow in vite.config.ts) so this proves the port against
 * the actual unmodified legacy content, not a copy. That only works against
 * the dev server - packaging Lua content for a production build is still
 * open (see docs/005).
 */
let imageManifestPromise: Promise<Set<string>> | null = null;
let audioManifestPromise: Promise<Set<string>> | null = null;

/**
 * The set of every real image path under legacy/images/ (in the
 * "images/<...>" form Lua's file_exists() receives), generated by
 * scripts/build-image-manifest.ps1 into web/public/lua/image-manifest.json.
 * Cached across loadLevelModels() calls within a page session - the
 * manifest doesn't change at runtime.
 */
export function getImageManifest(): Promise<Set<string>> {
  if (!imageManifestPromise) {
    imageManifestPromise = fetchText("/lua/image-manifest.json").then(
      (json) => new Set(JSON.parse(json) as string[]),
    );
  }
  return imageManifestPromise;
}

/**
 * The set of every real sound path (in the "sound/<...>" form Lua's
 * file_exists() receives) actually present in the *converted* web output,
 * generated by scripts/build-audio-manifest.ps1 into
 * web/public/lua/audio-manifest.json - see docs/018. Unlike the image
 * manifest (source-of-truth against legacy/images/), this deliberately
 * reflects only what's been converted so far (sound is sprite-packed, so
 * "exists" has to mean "is in a built sprite"), so file_exists correctly
 * reports false for levels outside whatever's been converted rather than
 * resolving a path that would 404 at playback time.
 */
export function getAudioManifest(): Promise<Set<string>> {
  if (!audioManifestPromise) {
    audioManifestPromise = fetchText("/lua/audio-manifest.json").then(
      (json) => new Set(JSON.parse(json) as string[]),
    );
  }
  return audioManifestPromise;
}

/**
 * A faithful *subset* of legacy/script/share/level_start.lua's real
 * initModels() - the per-model setup its own trailing content-loaders
 * depend on skip needing (see docs/024): `.afaze`/`.updateAnim`/`.X`/`.Y`/
 * `.XStart`/`.YStart`/`.dir`/`.anim`, exactly as the original sets them.
 * Deliberately omits the original's trailing borderShoutLoad()/
 * stdBoreJokeLoad()/stdBlackJokeLoad()/stdBublesLoad()/loadFonts() calls -
 * this loader only extracts goal-setting state (see loadLevelModels's own
 * doc comment), so pulling in sound/font content for that would be pure
 * waste, not fidelity. `resetanim` comes from prog_goanim.lua (already
 * loaded, pure Lua, no host bindings needed); `dir_no` comes from
 * prog_finder.lua (loaded alongside this, see loadLevelModels).
 */
const INIT_MODELS_SOURCE = `
function initModels()
    local models = getModelsTable()
    for key, model in pairs(models) do
        model.afaze = 0
        model.X, model.Y = model:getLoc()
        model.XStart, model.YStart = model:getLoc()
        model.dir = dir_no
        model.updateAnim = function(self)
            self:setAnim("default", self.afaze)
        end
        model.anim = ""
        resetanim(model)
    end
end
`;

export async function loadLevelModels(levelName: string): Promise<LevelData> {
  const imageManifest = await getImageManifest();
  const audioManifest = await getAudioManifest();
  const compatSource = await fetchText("/lua/lua50-compat.lua");
  const levelCreationSource = await fetchLegacyFile(
    "script/share/level_creation.lua",
  );
  const levelPlanSource = await fetchLegacyFile("script/share/level_plan.lua");
  // dir_no/dir_up/dir_down/dir_left/dir_right + pure-Lua pathfinding
  // helpers (unused here, harmless to define) - INIT_MODELS_SOURCE above
  // needs dir_no. prog_compatible.lua below file_include()s this too, but
  // that's a no-op stub in this loader (see below), so fetching it
  // directly here is what actually makes it run.
  const progFinderSource = await fetchLegacyFile("script/share/prog_finder.lua");
  // addm/addv/adddel/planSet/planBusy/xdist/ydist/dist/look_at/no_dialog/
  // isReady/odd/modelEquals/isWater - pure-Lua dialog-planning/distance
  // helpers several levels' code.lua call *synchronously* during
  // prog_init() (docs/024, same "some levels do more top-level work than
  // airplane/viking1 ever exercised" pattern as INIT_MODELS_SOURCE). Its
  // own getRestartCount() redefinition (calling the unbound
  // level_getRestartCounter()) is overridden back to this loader's fixed
  // stub right after it's loaded, below.
  const progCompatibleSource = await fetchLegacyFile(
    "script/share/prog_compatible.lua",
  );
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
    // Real lookup (docs/009, docs/018) - imgList() in level_creation.lua
    // uses this to discover every numbered frame ("_00", "_01", ...) of an
    // animation, not just the first; filename arrives in the same
    // "images/<...>"/"sound/<...>" form the manifest keys are in, so this
    // is a direct membership check against whichever manifest matches.
    lua.global.set(
      "file_exists",
      (filename: string) => imageManifest.has(filename) || audioManifest.has(filename),
    );

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
    lua.global.set("model_getLoc", (modelIndex: number) => {
      const model = hostModels[modelIndex];
      return LuaMultiReturn.of(model.x, model.y);
    });
    lua.global.set("model_setGoal", (modelIndex: number, goal: string) => {
      hostModels[modelIndex].goal = goal;
    });
    lua.global.set("model_change_turnSide", (modelIndex: number) => {
      const model = hostModels[modelIndex];
      model.isLeft = !model.isLeft;
    });
    lua.global.set("model_setBusy", () => {});
    lua.global.set("model_setEffect", () => {});

    // code.lua's top-level init prefix (sound_playMusic/getRestartCount,
    // occasionally dialog_addFont - see linux) - no-ops or fixed values,
    // since we only care about the goal-setting statements that follow
    // them, not sound/restart-flavored text. initModels() itself is real
    // now (INIT_MODELS_SOURCE, doString'd below) - see docs/024.
    lua.global.set("sound_playMusic", () => {});
    lua.global.set("getRestartCount", () => 1);
    lua.global.set("dialog_addFont", () => {});
    // No-op: code.lua's own file_include(...) targets are pre-fetched and
    // run just before codeSource below (extractFileIncludes) instead of
    // being handled reentrantly here - see the comment by that call.
    lua.global.set("file_include", () => {});
    // Real C++ host bindings (legacy/src/level/game-script.cpp,
    // legacy/src/level/level-script.cpp, legacy/src/gengine/
    // options-script.cpp) some levels' code.lua also calls synchronously
    // during prog_init() - safe no-ops here, same reasoning as
    // model_setBusy/model_setEffect above: purely visual (game_addDecor),
    // schedules a callback this loader never runs the per-round loop to
    // fire (level_planShow), or reads a config value nothing in a
    // goal-setting prefix depends on (options_getParam - returns "", a
    // stand-in for "no such option" callers already handle gracefully:
    // e.g. level_plan.lua's optionsGetAsInt() does
    // tonumber(options_getParam(x)) and falls back to 0 when that's nil).
    // Can't return null or undefined here even though both *mean* nil:
    // wasmoon's PromiseTypeExtension throws marshaling a returned `null`
    // (tries `.then` on it before reaching the plain-nil path), and
    // `undefined` marshals as *zero* Lua return values rather than one
    // nil - tonumber(options_getParam(x)) would call tonumber() with no
    // arguments, a Lua error, not a graceful nil.
    lua.global.set("game_addDecor", () => {});
    lua.global.set("level_planShow", () => {});
    lua.global.set("options_getParam", () => "");
    // level_plan.lua's planTimeAction() (itself pulled in by prog_compatible
    // .lua's adddel/planSet/planBusy/planDialog helpers above) queues its
    // callback via this - same "schedules something this loader's one-shot
    // synchronous pass never fires" reasoning as level_planShow.
    lua.global.set("game_planAction", () => {});

    lua.global.set("codename", levelName);

    await lua.doString(compatSource);
    // Defensive scratch table: a few levels (e.g. linux) stash ad-hoc
    // per-level flags on a global "text" table normally populated by
    // dialogs.lua (translated strings), which this loader doesn't run.
    await lua.doString("text = {}");
    await lua.doString(levelCreationSource);
    await lua.doString(levelPlanSource);
    await lua.doString(progFinderSource);
    await lua.doString(progCompatibleSource);
    // prog_compatible.lua redefines getRestartCount() to call the unbound
    // level_getRestartCounter() - put this loader's own fixed stub back.
    lua.global.set("getRestartCount", () => 1);
    await lua.doString(goanimSource);
    await lua.doString(INIT_MODELS_SOURCE);
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
    models: hostModels.map((model) => ({
      kind: model.kind,
      x: model.x,
      y: model.y,
      shape: model.shape,
      goal: model.goal,
      anims: Object.fromEntries(model.anims),
      initialAnim: model.currentAnim,
      initialPhase: model.currentPhase,
      isLeft: model.isLeft,
    })),
  };
}
