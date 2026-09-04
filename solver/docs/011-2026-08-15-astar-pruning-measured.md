# 011 — What the A* search actually throws away

2026-08-15

A measurement pass over the plain A\* loop, and one negative result: a
partial-order reduction that is correct, fires on nearly half of all successors,
and buys almost nothing. Recording why, because the reason points at what would
work.

## Where states are discarded today

Five places, all in the successor loop, and all of them **after** the move has
been simulated — nothing is rejected before paying the physics:

| # | test | catches |
| --- | --- | --- |
| 1 | `ApplyMove` returned false | the move was refused |
| 2 | `!IsSolvable()` | a fish died |
| 3 | `CannotMove()` | no fish will ever move again |
| 4 | `h >= Unreachable` | something that must leave the room no longer can |
| 5 | `!TryImprove(...)` | already seen, at this cost or better |

## What each one is worth

Counters on the loop, 9 levels, as a fraction of every move tried:

| level | tried | refused | dead | stuck | unreach | **dup** | new |
| --- | --- | --- | --- | --- | --- | --- | --- |
| start | 1.74 M | 14.1% | 0.0% | 0.0% | 0.0% | **68.5%** | 17.4% |
| noground | 752 k | 20.6% | 1.5% | 0.0% | 0.0% | 64.0% | 13.8% |
| wc | 1.58 M | 14.8% | 1.3% | 0.0% | 0.0% | 71.3% | 12.6% |
| submarine | 1.30 M | 24.1% | 0.6% | 0.0% | 0.0% | 61.9% | 13.4% |
| snowman | 25.2 M | 13.8% | 2.1% | 0.0% | 0.0% | 68.2% | 15.9% |
| floppy | 12.7 M | 3.4% | 0.5% | 0.0% | 0.0% | **79.9%** | 16.2% |
| grail | 6.13 M | 20.7% | 2.0% | 0.0% | 0.0% | 61.8% | 15.5% |

**The dead-end pruning is not what makes this work.** `CannotMove()` fired
**zero** times and the unreachable test fired **zero** times — on every level,
including four `goal_out` levels chosen precisely because there a fish can be out
while the room is unsolved. `IsSolvable()` catches 0.5-2.1%.

`Solver`'s own class comment lists dead-end pruning as one of "the three things
that make it tractable". On this evidence it is not one of them. The rules cost
single-digit nanoseconds so there is no reason to remove them, but tuning *them*
is not where a win lives.

**Duplicate detection is doing essentially all of it: 62-80%.**

## Where the time goes

About 6 µs per expansion, and it is all physics:

| operation | cost | calls per expansion |
| --- | --- | --- |
| `RestoreMobile` | 170-290 ns | **8** |
| `ApplyMove` | 150-600 ns | **8** |
| `WriteStateKey` | ~15 ns | ~7 |
| `IsSolvable` / `CannotMove` / `IsSolved` | 0-7 ns | ~7 |
| heuristic | ~60 ns | ~7 |

So `8 x (restore + apply)` is ~93% of the run. The A\* machinery itself — bucket
queue, node array, key hashing — is not the bottleneck, and micro-optimising it
would be aimed at the wrong 7%.

Nominal branching is 8; **effective branching is 1.3-1.4 new states per
expansion**. Around 5.5 of every 8 successors are states already held.

## The partial-order reduction, and why it failed

That redundancy has an obvious shape: while the two fish move independently,
every interleaving of A's moves and B's moves reaches the same state. So suppress
the orderings that are redundant.

Implemented as `SolveOptions.PartialOrder`. A move is **simple** when the only
thing in the room that changed is the fish that made it — nothing pushed, nothing
fallen, nobody dead or gone — which is detected by diffing the parent's state key
against the child's, needing no cooperation from the physics. Two simple moves by
different fish whose occupied-cell boxes are disjoint cannot affect each other, so
they commute, and only the ordering matching the canonical symbol rank is kept.

It is sound: take any optimal solution, and while some adjacent pair would be
suppressed, swap it. The swap is legal, preserves the final state and the length,
and strictly reduces rank inversions, which cannot go below zero — so the
rewriting terminates at an equally short solution that survives. (Only
*independent* pairs are ever swapped, so successive moves of the same fish keep
their order. It is not the claim that the sequence can be sorted by rank, which
would be false.)

