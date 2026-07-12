import { Dir, dir2xy } from "./Dir";
import { V2 } from "./V2";
import { Cube, Weight, Action } from "./Cube";
import { Field } from "./Field";
import { MarkMask } from "./MarkMask";
import { OnCondition, OnStack, OnWall, onStrongPad } from "./OnCondition";

/**
 * Rounds a dead model stays masked/solid before it's unmasked and removed
 * from the Field - port of legacy/src/effect/EffectDisintegrate.h's pixel-
 * dissolve counter (`DISINT_START = 400`, `DISINT_SPEED = 30`, decremented
 * once per rendered frame - `ceil(400/30) = 14` frames to reach zero). The
 * original ties that counter to draw calls, which `docs/009` established
 * run in lockstep with game-logic cycles in the original engine; here it's
 * counted in physics rounds directly instead (docs/009's animation/physics
 * decoupling means there's no render-call equivalent to hook on the
 * physics side) - see docs/011.
 */
const DEATH_REMOVE_ROUNDS = 14;

/**
 * Movement, pushing, falling and death rules for one Cube. Port of
 * legacy/src/level/Rules.h/.cpp - see docs/007 for the plain-English
 * writeup of what each check means.
 *
 * Intentionally dropped vs. the original: save/undo (change_setLocation) and
 * the "strict_rules" option toggle (this POC always uses the strict, default
 * branch of checkDeadMove). setTouched/touchDir ARE ported now - several levels'
 * code.lua reads getTouchDir() every round (e.g. cabin1's screen-shake gag), so
 * leaving model_getTouchDir unbound crashed the live Lua round loop (docs/033).
 * touchSpec (the output_* "go out on touch" special case) is ported too, for the
 * windoze level's spuntik plug (docs/035).
 */
export class Rules {
  dir: Dir = Dir.NO;
  /** Direction this model tried to move but was blocked (legacy Rules::
   *  m_touchDir) - reset to NO each round in occupyNewPos(), set by
   *  setTouched() when a move can't push. Read by Lua via getTouchDir(). */
  private touchDir: Dir = Dir.NO;
  private readyToDie = false;
  private readyToTurn = false;
  private readyToActive = false;
  private pushing = false;
  private lastFall = false;
  private outDepth = 0;
  /** Rounds left before a corpse unmasks and is removed - see DEATH_REMOVE_ROUNDS. */
  private deathRoundsLeft = 0;
  /** Consecutive-move streak driving the visual "swims faster" effect -
   *  see updateMoveStreak(). */
  private moveStreak = 0;

  private mask: MarkMask | null = null;

  constructor(private readonly model: Cube) {}

  takeField(field: Field): void {
    this.mask = new MarkMask(this.model, field);
    const resist = this.mask.getResist(Dir.NO);
    if (resist.length > 0) {
      throw new Error(
        `position is occupied: model=${this.model.kind}@${this.model.location} resist=${resist[0].kind}`,
      );
    }
    this.mask.mask();
  }

  private get m(): MarkMask {
    if (!this.mask) throw new Error("Rules used before takeField()");
    return this.mask;
  }

  /** Apply the move decided last round (occupyNewPos runs at the start of the *next* round - see docs/007). */
  occupyNewPos(): void {
    this.touchDir = Dir.NO;
    if (this.dir !== Dir.NO) {
      this.pushing = false;
      const shift = dir2xy(this.dir);
      this.model.changeSetLocation(this.model.location.plus(shift));
      this.m.mask();
    }
  }

  checkDead(lastAction: Action): boolean {
    let dead = false;
    if (this.model.isAlive) {
      switch (lastAction) {
        case Action.FALL:
          dead = this.checkDeadFall();
          break;
        case Action.MOVE:
          dead = this.checkDeadMove();
          break;
        default:
          dead = false;
      }
      if (!dead) {
        dead = this.checkDeadStress();
      }
      if (dead) {
        this.readyToDie = true;
      }
    }
    return dead;
  }

  /** Crushed by an object that was just pushed/fell squarely onto your back. */
  private checkDeadMove(): boolean {
    const resist = this.m.getResist(Dir.UP);
    for (const r of resist) {
      if (!r.isAlive) {
        const resistDir = r.rules.dir;
        if (resistDir !== Dir.NO && resistDir !== Dir.UP) {
          if (r.rules.isOnHolderBacks()) {
            return true;
          }
        }
      }
    }
    return false;
  }

  /** Something actively falling lands on you with no wall in its support chain. */
  private checkDeadFall(): boolean {
    const killers = this.whoIsFalling();
    return killers.some((k) => !k.rules.isOnWall());
  }

  /** Something too heavy for your power is resting on you with no other adequate support. */
  private checkDeadStress(): boolean {
    const killers = this.whoIsHeavier(this.model.power);
    return killers.some((k) => !k.rules.isOnStrongPad(k.weight));
  }

