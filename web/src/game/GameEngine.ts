import type { LevelData, LevelModel } from "../lua/levelLoader";
import { V2 } from "./V2";
import { Goal } from "./Goal";
import { Cube } from "./Cube";
import { createModel, isFishKind } from "./ModelFactory";
import { Room } from "./Room";
import { Unit, KeyControl, InputProvider } from "./Unit";

/** legacy/src/level/ModelFactory.cpp's createUnit() - real per-fish keys, unchanged. */
const SMALL_FISH_KEYS: KeyControl = {
  up: "KeyI",
  down: "KeyK",
  left: "KeyJ",
  right: "KeyL",
};
const BIG_FISH_KEYS: KeyControl = {
  up: "KeyW",
  down: "KeyS",
  left: "KeyA",
  right: "KeyD",
};

function goalFromName(name: string): Goal {
  switch (name) {
    case "goal_out":
      return Goal.outGoal();
    case "goal_escape":
      return Goal.escapeGoal();
    case "goal_alive":
      return Goal.aliveGoal();
    default:
      return Goal.noGoal();
  }
}

export interface RenderModel {
  index: number;
  kind: string;
  x: number;
  y: number;
  isLeft: boolean;
  isAlive: boolean;
  isOut: boolean;
  isLost: boolean;
  /** Rules.getAction() this tick ("move_left"/"turn"/"rest"/...) - see web/src/game/UnitAnimator.ts. */
  action: string;
  /** Rules.getState() this tick ("pushing"/"normal"/"dead"/...) - see web/src/game/UnitAnimator.ts. */
  state: string;
}

/**
 * Wires the parsed Lua level (web/src/lua/levelLoader.ts) into the game
 * rules port (Room/Cube/Unit/Rules/...), and exposes a per-round tick plus
 * render-friendly state snapshots. This is the "bare game logic" layer -
 * no rendering, sound, dialogs or save/load. Animation frame data
 * (LevelModel.anims) is static per level and lives in the LevelData the
 * caller already holds - GameEngine only reports live state. See docs/007
 * for the physics/rules port, docs/009 for animation.
 */
export class GameEngine {
  readonly room: Room;

  constructor(levelData: LevelData) {
    this.room = new Room(levelData.roomWidth, levelData.roomHeight);

    for (const modelData of levelData.models) {
      const cube = buildCube(modelData);
      const unit = buildUnit(cube, modelData.kind);
      this.room.addModel(cube, unit);
    }
  }

  tick(input: InputProvider): void {
    this.room.nextRound(input);
  }

  /** Space key: switch which fish is active - see docs/016. */
  switchFish(): void {
    this.room.switchFish();
  }

  isSolved(): boolean {
    return this.room.isSolved();
  }

  isSolvable(): boolean {
    return this.room.isSolvable();
  }

  cannotMove(): boolean {
    return this.room.cannotMove();
  }

  /** Cubes that died during the most recently finished round. */
  get lastDead(): Cube[] {
    return this.room.lastDead;
  }

  getRenderModels(): RenderModel[] {
    return this.room.models.map((cube, index) => ({
      index,
      kind: cube.kind,
      x: cube.location.x,
      y: cube.location.y,
      isLeft: cube.isLeft,
      isAlive: cube.isAlive,
      isOut: cube.isOut,
      isLost: cube.isLost,
      action: cube.rules.getAction(),
      state: cube.rules.getState(),
    }));
  }
}

function buildCube(modelData: LevelModel): Cube {
  const cube = createModel(
    modelData.kind,
    new V2(modelData.x, modelData.y),
    modelData.shape,
  );
  cube.goal = goalFromName(modelData.goal);
  return cube;
}

function buildUnit(cube: Cube, kind: string): Unit | undefined {
  if (!isFishKind(kind)) return undefined;
  const isSmall = kind === "fish_small";
  const keys = isSmall ? SMALL_FISH_KEYS : BIG_FISH_KEYS;
  // legacy/src/level/ModelFactory.cpp's createUnit(): only fish_small starts active.
  return new Unit(cube, keys, isSmall);
}

export type { InputProvider };