**And it does essentially nothing:**

| level | moves off/on | expanded | stored |
| --- | --- | --- | --- |
| start | 54 / 54 | 1.00x | 1.04x |
| noground | 44 / 44 | 1.00x | 1.02x |
| wc | 100 / 100 | 1.04x | 1.04x |
| submarine | 83 / 83 | 1.00x | 1.02x |
| cannons | 39 / 39 | 1.01x | 1.09x |
| wreck | 91 / 91 | 1.03x | 1.06x |
| snowman | f60 / f61 | 0.97x | 0.95x |
| grail | f71 / f72 | 0.96x | 0.96x |
| floppy | f57 / f57 | 0.97x | 0.97x |

(These are the deterministic counts, which are load-independent. The timings this
section originally carried came from wall clock on a loaded machine and were
dropped - see the re-measurement below, which puts it at ~1.02x CPU.)

Every level A\* finishes comes out at the identical length, and every answer
replays — so the reduction is correct. It is also *slightly negative* on the three
levels that ran out of time, which are the ones that matter.

**It is not failing to fire.** Measured: **2.1-3.25 suppressions per expansion**,
roughly 45% of all successors.

The reason it buys nothing is the important part. **Pairwise move pruning removes
redundant edges, not states.** In the diamond A0B0 → {A1B0, A0B1} → A1B1, the
reduction drops one of the two edges into A1B1 — but the transposition table was
already collapsing that, and the interior states A1B0 and A0B1 are each still
reached by an unsuppressed move from A0B0, so both are still stored and still
expanded. The state count barely moves; only work the duplicate check was doing
anyway gets done slightly earlier, and the classification is not free.

## Sleep sets: the stronger version, also a loss

The obvious objection to the above is that it only consults the *previous* move.
Sleep sets are the real formulation: a state carries a set of moves it must not
try, computed from the siblings already explored at its parent, and — this is the
point — **a sleeping move is skipped before `ApplyMove` runs**, which is the 93%
that the pairwise version still paid.

Implemented, replacing the pairwise rule. `Node.Sleep` is one byte, a bitmask over
the ≤ 8 symbols. `SleepSetFor` gives a child every move already explored from its
parent that is independent of the move producing that child. Refused and dead-end
moves are left out — neither leaves a branch for the commutation argument to lean
on — while duplicates are included, since that state does exist in the search.

**The correctness fix this needs is what sinks it.** A state expanded under a
sleep set is only *partially* expanded, so reaching it again by a path that
forbids less is not a duplicate even at the same cost: it has to be expanded
again, or those successors are lost. `StateSet` therefore stores a sleep set per
state and intersects it on every accepted arrival, which bounds re-expansions at
one per bit. And that churn costs more than the skipping saves:

| level | expanded | applied | stored |
| --- | --- | --- | --- |
| start | **1.15x more** | 1.09x fewer | 1.01x |
| noground | **1.23x more** | 1.05x more | 1.00x |
| wc | **1.25x more** | 1.02x more | 1.00x |
| submarine | **1.20x more** | 1.07x more | 1.00x |
| cannons | **1.32x more** | 1.11x more | 1.02x |
| wreck | **1.33x more** | 1.15x more | 1.00x |

Stored states are **flat** and expansions rise 15-33%. Both are deterministic, so
that much is solid; the timing is in the re-measurement below (0.80-0.96x CPU,
i.e. consistently slower). Every optimum is preserved and every answer replays, so
it is correct; it simply does not pay.

**A warning about reading the time-limited runs.** On `snowman`, `grail` and
`floppy` the reduced run stores 1.22-1.28x *fewer* states, which looks like the
win finally appearing. It is not: those runs are capped at 120 s, and the reduced
one also got *less far* (`snowman` drained to f57 against f60). Fewer states at a
lower bound means less progress, not a smaller space. Only levels that run to a
solution compare honestly, which is why the table above is solved levels only.

Kept in the tree behind `PartialOrder`, **off by default**, in the same spirit as
`MacroSolver.UseReachabilityUnblocking` (docs/008): measured, documented,
disabled. `ffsolve solve --partial-order` turns it on.
`SolverTests.PartialOrderReductionKeepsTheOptimum` pins the four known optima
with it enabled, because the failure mode is silent — a broken reduction still
returns a solution and still calls it optimal, just longer.