  changeState(): void {
    this.dir = Dir.NO;

    if (this.readyToTurn) {
      this.readyToTurn = false;
      this.model.changeTurnSide();
    }

    this.readyToActive = false;

    if (this.readyToDie) {
      this.readyToDie = false;
      this.model.changeDie();
      this.deathRoundsLeft = DEATH_REMOVE_ROUNDS;
    } else if (!this.model.isLost && this.deathRoundsLeft > 0) {
      this.deathRoundsLeft -= 1;
      if (this.deathRoundsLeft === 0) {
        this.m.unmask();
        this.model.changeRemove();
      }
    }
  }

  /** Let a goal_escape/goal_out model walk toward and through the room border. Returns out-depth (0 = not going out, -1 = just vanished). */
  actionOut(): number {
    if (!this.model.isLost && !this.model.busy && this.dir === Dir.NO) {
      if (this.model.goal.shouldGoOut()) {
        if (this.m.isFullyOut()) {
          this.model.changeGoOut();
          this.outDepth = -1;
        } else {
          const borderDir = this.m.getBorderDir();
          if (borderDir !== Dir.NO) {
            this.model.changeGoingOut();
            this.moveDirBrute(borderDir);
            this.outDepth += 1;
          } else {
            this.outDepth = 0;
          }
        }
      }
    }
    return this.outDepth;
  }

  actionFall(): void {
    this.dir = Dir.DOWN;
    this.lastFall = true;
  }

  clearLastFall(): boolean {
    const last = this.lastFall;
    this.lastFall = false;
    return last;
  }

  freeOldPos(): void {
    if (this.dir !== Dir.NO) {
      this.m.unmask();
    }
  }

  private isOnCond(cond: OnCondition): boolean {
    if (cond.isSatisfy(this.model)) return true;
    if (cond.isWrong(this.model)) return false;

    this.m.unmask();
    let result = false;
    const resist = this.m.getResist(Dir.DOWN);
    for (const r of resist) {
      if (r.rules.isOnCond(cond)) {
        result = true;
        break;
      }
    }
    this.m.mask();
    return result;
  }

  isOnStack(): boolean {
    return this.isOnCond(OnStack);
  }

  isOnWall(): boolean {
    return this.isOnCond(OnWall);
  }

  isOnStrongPad(weight: Weight): boolean {
    return this.isOnCond(onStrongPad(weight));
  }

  /** True when every alive model directly under us fully accounts for all our (possibly indirect) support. */
  private isOnHolderBacks(): boolean {
    let numDirectHolders = 0;
    for (const r of this.m.getResist(Dir.DOWN)) {
      if (r.isAlive) numDirectHolders += 1;
    }
    const pads = Array.from(new Set(this.getPads()));
    return numDirectHolders === pads.length;
  }

  /** All alive fish and walls (transitively) supporting us from below. */
  private getPads(): Cube[] {
    const pads: Cube[] = [];
    this.m.unmask();
    for (const r of this.m.getResist(Dir.DOWN)) {
      if (r.isAlive || r.isWall) {
        pads.push(r);
      } else {
        pads.push(...r.rules.getPads());
      }
    }
    this.m.mask();
    return pads;
  }

  private isFalling(): boolean {
    return !this.model.isAlive && this.dir === Dir.DOWN;
  }

  /** Actively-falling objects positioned directly above us (through a stack of inert items). */
  private whoIsFalling(): Cube[] {
    const result: Cube[] = [];
    this.m.unmask();
    for (const r of this.m.getResist(Dir.UP)) {
      if (!r.isWall && !r.isAlive) {
        if (r.rules.isFalling()) {
          result.push(r);
        } else {
          result.push(...r.rules.whoIsFalling());
        }
      }
    }
    this.m.mask();
    return result;
  }

  private isHeavier(power: Weight): boolean {
    return !this.model.isWall && !this.model.isAlive && this.model.weight > power;
  }

  /** Objects above us (transitively) too heavy for `power` to safely hold. */
  private whoIsHeavier(power: Weight): Cube[] {
    const result: Cube[] = [];
    this.m.unmask();
    for (const r of this.m.getResist(Dir.UP)) {
      if (!r.isWall) {
        if (r.rules.isHeavier(power)) {
          result.push(r);
        } else {
          result.push(...r.rules.whoIsHeavier(power));
        }
      }
    }
    this.m.mask();
    return result;
  }

  /** Whether everything resisting us in `dir` would retreat before a push of `power`. */
  canMoveOthers(dir: Dir, power: Weight): boolean {
    let result = true;
    this.m.unmask();
    for (const r of this.m.getResist(dir)) {
      if (this.model.goal.shouldGoOut() && r.isBorder) {
        continue;
      }
      if (!r.rules.canDir(dir, power)) {
        result = false;
        break;
      }
    }
    this.m.mask();
    return result;
  }

