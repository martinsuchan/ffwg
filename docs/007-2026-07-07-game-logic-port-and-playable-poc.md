# 007 - Game Logic Port And Playable POC

2026-07-07

## Goal

Find where the legacy engine actually defines its puzzle rules (fish
movement, pushing, falling, dying, winning), find how those rules connect
to the Lua content and to player input, then port the rules (not the
rendering/sound/save layer) to TypeScript and make the `airplane` level
actually playable in the browser: WASD for the big fish, IJKL for the small
fish, no animation/save/load - see `docs/006` for the static render this
builds on.

## Where the rules live (source analysis)

The simulation is entirely in `legacy/src/level/`, independent of Lua:

- **`Rules.cpp/h`** - the core: can a model move/push in a direction, does
  a move/fall/being-too-heavy kill a fish, does a `goal_escape` model walk
  out through the border. Every model (`Cube`) owns one `Rules` instance.
- **`MarkMask.cpp/h`** - translates a `Cube`'s `Shape` (the ASCII-art grid)
  into "who resists me in direction X" queries against the `Field`, and
  masks/unmasks a model's footprint on the grid as it moves.
- **`Field.cpp/h`** - the plain `w`x`h` grid of `Cube` pointers. Any
  out-of-bounds query returns a single shared **border** `Cube` (fixed,
  not alive) - this is what makes "am I at the edge of the room" and "walls
  block pushing" fall out of the same resist-query code path instead of
  needing special-case bounds checks everywhere.
- **`Landslip.cpp/h`** - the once-per-round gravity pass: repeatedly marks
  ("stones") everything that's resting on something fixed, then makes
  whatever's left fall one cell.
- **`Goal.cpp/h`** - per-model win/lose-forever condition (`noGoal`,
  `outGoal`, `escapeGoal`, `aliveGoal`); `addFishAnim()` in Lua sets both
  fish to `escapeGoal`.
- **`Unit.cpp/h` + `ModelFactory.cpp`** - a driveable fish wraps a `Cube` +
  a `KeyControl`. `ModelFactory::createUnit()` is where the **real**
  per-fish key bindings live: `fish_small` = I/K/J/L,
  `fish_big` = W/S/A/D - i.e. the port's ASDW/IJKL scheme isn't a new
  choice, it's the original game's actual control scheme.
- **`Room.cpp/h`** - orchestrates one round: `prepareRound()` (apply last
  round's pending moves, check who died), `fallout()` (let `goal_escape`
  models walk toward/through the border), `falldown()` (run `Landslip`).
- **`Controls.cpp/h`** - the input layer Room delegates to: holds the list
  of `Unit`s, and each round tries them in registration order, first one
  whose own keys are held *and* can move wins the round (only one model
  moves per round).

**How Lua connects to this:** it doesn't, directly - `models.lua`'s
`addModel`/`addFishAnim`/etc. (`legacy/script/share/level_creation.lua`)
call C++ host bindings (`game_addModel`, `model_setGoal`, ... in
`legacy/src/level/game-script.cpp`) purely to *construct* the `Cube`/`Unit`
graph once, at level load. After that, gameplay never touches Lua again
each round - `code.lua`'s `prog_update()` (see `docs/006`) only drives
ambient hint dialogs, confirmed by reading `legacy/script/airplane/code.lua`
end to end.

**How input reaches a fish:** `LevelInput` (`legacy/src/level/LevelInput.cpp`,
a `GameInput`) owns a keymap for the *non-movement* keys (space/F2/F3/
backspace/+/-) and forwards everything else to `Level::controlEvent()`.
Movement itself is polling-based, not event-based: every engine round,
`Room::nextRound()` calls `Controls::driving(input)` -> `Unit::drive(input)`
-> `input->isPressed(key)` for each unit's own keys - i.e. the engine asks
"is this key currently held" once per round, it doesn't react to keydown
events. This port's `LevelScene` mirrors that: a `Set<string>` of currently
-held `KeyboardEvent.code`s, sampled on a fixed tick.

## What was ported (`web/src/game/`)

