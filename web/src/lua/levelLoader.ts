import { LuaFactory } from "wasmoon";

/** legacy/src/level/View.h: View::SCALE - pixels per grid cell. */
export const GRID_SCALE = 15;

export interface LevelModel {
  kind: string;
  x: number;
  y: number;
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

/**
 * Runs the real legacy/script/<levelName>/models.lua - plus the shared
 * level_creation.lua glue it depends on (createRoom/addModel/addItemAnim/
 * addFishAnim/...) - through wasmoon, using a minimal host API that just
 * records room/model state instead of doing physics, rendering, sound or
 * dialogs. This intentionally does NOT run the level's dialogs.lua/code.lua
 * or the rest of level_funcs.lua (level_plan/level_update/level_dialog/...)
 * - see docs/006 for why this is scoped to "models only" for now.
 *
 * legacy/ files are fetched straight off disk via Vite's dev-only /@fs/
 * route (server.fs.allow in vite.config.ts) so this proves the port against
 * the actual unmodified legacy content, not a copy. That only works against
 * the dev server - packaging Lua content for a production build is still
 * open (see docs/005).
 */
export async function loadLevelModels(levelName: string): Promise<LevelData> {
  const compatSource = await fetchText("/lua/lua50-compat.lua");
  const levelCreationSource = await fetchText(
    new URL(
      "../../../legacy/script/share/level_creation.lua",
      import.meta.url,
    ),
  );
  const modelsSource = await fetchText(
    new URL(`../../../legacy/script/${levelName}/models.lua`, import.meta.url),
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
      (kind: string, x: number, y: number, _shape: string) => {
        hostModels.push({
          kind,
          x,
          y,
          anims: new Map(),
          currentAnim: null,
          currentPhase: 0,
          isLeft: true, // Cube::Cube() default (Cube.cpp)
        });
        return hostModels.length - 1;
      },
    );
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
    lua.global.set("model_setGoal", () => {});
    lua.global.set("model_change_turnSide", () => {});
    lua.global.set("model_setBusy", () => {});
    lua.global.set("model_setEffect", () => {});

    lua.global.set("codename", levelName);

    await lua.doString(compatSource);
    await lua.doString(levelCreationSource);
    await lua.doString(modelsSource);
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
      return {
        kind: model.kind,
        x: model.x,
        y: model.y,
        picture: frames?.[model.currentPhase] ?? null,
      };
    }),
  };
}
