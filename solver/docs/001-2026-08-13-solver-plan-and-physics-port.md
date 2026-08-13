# 001 — Solver plan and the C# physics port

2026-08-13

Starts a second, independent effort in this repo: a **console app that solves
Fish Fillets levels** — find a solution for a level, or find the shortest one.
No UI, no rendering, no dialogs, no scripting; just the puzzle simulation and a
search over it.

This entry records the plan for the whole effort and what landed in the first
milestone: the level export, the C# physics port, and the verification that
proves it faithful.

---

## 1. Why a separate implementation at all

The browser port (`web/`) already contains a complete, verified port of the
game's rules, and `web/src/game/SolutionValidator.ts` (docs/022) already replays
solutions headlessly. It would be tempting to write the search in TypeScript on
top of it.

Two reasons not to:

- **Allocation.** The TS rules allocate constantly in the hottest path — a `V2`
  per grid query, an `Array.from(new Set(...))` inside every `getResist()`, a
  fresh array from `getPads()`/`whoIsFalling()`/`whoIsHeavier()`, a `Landslip`
  object per round. That is entirely reasonable for a game running one round
  every 100 ms, and entirely wrong for a search running millions.
- **Memory control.** The binding constraint on a search like this is the size
  of the closed set, so the state store wants explicit layout, unmanaged
  arenas and value types.

So: same rules, re-implemented for throughput, in its own tree. The risk that
buys is **divergence** — a solver that solves a game slightly different from
the real one is worthless. Section 4 is how that risk is retired.

## 2. Language and runtime: .NET 10, Native AOT

Confirmed as the choice. This workload is hash-table probing plus small-array
manipulation, where .NET lands within roughly 1.3–2× of optimised C++ *provided*
the steady state allocates nothing — and that gap is far smaller than what the
algorithm choice buys. Rust or C++ would give maybe another 1.5–2×; not worth
the iteration speed at this stage, and `FishFillets.Physics` is a dependency-free
leaf assembly precisely so it could be ported later if it ever is.

