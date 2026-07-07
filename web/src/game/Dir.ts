import { V2 } from "./V2";

/** Port of legacy/src/level/Dir.h/.cpp. */
export enum Dir {
  NO = 0,
  UP = 1,
  DOWN = 2,
  LEFT = 3,
  RIGHT = 4,
}

export function dir2xy(dir: Dir): V2 {
  switch (dir) {
    case Dir.UP:
      return new V2(0, -1);
    case Dir.DOWN:
      return new V2(0, 1);
    case Dir.LEFT:
      return new V2(-1, 0);
    case Dir.RIGHT:
      return new V2(1, 0);
    case Dir.NO:
      return new V2(0, 0);
    default:
      throw new Error(`unknown dir: ${dir}`);
  }
}
