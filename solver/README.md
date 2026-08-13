# Fish Fillets solver

A console app that solves Fish Fillets levels — finds a solution for a level, or
the shortest one. No UI, no rendering, no dialogs, no scripting: just the puzzle
simulation (moving, pushing, falling, dying, escaping) and a search over it.

Separate from the browser port in `web/`, which is a different effort with a
different goal. The two share the game's rules and its level content, nothing
else. See [`docs/`](docs) for this tree's log, and the repo-root `docs/` for the
port's.

**Status:** the physics port is done and verified — all 80 of the game's recorded
reference solutions replay to *Solved*. The search itself is next; see
[`docs/001`](docs/001-2026-08-13-solver-plan-and-physics-port.md) for the plan.

## Layout

```
solver/
  src/FishFillets.Physics/   the rules: Level, Room, ApplyMove, IsSolved
  src/FishFillets.Cli/       the `ffsolve` console app
  tests/                     MSTest suite (dotnet test)
  levels/                    exported level geometry (checked in, 81 levels)
  docs/                      milestone log
```

## Running it

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). From the repo
root:

```
scripts\build-solver.ps1              # build + run the tests
scripts\build-solver.ps1 -Publish     # Native AOT exe, smoke test, then tests
```

Then, from `solver/`:

```
dotnet test                                                                  # 101 tests, ~120 ms
dotnet run --project src\FishFillets.Cli -c Release -- verify --all
dotnet run --project src\FishFillets.Cli -c Release -- verify airplane
dotnet run --project src\FishFillets.Cli -c Release -- info gems
dotnet run --project src\FishFillets.Cli -c Release -- bench map --rounds 200
```

## Tests

`dotnet test` is the regression suite — run it after any change to
`FishFillets.Physics`. It covers three things (see
[`docs/002`](docs/002-2026-08-14-test-project.md)):

- **the corpus**, one case per level: all 80 of the game's recorded solutions
  must replay to *Solved*. This is what proves the port matches the real game.
- **invalid solutions**, that wrong answers are actually rejected — and at the
  right place. A validator that accepts every real solution is worthless if it
  also accepts bad ones.
- **individual rules**, in hand-built rooms: pushing by power, turning, falling,
  automatic escape, death by stress.

`ffsolve verify --all` does the corpus half on its own, which is what the
`-Publish` path uses to smoke-test the AOT binary.

### Native AOT

`-Publish` produces a 2.5 MB self-contained `ffsolve.exe` with no JIT warm-up
(`verify --all` runs in 70 ms instead of 355 ms). It needs the **"Desktop
development with C++"** workload in the Visual Studio Installer; everything else
here works without it. The script picks this machine's architecture and puts
`vswhere` on PATH for the linker — see
[`docs/001`](docs/001-2026-08-13-solver-plan-and-physics-port.md) §2 if you'd
rather run `dotnet publish` by hand.

## Level data

`solver/levels/*.json` is generated from the game's own Lua by the browser port's
loader, and checked in so this project builds from a clean clone with no web
toolchain. Regenerate it (needs Node and a working `web/` setup) only if level
content changes:

```
scripts\export-levels.ps1        # from the repo root
```
