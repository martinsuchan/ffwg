import { Dir } from "./Dir";
import { Cube } from "./Cube";

export interface KeyControl {
  up: string;
  down: string;
  left: string;
  right: string;
}

export interface InputProvider {
  isPressed(key: string): boolean;
}

/**
 * A keyboard-driven fish. Port of legacy/src/level/Unit.h/.cpp - the
 * save-string ControlSym plumbing and the "borrowed shared arrow keys /
 * active unit" scheme from Controls are dropped (no save/undo, and this
 * POC gives each fish its own permanent keys instead of an
 * active-fish-switch - see docs/007).
 */
export class Unit {
  constructor(
    readonly cube: Cube,
    private readonly buttons: KeyControl,
  ) {}

  canDrive(): boolean {
    return this.cube.isAlive && !this.cube.isLost && !this.cube.busy;
  }

  willMove(): boolean {
    return this.cube.isAlive && !this.cube.isLost;
  }

  /** Tests held keys and tries to move. @return whether this unit moved or turned this round. */
  drive(input: InputProvider): boolean {
    if (!this.canDrive()) return false;
    if (input.isPressed(this.buttons.left)) return this.goLeft();
    if (input.isPressed(this.buttons.right)) return this.goRight();
    if (input.isPressed(this.buttons.up)) return this.goUp();
    if (input.isPressed(this.buttons.down)) return this.goDown();
    return false;
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
