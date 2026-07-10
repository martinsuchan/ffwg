/**
 * Which body anim a driven fish should be playing right now, and whether
 * it should loop (advance its phase over time) or hold a single frame.
 * Port of legacy/script/share/level_update.lua's animateFish() - a direct
 * TS port rather than calling back into Lua, same reasoning as Rules/
 * Landslip (docs/007): this is fixed game logic, not level content.
 */
export interface BodyAnim {
  name: string;
  running: boolean;
}

/** A head overlay drawn on top of the body anim at the same position (Anim::useSpecialAnim). */
export interface HeadAnim {
  name: string;
  phase: number;
}

/** The bits of live Cube/Rules state animation selection actually needs -
 *  kept as plain values (not a Cube reference) so this works equally from
 *  a real physics Cube or from a GameEngine.RenderModel snapshot. */
export interface AnimatableState {
  isAlive: boolean;
  /** Rules.getAction() - "move_left" | "move_right" | "move_up" | "move_down" | "turn" | "activate" | "busy" | "rest". */
  action: string;
  /** Rules.getState() - "goout" | "dead" | "pushing" | "normal". */
  state: string;
}

/**
 * Port of animateFish(model) (level_update.lua). Takes the same inputs
 * Rules.getAction() (already a faithful port of Rules::getAction(),
 * docs/007) produces - no physics changes needed for this.
 */
export function computeBodyAnim(model: AnimatableState): BodyAnim {
  if (!model.isAlive) {
    return { name: "skeleton", running: true };
  }

  switch (model.action) {
    case "move_up":
      return { name: "vertical_up", running: true };
    case "move_down":
      return { name: "vertical_down", running: true };
    case "move_left":
    case "move_right":
      return { name: "swam", running: true };
    case "turn":
      return { name: "turn", running: true };
    case "activate":
    case "busy":
      // Original: setAnim("turn", 0) - a held pose, not the running turn
      // loop. Currently unreachable (nothing in this port ever sets
      // readyToActive/busy - no fish-switching, no dialogs busying a
      // fish - see docs/007/docs/008's scope), kept for fidelity.
      return { name: "turn", running: false };
    default:
      return { name: "rest", running: true };
  }
}

/**
 * Port of animateHead(model) (level_update.lua). Talking now beats
 * pushing beats the occasional blink, matching the original's exact
 * priority order - ported once dialogs (docs/015) actually landed rather
 * than left half-done (see docs/029; the "busy" action branch that
 * overrides the *body* anim with a talk pose, `setAnim("talk", ...)`, is
 * still skipped - nothing in this port ever sets the "busy" action, same
 * as before). `talkPhase` (0-2, which of the 3 head_talking frames) is
 * owned and cycled by the caller (ModelAnimator) - this stays a pure
 * function, matching computeBodyAnim.
 */
export function computeHeadAnim(
  model: AnimatableState,
  isTalking: boolean,
  talkPhase: number,
  rollBlinkPercent: () => number,
): HeadAnim | null {
  if (!model.isAlive) {
    return null;
  }

  if (isTalking) {
    return { name: "head_talking", phase: talkPhase };
  }

  if (model.state === "pushing") {
    return { name: "head_pushing", phase: 0 };
  }

  // Original: random(100) < 6 - a ~6% chance each check, re-rolled
  // independently every call (see the presentation-layer head ticker).
  if (rollBlinkPercent() < 6) {
    return { name: "head_blink", phase: 0 };
  }

  return null;
}
