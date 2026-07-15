# 054 - Lua host-API gaps, part 1 (depth, model_equals, isSolved, options)

_2026-07-15_

Follow-up to the BACKLOG §0 audit. Implements the four cheap correctness wins;
the visual ones (`game_setRoomWaves`, `game_addDecor` ropes, `game_setScreenShift`)
and `mirror`/`zx` remain open.

## Analysis first (two the audit couldn't classify)

**`model_equals(index, x, y)`** — the original (`game-script.cpp`) asks *"is the
model occupying cell (x,y) the one with this index?"*, `-1` meaning "empty
water", resolved via `Room::askField()` -> `Field::getModel()`, i.e. through each
model's **real multi-cell mask**. Note its exact branch order — the border is a
wall, not water:

```cpp
Cube *other = askField(V2(x,y));            // border Cube for out-of-bounds
if (other) equals = (model_index == -1) ? false : model_index == other->getIndex();
else       equals = (model_index == -1);    // genuinely empty
```

Callers: `prog_compatible.lua`'s `isWater()`/`modelEquals()` and
`prog_finder.lua`'s `isFreePlace()` (which scans a model's whole W*H), driving
`creatures`/`cancan`/`turtle`'s scripted NPCs. They test against `room` — the
level's entire wall shape — so the port's anchor-only match reported **every wall
cell as free**.

**`file_exists`** — **not a gap**, reclassified. The Lua only calls it from
`level_creation.lua`'s addAnim/addFishAnim frame discovery, `level_dialog.lua`'s
sound gate + (bypassed) dialogLoad, `init.lua` (unused) and `demo_briefcase.lua`.
The live engine *does* run `models.lua`, so the discovery loop executes —
returning false for `images/` just makes it exit immediately, which is correct:
frame lists belong to `levelLoader.ts` and are consumed by `ModelAnimator`.

## Fixed

| Binding | Was | Now |
|---|---|---|
| `level_getDepth` | `0` | Real `LevelNode::m_depth`: 1 at the root, parent+1 per child, ending -1. New `computeDepths()` in `worldMapLoader.ts` -> `WorldMapData.depths`, plumbed through the launch data into `createLevelScript(..., depth)`. |
| `model_equals` | anchor-only | Real, via new `GameEngine.askFieldIndex(x,y)` (index / `-1` border / `null` water) exposed on the docs/035 `EngineControl` bridge. Honours the multi-cell mask **and** the border-isn't-water rule. |
| `level_isSolved` | `false` | `engineControl.isSolved()` (legacy `Room::isSolved`). `bordershout.lua` gates lines on it. |
| `options_getParam` | `""` | `"lang"`/`"speech"` -> the real setting (docs/038); anything else `""` (never null/undefined — docs/024). |

**Why depth matters:** `blackjokes.lua` picks its death-joke tier with
`switch(level_getDepth())` over `joke_table[1..15]`, and real depths run exactly
1..15. Depth `0` matched **nothing**, so the entire depth-varied death-joke system
was dead on all 80 levels; its `level_getDepth() == 2` early-return (briefcase,
the tutorial, gets no black jokes) never fired either, and `depth >= 9` likewise.

Also fixed two round-trip data drops found while plumbing: `LevelScene`'s P-replay
and `ReplayScene`'s Esc-back-to-level both rebuilt the level scene without its
`poster`/`depth`, so solving a final level after reviewing its replay would have
skipped the docs/050 recap poster.

## Verification (real browser, sandbox)

- Depths: `start`=1, `briefcase`=2, `gods`/`linux`=15, `ending`=-1, range **1..15**
  over 81 entries — matching the original's tree exactly. `creatures` reports 12
  in-level.
- `model_equals` on `creatures`: `modelEquals(room, 0,0)` is **true** (the
  multi-cell wall resolves — it was false before); `isWater(0,0)` false inside the
  wall; `isWater(-5,-5)` **false** (border is a wall, not water); the big fish
  registers on **both** its anchor and a second cell.
- `level_isSolved()` false on a fresh room; `options_getParam("lang")` = `"cs"`.
- e2e 7/7; `tsc -b` clean.

**The sweep earned its keep**: case 05 caught `engineControl?.isSolved is not a
function` on all 80 levels — its hand-rolled mock is plain JS, so TS couldn't see
it drift from the interface. Mock updated + commented to require every member.

## Files
- **Modify:** `web/src/game/GameEngine.ts` (`askFieldIndex`),
  `web/src/lua/levelScript.ts` (EngineControl +2, real `model_equals`/
  `level_isSolved`/`level_getDepth`/`options_getParam`, `depth` param),
  `web/src/lua/worldMapLoader.ts` (`computeDepths` + `depths`),
  `web/src/scenes/WorldMapScene.ts` / `LevelScene.ts` / `ReplayScene.ts` (plumb
  depth + poster), `web/tests/cases/05-levels-load.mjs` (complete mock).

## Still open (BACKLOG §0)
`game_setRoomWaves` (shader — user picked the custom-shader approach when we get
to it), `game_addDecor("rope")`, `game_setScreenShift`, `mirror`/`zx`, and the 5
unbound undo-path names.
