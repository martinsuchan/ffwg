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
  /** legacy Room::m_fastFalling - when set, a round settles every pending fall
   *  at once instead of one step per round (windoze uses it while the player
   *  solves the bonus). Outcome-identical to normal falling, just faster. */
  private fastFalling = false;

  constructor(
    readonly w: number,
    readonly h: number,
  ) {
    this.field = new Field(w, h);
  }

  /** game_setFastFalling(value) via the live-Lua bridge (docs/035). */
  setFastFalling(value: boolean): void {
    this.fastFalling = value;
  }

  /** game_checkActive(): switch control away from an active fish that can no
   *  longer drive (e.g. just made busy) - legacy Room::checkActive/
   *  Controls::checkActive. See docs/035. */
  checkActive(): void {
    this.controls.checkActive();
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
    // legacy Room::nextRound: fast-falling settles every pending fall in this
    // one round before accepting input, so the player never waits on the main
    // room settling while solving windoze's bonus (docs/035).
    if (this.fastFalling) {
      this.fastForwardSettle();
    } else {
      this.beginFall();
    }
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

  /** Every move symbol recorded so far, in order - legacy's Room::
   *  stepCounter(). See docs/021. */
  getMoves(): string {
    return this.controls.getMoves();
  }

  getStepCount(): number {
    return this.controls.getStepCount();
  }

  /** The currently-active fish (arrows drive it) - legacy's Controls::m_active.
   *  Drives the shared animation clock's phase count (docs/046). */
  getActiveUnit(): Unit | null {
    return this.controls.getActive();
  }

  /** Fast, render-free replay of one recorded move symbol - legacy's
   *  Room::loadMove(): settle any pending falls first, then apply exactly
   *  this one move. The move's own consequences (position committing, and
   *  whatever it triggers falling) are picked up by the *next* loadMove()/
   *  settleAll() call's own settling, exactly like the interactive round
   *  pipeline's decide-this-round/apply-next-round split (docs/007) - just
   *  run back to back with no real-time pause instead of one real round at
   *  a time. Used by the headless solution validator (docs/022).
   *  @throws Error if `symbol` doesn't name a valid move for any unit here. */
  loadMove(symbol: string): void {
    this.fastForwardSettle();
    if (!this.controls.makeMove(symbol)) {
      throw new Error(`invalid move: "${symbol}"`);
    }
    this.lastAction = Action.MOVE;
    this.updateMoveStreaks();
  }

  /** Settles any pending falls with nothing left to drive - call once
   *  after the last loadMove() in a solution to apply its consequences
   *  before checking isSolved(). */
  settleAll(): void {
    this.fastForwardSettle();
  }

  /** One round of *watchable* replay - same physics shape as nextRound()
   *  (settle, then only accept new input once fresh), but sourced from a
   *  recorded move string instead of live keyboard/mouse. Unlike
   *  loadMove(), does not fast-forward through pending falls - each call
   *  is exactly one round, so a caller ticking this on a real-time timer
   *  sees the same falling/sliding animation live play would. Doesn't
   *  consume `symbol` unless the round is actually fresh, matching how it
   *  was originally recorded (docs/021: a symbol is only ever captured
   *  when isFresh()). See docs/025.
   *  @return whether `symbol` was consumed this round. */
  replayRound(symbol: string | null): boolean {
    if (this.fastFalling) {
      this.fastForwardSettle();
    } else {
      this.beginFall();
    }
    let consumed = false;
    if (this.isFresh() && symbol !== null) {
      consumed = this.controls.makeMove(symbol);
      if (consumed) this.lastAction = Action.MOVE;
    }
    this.updateMoveStreaks();
    return consumed;
  }

  /** Start a show-driven round - legacy's Level::nextShowAction()'s
   *  room->beginFall(): settle pending falls before the show command runs.
   *  Public entry for the briefcase auto-play "show" (docs/031, Phase 2),
   *  which drives the round itself (input disabled) instead of nextRound(). */
  beginShowRound(): void {
    this.beginFall();
  }

  /** Apply one scripted "show" move - legacy's Room::makeMove(): only lands
   *  when the room is fresh (else the show command retries next round), throws
   *  on a fresh-but-impossible move (caught by the show driver as a graceful
   *  end). Assumes beginShowRound() already ran this round. See docs/031. */
  showMove(symbol: string): boolean {
    if (!this.isFresh()) return false;
    if (!this.controls.makeMove(symbol)) {
      throw new Error(`show move not possible: "${symbol}"`);
    }
    this.lastAction = Action.MOVE;
    this.updateMoveStreaks();
    return true;
  }

  /** Repeatedly resolves falls/fallout until nothing is left pending
   *  (isFresh()) - legacy's loadMove()'s "let object to fall fast" loop.
   *  Bounded defensively: a real level can never fall forever, so hitting
   *  the cap means something is wrong, not just a long level. */
  private fastForwardSettle(): void {
    const MAX_SETTLE_ROUNDS = 1000;
    let rounds = 0;
    do {
      this.beginFall();
      if (++rounds > MAX_SETTLE_ROUNDS) {
        throw new Error("settling did not converge - possible infinite fall loop");
      }
    } while (!this.isFresh());
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
