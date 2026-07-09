# 023 - Initial Facing Direction Bug

2026-07-09

## Bug report

User's question, following up on docs/022's validation run: does the
original record a move symbol for a fish turning in place, or only for an
actual position change - and if turning *is* recorded, shouldn't
`library`'s solution (starting `"ll"`) work, since the first move might
just be a turn? They were skeptical turning should be recorded at all, and
asked whether this is documented anywhere in the original.

## Investigation

**Is turning recorded?** Yes - already correctly ported in docs/021 and
re-confirmed here: `legacy/src/level/Unit.cpp`'s `goLeft()`/`goRight()`
return the unit's symbol in *both* the turn branch and the successful-move
branch, unconditionally. No comment in the source explains *why* - the
only doc comments are generic (`@return a symbol when unit has moved`,
`Unit.cpp:54`). Not documented anywhere as a deliberate design note; it's
just what the code does, discovered by reading the implementation, not
inferred from any comment or README. This part of the user's question is
settled and was already correct in this port.

**But `library`'s actual failure wasn't a turn-recording problem at all.**
Traced it precisely: `fish_small` spawns at `(5,27)` in `library` -
confirmed against `models.lua` directly. The *first* `'l'` moved the fish
from `x=5` to `x=4` (a real position change, confirmed via direct engine
inspection before/after) - not a turn. The *second* `'l'` then failed
because `(3,27)` is a real wall, confirmed directly from `models.lua`'s own
room-shape ASCII art (row 27: `XXXX.....X......XXXXX`, position 3 is `X`).
So by this port's behavior, the fish was *already* facing left at spawn,
and the first `'l'` was a move, not the turn the user suspected.

**Except that's wrong** - `library/models.lua` calls
`addFishAnim(small, LOOK_RIGHT, "images/fishes/small")`. `LOOK_RIGHT` means
the level wants this fish to start facing *right*, not left. Read
`legacy/script/share/level_creation.lua`'s real `addFishAnim()`:

```lua
function addFishAnim(model, look_dir, directory)
    unit_table[model.index] = model
    if model:isLeft() and look_dir == LOOK_RIGHT then
        model:change_turnSide()
    end
    ...
```

Every model defaults to `isLeft() == true` (matching `Cube::Cube()`'s own
default); if the level wants `LOOK_RIGHT`, this line flips it via
`change_turnSide()` - a direct, immediate mutation used during level setup
(not the deferred `actionTurnSide()`/`changeState()` two-step gameplay
turning uses). This *is* correctly wired end to end at the Lua level in
this port: `model:isLeft()`/`model:change_turnSide()`
(`level_creation.lua:91,118`) call through to real host bindings
(`model_isLeft`/`model_change_turnSide`, `levelLoader.ts:293-301`), and the
resulting flipped value is correctly exported as `LevelModel.isLeft`
(`levelLoader.ts:357`).

**The break was one level further down.** `GameEngine.ts`'s `buildCube()`
never read `modelData.isLeft` at all:

```ts
function buildCube(modelData: LevelModel): Cube {
  const cube = createModel(modelData.kind, new V2(modelData.x, modelData.y), modelData.shape);
  cube.goal = goalFromName(modelData.goal);
  return cube;   // isLeft never touched - Cube's own `isLeft = true` default always wins
}
```

`Cube.isLeft` defaults to `true` (`Cube.ts:35`, matching legacy's own
default) and nothing ever overwrote it with the Lua-computed value - so
*every* model in *every* level has always spawned facing left in this
port's physics, regardless of what the level's own unmodified script
actually specifies. `library`'s fish were both meant to start facing right
(`addFishAnim(big, LOOK_RIGHT, ...)` too); this port silently ignored that
and started them facing left instead.

**Why this was never caught before**: `airplane` and `viking1` - the only
two levels this project has ever exercised throughout its whole history -
both specify `LOOK_LEFT` for their fish (`legacy/script/airplane/
models.lua:41,48`, `legacy/script/viking1/models.lua:93,100`), where the
bug's effect is invisible (the buggy always-left default happens to match
the correct spec by coincidence). The bug has been present since the
original physics port (docs/007) - the solution validator (docs/022) is
what finally surfaced it, by exercising 79 more levels than this port ever
had before.

**Blast radius, confirmed before fixing**: grepped every level's
`models.lua` for `addFishAnim(...LOOK_RIGHT...)` - 44 levels request it for
at least one fish. Cross-referenced against docs/022's failure list: *every
single one* of the 37 "loads fine but solution fails" levels is in this
list, and *none* of the 32 passing levels are - as close to a smoking gun
as this kind of investigation gets.

## Fix

One line, `web/src/game/GameEngine.ts`'s `buildCube()`:

```ts
cube.isLeft = modelData.isLeft;
```

Mirrors the existing `cube.goal = goalFromName(modelData.goal);` line right
above it - same pattern, same function, just a second piece of parsed level
data that wasn't being applied. No `Cube`/`Rules`/physics changes.

## Verification

- `npx tsc -b` clean.
- `library` re-validated: now solves cleanly in 111 moves (previously
  failed on move 2).
- Full 81-level batch re-run: **69/81 passed, up from 32/81** - every one
  of the 37 previously "solution fails partway" levels now passes,
  including long solutions like `map` (2127 moves) and `grail` (1691
  moves, previously failed at move 48). The remaining 12 failures are
  unchanged from docs/022 and entirely pre-existing/unrelated: the same 11
  missing-Lua-host-binding level-load failures, `windoze`'s unsupported
  `fish_extra`, and `redhat`'s nonexistent level directory.
- `airplane`/`viking1` re-confirmed still solving (235/119 moves) - the fix
  doesn't touch anything for levels that already specified `LOOK_LEFT`.

## Open for next time

- The 11 missing-Lua-binding levels (unchanged from docs/022) are still
  the next real blocker for full coverage - a separate body of work
  extending `levelScript.ts`'s host API surface.
- This bug means every level rendered/played interactively before today
  (beyond `airplane`/`viking1`) would also have shown fish facing the
  wrong way at spawn for any `LOOK_RIGHT` level - worth keeping in mind if
  anyone recalls testing a `LOOK_RIGHT` level's *visuals* earlier in this
  project's history and it looked fine; it wouldn't have, this bug was live
  the whole time.
