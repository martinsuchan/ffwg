# 004 — A real solver, and what a level reducer can and cannot do

2026-08-14

Adds a from-scratch solver: A\* over settled states, one move symbol per edge,
with an admissible heuristic, dead-end pruning and a level reduction. It solves
several levels **provably shortest**, matching the hall of fame.

Also records a negative result worth more than the positive one: a *complete*
level reducer is not a realistic goal, and the analysis that tries to approximate
one was wrong three times before a corpus test caught it.

## Results — the first house ("Rybí domeček") plus corals

`ffsolve solve <level> --seconds 120`, single core, A\* at weight 1.0:

**Eight levels solved to proven optimality**, each matching the hall-of-fame
record exactly:

| level     | record | solver | time |
| --------- | ------ | ------ | ---- |
| cannons   | 39     | **39** | 2.4 s |
| noground  | 44     | **44** | 1.1 s |
| start     | 54     | **54** | 1.3 s |
| submarine | 83     | **83** | 0.9 s |
| wreck     | 91     | **91** | 4.4 s |
| wc        | 100    | **100** | 1.1 s |
| library   | 111    | **111** | 58 s (8.8 M states) |
| stairs    | 163    | **163** | 109 s (23.6 M, `--nodes 60000000`) |

Not finished inside 60 s, with the lower bound the search reached:

| level  | record | bound | | level    | record | bound |
| ------ | ------ | ----- |-| -------- | ------ | ----- |
| gems   | 59     | f=57  | | duckie   | 98     | f=79  |
| snowman| 66     | f=52  | | society  | 110    | f=80  |
| bathysc| 82     | f=78  | | tetris   | 134    | f=73  |
| bathroom| 90    | f=66  | | puzzle   | 142    | f=64  |
| corals | 252    | f=116 | | computer | 156    | f=84  |
| cellar | 375    | f=281 | | reactor  | 163    | f=93  |
| broom  | 193    | f=104 | | cave     | 186    | f=114 |
| reef   | 169    | f=74  | | briefcase| 225    | (219, unplayable - below) |

The bound is diagnostic. Where it is close to the record (`gems` 57 vs 59,
`bathyscaph` 78 vs 82) the level is a budget away. Where it is far (`snowman` 52
vs 66, `cave` 114 vs 186) the heuristic is the problem, not the throughput -
which is the macro-move argument in one number.

"Proven optimality" is the strong claim, not "matched the record": A* drains
buckets in ascending f = g + h with an h that never overestimates, so when a
finished room is popped from bucket N, everything cheaper has already been
expanded and no shorter solution can exist. It rests on three things - the
heuristic being admissible, the engine matching the game (80/80 reference
solutions replay), and the reduction not having wrongly frozen anything. That all
eight independently match records set by human players over two decades is
corroboration in both directions: it supports the model, and it is evidence those
records are themselves optimal.

`stairs` needed only a bigger budget - it hit the default 20 M node ceiling at
f=159 and finished at 22.6 M once raised.

**`corals` is the instructive one.** It looks like the easiest target in the game
— 28×25, two fish and five items — and it is not. 19.4 M states in 120 s only
pushed the bound to f=116 against a 242-move optimum. The model count is
misleading: the fish are large (4×2 and 3×1), the room is open, and the
heuristic is weak precisely where it matters, because summed fish-distance says
nothing about the item rearrangement that dominates the solution. docs/003
already showed windowing cannot reach its slack either.

### briefcase does not count

The solver returns 219 moves for `briefcase`, six under the recorded 225, and it
replays cleanly. It is nevertheless **not a legitimate improvement**: briefcase
runs a scripted auto-play tutorial (docs/031) that drives the fish unattended and
saves at a fixed position, so a player cannot take an arbitrary route through it.
The physics port ignores level scripting by design (docs/014), which is correct
for replaying solutions and wrong for judging what is reachable here.

Consequence for the whole effort: **`briefcase` and `windoze` are the two levels
whose scripting touches play**, and neither can be compared on physics alone. The
solver should not be pointed at them. No solution file was kept.

## What was built

**`RelaxedDistance`** — per-model lower bound on moves-to-exit, by 0-1 BFS over a
walls-only room, indexed by (cell, facing) so turn costs count. Sound because
fish swim rather than climb: they need no support and items only obstruct, so
deleting every item can never lengthen a path. Item distances get free downward
edges, since gravity moves them at no cost in moves.

The bound combines as `max(sum over fish, max over goal_out items)`: one symbol
steps exactly one fish, so fish distances add, but a single push can shift a
whole chain and gravity is free, so item distances do not.

**Dead-end pruning** (`Solver.IsDeadEnd`) — three sound rules, and this is where
most of the pruning comes from:
- a goal has failed for good (`IsSolvable()` — a fish died);
- nothing can move any more (`CannotMove()` with the level unsolved);
- something that must leave the room no longer can — its relaxed distance is
  infinite, and since that relaxation has every item deleted, no arrangement can
  help. This is the "an item got dropped where it can never be recovered from"
  case the search would otherwise chase forever.

