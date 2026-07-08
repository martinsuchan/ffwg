import { V2 } from "./V2";
import { Dir } from "./Dir";
import type { Unit } from "./Unit";

interface FinderPlace {
  startDir: Dir;
  loc: V2;
}

const NEIGHBOR_SHIFTS: ReadonlyArray<{ dir: Dir; shift: V2 }> = [
  { dir: Dir.LEFT, shift: new V2(-1, 0) },
  { dir: Dir.RIGHT, shift: new V2(1, 0) },
  { dir: Dir.UP, shift: new V2(0, -1) },
  { dir: Dir.DOWN, shift: new V2(0, 1) },
];

function key(loc: V2): string {
  return `${loc.x},${loc.y}`;
}

/**
 * Shortest-path first-step finder. Port of legacy/src/level/FinderAlg.cpp,
 * with FinderField/FinderPlace inlined (tiny, single-purpose, no other
 * consumers). Plain unweighted BFS over 4-directional moves; a candidate
 * cell is only explored if the unit's *whole* shape fits there with
 * nothing resisting (Unit.isFreePlace, already shape-aware via
 * MarkMask.getPlacedResist) - i.e. completely unobstructed, no pushing.
 *
 * Unlike the original, no explicit w*h-bounded closed array is needed:
 * Field.getModel already returns the shared border Cube for any
 * out-of-bounds location, so isFreePlace already fails at room walls -
 * the room border naturally bounds the search. A fresh closed-set is
 * built per findDir() call instead of a persistent one reset each time.
 *
 * Only returns the *first step* direction, not a full path - callers
 * (MouseControl) recompute fresh every round, which is what makes the
 * "follow the cursor while held" behavior adapt to obstacles moving.
 */
export class FinderAlg {
  findDir(unit: Unit, dest: V2): Dir {
    const uLoc = unit.cube.location;
    const uw = unit.cube.shape.w;
    const uh = unit.cube.shape.h;
    if (this.isInRect(uLoc, uw, uh, dest)) {
      return Dir.NO;
    }

    const closed = new Set<string>([key(uLoc)]);
    const fifo: FinderPlace[] = NEIGHBOR_SHIFTS.map(({ dir, shift }) => ({
      startDir: dir,
      loc: uLoc.plus(shift),
    }));

    const pushNext = (parent: FinderPlace, shift: V2): void => {
      const loc = parent.loc.plus(shift);
      if (!closed.has(key(loc))) {
        closed.add(key(loc));
        fifo.push({ startDir: parent.startDir, loc });
      }
    };

    while (fifo.length > 0) {
      const place = fifo.shift() as FinderPlace;
      if (unit.isFreePlace(place.loc)) {
        if (this.isInRect(place.loc, uw, uh, dest)) {
          return place.startDir;
        }
        for (const { shift } of NEIGHBOR_SHIFTS) {
          pushNext(place, shift);
        }
      }
    }
    return Dir.NO;
  }

  private isInRect(rectLoc: V2, w: number, h: number, dest: V2): boolean {
    return (
      rectLoc.x <= dest.x &&
      dest.x < rectLoc.x + w &&
      rectLoc.y <= dest.y &&
      dest.y < rectLoc.y + h
    );
  }
}
