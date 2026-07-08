# 016 - Active-Fish Control Scheme

2026-07-08

## Goal

Replace the placeholder POC controls (WASD permanently drives the big fish,
IJKL permanently drives the small fish, both always at once) with the
original game's real scheme: one fish is "active" at a time, arrow keys
always drive whichever fish is active, WASD/IJKL still drive their own
fish directly, and Space explicitly switches the active fish with a
brief "greet" animation.

## Investigated before implementing

Traced the exact mechanic in `legacy/src/level/Controls.{h,cpp}` and
`Unit.{h,cpp}` rather than guessing at "which fish is active" semantics:

- **`ModelFactory::createUnit()`**: `fish_small` is built with
  `startActive=true`, `fish_big` with the default `false` - the small
  fish is active at level start, always.
- **`Controls::driveUnit()`**: each round, the *active* unit is tried
  first against a shared arrow-key `KeyControl` (`driveBorrowed`). Only
  if that produces no move does the code fall through to trying every
  unit's *own* dedicated keys (`drive()`); whichever one moves becomes
  active via `setActive()` - silently, no animation.
- **`Controls::switchActive()`/`useSwitch()`**: `LevelInput`'s `KEY_SWITCH`
  (`SDLK_SPACE`) calls `Room::switchFish()` -> `switchActive()`, which
  cycles to the next unit that `canDrive()` (wrapping, skipping any
  dead/lost/busy fish) and sets `m_switch = true`. The *next* call to
  `driving()` consumes that flag: it calls the newly-active unit's
  `activate()` (`Rules::actionActivate()` -> `m_readyToActive = true`)
  and reports "no move this round" - a switch alone never doubles as a
  move. `checkActive()` also auto-switches (with the same greet flash)
  away from an active fish that can no longer ever move (dead/lost),
  independent of the player pressing Space.
- **The greet animation**: `Rules::getAction()` returns `"activate"`
  while `m_readyToActive` is set (cleared again next round by
  `changeState()`), and `level_update.lua`'s `animateFish()` maps that
  to `model:setAnim("turn", 0)` - a single held frame of the turn pose,
  not the running turn loop.

The physics/animation port had already anticipated all of this and just
never had a caller: `Rules.ts` already had `actionActivate()`/
`readyToActive`/`getAction() -> "activate"` line-for-line (docs/007), and
`UnitAnimator.ts`'s `computeBodyAnim()` already had the `"activate"` case
(`{name: "turn", running: false}`) with a comment noting it was
"currently unreachable... kept for fidelity." Both needed zero changes.

## What was built

- **`web/src/game/Controls.ts`** (new): port of the driving/switching
  subset of `Controls.cpp` - `addUnit` (re-scans for `startActive`, same
  as the original), `driving()`, `useSwitch()`/`checkActive()`/
  `switchActive()`, `driveUnit()` (arrows-borrowed-by-active-unit, then
  each unit's own keys), `cannotMove()`. Dropped, matching this
  project's existing no-save/no-demo scope (docs/007): move-string
  recording, mouse `activateSelected`, phase-lock speedup, and the
  discrete-keystroke/demo-replay path (`controlEvent`/`useStroke`).
- **`web/src/game/Unit.ts`**: `drive()` is now `driveBorrowed(input,
  this.buttons)`; new `driveBorrowed(input, buttons)` lets a unit be
  tested against a *different* key set (the shared arrows); new
  `activate()` (calls `cube.rules.actionActivate()`); new `startActive`
  constructor flag.
- **`web/src/game/Room.ts`**: now holds a `Controls` instead of a raw
  `Unit[]` - `addModel(model, unit)` registers through it,
  `driving()`/`cannotMove()` delegate to it, new `switchFish()` (Space).
- **`web/src/game/GameEngine.ts`**: `buildUnit()` passes `startActive:
  kind === "fish_small"`; new public `switchFish()`.
- **`web/src/scenes/LevelScene.ts`**: `keydown-SPACE` calls
  `engine.switchFish()`; `addCapture("UP,DOWN,LEFT,RIGHT,SPACE")` so the
  browser doesn't scroll the page while playing; status text updated.

No changes were needed in `Rules.ts`, `UnitAnimator.ts`, or
`ModelAnimator.ts` - the greet-flash animation already worked end to end
once something finally called `actionActivate()`.

## Verification

- `npx tsc -b` clean.
- Direct engine-driven tests (`GameEngine`/`Room.controls`, no real-time
  waiting): confirmed small fish is active by default and arrow keys
  drive it; confirmed a big-fish-only key (`KeyD`/`KeyA`) drives big
  directly and silently makes it active (arrows then drive big, not
  small); confirmed `engine.switchFish()` produces `action: "activate"`
  on the newly-active fish with *zero* position change for either fish
  that round, and that a plain idle round afterward clears the flash.
  (First attempt at this test gave confusing all-"rest" results - traced
  to the airplane level needing ~3 rounds to settle its initially-
  floating items before `Room.isFresh()` allows any input at all, a
  pre-existing physics constraint unrelated to this change, not a bug.)
- Real-browser check: loaded airplane, pressed Space/arrows/WASD/IJKL in
  sequence, screenshotted - level renders correctly, updated status text
  shows, big fish visibly moved/turned after being made active via
  Space + arrow key.
- Full regression suite re-run: docs/007's synthetic-room tests (fixed
  one test script that pushed directly onto the now-removed `Room.units`
  array instead of going through `addModel(model, unit)` - a test-API
  update, not a logic change; all 7 cases still match expected values),
  docs/008's goal-levels test, docs/009's stress-play script, docs/011's
  corpse-removal test, docs/012's restart-leak test, docs/013's death-
  reaction-delay test, docs/014's item-animation and rapid-restart
  tests, docs/015's dialog/restart-counter/death-with-dialog tests - all
  unchanged, identical results.

## Open for next time

- No persistent visual indicator of which fish is active beyond the
  original's brief greet flash - matches the original exactly, but a
  permanent highlight/outline could be a nice non-canonical addition if
  ever wanted.
- Mouse-click fish selection (`Controls::activateSelected`) is not
  ported - no mouse input in this project yet at all.
- The discrete keystroke/demo-replay path (`m_strokeSymbol`,
  `setMoves`/`activateDriven`) stays dropped, consistent with no save/
  undo/demo mode existing in this port.
