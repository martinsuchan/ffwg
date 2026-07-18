# 071 - Mouse clicks outside the level no longer drive a fish

_2026-07-17_

Play-test report: with a small browser window (page scrollbar showing), clicking
the scrollbar - or anywhere in the window outside the rendered level - moved the
fish toward the click. Only clicks **inside the rendered level** should drive a
fish.

## Cause

The mouse-control path (`MouseControl.mouseDrive`, docs/017): hold left =
path the active fish toward the cursor, hold right = push toward it. It fires
whenever `input.isLeftPressed()`/`isRightPressed()` and drives toward
`getMouseField()`. `LevelScene` wired those straight to
`pointer.leftButtonDown()` / `pointer.rightButtonDown()` and
`toFieldPos(pointer)` with **no bounds check**.

The original (`legacy/src/level/MouseControl.cpp`) has no bounds check either -
and never needed one: its SDL window **is** the level, so the cursor can't be
"outside the level but inside the window". In the browser the canvas can be
smaller than the window (margins, a page scrollbar, or a fullscreen letterbox),
so a press outside the rendered area still reaches the polled button state.

The subtle part: **a world-coordinate bounds check alone can't fix this.** When
the pointer is off-canvas Phaser leaves `pointer.worldX`/`worldY` **stale** at
their last in-canvas value, so an out-of-room click reports an in-room cell.
Verified: holding the button below the canvas reported `worldX 608` (inside the
45-cell / 675px room) while the cursor was demonstrably off-canvas - a
worldX-in-room test would have passed it through.

## Fix (`web/src/scenes/LevelScene.ts`)

Track whether the pointer is genuinely **over the canvas** via the canvas's own
`pointerenter`/`pointerleave` DOM events (reliable - confirmed firing on both
transitions), and gate every mouse fish-action on it:

- New `pointerOverCanvas` flag (default true - the player is over the canvas
  when they click a node to enter; corrected on the first `pointerleave`), set
  by canvas `pointerenter`/`pointerleave` listeners added in `create()` and
  removed on `SHUTDOWN` (the canvas is reused across scenes, so they'd otherwise
  accumulate). Reset to true in `create()` since Phaser reuses the scene
  instance across entries.
- `isPointerInLevel(pointer)` = `pointerOverCanvas` **and** the world coords fall
  in the room rectangle `[0, roomW*SCALE) x [0, roomH*SCALE)`. The over-canvas
  check is the one that matters (it catches the stale-coord case); the
  room-rect check is a secondary guard for any in-canvas region outside the room
  (e.g. a future letterbox inside the canvas).
- Gated: the `pointerdown` click-to-select, and `isLeftPressed`/`isRightPressed`
  in the per-round input object. Keyboard driving is untouched (mouse only ever
  runs when the keyboard produced no move, docs/017).

## Verification (real browser, Playwright)

- Baseline: an in-room left-hold still paths the fish (steps 0 -> 5).
- Out-of-room: move to a far-right in-room cell (worldX 608), then move below the
  canvas and hold left - `pointerOverCanvas` reads false and the fish does **not**
  move (steps 5 -> 5), despite the stale worldX being inside the room rect.
- Proven to discriminate: neutralizing the `pointerOverCanvas` guard makes the
  same out-of-room hold move the fish (5 -> 12) - the reported bug.
- e2e 7/7; tsc clean.
