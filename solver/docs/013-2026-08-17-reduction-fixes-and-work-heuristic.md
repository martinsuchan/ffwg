# 013 - Fixing the reduction, and a heuristic that can see items

2026-08-17

Two things docs/012 identified, built: the live bugs in `LevelReduction`, and the term the
heuristic was missing. A third turned up on the way - `--weight` never worked at all.

## 1. `LevelReduction`

docs/012 found the analysis contradicted by a real solution on **7 of 80 levels**, with
`Verified` quietly discarding the whole reduction on each. Two distinct defects.

**A push is a chain, and the fish is not always the body that lands.** `CanPush` asked
whether the *fish's own shape*, stepped one cell, would land on the target, and the class
comment argued chains needed no special case: once the item in between is known mobile it
gets deleted, the fish reaches the vacated square, and pushes directly. That fails whenever
the shapes differ. In `magnet` the big fish shoves a seven-cell item whose own bottom cell
moves a heavy bar, from a square the fish can never occupy - right of the bar is solid and
the fish is two cells tall. `corals` is the same defect a level deeper (fish, then three
items in series), where the last item only becomes pushable 122 moves in, once other items
have moved next to it.

Fixed by letting **any model already known mobile** be a pusher. A fish's reach is
connectivity from where it is, as before; an item goes where it is shoved rather than where
it swims, so the honest bound on where one might stand is every placement that fits, and on
its strength, the strongest fish. `CanPush` also solves for the pushing anchor directly from
the target's cells instead of scanning the room for it, which more than pays for the extra
pushers.

**Declared positions mixed with settled ones.** Shapes and the wall grid were read from
`ModelDef.X/Y` while occupancy came from `Room.GetModel`. On a level that floats a model in
its file these are different rooms. `Verified` had it too, comparing each frozen model
against `ModelDef.X/Y` after every move: `society` declares item 11 at (7,2), the opening
settle drops it to (7,12), and that first fall was read as "a frozen model moved" - so the
analysis was thrown away on a level where it was right. The test pinning all this,
`FrozenModelsNeverMoveDuringReferenceSolutions`, asserted the same wrong thing.

| | frozen | contradicted by a real solution |
|---|---|---|
| before | 149 | **7** (hanoi, captain, map, city, pavement, magnet, warcraft) |
| after | 125 | **0** |

Fewer models frozen - the corrected analysis is properly more cautious - but consistent with
all 80 recorded solutions, so **`Verified` no longer falls back anywhere**. That is the point
of the exercise rather than a side effect: a fallback that fires in normal use is not a
safety net, it is a bug being papered over, and anything wanting to run this analysis on a
state reached mid-search has no reference solution to fall back on. New test
`UnverifiedAnalysisAgreesWithEveryReferenceSolution` pins the unguarded analysis directly.

Weight-1 searches are unchanged where the reduction was already sound: `start`, `wc`,
`submarine`, `noground` and `cannons` expand exactly the counts recorded in `results.csv`.

## 2. `--weight` was broken

Trying weighted A* as a baseline, `solve society --weight 2` returned **"exhausted: no
solution exists" after 350 expansions**, on a level with a bundled 110-move solution.

The open list is an array of buckets indexed by f, drained by a single forward sweep. That is
only valid while f cannot fall along an edge. At weight 1 it cannot - h is consistent, so f
is monotone. Above 1, an edge shifts f by `1 - weight*(drop in h)`, which is negative as soon
as weight exceeds 1: **f drops by one on every move that makes progress**. Those children
were pushed into buckets the sweep had already passed, cleared and trimmed, and were lost.

Now the loop pops the lowest-f node outright rather than sweeping: each bucket carries a
drain cursor, and a push below the cursor pulls it back. At weight 1 nothing can push below
the cursor, the cursor walks each bucket front to back exactly as the old index loop did, and
the search is bit-identical - the five levels above still expand their recorded counts, which
also keeps docs/011's measurements valid.

Two reporting fixes went with it. `deepestF` is now a running maximum rather than whatever
bucket was last touched, and it is only reported as a bound when f actually is one - at
weight 1 with no toll. It feeds the `bound` column, which is used to argue that a hall-of-fame
number is optimal, and an inflated priority is not a lower bound on anything.

**Weighted A\* still solves nothing new.** At weight 5, `stairs` and `snowman` fail in 20 M
expansions though plain A* solves both. That is not a bug, it is the diagnosis in section 3.

## 3. The missing term: a heuristic that can see items

`ExitHeuristic` measures the way out with every movable item deleted - which is what makes it
admissible, and on the levels that matter leaves nearly nothing to go on.

Measured along the reference solutions:

| level | L | h at start | mean h / true remaining | correlation with true remaining |
|---|---|---|---|---|
| map | 2127 | 8 | 0.01 | **0.96** |
| city | 485 | 69 | 0.33 | 0.81 |
| wc | 100 | 30 | 0.60 | 0.63 |
| stairs | 163 | 93 | 0.71 | 0.98 |
| cabin1 | 168 | 72 | 0.81 | 0.79 |

The correlation column is the surprise and it corrects the guess in docs/012. The estimate is
**not** uninformative in shape - it tracks the remaining cost closely all the way down,
0.96 even on `map`. What is wrong is its *size*. With h at 1% of the truth, `f = g + h` is
`g`, and A\* is breadth-first search with extra steps. That is also exactly why weighting
cannot help: multiplying every estimate by the same factor reorders nothing.

So the fix is not a better-shaped estimate, it is a bigger one. `WorkHeuristic` charges a toll
- a step into cells holding a movable item costs `1 + K` instead of 1, on the reasoning that
whatever is in the way generally has to be dealt with. It is measured backwards from the
exits, so one pass serves every fish position sharing an item layout, and it is cached on the
item half of the state key: items move on a small fraction of edges, fish on all of them.

| level | current | K=2 | K=5 | K=10 |
|---|---|---|---|---|
| start | 0.79 | 0.92 | **1.01** | 1.12 |
| wc | 0.60 | **0.98** | 1.47 | 2.27 |
| stairs | 0.71 | 0.81 | **0.95** | 1.16 |
| society | 0.81 | **0.95** | 1.13 | 1.43 |
| cabin1 | 0.81 | 0.92 | **1.05** | 1.26 |
| city | 0.33 | 0.49 | 0.72 | **1.10** |
| map | 0.01 | 0.11 | 0.11 | 0.12 |

**This is not admissible** - an item shoved along ahead of a fish costs no extra move, and the
toll charges for it anyway - so `--work` gives up the shortest-solution guarantee, reports
`Optimal = false`, and records no bound. The admissible estimate is still what proves shortest
and still what the dead-end test uses; the toll only orders the open list.

`map` barely moves, and its own row explains why: raising K past 2 does not change the
estimate, so the route the toll finds already avoids items entirely and the 2127 moves are
spent on something this model cannot see.

## Results

Corpus run at `--work 8`, 90 s and 12 M nodes per level, four at a time (stopped after 74 of
78 - the last four were the same story). Nine levels solved:

| level | found | best known | time |
|---|---|---|---|
| noground | 44 | 44 | 0 s |
| start | 56 | 54 | 0 s |
| wc | 100 | 100 | 1 s |
| submarine | 83 | 83 | 1 s |
| wreck | 109 | 91 | 1 s |
| **cabin1** | 174 | 168 | 8 s |
| library | 113 | 111 | 36 s |
| **duckie** | **108** | 98 | 36 s |
| cannons | 43 | 39 | 41 s |

**`duckie` had never been solved by anything**, in 902 s of A\* or otherwise. **`cabin1` had
only ever been solved through `cabin1-simple`**, a hand-edited copy with an item removed;
this is the real room. Levels A\* already handled come out far faster - `library` in 36 s
against 8 M expansions, `submarine` and `wc` in about a second.

The toll is a knob, and it trades reach against quality in the obvious direction. At
`--work 3` on a 16-level set, four of six answers are exactly optimal (`start` 54, `wc` 100,
`submarine` 83, `noground` 44) where `--work 8` overshot every one of them - but `cabin1`,
`duckie` and `library` no longer finish in the budget. Given room, the gentler setting is
better still: **`cabin1` at `--work 3` with 300 s returns 168, matching the known optimum**,
on the real level, in 6.3 M expansions.

It is emphatically **not a replacement** for the admissible search. `stairs`, `snowman`,
`bathyscaph` and `gems` all solve under plain A\* and do not solve under the toll - an
overestimate that sends the search down a wrong corridor has nothing to pull it back. The two
modes are complementary: A\* proves shortest where it can reach at all, the toll reaches
levels A\* cannot.

Recorded: `duckie` at 108, unproven, with its existing A\*-proven bound of 85 preserved -
which is the one honest way to hold "we have an answer, and we know it is at least 85".

## Follow-ups

- The dead-end detector from docs/012 is still unbuilt - sound, 32,031 states clean, but it
  needs caching before it is affordable, and it gives the heuristic nothing.
- `K` is a single hand-set constant. A toll derived from the item (how boxed in it is, how
  heavy) would be better than one number for a whole room.
- `map` and `propulsion` are untouched by any of this.
- Batch mode ignored `--no-record`; fixed while a batch was running.