## Undoing the move instead of rebuilding the state: 1.03-1.43x

The lever that did pay. An expansion tries every move symbol from one state, so
all but the first need the previous move undone - and that was done by
`RestoreMobile(key)`, which re-derives **every** mobile model whether it moved or
not: unmask, reset to declared, decode 12 fields from the key, re-mask.

A move touches one or two models. So `Room.SaveMobile` now copies the mobile
`ModelState` structs once per expansion, and a new `RestoreMobile(snapshot)`
copies them straight back, re-stamping the grid **only for models whose position
actually changed**. Copying the struct also restores fields the key does not carry
at all (`TouchDir`, `OutDepth`, the `ReadyTo*` flags), so unlike a key rebuild
there is no question of what a settled state leaves behind.

| level | mobile | key k/s | undo k/s | speedup | |
| --- | --- | --- | --- | --- | --- |
| start | 7 | 108.0 | 154.8 | **1.43x** | solved 54, searches identical |
| noground | 7 | 26.3 | 30.7 | 1.17x | solved 44, identical |
| wc | 5 | 43.2 | 58.1 | **1.35x** | solved 100, identical |
| submarine | 8 | 63.9 | 65.6 | 1.03x | solved 83, identical |
| cannons | 18 | 21.5 | 28.5 | **1.33x** | solved 39, identical |
| wreck | 7 | 38.1 | 45.3 | 1.19x | time-limited |
| gems | 90 | 4.8 | 5.4 | 1.12x | time-limited |
| steel | 22 | 23.3 | 27.5 | 1.18x | time-limited |
| duckie | 10 | 33.0 | 37.3 | 1.13x | time-limited |
| library | 10 | 31.5 | 38.0 | **1.21x** | time-limited |

Every level gains, including the hard ones that are the point of the exercise.

**The searches are bit-identical**, which is the correctness argument: on every
level that solves, `expanded`, `stored` and the move string all match the old path
exactly. An undo that got the grid wrong could not reproduce the same counts.

### How it had to be measured

The first attempt compared wall clock against numbers taken earlier and concluded
the undo was *2-3x slower*. It was not: this machine's wall clock swings that far
between identical runs on its own - `start` measured 1.3 s, 1.66 s and 3.21 s for
the same work - and a microbenchmark with min-of-7 rounds still produced negative
deltas, i.e. noise larger than the signal.

Two changes made it measurable. **CPU time consumed by the process** instead of
elapsed time, which other processes cannot inflate; and **expansions per
CPU-second** instead of total time, because a time-limited run burns its whole
budget either way, so elapsed time cannot show a difference on exactly the levels
that matter. Both variants alternate within one process so any drift hits them
equally.

This is the docs/006 contaminated-timing lesson again, and it very nearly produced
a confident wrong answer in the opposite direction from last time.

## Re-measuring both reductions properly

Both verdicts above were originally reached with wall clock on a loaded machine,
which is exactly the instrument the undo work proved untrustworthy. So both were
re-measured against total CPU time. **Both verdicts hold** - but getting there took
three attempts, and the two wrong ones were wrong in *opposite* directions.

| level | pairwise | sleep sets | expansions off / pair / sleep |
| --- | --- | --- | --- |
| start | 1.02x | 0.96x | 217,622 / 217,605 / 250,135 |
| noground | 0.98x | 0.86x | 94,036 / 94,056 / 115,398 |
| wc | 1.04x | 0.89x | 197,164 / 189,654 / 245,828 |
| submarine | 1.00x | 0.86x | 162,860 / 162,862 / 195,323 |
| cannons | 1.05x | 0.83x | 215,117 / 212,027 / 282,414 |
| wreck | 1.05x | 0.80x | 756,029 / 731,566 / 1,001,731 |

Pairwise is **~1.02x** - within noise, as first concluded. Sleep sets are
**0.80-0.96x**, i.e. consistently *slower*, which the deterministic expansion
counts explain on their own: 15-33% more expansions, because re-expanding a state
whose sleep set shrank costs more than the skipped moves save.

### Three ways to measure this wrong

