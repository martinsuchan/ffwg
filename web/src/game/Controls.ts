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
 * switching subset this port needs - save/undo move recording (m_moves),
 * phase-lock speedup and discrete-keystroke/demo replay (controlEvent/
 * useStroke/m_strokeSymbol) are dropped, matching this project's existing
 * no-save/no-demo scope (docs/007). See docs/016, docs/017.
 */
export class Controls {
  private units: Unit[] = [];
  private active = -1;
  /** Set when a switch (Space, or an automatic switch away from a fish
   *  that can no longer drive) is pending consumption by the next
   *  driving() call - legacy's m_switch. */
  private switchPending = false;

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

  /** One round of input resolution. @return whether a fish moved/turned
   *  this round - a switch alone does not count (legacy's driving(),
   *  without the discrete-stroke branch this port doesn't use). */
  driving(input: InputProvider): boolean {
    if (this.useSwitch()) return false;
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
   *  (dead/lost/busy) - legacy's checkActive(). */
  private checkActive(): void {
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
   *  making it active (no greet animation - legacy's setActive()). */
  private driveUnit(input: InputProvider): boolean {
    let moved =
      this.active !== -1 && this.units[this.active].driveBorrowed(input, ARROW_KEYS);
    if (!moved) {
      for (let i = 0; i < this.units.length; i++) {
        moved = this.units[i].drive(input);
        if (moved) {
          this.active = i;
          break;
        }
      }
    }
    return moved;
  }
}
