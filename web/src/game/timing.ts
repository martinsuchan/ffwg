/**
 * One physics round per this many ms - see docs/007 "Round timing". Shared
 * between LevelScene's round timer and ModelAnimator's position-slide
 * duration (docs/010) - the slide must finish before the next round's
 * position update arrives, or Phaser stacks a new tween on top of the
 * still-running one and the sprite visibly lags/blends axes.
 */
export const ROUND_MS = 130;
