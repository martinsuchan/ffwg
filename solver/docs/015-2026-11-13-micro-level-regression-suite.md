# 015 — A micro-level regression suite, and what it found on day one

Before fixing anything from the code review (C1-C6, P1-P4), a suite that can
show each fix is real and each optimisation does not regress. Lives in
`tests/FishFillets.Physics.Tests/Micro/`.

## Shape

- **Micro-levels**: hand-drawn ASCII rooms (`Levels/*.lvl`), 4-25 moves today,
  each with a header: `expect` (optimum or `none`), `tags`, an optional `detour`
  (a valid longer solution for the window optimiser) and `known-bug` entries.
  Format documented on `MicroLevel`.
- **Oracle** (`ReferenceBfs`): plain BFS over the whole state space with its own
  key (every `ModelState` field but `TouchDir`), no reduction, heuristic or
  pruning beyond "a dead fish stays dead". It also gives the exact distance to
  goal of every state, via a backward BFS over the explicit graph. Every
  `expect` is checked against it, never against a solver.
- **Checks**, each over every level: oracle agreement; a matrix of 11 solver
  variants held to what each promises (optimal / weight-bounded / valid);
  heuristic admissibility and consistency in every reachable state; frozen
  models never move in any generated state; the stuck rule never condemns a
  solvable state; the window optimiser removes a detour.
- **Baseline** (`micro-baseline.csv`): status, length, expansions, stored states
  per (level, variant). Node-limited, so deterministic; >5% (+20 nodes) more work
  fails, improvements print. `UPDATE_BASELINE=1` rewrites it.
- **KnownBug**: a level's `known-bug: <check>=<id>` (or a variant-wide id) moves
  that check into `[TestCategory("KnownBug")]`, excluded from the normal run and
  expected red until the fix. An id ending in `!` is not executed at all
  (hazardous). `scripts\build-solver.ps1 -KnownBugs` runs them.

## What it found immediately

Confirmed from the review:

- **C2** - `MacroSolver`/`ItemSolver` above weight 1 lose the search after the
  first expansion on nearly every micro-level (even the 14-move `swim-turn`).
- **C3** - `goal-out-push-right`: item distance charges a phantom turn; 5
  states overestimate, and one edge is inconsistent (the last push: h 2 -> 0).
- **C5** - `exit-detour`: the window optimiser cannot drop a detour whose
  shortcut ends with the fish out of the room.

New:

- **C7** - `Solver.NoWayOut` requires every living fish to reach a border, even
  on goal_alive levels where none has to: `--stuck` declares
  `goal-out-drop-hole` unsolvable at the start (130 solvable states condemned).
- **C2 x C3** - the macro and item solvers fail `goal-out-push-right` even at
  weight 1: C3's inconsistent edge drops f below the forward sweep, and C2 loses it.
- **C4 is worse than "steers wrong"** - with `--work` on a goal_alive level whose
  fish has no exit, the start's guide is `Unreachable`, and `EnsureOpen` allocates
  an f-bucket array of ~536 M lists (minutes, many GB). Marked `C4!`, never run.
- **MACRO-PARK** - `heavy-gate`: the macro search cannot express "small fish
  steps aside so the big one can push" (docs/008's open gap, now pinned by a
  24-move level).

## Open

- No reproduction for **C1** (stuck cache keyed without fish presence) yet - it
  depends on expansion order; the planned fuzzer is the tool for it.
- Today's levels are too easy to measure performance (most searches expand
  tens of nodes). Next: harder tier-30 micro-levels, a seeded random-room
  fuzzer differential against the oracle, golden counts for the proven corpus
  levels, a physics differential test for the inert fast path (P2), and an
  `ffsolve bench` for wall-clock comparisons.