Direct, mostly line-for-line ports of the classes above: `V2`, `Dir`,
`Shape`, `Cube`, `Goal`, `Field`, `MarkMask`, `OnCondition`
(`OnStack`/`OnWall`/`OnStrongPad`), `Rules`, `Landslip`, `Unit`,
`ModelFactory`, `Room`. `web/src/game/GameEngine.ts` wires the parsed level
(`web/src/lua/levelLoader.ts`, extended to also capture each model's raw
shape string and goal name - both were previously discarded, only needed
for static rendering) into real `Cube`/`Unit` instances and exposes
`tick(input)` + a render-friendly snapshot. `LevelScene` now runs a fixed
130ms round timer instead of a static one-shot render, repositions/flips/
tints sprites from that snapshot each round, and shows a win/lose banner.

**Deliberately not ported** (rendering/save/UI concerns, not physics):
sound, dialogs, `code.lua`, save/undo/replay, the mouse-driven pathfinder
(`FinderAlg`), the shared-arrow-keys/active-fish-switch scheme (this POC
gives each fish permanent dedicated keys instead), the discrete keystroke
queue (`Controls::useStroke`) alongside the polling path, animation-phase
timing (`PhaseLocker`/`TimerAgent` - replaced with a flat 130ms tick),
`touchDir`/hint-dialog bookkeeping, and `output_*` border items (unused by
`airplane`).

## Bugs found and fixed

- **`Field` never wired the border `Cube`'s own `Rules` to itself** (the
  original's `Field::Field()` calls `m_border->rules()->takeField(this)`;
  this port's constructor skipped it). Harmless in every path actually
  exercised (every call site checks `isWall`/`isAlive` on a resist before
  ever recursing into its `.rules`, so the border is always short-circuited
  first) - found by tracing a test failure, not by observing a real crash.
  Fixed by calling `takeField` in the `Field` constructor, matching the
  original exactly.
- Two apparent test failures during verification turned out to be the
  engine being *more* correct than the test: a "heavy item resists a push"
  test placed the heavy item directly on top of the test fish, which
  legitimately crushed the fish by sustained weight (`checkDeadStress`)
  before the push was ever attempted - and the resulting corpse then fell
  like any other item, since a dead-but-not-yet-removed fish is no longer
  automatically "fixed". Another test gave a fish sitting on the bottom
  border row `escapeGoal`, which correctly made it walk out through the
  floor immediately (`shouldGoOut()` lets a model treat the border as
  non-resisting) before a falling item ever reached it. Both were fixed by
  correcting the test setup, not the engine - see the reasoning in the
  test scripts if this needs revisiting.

## Verification

Two layers, both against the real ported code (not a re-implementation):

- **Synthetic mini-rooms** (Playwright driving the real browser modules via
  `page.evaluate` + dynamic `import()`, bypassing Lua/Phaser entirely):
  light item pushed by a `LIGHT`-power fish; `HEAVY` item resists a
  `LIGHT`-power push but yields to a `HEAVY`-power push; an unsupported
  item free-falls and stops exactly on the floor; a heavy item dropped from
  well above a fish kills it on landing (`checkDeadFall`); a fish with
  `escapeGoal` at the border walks out and `Room.isSolved()` flips true;
  facing - pressing "right" while facing left just turns (no move), the
  next "right" press then actually moves.
- **Real `airplane` level in a real browser** (Playwright + headless
  Chromium against the Vite dev server): confirmed both fish move/turn/
  flip correctly, a small fish is correctly blocked by a heavy pipe it
  can't push, and an extended ~20-second mixed-key session across both
  fish produced zero console errors and zero failed requests.

## Core game logic rules

Everything below is what `docs/007`'s port actually implements - i.e. this
is accurate for the browser POC, not just the original.

