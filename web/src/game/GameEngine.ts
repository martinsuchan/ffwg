import type { LevelData, LevelModel } from "../lua/levelLoader";
import { V2 } from "./V2";
import { Goal } from "./Goal";
import { Cube, Weight } from "./Cube";
import { createModel, isFishKind } from "./ModelFactory";
import { Room } from "./Room";
import { Unit, KeyControl, ControlSym, InputProvider } from "./Unit";

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
/** legacy/src/level/ModelFactory.cpp's createUnit(): recorded-move symbols -
 *  lowercase for fish_small, uppercase for fish_big. See docs/021. */
const SMALL_FISH_SYMBOLS: ControlSym = {
  up: "u",
  down: "d",
  left: "l",
  right: "r",
};
const BIG_FISH_SYMBOLS: ControlSym = {
  up: "U",
  down: "D",
  left: "L",
  right: "R",
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
  /** Rules.getMoveStreak() this tick - consecutive-move streak driving the
   *  visual "swims faster" effect (docs/017). */
  moveStreak: number;
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

  /** Click-to-select: activates whichever fish (if any) occupies
   *  `fieldPos` - see docs/017. */
  selectAt(fieldPos: V2): void {
    const model = this.room.askField(fieldPos);
    if (model) this.room.selectFish(model);
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

  /** Weight of whatever just landed after falling this round, NONE if
   *  nothing did - see docs/018. */
  get lastImpact(): Weight {
    return this.room.lastImpact;
  }

  /** Every move symbol recorded so far, in order - see docs/021. */
  getMoves(): string {
    return this.room.getMoves();
  }

  getStepCount(): number {
    return this.room.getStepCount();
  }

  /** Fast, render-free replay of one move symbol - throws for a move that
   *  doesn't belong to any unit or is currently blocked. See docs/022. */
  loadMove(symbol: string): void {
    this.room.loadMove(symbol);
  }

  /** Settles any pending falls once a solution's moves are exhausted. */
  settleAll(): void {
    this.room.settleAll();
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
      moveStreak: cube.rules.getMoveStreak(),
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
  // legacy/script/share/level_creation.lua's addFishAnim() flips isLeft via
  // model:change_turnSide() when a level requests LOOK_RIGHT - the Lua-side
  // LevelModel.isLeft already reflects this, but Cube itself always
  // defaults to true (facing left, matching Cube::Cube()'s own default)
  // and nothing was applying the parsed value on top of it - see docs/023.
  cube.isLeft = modelData.isLeft;
  return cube;
}

function buildUnit(cube: Cube, kind: string): Unit | undefined {
  if (!isFishKind(kind)) return undefined;
  const isSmall = kind === "fish_small";
  const keys = isSmall ? SMALL_FISH_KEYS : BIG_FISH_KEYS;
  const symbols = isSmall ? SMALL_FISH_SYMBOLS : BIG_FISH_SYMBOLS;
  // legacy/src/level/ModelFactory.cpp's createUnit(): only fish_small starts active.
  return new Unit(cube, keys, symbols, isSmall);
}

export type { InputProvider };
