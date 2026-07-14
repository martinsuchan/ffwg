# 047 - In-level step counter

_2026-07-13_

## Why

The port had no on-screen move counter during play (only the replay/pedometer
screens showed one). Added the original's in-level counter, always visible per
the user's request.

## What

A faithful port of legacy `StepDecor` (`legacy/src/level/StepDecor.cpp`):

- **Position:** top-right corner - right edge at the room width, `y = 10`
  (Phaser `Text` at `(roomWidthPx, 10)`, origin `(1, 0)`).
- **Font/size:** `font_console`-style at size **20**, black-outlined
  (`stroke #000000`, monospace family - the port doesn't bundle the console
  TTF; the outline mirrors `renderTextOutlined`).
- **Colors:** orange `#ffc566` (legacy `COLOR_ORANGE` 255,197,102) normally,
  blue `#a2f4ff` (`COLOR_BLUE` 162,244,255) when the **powerful** (big) fish is
  active - `Unit::isPowerful()` = active fish's `power >= HEAVY`.
- **Value:** the recorded move count (`GameEngine.getStepCount()`).
- **Always visible** - the original gates it behind a `show_steps` toggle
  (`KEY_SHOW_STEPS`); here it's always on.

Wiring: `GameEngine.getActiveInfo()` gained a `powerful` flag (active fish
`power >= Weight.HEAVY`); `LevelScene` creates the `Text` in `create()` and
updates its text + color each round in `tick()`. No physics/other changes.

## Verification

Real browser (airplane): starts visible at `0`; font size 20px, origin (1,0) at
`(roomWidth, 10)`, black outline; small fish active → orange; increments on a
move; switching to the big fish → blue. Screenshot confirms the top-right blue
"4" after a big-fish move. `tsc -b` clean.

## Files
- **Modify:** `web/src/game/GameEngine.ts` (`getActiveInfo().powerful`),
  `web/src/scenes/LevelScene.ts` (step-counter Text + per-round update).
