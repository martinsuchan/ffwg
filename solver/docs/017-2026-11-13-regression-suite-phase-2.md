# 017 — Regression suite, phase 2: physics fingerprint, fuzzer, bench

Phase 2 of docs/015: the pieces the optimisation work needs that phase 1 could
not give - something that notices a physics change off the solution paths,
something that finds bugs nobody drew a level for, and a wall-clock comparison.

## What was added

- **Physics fingerprint** (`PhysicsDigestTests`, `Micro/physics-digest.csv`): per
  corpus and micro level, a seeded 6,000-move random walk (branching from a pool of
  earlier states, restarting on death or win) hashed over every move's symbol,
  accept/reject and every model field except `TouchDir`. The safety net for P2/P3
  (skipping the round simulation for moves that change nothing else): such a change
  must leave every digest unchanged. `TouchDir` is excluded on purpose - it is
  write-only for physics and a fast path may leave it stale.
- **Corpus golden counts**: the baseline now also pins expansions/stored for plain
  A* on start, wc, noground, submarine, cannons and wreck (Verified reduction, as
  the CLI runs it). Micro-levels are too small to show effort changes; these are not.
- **Differential fuzzer** (`MicroFuzzer`, `MicroFuzzTests`): random 8-13 x 5-9
  rooms (openings, interior walls, ledges, one or two fish, up to four items, a
  quarter of them goal_alive + goal_out), every check of `MicroChecks` against the
  oracle. Violations are **attributed by mechanism**: C3 only if the estimate with
  item facing ignored is admissible there, C7 only if the condemned fish never has
  to leave, C1 only if `--stuck` fails while the uncached analysis condemns nothing,
  C2 for the weighted macro/item columns. Anything unattributed fails; deep runs
  minimise each new failure into a `.lvl`. Smoke (400 candidates, fixed seed) runs
  in the default suite in ~1 s.
- **C1 pinned directly** (`WhiteBoxTests`): `NoWayOut` on heavy-gate's opening is
  `false` fresh, but `true` once a same-layout state without the big fish was
  evaluated first. Reflection on private members, deliberately - see below.
- **Three more curated levels**: `push-chain` (9), `fall-kill-trap` (11),
  `perf-crossing` (27, 37 k states, a performance ratchet). All pass every check.
- **`ffsolve bench --suite`**: best-of-N wall clock of fixed node-limited A* runs,
  expansions/s, bytes per stored state (from `Solver.MemoryBytes` - array sizes,
  not the GC, so exact), CSV out and `--compare` against an earlier run.
- `MicroTests` refactored: the checks moved to `MicroChecks` so the fuzzer and the
  curated suite apply identical rules.

## Results

- Fuzzing found **no new bugs**: 10,649 usable rooms of 40,000 (seed 11; optimum
  up to 38, up to 39 k states), plus 1,586 more (seed 7). The known findings
  account for everything: C3 167/177 rooms (admissible/consistent), C7 145-148.
- **C1 is real but rare end to end.** The fuzzer never hit it: the search's FIFO
  order almost always computes a layout's verdict with both fish present first,
  because the state where the big fish left early lags one generation behind. So it
  is pinned by the white-box test instead, which fails exactly as predicted.
- The review's "bridge" parking gap (a fish parked under an item's future position)
  turned out to be nearly moot: an item pushed fully onto fish is crushing them
  (`CheckDeadMove`/`IsOnHolderBacks`), so a fish can only usefully share support
  with something else - a narrow case not worth a level yet.
- Baseline numbers on this machine (.NET 11 JIT, best of 3):

  | level | expanded | k exp/s | B/state |
  |---|---|---|---|
  | start | 217,622 | 257 | 139 |
  | cannons | 215,117 | 133 | 202 |
  | wreck | 756,029 | 216 | 95 |
  | society (1.5 M cap) | 1,500,000 | 149 | 191 |
  | library (2 M cap) | 2,000,000 | 169 | 177 |

  B/state swings 95-202 with where each table sits in its doubling cycle - which
  is itself the P1 problem.

## Workflow for an optimisation

1. `ffsolve bench --suite --out before.csv` on the old build.
2. Make the change; `scripts\build-solver.ps1` (default suite: oracle, matrix,
   properties, fingerprint, baseline, fuzz smoke).
3. `ffsolve bench --suite --compare before.csv`.
4. Accept intended count changes with `UPDATE_BASELINE=1`, commit the CSV diff.
5. For a fix: `scripts\build-solver.ps1 -KnownBugs`; delete the level's
   `known-bug` entry once green.

## Open

- The white-box C1 test reads private `Solver` fields; when P1 restructures the
  solver it has to follow (or be replaced by an end-to-end repro).
- Still no curated level for lifting an item, support handover, or the windoze
  extra fish - the fuzzer covers lifting incidentally; the other two need hand
  design.