(Note on wording: the request said "C# 10.0", but the discussion was about
.NET 10 — so this targets `net10.0` with `LangVersion=latest` (C# 14). C# 10 the
*language* is from 2021 and would rule out most of what makes this code fast.)

What that means concretely, and what is already enforced in the build:

- `PublishAot` + `IsAotCompatible` — the trim/AOT analysers run on every build,
  so anything reflection-based fails here rather than at publish time.
  `TreatWarningsAsErrors` keeps that honest. Publishing produces a 2.5 MB
  self-contained exe (`scripts\build-solver.ps1 -Publish`).
- JSON goes through a source-generated `JsonSerializerContext`; no reflection
  serialiser is ever pulled in.
- No allocation in the steady state. Measured, not asserted — see section 5.
- `ServerGarbageCollection` for the eventual parallel search; irrelevant while
  nothing allocates, but correct once the state store exists.

Two environment prerequisites, both hit while getting the first AOT publish out,
both now handled by `scripts\build-solver.ps1`:

- The **"Desktop development with C++" workload** must be installed. Having the
  MSVC *binaries* is not enough — a partial install can leave `link.exe` present
  but no `VC\Tools\MSVC\<ver>\lib` or `include` and no `vcvarsall.bat`, which
  fails as the misleading "Platform linker not found".
- **`vswhere.exe` must be on PATH.** ILCompiler's Windows targets shell out to it
  by name to find the linker, but the VS Installer directory it lives in isn't on
  PATH by default; the link step then fails with vswhere's own "not recognized"
  message spliced into its command line (exit 123). The build script prepends
  `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`.

## 3. Architecture

```
legacy/script/<level>/{models,code}.lua        the game's own content
      │
      │  web/src/lua/levelLoader.ts  (wasmoon; docs/005/008/024)
      │  driven by web/tools/export-levels.mjs through Playwright
      ▼
solver/levels/<level>.json                     room size + models, nothing else
      │
      ▼
FishFillets.Physics   Level → Room → ApplyMove/IsSolved      ← this milestone
      │
      ▼
FishFillets.Cli       ffsolve verify | info | bench
                      ffsolve solve  ← next milestones
```

### Level data: exported, not interpreted

The solver has **no Lua dependency**. `web/tools/export-levels.mjs` runs the real
browser-side loader (which already handles the Lua 5.0 compat shim, the
`file_include` pre-scan, the synchronous `prog_init()` cases, and the
world-final-level goal reassignment — docs/005/008/024) once, and writes a flat
JSON file per level holding only what physics needs: room size, and each model's
kind, position, shape, goal and facing.

This works because the live Lua engine is deliberately physics-free (docs/014).
The one exception is `windoze`, whose `code.lua` drives `busy` / `checkActive` /
`setFastFalling` (docs/035); the primitives are ported (`Room.SetBusy`,
`SetFastFalling`) but nothing calls them, so a `windoze` *search* would be
unfaithful. Replaying its recorded solution is fine, and does pass.

81 levels export (80 world-map nodes plus the `ending` level). `redhat` has a
reference solution but no level content in this repo, exactly as in the browser
port.

### Physics: `FishFillets.Physics`

A port of `web/src/game/` — itself a port of `legacy/src/level/` — restructured
for throughput. Method names and comments track the originals so the three can
be read side by side.

| legacy / web              | here                                        |
| ------------------------- | ------------------------------------------- |
| `Field`, `MarkMask`       | `Room.cs` — one flat `short[]` grid          |
| `Rules`, `OnCondition`    | `Room.Rules.cs`                              |
| `Landslip`                | `Room.Landslip.cs`                           |
| `Room` (round pipeline)   | `Room.Round.cs`                              |
| `Unit`, `Controls`        | `Room.Controls.cs` — `MakeMove` only         |
| `Cube`                    | `ModelState` (mutable) + `ModelDef` (shared) |
| `ModelFactory`, `Goal`    | `Level.cs`, `GoalKind`                       |

The structural changes, and why each is behaviour-preserving:

- **Index-based, not object-graph.** A model is an index into two arrays;
  `ModelDef` holds what never changes (shape, power, goal), `ModelState` what
  does. So a whole room's state is one contiguous struct array — snapshottable
  with a memcpy, which is what the search will want.
- **`Field`/`MarkMask` folded into `Room`.** In the original these are per-model
  objects whose only job is to reach the one shared grid; collapsing them removes
  an indirection from `getResist`, the hottest call in the engine.
- **The border is a real model** at the end of the arrays, rather than a special
  Cube with index −1 outside the model list. Every recursive rule then walks into
  it without a special case, and `getPlacedResist`'s existing "skip myself" rule
  is what terminates the recursion there — exactly as in the original, where the
  border's own probes also resolve back to itself.
- **`OnCondition` is an enum, not an interface** — no allocation, no virtual
  dispatch in the recursion.
- **Resist lists come from a stack arena** (`ResistArena`) instead of a fresh
  deduplicated array per call. Frames nest because the rules recurse while
  iterating an outer list, so the discipline is strictly LIFO — always consume a
  frame inside its `using`. Dedup is a rolling stamp per model, so it is O(1) per
  candidate with no clearing between frames. The three rules that accumulate
  across their *own* recursion while an outer frame is open (`getPads`,
  `whoIsFalling`, `whoIsHeavier`) would violate that discipline, so each has its
  own `ModelCollector`.

Deliberately **not** ported, because the solver has no player and no screen:
input handling (`InputProvider`, held/queued keys, `MouseControl`, `FinderAlg`),
the active-fish switching scheme, all animation state, and the visual move-streak
(docs/017). Save/undo and the `strict_rules` option are absent in the browser
port too.

### The state graph the search will run on

Established while porting, and the reason several of the above choices matter:

- **Nodes are settled states, edges are single move symbols.** `ApplyMove`
  applies one symbol and settles completely. The original settles *before*
  applying and leaves consequences to the next call; settling after produces the
  same sequence of settled states, because `Reset()` also settles (a room's items
  can start unsupported — the load-time settling a player sees, docs/019) and
  settling is idempotent on an already-fresh room.
- **The active fish is not state.** A symbol names both the fish and the
  direction (`udlr` / `UDLR`), and `MakeMove` tries every unit, so successor
  generation never touches the active-fish machinery. One whole dimension gone.
- **Facing is state.** Moving against your facing is a *turn*: it costs a symbol
  and moves nothing. One bit per fish.
- **Death is always a prune.** Both fish goals require `alive`, and only fish can
  die, so `IsSolvable()` goes false the moment one does. That also means the
  corpse-removal countdown (docs/011) never needs to be part of a state key — it
  is ported for fidelity, not for the search.
- **`TouchDir` must be left out of the state key.** A rejected move still records
  it (`SetTouched`), so two otherwise-identical states can differ in it. It is
  write-only as far as physics goes — the next round clears it — and exists only
  because levels read it from Lua (docs/033).
- **Cost is uniform**: one symbol, one step, which is exactly how the game's
  pedometer counts (docs/047). So BFS is optimal, and A\* stays optimal with an
  admissible heuristic.

## 4. Verification

The corpus is the argument. `legacy/solution/*.lua` holds a recorded solution
for 81 levels; between them they exercise pushing, chained falls, crush deaths,
escapes, `goal_out` items, multi-cell shapes, `windoze`'s extra fish and output
plug, and solutions from 39 to 2,127 moves. Requiring every one of them to drive
this engine to `IsSolved()` is a strong equivalence test against the browser
port, which passes the same corpus.

```
> ffsolve verify --all
SOLVED  start             54 moves
SOLVED  briefcase        225 moves
...
SOLVED  windoze          525 moves
SOLVED  floppy           946 moves
SKIP    ending         no reference solution

80/80 solved, 1 skipped - 32031 moves in 333 ms
```

**80/80**, first run, matching the browser port exactly (`ending` has a level but
no recorded solution; `redhat` has a solution but no level). Nothing was adjusted
to make this pass.

Two things this does *not* yet cover, both listed under "open" below:
differential fuzzing against the TS engine on random move sequences (the corpus
only walks solution paths, never the dead ends a search spends its time in), and
any level state reachable only through `windoze`'s scripted `busy` toggling.

*(docs/002 turns all of this into `dotnet test`, and adds the complementary half —
tests that a **wrong** solution is actually rejected, which the corpus alone can
never catch.)*

## 5. Measured performance

`ffsolve bench <level> --rounds 200` replays a reference solution repeatedly.

| level     | room  | models | moves/s (1 core) | allocated |
| --------- | ----- | ------ | ---------------- | --------- |
| map       | 48×33 | 26     | **0.71 M**       | 0 B       |
| warcraft  | 52×37 | 24     | 0.38 M           | 0 B       |
| airplane  | 45×27 | 12     | 0.24 M           | 0 B       |
| gems      | 29×18 | 112    | 0.04 M           | 0 B       |

Allocation is **40 bytes for an entire run** regardless of move count — that is
the harness's own `Stopwatch`, not the simulation. The zero-allocation goal is
met.

Native AOT changes throughput hardly at all (`map`: 0.69 M/s AOT vs 0.71 M/s
JIT — a steady-state hot loop is exactly where the JIT catches up), but it
removes warm-up: `verify --all` is **70 ms AOT vs 355 ms JIT**, because 80 short
replays are nearly all warm-up. That is the shape a batch solve has too, so AOT
stays worth it. Measured on ARM64; the same binary published for `win-x64` runs
under Prism emulation at 104 ms, so `win-arm64` is the right default here.

Read the throughput column carefully: each "move" here includes a full settle,
and the bench re-`Reset()`s (rebuild + settle) once per replay, so short-solution
levels look worse than they are — `gems` pays 200 resets over 111 models for
11,800 moves. `map` (2,127 moves per reset) is the honest figure: **~0.7 M
moves/s per core, ~1.4 µs per settled move.**

That is already ~700× the "hundreds or thousands per second" the effort was
scoped against, but well short of what this should reach. The three known costs,
all deliberately left alone this milestone in favour of a faithful port:

1. **`Landslip` is a fixpoint over every model, every round** — O(n²) on the
   item-heavy levels. A move only disturbs support locally, so this wants an
   incremental version that reconsiders only models whose support changed.
   Biggest single win, and the reason `gems` is an order of magnitude off.
2. **`PrepareRound` makes four full passes** over all models. Wants a dirty set.
3. **`Fallout` iterates every model** to find the handful with `shouldGoOut`.
   Trivially precomputable.

## 6. Plan for the rest of the effort

Staged, because "any solution" and "the shortest solution" are different
problems here — the reference solutions run 39 to 2,127 symbols.

- **Stage A — exact BFS.** Uniform cost means BFS is optimal on the game's own
  step count. Solves the short levels (`cannons` 39, `noground` 44, `start` 54,
  `gems` 59) and is the baseline every later stage is checked against.
- **Stage B — A\* with an admissible heuristic.** Fish swim freely and need no
  support, and items can only ever obstruct them — so a BFS distance over a
  *walls-only* grid is a genuine lower bound. Precompute it per level over
  `cell × facing` so turn costs are included. Summing over the two fish stays
  admissible, because a symbol advances exactly one fish by at most one cell.
- **Stage C — macro moves.** The Sokoban insight: in a 235-symbol solution like
  `airplane`, nearly every symbol is a fish swimming through open water, and the
  decisions are the pushes. Reformulate an edge as "route fish F to a push
  position, then push item I" with the route costed by a `FinderAlg`-style BFS.
  Depth drops from ~235 to ~30. The trap to get right: a fish is a solid
  platform, so a route step that drops something it was supporting is a decision,
  not routing.
- **Stage D — anytime search** for the monsters (`map`, `propulsion`, `grail`,
  `barrel`): weighted A\*, beam search, greedy best-first. A solution, not an
  optimal one.
- **Stage E — solution improvement.** Highest value per effort, and what
  actually answers "best possible": replay an existing solution, then for each
  window `[i, j]` run a bounded A\* between the two states and splice in anything
  shorter. Human-recorded solutions wander, so this should beat the references
  without ever solving a level from scratch.
- **Parallelism, last.** Batch mode (80 levels × N cores) is free. Level-
  synchronous BFS shards its closed set by hash; A\* goes to HDA\*; beam search
  parallelises almost perfectly. Expect memory to bind before CPU.

State encoding, when Stage A needs it: per fish a cell index + facing bit, per
movable item a cell index, with `item_fixed` folded into a static wall bitboard.
Three reductions to build in early — freezing provably unreachable items into the
wall, canonicalising interchangeable items (`gems` has 111 identical light items,
`experiments` 78, `columns` 51), and delta-encoding against the initial state,
since most items never move.

## 7. Open

- **Differential fuzzing vs. the TS engine** on random move sequences, to cover
  the dead-end states the solution corpus never visits.
- **`windoze` search fidelity** — its `busy` toggling comes from Lua that this
  tree deliberately doesn't run.
- **Where the level export lives.** `solver/levels/*.json` (504 KB) is checked in
  so the solver builds from a clean clone without the web toolchain; it must be
  regenerated with `scripts\export-levels.ps1` if level content ever changes.
- The performance items in section 5, in that order.
