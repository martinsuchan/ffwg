import { Dir } from "./Dir";
import { V2 } from "./V2";
import { Cube } from "./Cube";

export interface KeyControl {
  up: string;
  down: string;
  left: string;
  right: string;
}

/**
 * Move symbols recorded for this unit's four directions - legacy's
 * ControlSym. Lowercase for fish_small ('u','d','l','r'), uppercase for
 * fish_big ('U','D','L','R'), matching ModelFactory::createUnit() exactly.
 * Each symbol uniquely identifies both *which* unit moved and *which*
 * direction, so a flat move string needs no separators and no "active
 * fish" context to replay deterministically - see docs/021.
 */
export interface ControlSym {
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
  /** One-shot queued keydown edge - a key captured the instant it went down,
   *  independent of whether it's still held by the time a round consumes it.
   *  Optional so keyboard-only test harnesses (isPressed-only) keep working -
   *  Controls falls back to plain held-state polling when absent. See
   *  Controls.driving() and legacy's Controls::controlEvent()/m_strokeSymbol. */
  takeQueuedKey?(): string | null;
}

/**
 * A keyboard-driven fish. Port of legacy/src/level/Unit.h/.cpp. The
 * "borrowed shared arrow keys / active unit" scheme lives in
 * web/src/game/Controls.ts (see docs/016); move-symbol recording (legacy's
 * ControlSym/m_moves) is ported as of docs/021.
 */
export class Unit {
  constructor(
    readonly cube: Cube,
    private readonly buttons: KeyControl,
    private readonly symbols: ControlSym,
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

  /** Tests this unit's own dedicated keys and tries to move.
   *  @return the move symbol recorded this round, or null. */
  drive(input: InputProvider): string | null {
    return this.driveBorrowed(input, this.buttons);
  }

  /** Tests held keys against a possibly-different (e.g. shared arrow) key
   *  set and tries to move - legacy's driveBorrowed(), used by Controls to
   *  let arrow keys always drive the currently active fish. */
  driveBorrowed(input: InputProvider, buttons: KeyControl): string | null {
    if (!this.canDrive()) return null;
    if (input.isPressed(buttons.left)) return this.goLeft();
    if (input.isPressed(buttons.right)) return this.goRight();
    if (input.isPressed(buttons.up)) return this.goUp();
    if (input.isPressed(buttons.down)) return this.goDown();
    return null;
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

  /** Drives directly in `dir`, bypassing the key lookup entirely - used by
   *  MouseControl, which already knows the exact direction it wants (from
   *  pathfinding or coordinate comparison), not a held key. Still resolves
   *  and returns this unit's own symbol for that direction (legacy's
   *  driveOrder()/myOrder() combined), so the caller can record it.
   *  @return the move symbol recorded this round, or null. */
  driveDir(dir: Dir): string | null {
    if (!this.canDrive()) return null;
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
        return null;
    }
  }

  /** Applies `symbol` if it belongs to this unit, independent of any held
   *  key - legacy's Unit::driveOrder(), used by solution replay/validation
   *  (see SolutionValidator.ts, docs/022) and the demo "show" to drive from a
   *  recorded/scripted move string instead of live input.
   *
   *  Gated on willMove() (alive + not out), NOT canDrive(), i.e. it ignores
   *  `busy` - unlike the interactive drive()/driveBorrowed() path. `busy` is a
   *  live-Lua-driven *interactive control* concept (which fish the player may
   *  steer, windoze's docs/035); a recorded symbol is an already-decided move
   *  that provably happened, uniquely names its fish + direction, and replays
   *  deterministically regardless of who was drivable at the time. Re-checking
   *  `busy` here desynced windoze's replay - the live Lua toggles busy a round
   *  off from when the string was recorded, blocking valid extra-fish moves.
   *  @return the symbol on success, or null - including when `symbol` belongs
   *  to this unit but the move it names is currently blocked. */
  driveOrder(symbol: string): string | null {
    if (!this.willMove()) return null;
    if (this.symbols.left === symbol) return this.goLeft();
    if (this.symbols.right === symbol) return this.goRight();
    if (this.symbols.up === symbol) return this.goUp();
    if (this.symbols.down === symbol) return this.goDown();
    return null;
  }

  /** Facing left is required before a left move actually happens - facing
   *  right first just turns you around (see docs/007 "facing"). Legacy's
   *  Unit::goLeft() records the same symbol for a turn as for the eventual
   *  move - the move string doesn't need a separate "turn" marker, since
   *  replaying the same symbol against the same starting facing reproduces
   *  the turn-then-move split deterministically (see docs/021). */
  private goLeft(): string | null {
    if (this.cube.isLeft) {
      return this.cube.rules.actionMoveDir(Dir.LEFT) ? this.symbols.left : null;
    }
    this.cube.rules.actionTurnSide();
    return this.symbols.left;
  }

  private goRight(): string | null {
    if (!this.cube.isLeft) {
      return this.cube.rules.actionMoveDir(Dir.RIGHT) ? this.symbols.right : null;
    }
    this.cube.rules.actionTurnSide();
    return this.symbols.right;
  }

  private goUp(): string | null {
    return this.cube.rules.actionMoveDir(Dir.UP) ? this.symbols.up : null;
  }

  private goDown(): string | null {
    return this.cube.rules.actionMoveDir(Dir.DOWN) ? this.symbols.down : null;
  }
}
