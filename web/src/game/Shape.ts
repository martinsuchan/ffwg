import { V2 } from "./V2";

/**
 * Port of legacy/src/level/Shape.cpp. Parses the ASCII-art shape strings
 * used by addModel() in models.lua ("X" = occupied cell, "." = empty,
 * "\n" = next row).
 *
 * NOTE: width/height are the bounding box of the "X" marks only (the max
 * column/row that actually has an "X"), not the raw string's row length -
 * trailing "."-only padding on a row, or trailing "."-only rows, don't
 * count. This matches the original exactly (Shape::Shape only advances
 * max_x/max_y when it hits an 'X').
 */
export class Shape {
  readonly marks: readonly V2[];
  readonly w: number;
  readonly h: number;

  constructor(shape: string) {
    const marks: V2[] = [];
    let x = 0;
    let y = 0;
    let maxX = -1;
    let maxY = -1;

    for (const ch of shape) {
      switch (ch) {
        case "\n":
          y += 1;
          x = 0;
          break;
        case "X":
          marks.push(new V2(x, y));
          maxX = Math.max(maxX, x);
          maxY = Math.max(maxY, y);
          x += 1;
          break;
        case ".":
          x += 1;
          break;
        default:
          throw new Error(`bad shape char "${ch}" in shape "${shape}"`);
      }
    }

    this.marks = marks;
    this.w = maxX + 1;
    this.h = maxY + 1;
  }
}
