import { Cube } from "./Cube";
import { InputProvider, KeyControl, Unit } from "./Unit";

/**
 * Shared key set that always drives whichever fish is currently active -
 * legacy/src/level/KeyControl.cpp's default ("arrows"), borrowed by
 * Controls::driveUnit via driveBorrowed().
 */
const ARROW_KEYS: KeyControl = {
  up: "ArrowUp",
  down: "ArrowDown",
  left: "ArrowLeft",
  right: "ArrowRight",
};

/**
 * Tracks which fish is "active" and resolves one round of key input to a
 * move. Port of legacy/src/level/Controls.h/.cpp, reduced to the driving/
 * switching/recording subset this port needs - phase-lock speedup and
 * discrete-keystroke/demo replay (controlEvent/useStroke/m_strokeSymbol)
 * are dropped (superseded by docs/019's queued-key edge trigger, which
 * covers the same reliability need without the full symbol-string demo
 * machinery). See docs/016, docs/017, docs/021 (move recording).
 */
export class Controls {
  private units: Unit[] = [];
  private active = -1;
  /** Set when a switch (Space, or an automatic switch away from a fish
   *  that can no longer drive) is pending consumption by the next
   *  driving() call - legacy's m_switch. */
  private switchPending = false;
  /** Every move symbol produced so far, in order - legacy's Controls::
   *  m_moves / StepCounter. Recorded regardless of input source (held key,
   *  queued-key edge, or mouse - see driveUnit()/recordMove()), so replay
   *  doesn't need to know how a move was originally triggered. */
  private moves = "";

  /** legacy's Controls::addUnit(): re-scans for a startActive() unit (or
   *  defaults to the first) every time a unit is added. */
  addUnit(unit: Unit): void {
    this.units.push(unit);
    const startActiveIndex = this.units.findIndex((u) => u.startActive);
    this.active = startActiveIndex !== -1 ? startActiveIndex : 0;
  }

  getActive(): Unit | null {
    return this.active !== -1 ? this.units[this.active] : null;
  }

  /** The recorded move string so far - legacy's StepCounter::getMoves(),
   *  used for solution validation/replay and step-count display. */
  getMoves(): string {
    return this.moves;
  }

  getStepCount(): number {
    return this.moves.length;
  }

  /** Appends a move symbol produced outside driveUnit()'s own key-lookup
   *  path - used by MouseControl, which already resolves a Unit + Dir
   *  itself (pathfinding or coordinate comparison) and calls Unit.driveDir()
   *  directly. Legacy's Controls::makeMove() is the single choke point
   *  both keyboard and mouse paths record through; this port splits it in
   *  two (driveUnit() records its own successful moves internally) for the
   *  same effect. */
  recordMove(symbol: string): void {
    this.moves += symbol;
  }

  /** Drives whichever unit owns `symbol` (independent of held keys or
   *  which fish is "active"), for solution replay/validation - legacy's
   *  Controls::makeMove(). Since symbols never overlap between units, this
   *  fully identifies both the unit and the direction on its own.
   *  @return whether a unit successfully drove (and recorded) `symbol`. */
  makeMove(symbol: string): boolean {
    for (let i = 0; i < this.units.length; i++) {
      if (this.units[i].driveOrder(symbol) !== null) {
        this.active = i;
        this.moves += symbol;
        return true;
      }
    }
    return false;
  }

  /** One round of input resolution. @return whether a fish moved/turned
   *  this round - a switch alone does not count (legacy's driving()).
   *
   *  A queued keydown edge (see InputProvider.takeQueuedKey) is tried
   *  first and, if present, always counts as "used" for this round even
   *  if the move it resolves to is blocked - legacy's useStroke() ("NOTE:
   *  returns true even for bad move"). This guarantees a fast tap that's
   *  already released by the time this round polls held-key state still
   *  produces exactly one move attempt, instead of silently vanishing
   *  into the gap between two round polls. Held-key polling (driveUnit)
   *  is still the fallback for genuinely continuous holding once the
   *  queued edge (set at most once per keydown) has been drained. */
  driving(input: InputProvider): boolean {
    if (this.useSwitch()) return false;

    const queuedKey = input.takeQueuedKey?.();
    if (queuedKey != null) {
      this.driveUnit({ isPressed: (code) => code === queuedKey });
      return true;
    }

    return this.driveUnit(input);
  }

  /** Space key: request the next drivable fish become active - legacy's
   *  Room::switchFish() -> Controls::switchActive(). */
  requestSwitch(): void {
    this.switchActive();
  }

  /** True once every driven fish is permanently unable to move (dead/lost). */
  cannotMove(): boolean {
    return this.units.every((u) => !u.willMove());
  }

  /** Click-to-select: makes the unit owning `model` active, with the same
   *  greet flash as Space - legacy's Controls::activateSelected(). Even
   *  reclicking the already-active fish re-triggers the flash, matching
   *  the original (it never checks whether active actually changed). */
  activateSelected(model: Cube): boolean {
    const index = this.units.findIndex((u) => u.cube === model);
    if (index === -1) return false;
    this.active = index;
    this.switchPending = true;
    return true;
  }

  /** Consumes a pending switch: greets the new active fish (activate())
   *  and reports true so driveUnit() is skipped this round. Also auto-
   *  switches away from an active fish that can no longer ever move. */
  private useSwitch(): boolean {
    let result = false;
    if (this.active !== -1) {
      if (!this.units[this.active].willMove()) {
        this.checkActive();
      }
      if (this.switchPending && this.active !== -1) {
        this.units[this.active].activate();
        result = true;
      }
    }
    this.switchPending = false;
    return result;
  }

  /** Switch away from the active unit if it can't currently drive
   *  (dead/lost/busy) - legacy's checkActive(). Public so the engine can
   *  invoke it for game_checkActive() (windoze, docs/035). */
  checkActive(): void {
    if (this.active === -1 || !this.units[this.active].canDrive()) {
      this.switchActive();
    }
  }

  /** Cycle forward (wrapping) to the next drivable unit - legacy's
   *  switchActive(). Leaves `active` unchanged if none but the current
   *  one can drive. */
  private switchActive(): void {
    if (this.units.length === 0) return;
    const start = this.active;
    let candidate = this.active;
    do {
      candidate = candidate === -1 || candidate + 1 >= this.units.length ? 0 : candidate + 1;
    } while (candidate !== start && !this.units[candidate].canDrive());

    if (start !== candidate) {
      this.active = candidate;
      this.switchPending = true;
    }
  }

  /** Arrow keys always try the active fish first (borrowed input); failing
   *  that, any unit's own dedicated keys can drive it directly, silently
   *  making it active (no greet animation - legacy's setActive()). Records
   *  the resulting symbol on success - legacy's driveUnit() appending to
   *  m_moves. */
  private driveUnit(input: InputProvider): boolean {
    let symbol: string | null =
      this.active !== -1 ? this.units[this.active].driveBorrowed(input, ARROW_KEYS) : null;
    if (symbol === null) {
      for (let i = 0; i < this.units.length; i++) {
        symbol = this.units[i].drive(input);
        if (symbol !== null) {
          this.active = i;
          break;
        }
      }
    }
    if (symbol !== null) {
      this.moves += symbol;
    }
    return symbol !== null;
  }
}
