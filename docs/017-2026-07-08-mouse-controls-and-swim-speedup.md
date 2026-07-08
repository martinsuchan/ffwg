# 017 - Mouse Controls and Swim Speedup

2026-07-08

## Goal

Add the original's mouse controls on top of `docs/016`'s keyboard-only
active-fish scheme: click a fish to make it active; click-and-hold the
left button to have the active fish path around obstacles toward the
cursor for as long as it's held; hold the right button to push straight
toward the cursor instead; and a "swims faster" boost after moving
several tiles in a row.

## Investigated before implementing

Traced the real mechanics in `legacy/src/level/{MouseControl,FinderAlg,
FinderField,FinderPlace,Room,Controls}.cpp`:

- **`Room::nextRound()`'s real precedence**: keyboard `Controls::driving()`
  is tried first; `MouseControl::mouseDrive()` only runs if keyboard
  produced no move that round. Mouse never overrides a held key.
- **Click-to-select** (`Room::controlMouse`, one discrete event per
  mouse-down, not held): resolves the clicked cell via `Field::getModel`
  (already ported, and already correct for any cell within a multi-cell
  shape since `MarkMask.mask()` writes every shape cell) and calls
  `Controls::activateSelected(model)` - makes the matched fish active and
  triggers the same greet flash as Space.
- **Left-hold pathfinding** (`FinderAlg::findDir`): a plain unweighted
  BFS recomputed *fresh every round*, returning only the first-step
  direction (not a cached plan) - which is exactly what makes it adapt
  live if the button is held while something moves into the path. A
  candidate cell is explored only if `Unit::isFreePlace(loc)` (already
  ported, already shape-aware via `MarkMask.getPlacedResist`) - fully
  unobstructed, no pushing. `Field.getModel` already returns the shared
  border Cube for any out-of-bounds probe, so `isFreePlace` already fails
  at room walls with no extra bounds-checking needed - the original's
  explicit `w*h`-bounded `FinderField` closed-array turned out to be
  unnecessary; a fresh `Set<string>` per call is simpler and behaviorally
  identical.
- **Right-hold direct push** (`MouseControl::moveHardTo`) - a different
  mechanic the user hadn't mentioned (picks one axis toward the cursor
  by raw coordinate comparison, no pathfinding, so it *can* push movable
  objects but doesn't route around anything). Asked the user directly -
  confirmed to include it.
- **Speedup** (`Controls::lockPhases()`/`getNeededPhases()`) ties
  continuous movement to `PhaseLocker`, which controls how many
  animation phases must play *before the next round begins* - i.e. in
  the original, moving fast literally shortens the round's real-world
  duration, not just a cosmetic animation change. This project fixed the
  round duration (`docs/010`: one grid cell per `ROUND_MS`, always) to
  keep physics/animation cleanly decoupled since `docs/009`, and
  unwinding that for this one feature would be a large, risky rework.
  Asked the user - confirmed a **visual-only approximation**: grid
  movement rate stays exactly 1 cell/round; only the swim animation's
  frame rate and the position-slide tween get temporarily faster as a
  streak builds, purely in `ModelAnimator`.

## What was built

- **`web/src/game/FinderAlg.ts`** (new): the BFS above, with
  `FinderField`/`FinderPlace` inlined (tiny, single-purpose, no other
  consumers - same reasoning as not separately porting `ControlSym`).
- **`web/src/game/MouseControl.ts`** (new): `mouseDrive(input)` - left
  button calls `FinderAlg.findDir` + `Unit.driveDir`; right button does
  the axis-priority direct move. Constructed once by `Room`, holding
  `Controls`/`FinderAlg` references (both stateless per call).
