# 005 — Macro-moves: built, correct, and a dead end

2026-08-15

Implemented the macro-move search from docs/001 §6 stage C and docs/004's "open"
list. It works and is faithful — it reproduces four known optima exactly — and it
is **slower than the plain per-symbol A\* on every level tried**, including the
ones we were hoping it would crack.

The reasoning that motivated it was wrong in a way worth writing down, because it
is the standard argument for this technique and it does not transfer to this game.

## What was built

`MacroSolver` expands a state into *decisions* rather than key presses: an inert
route (travel) followed by one thing that changes the world.

**`InertRouter`** finds where a fish can swim without disturbing anything. A move
is inert when it changes nothing except that fish's own position and facing, and
that is exactly when the destination cells are empty (nothing pushed) and the
fish is holding nothing up (nothing falls). Both are grid tests, so routing needs
no simulation at all — and because an inert route leaves the world untouched, the
state it reaches is just *the parent's key with one fish's entry rewritten*. A
whole route costs zero physics calls; only the action at the end touches the
engine.

Two things had to be right and were not, at first:

- **Exits are not waypoints.** A fish does not leave by stepping out of bounds —
  it leaves the moment it stands against the border with a clear way out, and
  `Rules::actionOut()` carries it off during the settle, costing no move. The
  first version routed straight through exit squares, so reconstructed solutions
  had the big fish making moves after it had already left the room (`submarine`
  failed on move 2). Border placements are now actions, never traversed.
- **Parking has to be generated.** An inert run can also end with a fish simply
  waiting somewhere, because a fish is *solid*: it blocks pushes and catches
  falling items. Restricting parking to "touching something" got `wc` to 106
  against a true optimum of 100 — a real completeness leak, caught by the
  known-optima regression test. Widening it to line-of-sight in all four
  directions (anything that could fall onto it or be pushed into it) fixed `wc`
  to exactly 100.

## The measurements

Correctness first — with the wider parking set it matches every known optimum it
can reach: `start` 54, `noground` 44, `submarine` 83, `wc` 100, all replaying
cleanly. So the decomposition is sound.

Then the cost, on `start`:

| | expanded | stored | time |
| --- | --- | --- | --- |
| plain A\* | 217,622 | 302,388 | **1.3 s** |
| macro | 225,367 | 1,282,686 | 100.6 s |

And on the two levels plain A\* cannot finish, comparing the lower bound reached
in 90 s (higher is better):

| level | plain | macro |
| --- | --- | --- |
| gems (opt 59) | f=**57** | f=55 |
| snowman (opt 66) | f=**57** | f=36 |

`cannons` and `wreck`, which plain A\* solves in 2.4 s and 4.4 s, now time out
entirely at 120 s. There is no case where it wins.

## Why it cannot work here

The estimate in docs/004 was "branching 8 at depth 54 becomes branching 20 at
depth 12". That is tree-search arithmetic, and **A\* with a transposition table
does not pay for paths, it pays for states.** Collapsing a route into one edge
does not remove a single state from the space — the fish still has to be
somewhere, and every one of those somewheres is still a distinct state.

Look at the `start` numbers again: macro expanded essentially the same count
(225 k vs 218 k) while *storing four times as many*. Each expansion now generates
hundreds of children (a park child per reachable parking placement) instead of at
most eight, most of which are never expanded. All the extra work, none of the
reduction.

**Why Sokoban is different.** The win there is not path collapsing either — it is
*player-position normalisation*. Two Sokoban states with the same box layout and
the player anywhere in the same reachable region are the **same state**, because
walking is free. That genuinely collapses the space.

That normalisation is unavailable here for a specific reason: **walking costs
moves.** The pedometer counts every swim stroke, and it is exactly what we are
minimising, so two states differing only in where a fish is parked have different
future costs and cannot be merged without losing optimality. The fish-position
dimension of the state space is irreducible for an optimal search.

So macro-moves in a game where travel is free are a state-space reduction; in a
game where travel is counted they are only a path reduction, and a path reduction
buys nothing once transpositions are being tracked.

## What this leaves

Not deleted. `MacroSolver` is correct, tested against four known optima, and kept
behind `ffsolve solve <level> --macro`, because the one place its logic does
apply is a search that has given up on exact costs — a beam or heavily weighted
search could normalise fish positions the way Sokoban does and get the real
collapse. That is a different algorithm, not a tweak to this one.

Also landed, and worth keeping regardless:

- **The goal test moved from generation to pop** (docs/004's known gap), which is
  what makes "provably shortest" true rather than merely likely. Two extra
  expansions on `start`.
- **Batch solving**: `solve --all` / `--levels-list a,b,c --parallel N`,
  in-process. Levels share nothing, so no locking. Default 4, not the core count,
  because memory binds first (`stairs` peaks near 1.6 GB). Six fast levels at
  `--parallel 6`: 5.6 s against ~11 s serial.
- **One shared `ExitHeuristic`**, so the two searches cannot drift apart.
- **`SolverTests`** pinning four known optima, which is the test that caught the
  `wc` completeness leak.

## Where the leverage actually is

The `snowman` line is the whole story: the optimum is 66, and 90 s of plain A\*
proves only f=57. The search is not slow, it is *under-informed* — the heuristic
counts fish travel and says nothing about the item rearrangement that dominates
these solutions.

Ranked by expected value now:

1. **A heuristic that understands items.** Some lower bound on the pushes needed
   to get each goal item where it has to go, admissibly combined with fish
   travel. This is the only thing that changes what is reachable.
2. **Suboptimal search** — weighted A\* is implemented but unexercised; beam
   search with fish-position normalisation is where the macro machinery could
   still earn its place. For the 12 gap levels we only need to beat the bundled
   solution, not prove optimality.
3. **Constant factors** — an undo journal would remove seven of the eight state
   restores per expansion; node and key packing would push back the memory wall.