- **Throughput (expansions per CPU-second) is invalid here.** It scored sleep sets
  at 1.03-1.21x *better*, because they do more, cheaper expansions - the metric
  rewards exactly the churn that makes them slower. Throughput is only meaningful
  when the search shape is unchanged, which is why it was the right metric for the
  undo (bit-identical searches) and the wrong one for a reduction.
- **Order bias.** Running Off, then Pairwise, then Sleep every repetition makes Off
  always inherit the previous repetition's garbage. That alone scored pairwise at
  **1.07-2.01x**, a result with no mechanism behind it - the expansion counts were
  identical to Off, so there was nothing for the saving to come from. Rotating the
  order and forcing a full GC between runs collapsed it to 1.02x, and dropped every
  absolute time by ~2.5x (`start` 2.19 s to 0.86 s) - that run had been measuring
  GC state.
- **Between-process comparison.** Throughput for one fixed configuration shifts
  ~2x between process runs while varying only 1.04-1.17x *within* one. Any
  comparison has to be interleaved inside a single process.

**When a result has no mechanism, distrust the measurement before believing it.**
Both bogus numbers looked plausible in isolation; what gave them away was that
neither had anything to spend its saving on.

## Gravity: the O(n²) that mostly is not

The last lever, and the one the code itself had been advertising since docs/001:
`ComputeFall` is a fixpoint that rescans every model until a whole pass changes
nothing, flagged in its own comment as "O(n²) on the item-heavy levels" and the
obvious thing to make incremental.

Support only ever travels upwards, so it is really a graph search: when a model
becomes stoned, the only models whose answer can change are those resting directly
**on** it. Built that - one seeding pass, then a worklist.

**It is slower on all but one level.**

| level | models | passes the fixpoint needs | worklist |
| --- | --- | --- | --- |
| start | 8 | 2.00 | 0.88x |
| wc | 7 | 2.00 | 0.88x |
| cannons | 19 | 2.03 | 0.86x |
| alibaba | 45 | 3.39 | 0.88x |
| columns | 54 | 3.00 | 0.75x |
| experiments | 79 | 7.00 | 0.98x |
| **gems** | 112 | 8.99 | **1.24x** |

**The premise was wrong.** `StoneModel` short-circuits on an already-stoned model,
so once the first pass has stoned nearly everything, every later pass is n cheap
boolean checks - and the measured pass count is **exactly 2** on the rooms that are
not deep stacks. It is O(2n) there, not O(n²). Only `gems` and `experiments` have
support chains deep enough for the fixpoint to grind, and the worklist needs 7
passes just to break even.

So it helps one level out of eighty, at 12-25% off everything else, and gating it
would mean fitting a threshold to a single data point. Reverted; the fixpoint
stands, with its comment corrected to say what was measured rather than what it
looks like.

One thing worth keeping from the attempt: the first version was **twice** as slow,
because the room's own wall is a single model covering hundreds of cells, and
propagating "what rests on this" from it walked every one of them. Models resting
on something fixed outright are found by the seeding pass whatever the order, so
propagating from walls, fish and corpses can never discover anything.

### Measuring on unsolved levels

A time limit cannot compare two variants on a level neither solves - both burn the
budget, and the counts differ only because they stopped at different points, which
also makes any "are the searches identical?" check fire falsely. A **fixed node
budget** fixes both: each variant expands exactly the same nodes, so the counts
must match exactly (correctness) and the CPU time compares directly (speed). Every
row above is 150,000 expansions with identical counts confirmed.

## What follows from this

Two partial-order variants have now been measured and neither pays. The redundancy
is real - 62-80% of everything generated is a state already held - but the
transposition table is already catching it cheaply, and every scheme that tries to
catch it *earlier* has cost more than the hash lookup it replaces.

Nor does the gravity fixpoint, whose cost turned out to be misdescribed. Of the
four things tried, **only the undo paid**.

What is left:

- **Deadlock detection** for items with *no* goal is entirely absent: today only
  goal-bearing models get a reachability test, so an item shoved into a corner
  where it permanently seals a corridor is never recognised. Unlike the orderings
  work, this would remove states the transposition table cannot.
- **The heuristic**, which nothing here has touched. It is the one lever that
  shrinks the tree rather than the cost of walking it. `ExitHeuristic` only counts
  distance to the exit and knows nothing about items that have to be moved out of
  the way first, so it is weakest on exactly the levels still open.
