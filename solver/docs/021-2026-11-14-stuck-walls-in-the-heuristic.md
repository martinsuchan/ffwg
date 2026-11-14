# 021 — Stuck items as walls in the heuristic

Follow-up to docs/020. The stuck-item detector already knows, for each item
layout, which cells are wall for good. Until now it only used that to drop dead
states. This entry also uses it for the estimate.

## Why

The walls-only estimate (`ExitHeuristic`) covers 24-58% of the optimum at the
start of the six proven levels. Along the optimal path it is 46-82% of the
remaining cost.

Two ideas were measured first, before writing any code:

- **"The fish has to reach an item first."** An admissible bound: before the
  first push, a fish must swim to a spot where it touches an item, then go on to
  the exit. Gain: **0** on all six levels. An item always lies next to the
  shortest route, so touching one is cheap. What is expensive is moving the
  right one the right way. Dropped.
- **Stuck items as walls.** Over every state A* stored, the estimate rose on
  **47%** of library's states, by **58.6 moves on average**. On wc it rose on
  0.5%; on society, wreck and steel never. An item that falls into a shortcut
  without sealing the room leaves a live but much longer way out. The
  walls-only estimate keeps routing the fish through the shortcut.

## What changed

- **Distance maps.** `StuckDetector` keeps a distance map per fish per layout:
  the moves out from every (anchor, facing), with everything stuck as wall.
  - It uses the same relaxation as `RelaxedDistance.ToExit` for a fish, and the
    same unreachable cells as before.
  - So one map serves both the dead ends and the estimate.
  - The maps are `ushort`, and the cache has a byte budget (256 MB).
- **`Evaluate`.** It returns -1 for a dead state, otherwise
  max(h, sum of the fish distances). The sum can only raise the fish half of the
  estimate; the goal_out item half is left alone.
- **Pathmax.** A child never gets less than its parent's estimate minus one.
  - The stuck-wall distances are admissible but not always consistent: the
    analysis can prove an item stuck in a parent and fail to in its child.
  - Pathmax keeps f non-decreasing along every path. Buckets therefore drain in
    order, `DeepestF` stays a proven bound, and the parallel batch argument
    (docs/019) holds.
  - It is sound because h(parent) is admissible:
    h*(child) ≥ h*(parent) − 1 ≥ h(parent) − 1.
- **Parallel.** Phase 1 keeps both estimates, with and without the rule, and
  phase 2 chooses between them in serial order. A probation ending mid-batch
  therefore still matches the serial search exactly.
- **Probation** now counts raised estimates as well as dropped states.
- **Options.** `SolveOptions.StuckDistances` (default on), CLI and bench
  `--no-stuck-dist`.
- **Analysis speed-ups** found while chasing gems (see below). Both give the
  same fixpoint:
  - pushers of the same shape are tried once each;
  - a model's cells are freed as soon as it turns mobile. The rule is monotone,
    so the order does not matter; the fixpoint is just reached in fewer passes.
  - Per-analysis cost on gems went from 411 to 191 µs.

## Tests

- **`StuckEstimate_IsAdmissible`.** On every solvable state of every
  micro-level, the raised estimate is at most the true distance d* from the
  oracle. It goes through the solver's own detector and cache. The fuzzer runs
  the same check on every room.
- **Micro-level `detour-wedge` (25).** A dropped post closes the short tunnel
  but leaves a long way round.
  - No state there is dead, so the dead-end rule does nothing (67 expansions).
  - The distances cut the search to 40 expansions.
  - Pinned by `StuckDistances_CutTheDetour`.
- **Matrix column `astar-nodist`** (dead ends only), so the baseline shows what
  the distances add on their own.
- **Results:**
  - 822 tests pass;
  - KnownBug: the 7 pinned cells;
  - deep fuzz on seeds 77, 4242 and 9001, 20,000 rooms each: no unexplained
    failures.
  - The rooms include the Optimal matrix check, so A* with pathmax still returns
    exactly the optimum on every one.

## Measured

Baseline: cannons **214,855 → 157,588** expansions, the same 39 moves. Every
other corpus row is unchanged.

Every level at a fixed 1.5 M expansions, which is deterministic, so no noise:

- the bound went up on library (82 → **92**), magnet (53 → 55) and duckie
  (75 → 76);
- it went down nowhere;
- 75 levels are unchanged.

Library:

- 3 M expansions: bound f 94 → **104**.
- Proven 111-move optimum: 7.54 M → **4.69 M** expansions, 8.4 s → 5.5 s
  (12 threads).

`bench --suite`, serial, against `--no-stuck-dist`:

- cannons 1.31x faster;
- library: f 86 → 98 in the same 2 M expansions;
- everything else within ±3%.

## Found: gems is a net loss for the stuck rule

Not caused by this entry: it dates from the dead-end rule (docs/020). A 10 s
batch hid it in noise.

- **The level:** gems has 90 movable gems. Almost every push makes a new layout:
  59,643 analyses in 100 k expansions.
- **Cost per expansion:** 3.1 s without the rule, 10.7 s with dead ends only,
  15.2 s with distances (per 100 k expansions).
- **Net effect:** without the rule the 59-move optimum is proven in about 30 s
  on 12 threads. With it, the search has not finished after 60 s, despite
  reaching f = 59 in fewer expansions.
- **Why probation misses it:** probation looks only at usefulness, and the rule
  does prune on gems. It does not look at cost.

## Open

- **Identical items are distinct in the state key.** Each model has its own
  slot. Canonicalising interchangeable items (same shape and weight, no goal),
  by sorting their positions per class, would merge duplicate states. Measured
  over stored states at 300 k expansions:
  - gems: **79.4%** duplicates (679 k stored, 140 k distinct);
  - party1: 13.9%;
  - every other level tried: about 0. Identical items there rarely pass one
    another.

  It would also collapse gems's layouts and with them its analyses. This is the
  natural fix for the gems loss.
  **Done in docs/022:** gems now proves its optimum in 0.49 M expansions
  (6.1 s), 4.6x faster than before the stuck rule.
- A cost-aware probation would be a fallback, but it needs a deterministic cost
  measure: analysis counts differ between serial and parallel runs.
