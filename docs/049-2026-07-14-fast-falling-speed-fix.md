# 049 - Falling items too slow (phase-lock refinement)

_2026-07-14_

## Why

User reported that after the docs/046 animation rework, falling items fell at the
**same speed as a fish swims** - but in the original game a released/unsupported
item drops noticeably **faster** than a fish's base swim.

## Cause

docs/046 derives each round's shared phase count (`cyclesThisRound`) from the
**active fish** via `movePhases()`. When items are falling but no fish is
driving the move, `getActiveInfo().action` is `"rest"`, and `movePhases` hit its
`return 3` fallback - so a pure gravity fall took **3 cycles = 300ms/cell**,
identical to a base swim.

The original paces this differently (`PhaseLocker` +
`Room::finishRound`/`fallout` + `Level::own_updateState` + `View::computeShiftSize`):

- **Active fish driving** a move: `Controls::lockPhases` → `ensurePhases(getNeededPhases)`
  = 3 base (`swam/2`), so a swim + everything it pushes takes 300ms/cell,
  accelerating to 200/100ms.
- **A model leaving the room** (`fallout`): `ensurePhases(3)` → the go-out slide
  is 300ms.
- **Pure gravity** (`falldown`): ensures **nothing**. `getLocked()` stays `0`, so
  `View::computeShiftSize(0)` sets `m_shiftSize = SCALE` and the item traverses a
  full cell in a **single 100ms cycle** - ~3x faster than a base swim.

(Falling *while the active fish itself falls* - action `"move_down"` - counts as
the fish moving in `Unit::isMoving`, so `ensurePhases(3)` applies and that stack
slides at 300ms, locked together. The port matches this: a falling active fish's
action is `"move_down"`, a drive action.)

## Fix

New shared `roundPhases()` in `ModelAnimator.ts`, replacing the inline
`movePhases`-only logic in both `LevelScene.updateRoundPacing` and
`ReplayScene.updateRoundPacing`:

```ts
if (activeInfo && isDriveAction(activeInfo.action)) return movePhases(...);   // fish drives
if (renderModels.some((m) => m.state === "goout")) return 3;                  // fallout
return 1;                                                                     // pure fall
```

`isDriveAction` = action is `move_*` or `turn` (mirrors `Unit::isMoving`). So a
fall with no fish driving now runs at **1 cycle = 100ms/cell**, a go-out at 3,
and a fish swim/turn unchanged. Physics is untouched - this is purely the shared
animation clock's cell duration.

## Verification

Real browser (airplane, `/sandbox`), instrumenting `moveDurationMs`:

- Initial settle (unsupported items falling, fish `"rest"`): **100ms/cell**
  (`cyclesThisRound = 1`).
- Held fish swim (all four directions): **300ms/cell** - unchanged.

So falls are now 3x faster than swims, matching the original. A driven `move_down`
(fish swimming down under control) stays 300ms; only gravity is fast.

e2e suite `scripts\test.ps1`: **7/7** green, incl. the all-solutions headless
replay (80/81) - the validator uses the fast `loadMove`/`settleAll` path
(pacing-independent), confirming physics is unaffected. `tsc -b` clean.

## Files
- **Modify:** `web/src/scenes/ModelAnimator.ts` (new `roundPhases`/`isDriveAction`),
  `web/src/scenes/LevelScene.ts` + `web/src/scenes/ReplayScene.ts`
  (`updateRoundPacing` calls `roundPhases`).
