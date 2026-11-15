# solver/CLAUDE.md — the level solver

Loaded on demand when Claude touches files under `solver/`. Full command reference and
background: `solver/README.md`. Milestone log: `solver/docs/` (convention in
`solver/docs/README.md`; cite browser-port entries as "root `docs/NNN`" to stay unambiguous).

## What it is

A C#/.NET 11 console app (`ffsolve`) that finds or proves shortest solutions for Fish
Fillets levels: puzzle simulation only (move, push, fall, die, escape) plus search. No
rendering, Lua, audio or UI. Independent of `web/`; shares only the rules and level content.

```
src/FishFillets.Physics/   rules: Level, Room, ApplyMove, IsSolved (its own port of legacy/src/level)
src/FishFillets.Search/    A*, window optimiser, level reduction, stuck detection, heuristics
src/FishFillets.Cli/       `ffsolve` (solve, improve, verify, results, reduce, info, bench)
src/FishFillets.Editor/    `ffedit`, WPF editor for simplifying levels
tests/                     MSTest suite
levels/*.json              exported level geometry (generated, checked in)
solutions/                 solutions shorter than the bundled ones
docs/                      milestone log, hall of fame, results.csv
```

## Build, run, test

```
scripts\build-solver.ps1              # build + regression suite (~580 tests, seconds)
scripts\build-solver.ps1 -Publish     # + Native AOT exe and smoke test (needs VS C++ workload)
scripts\build-solver.ps1 -KnownBugs   # only the [TestCategory("KnownBug")] tests
dotnet run --project src\FishFillets.Cli -c Release -- <command> [options]   # from solver/
```

Use the AOT exe for quick commands, but the **JIT build for long searches** (dynamic PGO is
~15-20% faster on multi-minute runs).

## Invariants

- **Physics must stay byte-identical to the real game.** All 80 recorded solutions
  (`legacy/solution/`) must replay to Solved, and `tests/.../Micro/physics-digest.csv`
  (seeded random-walk fingerprint) must not change unless physics is meant to change.
  Run the suite after any change to `Physics` or `Search`.
- **Search changes are pinned by `Micro/micro-baseline.csv`** (node counts). Accept a
  deliberate change with `$env:UPDATE_BASELINE=1` and commit the CSV diff.
- Pruning/heuristics must stay admissible where optimality is claimed — the micro-level
  suite checks them against an independent full-state BFS in every reachable state.
- `levels/*.json` is generated from the legacy Lua by the web loader
  (`scripts\export-levels.ps1`); don't hand-edit it.
- A fixed `KnownBug` test going green means: delete that level's `known-bug:` entry.
