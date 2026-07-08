import { Dir } from "./Dir";
import { V2 } from "./V2";
import { Cube } from "./Cube";

export interface KeyControl {
  up: string;
  down: string;
  left: string;
  right: string;
}

export interface InputProvider {
  isPressed(key: string): boolean;
  /** Mouse controls (docs/017) - optional so keyboard-only test harnesses
   *  keep working unmodified; real gameplay input implements all three. */
  isLeftPressed?(): boolean;
  isRightPressed?(): boolean;
  getMouseField?(): V2 | null;
}

/**
 * A keyboard-driven fish. Port of legacy/src/level/Unit.h/.cpp - the
 * save-string ControlSym plumbing is dropped (no save/undo), but the
 * "borrowed shared arrow keys / active unit" scheme now lives in
 * web/src/game/Controls.ts (see docs/016).
 */
export class Unit {
  constructor(
    readonly cube: Cube,
    private readonly buttons: KeyControl,
    /** legacy/src/level/ModelFactory.cpp's createUnit(): only fish_small
     *  starts active. */
    readonly startActive: boolean = false,
  ) {}

  canDrive(): boolean {
    return this.cube.isAlive && !this.cube.isLost && !this.cube.busy;
  }

  willMove(): boolean {
    return this.cube.isAlive && !this.cube.isLost;
  }

  /** Tests this unit's own dedicated keys and tries to move. @return whether it moved or turned this round. */
  drive(input: InputProvider): boolean {
    return this.driveBorrowed(input, this.buttons);
  }

  /** Tests held keys against a possibly-different (e.g. shared arrow) key
   *  set and tries to move - legacy's driveBorrowed(), used by Controls to
   *  let arrow keys always drive the currently active fish. */
  driveBorrowed(input: InputProvider, buttons: KeyControl): boolean {
    if (!this.canDrive()) return false;
    if (input.isPressed(buttons.left)) return this.goLeft();
    if (input.isPressed(buttons.right)) return this.goRight();
    if (input.isPressed(buttons.up)) return this.goUp();
    if (input.isPressed(buttons.down)) return this.goDown();
    return false;
  }

  /** Greets the player with a brief held pose - legacy's Unit::activate(),
   *  triggered when this unit becomes active via Space, a click, or an
   *  automatic switch (see Controls.ts). */
  activate(): void {
    this.cube.rules.actionActivate();
  }

  /** Whether this unit's whole shape would fit at `loc` with nothing
   *  resisting - legacy's Unit::isFreePlace(), used by FinderAlg. */
  isFreePlace(loc: V2): boolean {
    return this.cube.rules.isFreePlace(loc);
  }

  /** Drives directly in `dir`, bypassing the symbol/key lookup entirely -
   *  used by MouseControl, which already knows the exact direction it
   *  wants (from pathfinding or coordinate comparison), not a held key.
   *  Replaces the original's driveOrder()/ControlSym symbol round-trip,
   *  which existed only for move-string recording (save/replay) this
   *  port doesn't have (docs/007). */
  driveDir(dir: Dir): boolean {
    if (!this.canDrive()) return false;
    switch (dir) {
      case Dir.LEFT:
        return this.goLeft();
      case Dir.RIGHT:
        return this.goRight();
      case Dir.UP:
        return this.goUp();
      case Dir.DOWN:
        return this.goDown();
      default:
        return false;
    }
  }

  /** Facing left is required before a left move actually happens - facing
   *  right first just turns you around (see docs/007 "facing"). */
  private goLeft(): boolean {
    if (this.cube.isLeft) {
      return this.cube.rules.actionMoveDir(Dir.LEFT);
    }
    this.cube.rules.actionTurnSide();
    return true;
  }

  private goRight(): boolean {
    if (!this.cube.isLeft) {
      return this.cube.rules.actionMoveDir(Dir.RIGHT);
    }
    this.cube.rules.actionTurnSide();
    return true;
  }

  private goUp(): boolean {
    return this.cube.rules.actionMoveDir(Dir.UP);
  }

  private goDown(): boolean {
    return this.cube.rules.actionMoveDir(Dir.DOWN);
  }
}
