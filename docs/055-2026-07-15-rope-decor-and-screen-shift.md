# 055 - Rope decor + screen shift (Lua host gaps, part 2)

_2026-07-15_

Part 2 of the BACKLOG §0 audit (part 1 = docs/054). Leaves only
`game_setRoomWaves` and the `mirror`/`zx` effects open.

## game_addDecor("rope", ...)

Legacy `game-script.cpp` only implements one decor name - `"rope"` (anything else
just `LOG_WARNING`s) - constructing a `RopeDecor(model1, model2, shift1, shift2)`.
`RopeDecor::drawOnScreen()` is a one-liner:

```cpp
V2 loc1 = view->getScreenPos(m_model1).plus(m_shift1);
V2 loc2 = view->getScreenPos(m_model2).plus(m_shift2);
lineColor(screen, loc1.x, loc1.y, loc2.x, loc2.y, 0x30404eff);   //NOTE: steel color
```

Only `elevator1`/`elevator2` use it, registering a "double rope" from the lift
(`vytah`) to its machine (`stroj`) once inside `prog_init` - never removed.

**Port:** `levelScript.ts` collects them into `state.decors` (`RopeDecor[]`,
exposed via `getRopeDecors()`); `LevelScene.drawRopes()` lazily creates one
`Graphics` at depth 5 and each frame clears + `lineBetween`s each rope in
`0x30404e`. Endpoints come from `ModelAnimator.getScreenPos()` (new) - sprites
use origin (0,0), so a sprite's own position *is* `getScreenPos()`, already
carrying the slide + viewShift + screenShift. Drawn after the models each frame,
matching `View::drawOn()` (models, then `drawDecors()`).

## game_setScreenShift(x, y)

`View::m_screenShift`, a **pixel** offset added last in `getScreenPos()` and
subtracted in `getFieldPos()` (mouse -> field). Users:

- **engine**: while the motor runs, `game_setScreenShift(radius*sin(t), radius*cos(t))`
  - a circular shake, reset to (0,0) when it stops.
- **cabin1**: a ~1%/round gag arms `room.mov`; when the big fish shoves the wall
  (`big:getTouchDir() ~= dir_no and room:getTouchDir() ~= dir_no`) the view jolts
  a full cell (`shift += dirShift * 15`) in the touch direction, then springs back
  with `shift -= shift/10` each round. (`getTouchDir` was already ported in
  docs/033 - the gag was computing, it just had nowhere to render.)

**Important detail confirmed in the source:** `Room::drawOn()` draws the
background **first, at a fixed (0,0)**, then `m_view->drawOn()` - and only the
*view* carries `m_screenShift`. So the backdrop never moves; every **model** does.
That still reads as "the whole level moves" because the walls are a model too
(`room = addModel("item_fixed", ...)` with its own image overlay) - walls + items
+ fish jolt together against the static distant backdrop.

**Port:** `state.screenShift` + `getScreenShift()`; `ModelAnimator.render()` takes
`shiftX/shiftY` and adds them last (same place as `getScreenPos`); `LevelScene`'s
`update()` passes them, and `toFieldPos()` subtracts them so clicking a fish
doesn't miss by the shift while the view is jolting. Values are `Math.trunc`'d to
match `luaL_checkint` - both callers pass floats.

Scope: `LevelScene` only. `ReplayScene` keeps its deliberate docs/025 scope
(physics + fish anim + music; no item anim/decor).

## Verification (real browser, sandbox)

- **elevator1**: 2 ropes registered with the real shifts (43,0)/(58,27) and
  (46,0)/(61,27) - i.e. `43` and `43+3`; Graphics at depth 5, visible. Screenshot
  shows both steel cables running from the pulley down to the lift.
- **cabin1**: `game_setScreenShift(30, -20)` moves a model by exactly (+30,-20)
  while `bgImage` stays at (0,0) - the background-doesn't-move rule holds;
  `toFieldPos` subtracts the shift (returns the intended cell 4,3); `(7.9, -7.9)`
  truncates to `(7, -7)` like `luaL_checkint`.
- e2e 7/7; `tsc -b` clean.

Guarded a known trap: Phaser reuses the scene instance across `scene.start()`
while SHUTDOWN destroys its GameObjects, so `init()` drops the stale
`ropeGraphics` handle rather than touching a destroyed object (cf. docs/012).

## Files
- **Modify:** `web/src/lua/levelScript.ts` (`RopeDecor` type, `decors`/
  `screenShift` state, real `game_addDecor`/`game_setScreenShift`, getters),
  `web/src/scenes/ModelAnimator.ts` (`render(progress, shiftX, shiftY)`,
  `getScreenPos()`), `web/src/scenes/LevelScene.ts` (`drawRopes()`, shift in
  `update()`, `toFieldPos()` inverse, `ropeGraphics` reset).

## Still open (BACKLOG §0)
`game_setRoomWaves` (81 levels; per-scanline sine ripple - custom shader agreed)
and the `mirror`/`zx` per-pixel screen effects. Plus the 5 unbound undo-path names
(dead unless undo is built).
