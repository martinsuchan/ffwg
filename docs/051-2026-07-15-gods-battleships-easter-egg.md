# 051 - gods' battleships easter egg (view shift + draw effects)

_2026-07-15_

## Why

User reported a hidden easter egg in `gods`: the two gods (Neptun/Poseidon)
aren't babbling randomly - they're playing **battleships**, and when a ship is
sunk a wreck falls through the background.

## How the original does it (traced)

- **The game is real.** `legacy/script/gods/prog_ships.lua` is a complete 10x10
  battleships engine in pure Lua: 7 ships (`getNShips`), random legal placement
  (`initLode`), and a per-player AI (`hrajlode(h)`) with distinct personalities -
  player 1 hunts a hit ship 50% of the time, player 2 90% and **cheats**
  (`podvod`: it can deny a sink, or claim a hit that wasn't). Results are 1=water,
  3=hit, 4=sunk, 5/6/7=cheat variants, 8/9="you already said that".
- `code.lua` calls `initLode()` at init, then each round plays `hrajlode(1)` /
  `hrajlode(2)`; the gods' dialogs are the **coordinates and answers**.
- **On a sink**, `planSet(room, "shodit", getLastHit())` - `getLastHit()` returns
  which ship kind sank (0..4). The room's per-round closure calls
  `shinkShip(room.shodit)`, which sets up the `objekty` model: `setEffect("none")`
  (show), `afaze = ship` (-> `potop_00..04.png`), `shiftX = randint(10,30)`,
  `shiftY = 0`, `speedY = 1`, `speedX = randint(-1,1)`.
- `objekty` is a 1-cell `item_light` parked at (0,0) (resting on the room wall, so
  physics never moves it) and normally `setEffect("invisible")`. Its own per-round
  closure flies it purely with **`model_setViewShift(index, shiftX, shiftY)`**,
  incrementing shiftY/shiftX each round until `shiftY >= room:getH()`, then
  `afaze = -1` + `setEffect("invisible")` again.

## The gap in the port

The battleships engine **was already running** (`prog_ships.lua` loads via the
docs/008 file_include pre-scan; the gods already speak their coordinates). Only
the visual was missing, because both host functions it needs were **no-op stubs**:

- `model_setViewShift` (docs/028) - so the wreck could never move.
- `model_setEffect` (docs/014) - so `setEffect("invisible")` did nothing and the
  wreck sat **permanently visible in the top-left corner** at (0,0). A real,
  visible bug.

## What changed

Both are real now (`web/src/lua/levelScript.ts`), stored per model and exposed via
`getViewShift(index)` / `getEffect(index)`:

- **`model_setViewShift(index, x, y)`** - a cosmetic offset in **grid cells**,
  applied by `ModelAnimator.sync()` as legacy `View::getScreenPos()` does:
  `(location + viewShift) * SCALE + moveShift` (before scaling, not pixels).
  `model_getViewShift` returns what was set (was hardcoded (0,0), docs/033).
- **`model_setEffect(index, name)`** - legacy `Anim::setEffect`. Implemented the
  two that are plain draw rules: **`invisible`** (draw nothing) and **`reverse`**
  (flip left/right -> `setFlipX`). `none` draws normally. `mirror` (submarine's
  per-pixel screen reflection) and `zx` (emulator's colour-clash gag) are recorded
  but drawn normally - genuine per-pixel screen effects, out of scope.

Both are gated to **non-fish models only** in `LevelScene`, matching the existing
scriptAnim ownership split (docs/014) - verified no level calls `setEffect` on a
fish.

**This fixes 5 more levels beyond gods**, which also use `setEffect("invisible")`
and were wrongly drawing hidden decorations: party1 (4), party2 (6), rotate (6),
windoze (`spuntik`), corridor (sets it mid-play).

## Verification (real browser, sandbox)

- Battleships engine live in gods: `getNShips()=7`, both gods `lodi=7`, the board
  `room.planek` built.
- Wreck hidden at start (`effect=invisible`, sprite not visible) - previously
  stuck visible at (0,0).
- Poking `room.shodit = 2` (exactly what a real sink does) makes the wreck appear
  and fall: viewShift `(13,2)` -> `(47,36)` at 1 cell/round with sideways drift,
  sprite y 30 -> 540, then auto-hides at the room floor. Screenshot shows the
  `potop_02` steamship sinking behind the two gods.
- corridor/party1/party2/rotate/windoze all load clean, hiding exactly the models
  their scripts ask to hide; no page errors.
- e2e suite 7/7; `tsc -b` + `vite build` clean.

## Files
- **Modify:** `web/src/lua/levelScript.ts` (real `model_setViewShift`/
  `model_getViewShift`/`model_setEffect` + `ViewShift` type + getters),
  `web/src/scenes/ModelAnimator.ts` (apply view shift + invisible/reverse),
  `web/src/scenes/LevelScene.ts` (pass both, non-fish only).
