import { V2 } from "./V2";
import { Dir } from "./Dir";
import { Shape } from "./Shape";
import { Goal } from "./Goal";
import { Rules } from "./Rules";

/** Port of legacy/src/level/Cube.h's Cube::eWeight. Order matters - power/weight comparisons use >=. */
export enum Weight {
  NONE = 0,
  LIGHT = 1,
  HEAVY = 2,
  FIXED = 3,
}

/** Port of legacy/src/level/Cube.h's Cube::eAction. */
export enum Action {
  NO,
  FALL,
  MOVE,
}

/**
 * A single game object: a fish, an item, or the room's fixed wall shape.
 * Port of legacy/src/level/Cube.h/.cpp (rendering/dialog/save-state fields
 * dropped - see docs/007 for what this POC intentionally leaves out).
 */
export class Cube {
  index = -1;
  busy = false;
  location: V2;
  isAlive: boolean;
  isOut = false;
  isLost = false;
  weight: Weight;
  power: Weight;
  isLeft = true;
  goal: Goal = Goal.noGoal();
  /** "output_DIR" plug direction (windoze's spuntik) - a fish that pushes
   *  into it goes out (Rules.touchSpec). Dir.NO for every normal model. */
  outDir: Dir = Dir.NO;
  /** How many more fish this plug can absorb before it turns into a normal
   *  LIGHT item - legacy Cube::m_outCapacity. See setOutDir/decOutCapacity. */
  outCapacity = 0;
  readonly shape: Shape;
  readonly rules: Rules;
  /** Lua "kind" string (e.g. "fish_small", "item_heavy") - for rendering/debugging only. */
  readonly kind: string;

  constructor(
    kind: string,
    location: V2,
    weight: Weight,
    power: Weight,
    isAlive: boolean,
    shape: Shape,
  ) {
    this.kind = kind;
    this.location = location;
    this.weight = weight;
    this.power = power;
    this.isAlive = isAlive;
    this.shape = shape;
    this.rules = new Rules(this);
  }

  get isWall(): boolean {
    return this.weight >= Weight.FIXED;
  }

  /** Configure this cube as a one-way "output_DIR" plug - legacy
   *  Cube::setOutDir (default capacity 2, i.e. absorbs two fish). Only used by
   *  windoze's spuntik (output_left). */
  setOutDir(dir: Dir, capacity = 2, weight = Weight.FIXED): void {
    this.outCapacity = capacity;
    this.outDir = dir;
    this.weight = weight;
  }

  isOutDir(dir: Dir): boolean {
    return this.outDir === dir;
  }

  /** A fish just went out through this plug - legacy Cube::decOutCapacity.
   *  Once the capacity is exhausted the plug stops being an output and turns
   *  into a normal light item. */
  decOutCapacity(): void {
    if (this.outCapacity > 0) {
      this.outCapacity--;
      if (this.outCapacity === 0) {
        this.outDir = Dir.NO;
        this.weight = Weight.LIGHT;
        this.outCapacity = -1;
      }
    }
  }

  get isBorder(): boolean {
    return this.index === -1;
  }

  get isSatisfy(): boolean {
    return this.goal.isSatisfy(this);
  }

  get isWrong(): boolean {
    return this.goal.isWrong(this);
  }

  changeDie(): void {
    this.isAlive = false;
  }

  /** Object is going out of the room - becomes immovable while crossing the border. */
  changeGoingOut(): void {
    this.weight = Weight.FIXED;
  }

  changeGoOut(): void {
    this.isOut = true;
    this.changeRemove();
  }

  changeRemove(): void {
    this.isLost = true;
    this.weight = Weight.NONE;
    this.location = new V2(-1000, -1000);
  }

  changeTurnSide(): void {
    this.isLeft = !this.isLeft;
  }

  changeSetLocation(loc: V2): void {
    this.location = loc;
  }
}

/** Port of legacy/src/level/ModelFactory.cpp's createBorder(). */
export function createBorderCube(): Cube {
  return new Cube(
    "border",
    new V2(-1, -1),
    Weight.FIXED,
    Weight.NONE,
    false,
    new Shape("X\n"),
  );
}
