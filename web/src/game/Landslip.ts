import { Dir } from "./Dir";
import { Cube, Weight } from "./Cube";

/**
 * Per-round gravity resolution: figures out which inert objects are
 * unsupported and should fall. Port of legacy/src/level/Landslip.h/.cpp.
 * Fish never fall (isAlive models are always treated as "fixed").
 */
export class Landslip {
  private impact: Weight = Weight.NONE;
  private readonly stoned: Set<Cube> = new Set();

  constructor(private readonly models: readonly Cube[]) {}

  /** Repeatedly "stones" (marks as supported) everything resting on something
   *  fixed, then lets whatever's left fall one cell. Returns whether anything fell. */
  computeFall(): boolean {
    let changed = true;
    while (changed) {
      changed = false;
      for (const model of this.models) {
        if (this.stoneModel(model)) changed = true;
      }
    }
    let falling = false;
    for (const model of this.models) {
      if (this.fallModel(model)) falling = true;
    }
    return falling;
  }

  getImpact(): Weight {
    return this.impact;
  }

  private stoneModel(model: Cube): boolean {
    if (!this.isStoned(model) && (this.isFixed(model) || this.isOnPad(model))) {
      this.stoned.add(model);
      return true;
    }
    return false;
  }

  private isOnPad(model: Cube): boolean {
    return model.rules.getResist(Dir.DOWN).some((pad) => this.isFixed(pad));
  }

  private isFixed(model: Cube): boolean {
    return this.isStoned(model) || model.isWall || model.isAlive || model.isLost;
  }

  private isStoned(model: Cube): boolean {
    if (model.isBorder) return true;
    return this.stoned.has(model);
  }

  private fallModel(model: Cube): boolean {
    if (!this.isFixed(model)) {
      model.rules.actionFall();
      return true;
    }
    const lastFall = model.rules.clearLastFall();
    if (lastFall && this.impact < model.weight) {
      this.impact = model.weight;
    }
    return false;
  }
}
