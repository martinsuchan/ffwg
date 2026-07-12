# 035 - The `windoze` level: nested "bonus" child level + extra fish

_2026-07-12_

`windoze` ("Tajný počítač" / Secret Computer) was the last level this port
skipped (docs/022/024/027). It's special: a **second pair of fish** ("the old
couple") lives in a "bonus" sub-window that must be solved before the main room's
normal fish can finish, and it's the only level whose replay uses an **extended
move alphabet** (`w/x/y/z` for the small extra fish, `W/X/Y/Z` for the big extra
fish, on top of `u/d/l/r`/`U/D/L/R`). Now fully playable, with save/load and replay.

## The mechanic

Four fish, all with `goal_escape` (so `isSolved` needs all four out):
`small`/`big` (normal) and `staravelka`/`staramala` (the extra couple, kinds
`fish_EXTRA-WXYZ` / `fish_extra-wxyz`). `code.lua`'s `room.resit` state machine:

- `resit=0`: drive the normal fish; the extra couple is `setBusy(true)` (frozen).
- `big` pushes the `bonuslevel` item (`big:getTouchDir()~=dir_no and
  bonuslevel:getTouchDir()~=dir_no`) → `resit=1`: normal fish frozen, extra couple
  un-frozen, `game_checkActive()` swaps player control to them, `game_setFastFalling
  (true)`. The extra fish escape the bonus through `spuntik` (an `output_left` plug).
- Both extra fish out → `resit=2`: normal fish un-frozen; finish by escaping them.

## What was ported (legacy `src/level/`)

All in the physics `GameEngine` layer (`web/src/game/`):

- **Extra fish** — `ModelFactory.ts`: `fish_extra*` (LIGHT power) / `fish_EXTRA*`
  (HEAVY power) params; `parseExtraControlSym()` reads the 4 move symbols out of the
  kind string. `buildUnit` (GameEngine) gives them **no keyboard keys** (driven only
  when active, via arrows — matching the original, and why their replay symbols are
  separate) and never `startActive`. `isFishKind` extended so they render + get units.
- **`output_left` / `touchSpec`** — `Cube.ts` gains `outDir`/`outCapacity`/
  `setOutDir`/`decOutCapacity`/`isOutDir`; `ModelFactory.createOutputItem` builds a
  FIXED plug (capacity 2); `Rules.touchSpec` (re-added to `actionMoveDir`'s blocked
  branch before `setTouched`, exactly as `Rules.cpp`) sends a fish that pushes into
  the plug out through it. Fires only when blocked by exactly one `output_*` cube, so
  no other level is affected (confirmed by re-validating all solutions).
- **`busy`** — already a `Cube` field / `Unit.canDrive()` gate; wired to Lua now.
- **`game_checkActive`** → `Room.checkActive()` → `Controls.checkActive()` (made public).
- **`game_setFastFalling`** → `Room.setFastFalling()` + a fast-settle loop in
  `nextRound`/`replayRound` (reuses the existing `fastForwardSettle`). Outcome-
  identical to normal falling, just faster.

### Live-Lua → engine bridge (the one new coupling)

`levelScript.ts` deliberately keeps the live Lua engine physics-free (docs/014).
windoze is the sole level that needs `code.lua` to drive physics, so a small opt-in
`EngineControl` interface (`setBusy`/`checkActive`/`setFastFalling`) is passed to
`createLevelScript`; `LevelScene` and `ReplayScene` supply one closing over their
`engine`. The three previously-stubbed bindings now call it. `model_getTouchDir`
(docs/033) already returned the value `bonuslevel:getTouchDir()` needs.

### Rendering — no new code

Extra fish are `fish_*`, so the generic fish animator path (docs/009) renders them
from the `ex_big`/`ex_small` frames. `spuntik` has no `addItemAnim`, so
`buildAnimators` skips it (its `setEffect("invisible")` is moot — it never had a
texture). The room canvas already resizes per level.

### The one real gotcha — replay `busy` desync

`Unit.driveOrder` (the recorded-symbol path used by replay/validation/demo) was
gated on `canDrive()` (which includes `busy`). During the watchable `ReplayScene`,
the live Lua toggles `busy` a round off from when the string was recorded, so a
valid extra-fish symbol hit a still-`busy` fish and the replay **stalled at move
163/525**. Fix: `driveOrder` now gates on `willMove()` (alive + not out), not
`canDrive()` — a recorded symbol is an already-decided move that uniquely names its
fish + direction and replays deterministically regardless of who was interactively
drivable at the time. `busy` still gates the interactive `drive`/`driveBorrowed`
path. This only changes behavior when `busy` differs (windoze-only); all other
levels have `busy===false` throughout, so `willMove()===canDrive()`.

## Verification (all via the dev server, temp `window.__game` hook removed after)

1. **Headless physics (primary):** replayed `legacy/solution/windoze.lua` (525 moves,
   using `X/Y/Z/w/x/y/z`) through a fresh `GameEngine` — reaches `isSolved()`, all
   four fish `isOut`. Proves extra fish + `touchSpec`/`output_left` + extended alphabet.
2. **All 81 reference solutions:** **80 SOLVED** (was 79) — windoze now passes, every
   other level unchanged; only `redhat` fails (no level directory in this repo). Re-run
   after the `driveOrder` change: still 80/80.
3. **Interactive bridge:** launched windoze, drove control-swap through the real Lua
   bindings → the extra couple un-freezes, `game_checkActive` makes an extra fish
   active, and driving it with arrows records its `W/X/Y/Z` symbols; `game_setFastFalling`
   reaches the engine. Screenshot confirms the Win95 room renders with both fish and
   the bonus window (the old couple + bonus items) — invisible `spuntik` absent.
4. **Replay:** `P` → `ReplayScene` consumes all 525 extended-alphabet moves to
   `isSolved` (no stall after the `driveOrder` fix).
5. **Save/load:** F2 save + F3 load round-trips the recorded move string; the
   `captureModelState`/`restoreModelState` pickle of windoze's model tables
   (incl. `room.resit`) round-trips (8.5 KB, docs/026 path).

## Files

- **Modify:** `web/src/game/{ModelFactory,Cube,Rules,Unit,Controls,Room,GameEngine}.ts`;
  `web/src/lua/levelScript.ts`; `web/src/scenes/{LevelScene,ReplayScene}.ts`.
- No rendering changes (generic fish/anim-less-item paths already cover it).

## Open / notes

- Extra-fish controls are faithful: no dedicated keys — switch with Space/click, drive
  with arrows.
- `redhat` remains the only unimplemented node, and only because its level content
  doesn't exist in this repo (not a code gap).