**Goal test on pop, not on generation** — this is what makes optimality a proof
rather than a likelihood. A state can have `h == 0` without being solved (both
fish where the walls-only relaxation says they could leave, with a real item
blocking); it sits in bucket `g` and generates a finished room at `g + 1`,
possibly before the genuinely optimal finish from another node in the same
bucket. Popping in `f` order cannot do that. Two details it needs: a finished
room is queued like any other state with `h = 0` (landing in bucket `g + 1`,
never an already-drained one, because `h` is consistent), and it must bypass
`IsDeadEnd` - a finished room has both fish out, so `CannotMove()` is true and
the dead-end rules would otherwise discard the answer. Cost measured on `start`:
two extra expansions out of 217,620.

**Batch solving** — `solve --all` / `--levels-list a,b,c` with `--parallel N`,
in-process. Levels share nothing (each `Solver` owns its room, table and nodes),
so it needs no locking beyond the console. The default is 4 rather than the core
count because **memory binds long before CPU does** - `stairs` peaked near 1.6 GB
and `library` ~590 MB. Six fast levels at `--parallel 6`: 5.6 s wall clock
against ~11 s serial. Parallelising a *single* level's search is a different
problem (shared frontier and visited set - HDA*) and would not help the levels
that are currently stuck, since those are heuristic-bound rather than
throughput-bound.

**State storage** — a settled, still-solvable state's key *is* the whole state, so
nodes store a key offset rather than a snapshot. `Room.RestoreMobile` rewrites
only the models that can differ instead of re-stamping the room shape, which
matters because a node expansion restores up to nine times.

## The level reducer: a complete one is not feasible

Deciding "can this model ever move?" is an existential reachability query over
the same state space the solver is searching — answering it exactly is as hard as
solving the level. So the goal can only be a cheap *sound over-approximation*:
never freeze something that can move; accept freezing less than ideal.

Getting even that right was harder than expected. Three successive versions were
unsound, each for a subtler reason:

1. **Wrong fixpoint direction.** Assuming everything moves and proving models
   immobile measures fish reachability with every item deleted, so the fish
   reaches the whole room and almost nothing freezes (31 of 1,619). The fixpoint
   has to *grow* the mobile set from nothing, which is justified by induction on
   which item moves first: whatever moves first was pushed with everything still
   at its starting position.
2. **Pushes propagate.** Treating a neighbouring item as a blocker declared two
   items leaning on each other both stuck, when in reality either push moves the
   whole chain (`Rules::moveDirBrute` recurses). Fixed by a static reading of
   `canMoveOthers`, carrying the chain's heaviest member so the fish's power is
   checked against it.
3. **The configuration changes.** The chain test read the initial layout, but by
   move 400 items have moved; something blocked at the start can be pushable
   later. Fixed by treating already-mobile models as absent.

After all three the analysis froze 1,055 of 1,619 — and the corpus test *still*
found counterexamples on most levels. So the contract changed:
`LevelReduction.Verified` runs the analysis, replays the level's recorded
solution against it, and falls back to the always-sound type-level reduction
wherever the evidence contradicts it. That leaves **134 frozen of 1,619** —
modest, but every one of them backed by evidence.

**Why this is safe to be imprecise about.** Freezing only affects the state key
and the heuristic. Every edge is a real `Room.ApplyMove` and the goal test is the
real `Room.IsSolved`, so a returned path is always a genuine legal solution;
every solution is replay-verified besides. An over-aggressive reduction costs
completeness or optimality — never correctness. That asymmetry is what makes a
heuristic reducer a reasonable thing to ship at all.

The lesson worth keeping: this class of analysis cannot be reasoned into
correctness, only tested into it. Without
`FrozenModelsNeverMoveDuringReferenceSolutions` — 80 levels, 32,031 moves of real
play — a reducer that silently broke optimality on most of the corpus would have
shipped, and the damage would have looked like "the solver is just slow".

## Open

- **A better heuristic is the real blocker.** Summed fish-distance ignores item
  rearrangement, which is what actually costs moves on `corals`, `reef` and
  `broom`. The macro-move reformulation (docs/001 §6 stage C) is still the
  principled answer.
- **Weighted A\*** (`--weight`) is implemented but not yet exercised; it should
  find non-optimal solutions on levels A\* cannot finish.
- **Macro-moves are the agreed next step.** Almost every symbol in a solution is
  a fish swimming through open water; the decisions are pushes, drops, and
  parking. Collapsing travel into a routing sub-problem takes `start` from
  branching 8 / depth 54 to roughly branching 20 / depth 12. The trap is that a
  fish is a solid platform, so a route step is only "free" when it is *inert*
  (changes nothing but that fish's own position - exactly testable against the
  state key), and parking positions still have to be generated as terminals or
  completeness leaks. The eight proven optima above are the regression test: if a
  macro version stops returning 54 for `start`, its terminal set is too narrow.
- **Memory, not CPU, binds first** — `library` stored 8.8 M states for a 111-move
  solution. Node records and the key arena both want packing.
- **Skip `briefcase` and `windoze`** in any batch solve.
- The reducer's three failures suggest a fourth is plausible; the corpus test is
  the guard, and it should stay in front of any future tightening.
