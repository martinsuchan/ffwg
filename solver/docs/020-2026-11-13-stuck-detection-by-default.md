# 020 — Stuck-item detection: on by default, parallel, fast

Plain A* spends most of its effort on some levels in states that can no longer be
solved. This entry measures how much, makes the stuck-item rule (docs/012, 014)
cheap enough to leave on everywhere, runs it in the parallel search, and pins the
benefit with tests.

## How much of the search is dead

Every state plain A* stored, checked afterwards with the rule:

| level | stored | provably dead |
|---|---|---|
| wc | 199 k | **61.4%** |
| society (1.5 M expansions) | 2.2 M | **63.1%** |
| wreck | 885 k | 3.7% |
| library (1.5 M) | 1.7 M | 0.3% |
| start, cannons, noground | | 0% |

The rule existed but was opt-in, ran serially only, and docs/014 had measured
a small net loss.

## What changed

- **`StuckDetector`** (new): a shared, thread-safe front end to `StuckAnalysis`
  for the serial and parallel loops.
  - Verdicts are cached per item layout in a `ConcurrentDictionary`, looked up
    without allocating (alternate lookup on `ReadOnlySpan<byte>`).
  - Each thread has a two-entry front for the cache.
  - A verdict is a bitset of the anchors from which each fish can still leave.
  - A child made by a fast-path move (`Room.LastMoveWasFast`: a turn or an inert
    step) reuses its parent's verdict, which costs one bit test.
- **New half of the rule: a goal_out item that can never move again is dead.**
  The walled-in-fish half never fires on final-level-style rooms, where the
  fish are goal_alive.
- **Parallel.** Phase 1 marks condemned children and phase 2 drops them in
  serial order, so counts stay identical to `Threads = 1`.
  - Phase 1 now hands out slots in runs of 16 instead of interleaving them.
    Siblings stay on one worker, which keeps the verdict front warm; this also
    helped the search with the rule off.
