import type { Cube } from "./Cube";

enum Satisfy {
  TRUE,
  FALSE,
  IGNORE,
}

/** Port of legacy/src/level/Goal.h/.cpp. */
export class Goal {
  private constructor(
    private readonly out: Satisfy,
    private readonly alive: Satisfy,
  ) {}

  static noGoal(): Goal {
    return new Goal(Satisfy.IGNORE, Satisfy.IGNORE);
  }

  /** Model must leave the room (used by output_* items, unused by airplane). */
  static outGoal(): Goal {
    return new Goal(Satisfy.TRUE, Satisfy.IGNORE);
  }

  /** Model must leave the room alive - the fish's goal. */
  static escapeGoal(): Goal {
    return new Goal(Satisfy.TRUE, Satisfy.TRUE);
  }

  static aliveGoal(): Goal {
    return new Goal(Satisfy.IGNORE, Satisfy.TRUE);
  }

  isSatisfy(model: Cube): boolean {
    let result = true;
    if (this.out === Satisfy.TRUE) result &&= model.isOut;
    else if (this.out === Satisfy.FALSE) result &&= !model.isOut;

    if (this.alive === Satisfy.TRUE) result &&= model.isAlive;
    else if (this.alive === Satisfy.FALSE) result &&= !model.isAlive;

    return result;
  }

  /** Whether this goal can never be satisfied any more (dead fish stay dead). */
  isWrong(model: Cube): boolean {
    let wrong = false;
    if (this.alive === Satisfy.TRUE) wrong ||= !model.isAlive;
    if (this.out === Satisfy.FALSE) wrong ||= model.isOut;
    return wrong;
  }

  shouldGoOut(): boolean {
    return this.out === Satisfy.TRUE;
  }
}
