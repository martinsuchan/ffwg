# 026 — Layout solver: where the 2 and 4 moves went

2026-11-15

docs/025 left `--layouts` at 41 on `cannons` (optimum 39) and 104 on `wc`
(optimum 100), unexplained. `cannons` is fixed (**39**); `wc`'s 4 moves are two
separate causes, both located, neither fixed.

## The probe

`LayoutExpressibilityTests`: a known optimum is cut at every event, and from
each checkpoint the real expansion (`LayoutSolver.ProbeSuccessors`) has to
produce the next checkpoint's layout at the optimum's price. Now in the suite
for `start`, `cannons`, `wc`, `submarine`, `noground`, all at zero.

It sees one edge at a time from concrete fish positions, so it cannot see what
a deferred fish costs later. `wc` passes it and is still 4 long; that was found
by following the optimum's checkpoints through a real search and dumping the
small fish's field along the chain (ad hoc, not kept).

## `cannons`: fixed

Two consecutive lifts by the small fish were priced 2 for 1. The big fish
stands beside it and holds something up wherever it can be, so it is always
committed. Its home touched the one-cell margin round the actor, so it was
moved one square off first, and that step was charged. The margin is for a
deferred fish; a committed one is exact. It now stays home when it holds
something there too and does not overlap the actor itself.

## `wc`: two causes, 2 moves each

Both are at the event where the big fish pushes the bowl off and it comes down
over the small fish (optimum move 61).

1. **One same-layout child per fish.** The interaction step commits the small
   fish at the *cheapest* square under the bowl, (10,17) at field cost 12. The
   optimum has it at (13,19), cost 17, where its next job is. Only the first is
   emitted, so the small fish has to be brought round afterwards.
   Lifting the limit: `wc` 104 → 102, but `noground` no longer finishes in 90 s
   (35 s before). Reverted.
2. **A committed fish is priced from its home, not its field.**
   `TryCommittedFirst` routes the other fish from its home square. The small
   fish's home had been pushed to (8,13) by an earlier mask, while its field
   still held the cheaper history (step aside to (5,16) while the big fish was
   low, then under it): 17 by the field, 19 from home.

## Measured

`--layouts`, serial, 60 s: `cannons` **39** (41), 15 s; `start` 54, `submarine`
83, `noground` 44 (35 s), `wc` 104 - unchanged. `broom` not re-measured. Suite:
1,041 passed. `micro-baseline.csv` unchanged.

## Open

- Cause 1 wants no extra children at all: a placement proven "same layout with
  the fish there" could simply stay **unmasked in the generic child's field**.
  The physics is already paid (the loop runs every candidate today); it needs
  the exceptions stored per node beside the footprint cells, since `Phi`
  re-masks from them.
- Cause 2: price the committed leg by the solid field, and make reconstruction
  follow the field back for that leg as it does for a deferred fish.
- Everything open in docs/025 still is (probe cost, micro-level for the
  interaction rule, handover, `broom`).