  /** Whether others (pushing with `power`) can move us. Alive models can never be pushed. */
  private canDir(dir: Dir, power: Weight): boolean {
    if (!this.model.isAlive && power >= this.model.weight) {
      if (this.model.isWall && !this.model.goal.shouldGoOut()) {
        return false;
      }
      return this.canMoveOthers(dir, power);
    }
    return false;
  }

  private moveDirBrute(dir: Dir): void {
    this.m.unmask();
    for (const r of this.m.getResist(dir)) {
      if (!r.isBorder) {
        r.rules.moveDirBrute(dir);
        this.pushing = true;
      }
    }
    this.dir = dir;
    this.m.mask();
  }

  /** Try to move. Only sets `dir` (see occupyNewPos) - either everything resisting retreats, or nothing moves. */
  actionMoveDir(dir: Dir): boolean {
    if (this.canMoveOthers(dir, this.model.power)) {
      this.moveDirBrute(dir);
      return true;
    }
    // Blocked. Two special cases before recording a plain touch (matching
    // legacy Rules::actionMoveDir's canMoveOthers -> touchSpec -> setTouched):
    // if we're pushing straight into an output plug (windoze's spuntik), we go
    // out through it; otherwise just record which way we pushed for getTouchDir.
    if (this.touchSpec(dir)) {
      return true;
    }
    this.setTouched(dir);
    return false;
  }

  /** Special case: a fish blocked by exactly one "output_DIR" plug that opens
   *  in the push direction goes out through it (windoze's spuntik) - legacy
   *  Rules::touchSpec. The plug loses one unit of capacity (and eventually
   *  becomes a normal light item). @return whether we went out. */
  private touchSpec(dir: Dir): boolean {
    const resist = this.m.getResist(dir);
    if (resist.length === 1 && resist[0].isOutDir(dir)) {
      resist[0].decOutCapacity();
      this.m.unmask();
      this.model.changeGoOut();
      return true;
    }
    return false;
  }

  /** Mark this model - and every dead/immovable model it's pushing against in
   *  `dir` - as touched this round (legacy Rules::setTouched). Read via
   *  getTouchDir(); reset in occupyNewPos(). */
  private setTouched(dir: Dir): void {
    this.touchDir = dir;
    if (!this.model.isWall) {
      this.m.unmask();
      for (const r of this.m.getResist(dir)) {
        if (!r.isAlive) {
          r.rules.setTouched(dir);
        }
      }
      this.m.mask();
    }
  }

  /** legacy Rules::getTouchDir() - direction this model is pushing against
   *  something it can't move this round, or Dir.NO. */
  getTouchDir(): Dir {
    return this.touchDir;
  }

  actionTurnSide(): void {
    this.readyToTurn = true;
  }

  actionActivate(): void {
    this.readyToActive = true;
  }

  /**
   * Updates the "swims faster" streak - ported from
   * legacy/src/level/Controls.cpp's lockPhases()/m_speedup condition
   * structure, but per-Cube instead of a single counter on Controls
   * (only one unit ever moves per round, so this is behaviorally
   * equivalent, with one deliberate minor difference: a fish resumes its
   * own warm streak if reselected shortly after being paused, rather
   * than the original's hard reset the instant a different fish becomes
   * active - see docs/017). Must run after this round's driving()/
   * mouseDrive() decide dir/pushing/readyToTurn, but before the next
   * round's changeState()/occupyNewPos() reset them - i.e. once per
   * round, right after Room.nextRound() resolves input, matching the
   * original's finishRound() -> lockPhases() timing. Visual-only (see
   * docs/017): does not affect the physics round rate, only
   * ModelAnimator's swim-animation/slide speed.
   */
  updateMoveStreak(): void {
    if (this.pushing) {
      this.moveStreak = 0;
    } else if (this.readyToTurn) {
      // turning neither builds nor breaks the streak
    } else if (this.dir !== Dir.NO) {
      this.moveStreak += 1;
    } else {
      this.moveStreak = 0;
    }
  }

  getMoveStreak(): number {
    return this.moveStreak;
  }

  getAction(): string {
    if (this.readyToTurn) return "turn";
    if (this.readyToActive) return "activate";
    if (this.model.busy) return "busy";

    switch (this.dir) {
      case Dir.LEFT:
        return "move_left";
      case Dir.RIGHT:
        return "move_right";
      case Dir.UP:
        return "move_up";
      case Dir.DOWN:
        return "move_down";
      default:
        return "rest";
    }
  }

  getState(): string {
    if (this.outDepth === 1) return "goout";
    if (!this.model.isAlive) return "dead";
    if (this.pushing) return "pushing";
    return "normal";
  }

  isAtBorder(): boolean {
    return this.m.getBorderDir() !== Dir.NO;
  }

  isFreePlace(loc: V2): boolean {
    return this.m.getPlacedResist(loc).length === 0;
  }

  getResist(dir: Dir): Cube[] {
    return this.m.getResist(dir);
  }

  resetLastDir(): void {
    this.dir = Dir.NO;
  }
}
