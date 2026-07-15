import type { LevelData, LevelModel } from "../lua/levelLoader";
import { V2 } from "./V2";
import { Dir } from "./Dir";
import { Goal } from "./Goal";
import { Cube, Weight } from "./Cube";
import { createModel, isFishKind, parseExtraControlSym } from "./ModelFactory";
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
/** windoze's extra fish have NO dedicated keyboard keys (legacy createUnit uses
 *  SDLK_LAST for all four) - they're driven only while active, via arrow keys.
 *  Empty key codes never match held/queued input. See docs/035. */
const NO_KEYS: KeyControl = { up: "", down: "", left: "", right: "" };

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
  /** Rules.isAtBorder() - whether this model currently sits against the
   *  room's outer edge with a clear path out. Already computed internally
   *  since docs/007; exposed here for the live Lua engine's
   *  model_isAtBorder() binding (docs/028). */
  isAtBorder: boolean;
  /** Rules.getTouchDir() - the Dir this model pushed against something it
   *  couldn't move this round (Dir.NO otherwise). Backs model_getTouchDir()
   *  for levels whose code.lua reads getTouchDir() (e.g. cabin1) - docs/033. */
  touchDir: Dir;
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

  /** Which model occupies grid cell (x, y) - legacy's Room::askField()/
   *  Field::getModel(), i.e. resolved through each model's real multi-cell
   *  mask, not just its anchor. Returns the model's index, `-1` for the shared
   *  border Cube (any out-of-bounds probe - "border is as wall"), or null for
   *  empty water. Backs the model_equals() binding - see docs/054. */
  askFieldIndex(x: number, y: number): number | null {
    const cube = this.room.askField(new V2(x, y));
    if (!cube) return null;
    // indexOf() is -1 for the border Cube, which isn't in `models` - exactly
    // the index the original's border reports (Cube::getIndex() == -1).
    return this.room.models.indexOf(cube);
  }

  isSolvable(): boolean {
    return this.room.isSolvable();
  }

  cannotMove(): boolean {
    return this.room.cannotMove();
  }

  /** Nothing is still moving/falling this round (room settled) - the same
   *  gate isSolved() applies. Used to avoid declaring "both fish stuck"
   *  during the not-yet-settled round a winning escape completes on. */
  isFresh(): boolean {
    return this.room.isFresh();
  }

  /** model_setBusy(index, value): freeze/unfreeze a fish for player control
   *  (legacy Cube::setBusy). A busy fish can't be driven or made active, but
   *  still participates in physics and still counts as alive. windoze toggles
   *  this to swap control between the normal fish and the extra couple. */
  setBusy(index: number, busy: boolean): void {
    const cube = this.room.models[index];
    if (cube) cube.busy = busy;
  }

  /** game_checkActive(): switch control away from an active fish that can no
   *  longer drive (e.g. just made busy) - legacy Room::checkActive. */
  checkActive(): void {
    this.room.checkActive();
  }

  /** game_setFastFalling(value): when on, settle all pending falls in one round
   *  instead of one step per round - legacy Room::setFastFalling. Outcome-
   *  identical to normal falling, just faster (windoze uses it while the player
   *  solves the bonus). */
  setFastFalling(value: boolean): void {
    this.room.setFastFalling(value);
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

  /** One round of watchable replay, sourced from a recorded move string
   *  instead of live input - see docs/025.
   *  @return whether `symbol` was consumed this round. */
  tickReplay(symbol: string | null): boolean {
    return this.room.replayRound(symbol);
  }

  /** Settle pending falls at the start of a briefcase auto-play "show" round -
   *  see docs/031. The show drives the round itself (player input disabled). */
  beginShowRound(): void {
    this.room.beginShowRound();
  }

  /** Apply one scripted show move (legacy Room::makeMove semantics) - throws
   *  on a fresh-but-impossible move (the show driver catches it). See docs/031. */
  showMove(symbol: string): boolean {
    return this.room.showMove(symbol);
  }

  /** The active fish's model index + current action + speedup (its own
   *  moveStreak), for the scene's phase/speed computation - legacy
   *  Controls::lockPhases uses only the active fish, so every co-moving model
   *  shares one slide duration (no per-model desync). See docs/046. */
  getActiveInfo(): { index: number; action: string; speedup: number; powerful: boolean } | null {
    const active = this.room.getActiveUnit();
    if (!active) return null;
    const index = this.room.models.indexOf(active.cube);
    if (index < 0) return null;
    return {
      index,
      action: active.cube.rules.getAction(),
      speedup: active.cube.rules.getMoveStreak(),
      // legacy StepDecor: blue when the active fish is "powerful" (power >=
      // HEAVY, i.e. the big fish), orange otherwise (Unit::isPowerful).
      powerful: active.cube.power >= Weight.HEAVY,
    };
  }

  /** Any model has a move decided this round (a fish move, a push, or a fall) -
   *  gates the phase-locked slide vs. an idle one-cycle round (docs/046). */
  anyModelMoving(): boolean {
    return this.room.models.some((cube) => {
      const a = cube.rules.getAction();
      return a === "move_left" || a === "move_right" || a === "move_up" || a === "move_down";
    });
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
      isAtBorder: cube.rules.isAtBorder(),
      touchDir: cube.rules.getTouchDir(),
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
  // windoze's extra couple (fish_extra-wxyz / fish_EXTRA-WXYZ): no dedicated
  // keys, move symbols parsed from the kind string. Never start active.
  if (kind.startsWith("fish_extra") || kind.startsWith("fish_EXTRA")) {
    return new Unit(cube, NO_KEYS, parseExtraControlSym(kind), false);
  }
  const isSmall = kind === "fish_small";
  const keys = isSmall ? SMALL_FISH_KEYS : BIG_FISH_KEYS;
  const symbols = isSmall ? SMALL_FISH_SYMBOLS : BIG_FISH_SYMBOLS;
  // legacy/src/level/ModelFactory.cpp's createUnit(): only fish_small starts active.
  return new Unit(cube, keys, symbols, isSmall);
}

export type { InputProvider };
