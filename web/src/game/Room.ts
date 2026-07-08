import { V2 } from "./V2";
import { Cube, Action, Weight } from "./Cube";
import { Field } from "./Field";
import { Landslip } from "./Landslip";
import { Controls } from "./Controls";
import { FinderAlg } from "./FinderAlg";
import { MouseControl } from "./MouseControl";
import { Unit, InputProvider } from "./Unit";

/**
 * One round of the puzzle: apply the previous round's pending moves, check
 * for deaths, let goal_escape models walk out through the border, then let
 * unsupported items fall. Port of legacy/src/level/Room.h/.cpp, reduced to
 * just the simulation - drawing, sound and save/undo are dropped; the
 * active-fish-switch scheme lives in Controls.ts (docs/016), mouse
 * pathfinding/pushing in MouseControl.ts/FinderAlg.ts (docs/017).
 */
export class Room {
  readonly field: Field;
  readonly models: Cube[] = [];
  private readonly controls = new Controls();
  private readonly mouseControl = new MouseControl(this.controls, new FinderAlg());
  private lastAction: Action = Action.NO;
  /** Cubes that died (isAlive -> false) during the most recently finished round. */
  lastDead: Cube[] = [];
  /** Weight of whatever just landed after falling this round (NONE if
   *  nothing did) - legacy's Landslip::getImpact(), read by LevelScene to
   *  play an impact sound (docs/018). Ported since docs/007 but never
   *  read until now. */
  lastImpact: Weight = Weight.NONE;

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
      this.controls.addUnit(unit);
    }
    return model.index;
  }

  isFresh(): boolean {
    return this.lastAction === Action.NO;
  }

  isFalling(): boolean {
    return this.lastAction === Action.FALL;
  }

  /** One full engine round: settle any pending move/fall, then (if settled)
   *  accept new input - keyboard first, mouse only if keyboard produced no
   *  move this round (matches the original's real precedence). */
  nextRound(input: InputProvider): void {
    this.beginFall();
    if (this.isFresh()) {
      if (this.driving(input)) {
        this.lastAction = Action.MOVE;
      } else if (this.mouseControl.mouseDrive(input)) {
        this.lastAction = Action.MOVE;
      }
    }
    this.updateMoveStreaks();
  }

  private updateMoveStreaks(): void {
    for (const m of this.models) m.rules.updateMoveStreak();
  }

  private beginFall(): void {
    this.prepareRound();
    this.lastAction = Action.NO;
    // Reset every round regardless of which branch below runs, so a round
    // where nothing falls (or fallout() pre-empts falldown() entirely)
    // correctly reports "no impact" rather than a stale prior value.
    this.lastImpact = Weight.NONE;

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
    const falling = slip.computeFall();
    this.lastImpact = slip.getImpact();
    return falling;
  }

  private driving(input: InputProvider): boolean {
    return this.controls.driving(input);
  }

  /** Space key: switch to the next drivable fish - legacy's Room::switchFish(). */
  switchFish(): void {
    this.controls.requestSwitch();
  }

  /** Returns the model occupying `loc`, or the shared border Cube for an
   *  out-of-bounds location - legacy's Room::askField(). */
  askField(loc: V2): Cube | null {
    return this.field.getModel(loc);
  }

  /** Click-to-select - legacy's Room::controlMouse() (left-button branch). */
  selectFish(model: Cube): void {
    this.controls.activateSelected(model);
  }

  /** No unit will ever be able to move again (all driven fish dead/lost). */
  cannotMove(): boolean {
    return this.controls.cannotMove();
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