**Grid.** The room is a `w`x`h` grid of 15x15 pixel cells (`GRID_SCALE`,
`legacy/src/level/View.h`'s `SCALE`). Every object occupies exactly the
cells its ASCII-art shape marks with `X`.

**Every object has a weight and, if it's a fish, a power:**

| kind | weight | power |
|---|---|---|
| `item_light` | LIGHT | - |
| `item_heavy` | HEAVY | - |
| `item_fixed` (walls, the room's own collision shape) | FIXED | - |
| `fish_small` | LIGHT | LIGHT |
| `fish_big` | LIGHT | HEAVY |

**Who can move what.** A fish can push a chain of one or more inert
objects in the direction it moves if **every** object in that chain has
`weight <= the fish's power`. `FIXED`-weight objects (walls) can never be
pushed. Consequences:
- The small fish (`LIGHT` power) can push `item_light` objects, but not
  `item_heavy` ones.
- The big fish (`HEAVY` power) can push both `item_light` and `item_heavy`
  objects.
- Neither fish can push the other fish, or any wall.
- Pushing is all-or-nothing: if *any* object in the chain can't move, none
  of them do (including the fish itself).

**Facing.** Each fish starts facing left. Pressing "right" while facing
left doesn't move it - it just turns to face right (consuming that
input); the fish only actually steps right once it's already facing that
way. Pressing "left"/"right" again while already facing that way moves
immediately. Vertical moves (up/down) are unaffected by facing.

**Only one fish moves per round.** If both players hold a key at the same
moment, the fish added earlier in `models.lua` (for `airplane`: the small
fish, added before the big fish) wins that round; the other's key is
simply re-checked next round. Rounds run on a fixed ~130ms timer in this
POC (the original paces rounds by animation-phase count instead, which
this POC has no equivalent of).

**Gravity.** Every round, any inert (`item_light`/`item_heavy`) object
that isn't directly or indirectly resting on a wall or a fish falls one
cell. Fish never fall on their own. Falling stacks resolve top-down and
can cascade (knock loose whatever they land on).

**What can kill a fish** (fish only - inert objects are never "alive" and
can't die; a dead fish itself becomes an inert, falling corpse):

1. **Crushed by something landing on you.** If a pushed or falling object
   comes to rest directly on a fish's back, and that fish is the *sole*
   support holding it up (no wall anywhere else in its support chain), the
   fish is crushed.
2. **A falling object lands on you.** If something actively falling ends
   up directly above a fish (possibly through a stack of inert objects,
   but not through another fish), and there's no wall anywhere in that
   falling object's own support chain, the fish is crushed on impact.
3. **Sustained overweight.** Independent of any movement: if an object
   heavier than a fish's power is resting on that fish (directly or
   through a stack), and nothing else adequate (a wall, or another fish
   strong enough) is also holding it up, the fish is crushed by the
   ongoing weight - this is checked every single round, not just after a
   move or fall.

Once dead, a fish stays dead - there's no reviving it.

**Level end state.** Both fish in `airplane` have `escapeGoal` (set by
`addFishAnim()` in Lua): a fish with this goal automatically walks toward
and out through the room's border the moment it has a clear path to one -
no extra input needed once it reaches the edge.
- **Solved:** every model's goal is satisfied *and* nothing is still
  moving/falling. For `airplane` that means both fish have gone out of the
  room alive. Items have no goal at all (`noGoal`) and never block a win.
- **Failed:** the instant any `escapeGoal`/`aliveGoal` fish dies, that
  goal can never be satisfied again (death is permanent) - this POC
  detects that (`Room.isSolvable()` flips false) and shows a "press R to
  restart" banner. The original pauses on a countdown and auto-restarts
  the room instead; this POC just reruns Lua + rebuilds the engine from
  scratch on `R`.

## Open for next time

- `Controls::useStroke`'s discrete-keypress path and the shared-arrow/
  active-fish-switch scheme aren't ported - only the always-on
  per-fish-dedicated-keys polling path is. Revisit if a level ever needs
  more than 2 controllable units.
- No animation means moves "snap" a full cell per round rather than
  sliding - fine for proving the rules, not how the shipped port should
  feel.
- `output_*` border items (used by other levels, e.g. `windoze`) aren't
  implemented in `ModelFactory`/`Rules.touchSpec` - needed before porting
  any level that uses them.
- Round pacing is a flat timer; the original paces by animation-phase
  count (faster after sustained movement, slower on turns) - worth
  revisiting once real animation exists.