- **`web/src/game/Unit.ts`**: new `isFreePlace(loc)` (passthrough to
  `Rules.isFreePlace`, already existed) and `driveDir(dir)` - drives
  directly in a known direction, bypassing the original's
  `driveOrder`/`ControlSym` symbol round-trip entirely (that indirection
  only existed for move-string recording/replay, which this port
  doesn't have - docs/007).
- **`web/src/game/Controls.ts`**: new `activateSelected(model)` - same
  shape as the existing `switchActive`/greet-flash mechanism (docs/016),
  just targeting a specific clicked unit instead of "the next one."
- **`web/src/game/Room.ts`**: owns one `FinderAlg` + `MouseControl`;
  `nextRound()` now tries mouse driving as a fallback when keyboard
  produced no move; new `askField`/`selectFish` passthroughs; a new
  `updateMoveStreaks()` call at the end of every round.
- **`web/src/game/Rules.ts`**: new `moveStreak` + `updateMoveStreak()`,
  ported from `Controls::lockPhases()`'s condition structure but kept
  per-Cube rather than centralized on `Controls`. Deliberate, documented
  difference from the original: since only one unit can ever move in a
  given round (`driving()`/`mouseDrive()` both short-circuit on the
  first success), per-Cube tracking is behaviorally equivalent for
  everything that can actually happen, with one minor upside - a fish
  resumes its own warm streak if reselected shortly after being paused,
  rather than the original's hard reset the instant a different fish
  becomes active.
- **`web/src/game/GameEngine.ts`**: new `selectAt(fieldPos)`;
  `RenderModel` gained `moveStreak`.
- **`web/src/scenes/LevelScene.ts`**: `pointerdown` (left button) calls
  `engine.selectAt(...)`; the per-round input object now implements
  `isLeftPressed`/`isRightPressed`/`getMouseField` from Phaser's
  `activePointer`; browser context menu disabled (right-click is now a
  real game action); status text updated.
- **`web/src/scenes/ModelAnimator.ts`**: `speedStepsFor(moveStreak)` -
  three tiers (1/2/3), inspired by but not a literal port of the
  original's `SPEED_WARP1=6`/`SPEED_WARP2=10` (the exact phase-divisor
  math doesn't translate to a fixed-round-rate world). Applied to the
  swim body-phase advance rate (only while `bodyAnim === "swam"`,
  matching the original scoping the divisor to non-turning moves) and to
  the position-slide tween's duration (any direction, so the glide
  itself reads as faster too, not just a quicker fin-flap) - both stay
  comfortably under `ROUND_MS` at every tier (docs/010's constraint).

## Verification

- `npx tsc -b` clean.
- Direct engine-driven tests (synthetic rooms, no real-time waiting):
  confirmed the BFS genuinely detours around a wall (traced the exact
  cell-by-cell path) rather than just reaching a target via a clear
  corridor; confirmed an unreachable (fully enclosed) destination leaves
  the fish in place; confirmed `Room.selectFish` changes which unit a
  held mouse-left subsequently drives; confirmed a held keyboard key
  wins over a simultaneously-held mouse click; confirmed right-click
  pushes a movable item (with proper floor support across its whole push
  path) but is blocked outright by a fixed wall; confirmed `moveStreak`
  climbs 0->13 over 14 uninterrupted rounds and resets to 0 on an idle
  round. Two test-methodology mistakes surfaced and fixed along the way
  (not code bugs): checking position immediately after one round instead
  of accounting for the existing "move only applies at the *start* of
  the next round" pattern (docs/007), and a pushed item losing its floor
  support mid-push in an under-specified synthetic room.
- Real-browser check: clicking the big fish selected it (confirmed via
  `Controls.getActive()` and a visible greet-flash pose change),
  click-and-hold routed it toward the cursor, screenshotted. One real
  test bug found and fixed (not a game bug): `index.html`'s body uses
  `display:flex; align-items:center; justify-content:center`, so the
  canvas is CSS-centered in the page - raw canvas-relative pixel math
  needed the canvas's actual bounding box added in for Playwright clicks
  to land correctly.
- Live speedup wiring check: held a key in real gameplay and sampled
  `RenderModel.moveStreak` against the live `ModelAnimator`'s internal
  `speedSteps` every round - matched `speedStepsFor()` exactly at every
  sample. The airplane level's confined starting geometry didn't offer
  enough open runway in any direction to cross the tier-1 threshold in
  real play, so the higher tiers were confirmed via the direct
  engine-driven test instead (real `Rules.ts` code, not a mock).
- Full regression suite re-run clean: `docs/007`/`008`/`009`/`011`/
  `012`/`013`/`014`/`015`/`016`'s existing scripts, unmodified (mouse
  fields are optional on `InputProvider`, so no keyboard-only test
  harness needed updating this time).

## Open for next time

- Middle mouse button - unused by the original for movement, unused here.
- No persistent "which fish is selected" visual beyond the existing
  brief greet flash (same scope note as docs/016).
- Touch input (tap-to-select / drag-to-follow) - a natural follow-up
  reusing this same `MouseControl`/pointer-field-conversion path, but
  out of scope for this pass.
