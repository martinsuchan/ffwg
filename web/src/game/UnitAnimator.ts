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
  /** A held pose whose frame is chosen by an externally-cycled phase rather than
   *  the running loop - only the busy "talk" body pose (cycled by talk_phase).
   *  Undefined = start at phase 0 / use the running loop. */
  phase?: number;
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
 * Port of animateFish(model) + animateHead()'s body override (level_update.lua).
 * Takes the same inputs Rules.getAction() produces (a faithful port of
 * Rules::getAction(), docs/007), plus the fish's talking state - because the
 * original decides the *body* for a busy fish inside animateHead(), not
 * animateFish(): a fish held busy by a scripted conversation (planBusy) shows
 * the front-facing `talk` body pose while it's talking, and a held `turn` frame
 * while it isn't. `talkPhase` (0-2, cycled by the caller) picks the talk frame.
 */
export function computeBodyAnim(
  model: AnimatableState,
  isTalking = false,
  talkPhase = 0,
): BodyAnim {
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
    case "busy":
      // animateHead's `action=="busy"` branch: talking -> the front-facing
      // `talk` body pose (cycling body_talk_* by talk_phase, no head overlay -
      // see computeHeadAnim); otherwise a held `turn` frame. This is what makes
      // a fish face the player while it speaks in a scripted conversation.
      return isTalking
        ? { name: "talk", running: false, phase: talkPhase }
        : { name: "turn", running: false, phase: 0 };
    case "activate":
      // The greet flash (Space / fish switch, docs/016): setAnim("turn", 0) - a
      // held pose. Not busy, so it still gets the normal head overlays below.
      return { name: "turn", running: false, phase: 0 };
    default:
      return { name: "rest", running: true };
  }
}

/**
 * Port of animateHead(model) (level_update.lua). Talking beats pushing beats
 * the occasional blink, matching the original's exact priority order (docs/029).
 * `talkPhase` (0-2, which of the 3 head_talking frames) is owned and cycled by
 * the caller (ModelAnimator) - this stays a pure function, matching
 * computeBodyAnim.
 *
 * A **busy** fish gets NO head overlay: the original's `action=="busy"` branch
 * drives only the *body* (the front-facing `talk`/`turn` pose, see
 * computeBodyAnim) and never reaches the head-overlay code - the talk body
 * already includes the face, so a separate side-view head would double it up.
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

  if (model.action === "busy") {
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
