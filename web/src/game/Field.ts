import { V2 } from "./V2";
import { Cube, createBorderCube } from "./Cube";

/**
 * Two-dimensional game grid. Port of legacy/src/level/Field.h/.cpp.
 * Out-of-bounds cells always resolve to a single shared border Cube
 * (isWall, not alive, isBorder) - mirrors the original's "hack border
 * everywhere in outer space".
 */
export class Field {
  private readonly marks: (Cube | null)[][];
  readonly border: Cube;

  constructor(
    readonly w: number,
    readonly h: number,
  ) {
    this.border = createBorderCube();
    this.marks = Array.from({ length: h }, () =>
      new Array<Cube | null>(w).fill(null),
    );
    // Matches the original wiring the border into the same field (Field::Field) -
    // harmless in practice (border sits at (-1,-1), out of bounds, so mask()
    // never writes anywhere), but keeps border.rules usable if anything ever
    // queries it directly instead of short-circuiting on isWall/isAlive first.
    this.border.rules.takeField(this);
  }

  /** Returns the occupying Cube, null for an empty in-bounds cell, or the
   *  shared border Cube for any out-of-bounds location. */
  getModel(loc: V2): Cube | null {
    const { x, y } = loc;
    if (x >= 0 && x < this.w && y >= 0 && y < this.h) {
      return this.marks[y][x];
    }
    return this.border;
  }

  /** Writes `model` at loc, but only if the cell currently holds `toOverride` (or toOverride is null - always write). */
  setModel(loc: V2, model: Cube | null, toOverride: Cube | null): void {
    const { x, y } = loc;
    if (x >= 0 && x < this.w && y >= 0 && y < this.h) {
      if (toOverride === null || this.marks[y][x] === toOverride) {
        this.marks[y][x] = model;
      }
    }
  }
}