- **Analysis 3-5x cheaper** (per layout: party1 138 → 31 µs, chest 70 → 17,
  cannons ~300 → 17). Each optimisation either gives the same answer or only
  skips work whose answer is already known:
  - **Pushers placed on demand.** A pusher's placement is tested on demand
    instead of building a full fits-map for each model on each pass.
  - **Direction first.** The push chain is checked once per direction rather
    than once per pusher and direction.
  - **Faces only.** Only the pusher's front face and the target's back face can
    make contact; any other pairing overlaps the target, which `Fits` rejects
    anyway.
  - **Fixed models drawn once.** The cells of models that can never move (the
    room's walls) are computed once per level.
  - **Fish reach from precomputed fits.** Where each fish fits among the walls,
    and which of those anchors touch an edge, is computed once per level. An
    analysis only knocks out the anchors that overlap newly stuck cells.
- **Probation** (`SolveOptions.StuckProbation`, default 2^20 expansions). A rule
  that has dropped nothing by then is switched off.
  - The decision uses counts only, at the same expansion serially and in
    parallel.
  - The first dead end can come late: library at expansion 199,356, noground
    72,372, steel 78,509, wreck 67,003. That is why the default is about a
    million, not 2^18.
- **Default on.** CLI `--no-stuck` turns it off. `bench --suite` gains wc, a
  bound column f, a wall-clock ratio and `--no-stuck`.

## Bugs found on the way

1. **A verdict depended on where the living fish were.** `StuckAnalysis.Mobile`
   counted a fish as a support, but the layout key leaves fish positions out.
   - Effect: the parallel search stored 2,245,056 states on library, the serial
     one 2,245,129.
   - Fix: with `fishAnywhere`, living fish are no longer supports. This is
     sound:
     - an item held up only by a fish then rests on nothing, which counts as
       mobile;
     - an item that also rests on something immobile does not fall when the
       fish leaves;
     - a fish lifting the item from below is a push, already tried from every
       placement.
2. **A dead fish's position was not in the layout key.** To the analysis a dead
   fish is an item. Found by the fuzzer's new detector-vs-direct check.
   - The solver never asks about such states, since `IsSolvable` drops them
     first.
   - The key now carries a dead fish's position anyway, so the verdict is a
     function of the key in every state.
3. **PO-PAIRWISE (pre-existing, not fixed).**
   - `--partial-order pairwise` loses every solution in fuzz room seed 77 #1283.
     It gives the same answer at 576c966, before this work.
   - The mode is off by default and docs/011 measured it a net loss.
   - Pinned as micro-level `pairwise-unsound` (KnownBug) and attributed by
     mechanism in the fuzzer: only when plain A* passes the same room.
   - The macro and item searches, knowingly incomplete, also find nothing there.

## Tests

- **Micro-levels:**
  - `goal-out-wedge` (34) exercises the goal_out half alone. The fish is
    goal_alive. Expansions 117 → 59.
  - `two-fish-wedge` (33) is a dead subtree multiplied by a second fish.
    Expansions 520 → 361.
  - `drop-trap-well` is unchanged (67 → 40).
  - `pairwise-unsound` (KnownBug).
- **`StuckDetectionTests`:**
  - The rule cuts the search on the three wedge levels and keeps the answer.
  - The goal_out rule on its own.
  - Verdicts do not depend on query order.
  - Probation switches the rule off on start (counts then equal the search
    without it) but keeps it on wc.
  - On every reachable state of every micro-level, the detector matches the rule
    computed directly with `StuckAnalysis`.
- **`ParallelSolverTests`:** stuck, no-stuck, and a 64-expansion probation that
  ends mid-batch. Counts identical to serial in all three.
- **Fuzzer:** a new `stuck-detector` check (the direct-vs-fast comparison above)
  on every room. Deep runs on seeds 77, 4242 and 9001, 20,000 rooms each, found
  no unexplained failures.
- **Matrix:** column `astar-stuck` became `astar-nostuck`, since `astar` now
  includes the rule.
- **Baseline changes** (all improvements):
  - wc 197,003 → 76,266 expansions;
  - submarine 162,853 → 104,435;
  - wreck 752,746 → 727,311;
  - noground 93,978 → 93,960.

## Measured

Serial, `bench --suite` (12 cores, .NET 11 JIT, best of 3), against `--no-stuck`:

| level | time | note |
|---|---|---|
| start | 1.00x | |
| wc | **2.66x faster** | 197,003 → 76,266 expansions |
| cannons | 0.98x | |
| wreck | 1.01x | 752,746 → 727,311 expansions |
| society (1.5 M) | 1.12x | same expansions, bound f 74 → **78** |
| library (2 M) | 0.96x | |

12 threads: wc 1.5x faster, society 0.97x (f 74 → 78), the rest within ±5%.
Short parallel runs vary ±15%; cannons over 7 repetitions: 0.20 s vs 0.19 s.

3 M expansions, serial, against `--no-stuck`:

- Nothing dropped, so the probation switches the rule off:
  - party1 +10%;
  - chest +3%;
  - corridor +3%;
  - music +1.5%.
- Dead ends present:
  - library: bound f 92 → 94 (+4% time);
  - steel: 126 → 130 (+2%);
  - pearls: 66 → 70 (+13%);
  - duckie: 75 → 79 (+5%).

**Society, 110 s, 12 threads:** bound f 92 → **96**. f = 90 is reached at 30 s
instead of 90 s, at the same rate of about 620 k expansions/s.

A whole-corpus batch at 10 s per level showed bounds +5 / −4 / 69 unchanged. Two
identical no-stuck batches already differ on 26 levels by up to 9, so that run is
within noise; the fixed-count runs above are the evidence.

## Open

- **The heuristic is the next lever.** The detector already knows, per layout,
  which cells are wall for good. Fish distances measured over them, instead of
  over the opening walls, would be an admissible and stronger bound
  (review item 2).
- PO-PAIRWISE: fix it or remove the mode. The review lists partial order among
  the dead ends.
- The macro and item searches fail `pairwise-unsound` too. Not investigated.
