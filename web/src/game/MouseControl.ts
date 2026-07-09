import { V2 } from "./V2";
import { Dir } from "./Dir";
import { Controls } from "./Controls";
import { FinderAlg } from "./FinderAlg";
import type { InputProvider, Unit } from "./Unit";

/**
 * Mouse-driven movement for the active fish. Port of
 * legacy/src/level/MouseControl.{h,cpp}. Only ever consulted by
 * Room.nextRound() when keyboard driving produced no move that round -
 * mouse never overrides a held key, matching the original's real
 * precedence (Room::nextRound's if/else). See docs/017.
 */
export class MouseControl {
  constructor(
    private readonly controls: Controls,
    private readonly finder: FinderAlg,
  ) {}

  mouseDrive(input: InputProvider): boolean {
    if (input.isLeftPressed?.()) {
      const field = input.getMouseField?.() ?? null;
      return field ? this.moveTo(field) : false;
    }
    if (input.isRightPressed?.()) {
      const field = input.getMouseField?.() ?? null;
      return field ? this.moveHardTo(field) : false;
    }
    return false;
  }

  /** Left button: shortest unobstructed path toward `field`, no pushing. */
  private moveTo(field: V2): boolean {
    const unit = this.controls.getActive();
    if (!unit) return false;
    const dir = this.finder.findDir(unit, field);
    return dir !== Dir.NO && this.drive(unit, dir);
  }

  /** Right button: direct move toward `field`, one axis at a time (X
   *  before Y), no pathfinding - can push movable objects (still subject
   *  to the normal weight/canMoveOthers rules), doesn't route around
   *  anything. */
  private moveHardTo(field: V2): boolean {
    const unit = this.controls.getActive();
    if (!unit) return false;
    const loc = unit.cube.location;
    const { w, h } = unit.cube.shape;
    if (field.x < loc.x) return this.drive(unit, Dir.LEFT);
    if (loc.x + w <= field.x) return this.drive(unit, Dir.RIGHT);
    if (field.y < loc.y) return this.drive(unit, Dir.UP);
    if (loc.y + h <= field.y) return this.drive(unit, Dir.DOWN);
    return false;
  }

  /** Drives + records in one step - MouseControl already knows the exact
   *  Dir (from pathfinding or coordinate comparison) so it calls
   *  Unit.driveDir() directly rather than going through Controls' own
   *  key-lookup path, recording the resulting symbol itself (see
   *  Controls.recordMove(), docs/021). */
  private drive(unit: Unit, dir: Dir): boolean {
    const symbol = unit.driveDir(dir);
    if (symbol !== null) this.controls.recordMove(symbol);
    return symbol !== null;
  }
}
