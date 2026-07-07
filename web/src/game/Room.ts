import { Cube, Action } from "./Cube";
import { Field } from "./Field";
import { Landslip } from "./Landslip";
import { Unit, InputProvider } from "./Unit";

/**
 * One round of the puzzle: apply the previous round's pending moves, check
 * for deaths, let goal_escape models walk out through the border, then let
 * unsupported items fall. Port of legacy/src/level/Room.h/.cpp, reduced to
 * just the simulation - drawing, sound, mouse control, save/undo and the
 * shared-arrow/active-unit switching scheme are dropped (see docs/007).
 */
export class Room {
  readonly field: Field;
  readonly models: Cube[] = [];
  readonly units: Unit[] = [];
  private lastAction: Action = Action.NO;
  /** Cubes that died (isAlive -> false) during the most recently finished round. */
  lastDead: Cube[] = [];

  constructor(
    readonly w: number,
    readonly h: number,
  ) {
    this.field = new Field(w, h);
  }

  addModel(model: Cube, unit?: Unit): number {
    model.rules.takeField(this.field);
    this.models.push(model);
    model.index = this.models.length - 1;
    if (unit) {
      this.units.push(unit);
    }
    return model.index;
  }

  isFresh(): boolean {
    return this.lastAction === Action.NO;
  }

  isFalling(): boolean {
    return this.lastAction === Action.FALL;
  }

  /** One full engine round: settle any pending move/fall, then (if settled) accept new input. */
  nextRound(input: InputProvider): void {
    this.beginFall();
    if (this.isFresh()) {
      if (this.driving(input)) {
        this.lastAction = Action.MOVE;
      }
    }
  }

  private beginFall(): void {
    this.prepareRound();
    this.lastAction = Action.NO;

    if (this.fallout()) {
      this.lastAction = Action.MOVE;
    } else if (this.falldown()) {
      this.lastAction = Action.FALL;
    }
  }

  /** Apply last round's pending moves, then check who died as a result. */
  private prepareRound(): void {
    for (const m of this.models) m.rules.freeOldPos();
    for (const m of this.models) m.rules.occupyNewPos();

    this.lastDead = [];
    for (const m of this.models) {
      if (m.rules.checkDead(this.lastAction)) {
        this.lastDead.push(m);
      }
    }

    for (const m of this.models) m.rules.changeState();
  }

  /** Let goal_escape/goal_out models walk toward and through the border. */
  private fallout(): boolean {
    let wentOut = false;
    for (const m of this.models) {
      if (!m.isLost) {
        const outDepth = m.rules.actionOut();
        if (outDepth > 0) wentOut = true;
      }
    }
    return wentOut;
  }

  private falldown(): boolean {
    const slip = new Landslip(this.models);
    return slip.computeFall();
  }

  /** First unit (in registration order) whose own keys are held and can move wins the round. */
  private driving(input: InputProvider): boolean {
    for (const unit of this.units) {
      if (unit.drive(input)) return true;
    }
    return false;
  }

  /** No unit will ever be able to move again (all driven fish dead/lost). */
  cannotMove(): boolean {
    return this.units.every((u) => !u.willMove());
  }

  /** All goals can still possibly be satisfied (false forever once a goal_escape fish dies). */
  isSolvable(): boolean {
    return this.models.every((m) => !m.isWrong);
  }

  /** All goals are currently satisfied and nothing is still moving/falling. */
  isSolved(): boolean {
    return this.isFresh() && this.models.every((m) => m.isSatisfy);
  }
}
