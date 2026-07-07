import { V2 } from "./V2";
import { Dir, dir2xy } from "./Dir";
import { Cube, Weight } from "./Cube";
import { Field } from "./Field";

function unique(models: Cube[]): Cube[] {
  return Array.from(new Set(models));
}

/**
 * Marks/unmasks a Cube's shape onto the Field, and answers "what resists
 * me in direction X" queries used everywhere in Rules. Port of
 * legacy/src/level/MarkMask.h/.cpp.
 */
export class MarkMask {
  constructor(
    private readonly model: Cube,
    private readonly field: Field,
  ) {}

  /** Others that resist moving one step in `dir` (excludes null cells and self). */
  getResist(dir: Dir): Cube[] {
    const shiftLoc = dir2xy(dir).plus(this.model.location);
    return this.getPlacedResist(shiftLoc);
  }

  /** Others occupying the cells this model's shape would cover if placed at `loc`. */
  getPlacedResist(loc: V2): Cube[] {
    const models: Cube[] = [];
    for (const mark of this.model.shape.marks) {
      const cellLoc = loc.plus(mark);
      const resist = this.field.getModel(cellLoc);
      if (resist !== null && resist !== this.model) {
        models.push(resist);
      }
    }
    return unique(models);
  }

  mask(): void {
    this.writeModel(this.model, null);
  }

  unmask(): void {
    this.writeModel(null, this.model);
  }

  private writeModel(model: Cube | null, toOverride: Cube | null): void {
    const loc = this.model.location;
    for (const mark of this.model.shape.marks) {
      this.field.setModel(loc.plus(mark), model, toOverride);
    }
  }

  /** Direction that leads out of the room with nothing blocking, or Dir.NO. */
  getBorderDir(): Dir {
    const loc = this.model.location;
    for (const mark of this.model.shape.marks) {
      const cell = loc.plus(mark);
      if (cell.x === 0 && this.canGo(Dir.LEFT)) return Dir.LEFT;
      if (cell.x === this.field.w - 1 && this.canGo(Dir.RIGHT)) return Dir.RIGHT;
      if (cell.y === 0 && this.canGo(Dir.UP)) return Dir.UP;
      if (cell.y === this.field.h - 1 && this.canGo(Dir.DOWN)) return Dir.DOWN;
    }
    return Dir.NO;
  }

  private canGo(dir: Dir): boolean {
    return this.model.rules.canMoveOthers(dir, Weight.FIXED);
  }

  isFullyOut(): boolean {
    const loc = this.model.location;
    for (const mark of this.model.shape.marks) {
      const place = this.field.getModel(loc.plus(mark));
      if (place === null || !place.isBorder) {
        return false;
      }
    }
    return true;
  }
}
