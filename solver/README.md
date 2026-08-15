# Fish Fillets solver

A console app that solves Fish Fillets levels — finds a solution for a level, or
proves the shortest one. No UI, no rendering, no dialogs, no scripting: just the
puzzle simulation (moving, pushing, falling, dying, escaping) and a search over
it.

Separate from the browser port in `web/`, which is a different effort with a
different goal. The two share the game's rules and its level content, nothing
else. See [`docs/`](docs) for this tree's log, and the repo-root `docs/` for the
port's.

**Status:** the physics port is verified (all 80 recorded reference solutions
replay to *Solved*). Nine levels are solved to **proven** optimality, all matching
the hall of fame; four more have solutions shorter than the ones bundled with the
game. `ffsolve results` is the live scoreboard.

## Layout

```
solver/
  src/FishFillets.Physics/   the rules: Level, Room, ApplyMove, IsSolved
  src/FishFillets.Search/    A*, the window optimiser, the level reduction
  src/FishFillets.Cli/       the `ffsolve` console app
  src/FishFillets.Editor/    `ffedit`, a WPF editor for simplifying levels
  tests/                     MSTest suite (dotnet test)
  levels/                    exported level geometry (checked in, 81 levels)
  solutions/                 solutions shorter than the bundled ones
  docs/                      milestone log, hall of fame, results.csv
```

## Getting started

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). From the repo
root:

```
scripts\build-solver.ps1              # build + run the tests
scripts\build-solver.ps1 -Publish     # Native AOT exe, smoke test, then tests
```

Everything below is written as `ffsolve <command>`. Without a published binary,
run it from `solver/` as:

```
dotnet run --project src\FishFillets.Cli -c Release -- <command> [options]
```

`-Publish` puts a self-contained `ffsolve.exe` in
`src\FishFillets.Cli\bin\Release\net10.0\<rid>\publish\`, which is worth using for
long runs — no JIT warm-up (`verify --all` takes 70 ms instead of 355 ms). It
needs the **"Desktop development with C++"** workload in the Visual Studio
Installer; nothing else here does.

---

# Commands

## Global options

Accepted by every command:

| option | default | meaning |
| --- | --- | --- |
| `--levels <dir>` | `<repo>/solver/levels` | where level JSON lives |
| `--solutions <dir>` | `<repo>/legacy/solution` | the game's recorded solutions |

Both are found by walking up from the executable, so commands work from any
directory. Point `--levels` elsewhere to work on a separate set of edited levels.

## `solve` — search a level from scratch

```
ffsolve solve <level> [options]
```

A\* over settled states. At weight 1.0 the answer is **provably shortest**: when a
finished room is popped, everything cheaper has already been explored, so no
shorter solution exists.

| option | default | meaning |
| --- | --- | --- |
| `--seconds N` | none | give up after N seconds |
| `--nodes N` | 20,000,000 | give up after N expansions (this is also the memory cap in practice) |
| `--weight W` | 1.0 | inflate the heuristic. >1 finds solutions on levels A\* can't finish, but drops the optimality proof |
| `--macro` | off | search over decisions (travel, then one thing that changes the world) instead of key presses — see below |
| `--out FILE` | — | write the solution as `saved_moves = '…'` |
| `--progress N` | 1 | progress line every N seconds (`0` silences) |
| `--quiet` | off | no progress lines |
| `--no-record` | off | don't merge the run into `docs/results.csv` |

```
ffsolve solve start                                  # solves in ~1.5 s
ffsolve solve stairs --nodes 60000000 --seconds 600  # needs a bigger budget
ffsolve solve gems --weight 2 --seconds 300          # give up optimality for reach
ffsolve solve cellar --out solutions\cellar.lua
```

### `--macro`

An edge is *an inert route followed by one thing that changes the world* — a
push, a drop, or leaving the room — so the search stores states only at decision
points rather than at every cell a fish swims through. Cost is still counted in
symbols, so lengths stay comparable.

**It is not a proof.** A route may only end where the expansion says it may, and
that rule is argued rather than proven complete: it currently misses `wc`'s
optimum by 20 (`docs/008` has the measurements, and why every attempt to widen
the rule cost far more than it bought). So a `--macro` answer is a *solution*, not
a shortest one — `results` records no lower bound for these runs, and
"shortest among macro-expressible" means exactly that and nothing more.

Where it wins is reach: `start` goes from unsolved at 60 s under plain A\* macro
settings to solved in a second on a 150× smaller state space. The intended use is
as a **finder** on levels nothing else cracks, with `improve` shortening whatever
it returns.

Progress lines show elapsed, current bound, expansions, states stored, rate and
memory:

```
      45s  f=137  expanded    9,904,128  stored   10,698,584      220k/s     925 MB
```

**Memory is what runs out first**, not CPU — roughly 130 bytes per stored state,
so 20 M nodes is about 2.5 GB. Raise `--nodes` only as far as RAM allows.

## `solve --all` / `--levels-list` — batch, in parallel

```
ffsolve solve --all [options]
ffsolve solve --levels-list gems,cabin1,steel [options]
```

Levels share nothing, so this is plain parallelism with no locking. `--all` covers
every level that has a reference solution, skipping `briefcase` and `windoze`
(their Lua scripting drives play, so a physics-only answer may not be reachable
in the real game).

| option | default | meaning |
| --- | --- | --- |
| `--parallel N` | `min(cores, 4)` | levels at a time |
| `--seconds N` | 60 | per level |
| `--nodes N` | 20,000,000 | per level |
| `--weight W` | 1.0 | as above |
| `--out-dir DIR` | — | write each solution found into DIR |
| `--progress N` | 10 | per-level progress, prefixed with the level name |

The default is 4 workers rather than the core count **because memory binds long
before CPU** — a hard level can hold 1.5 GB+. Raise it for a sweep of easy levels,
lower it for hard ones.

```
ffsolve solve --all --parallel 3 --seconds 900 --nodes 30000000
```

Results are saved after *each* level, so an interrupted batch keeps what it
finished.

## `improve` — shorten an existing solution

```
ffsolve improve <level> [options]
```

Re-solves windows of a known solution, splicing in anything shorter. This is what
found the four solutions shorter than the bundled ones. It only finds *local*
shortcuts — it preserves both ends of each window, so it can never discover a
globally different route ([`docs/003`](docs/003-2026-08-14-window-optimizer.md)).

| option | default | meaning |
| --- | --- | --- |
| `--window N` | 16 | window length in moves. Bigger reaches further and costs exponentially more |
| `--stride N` | 1 | gap between window starts |
| `--nodes N` | 2,000,000 | per-window search budget |
| `--moves S` | the bundled solution | improve this string instead |
| `--out FILE` | — | write the result |
| `--quiet`, `--no-record` | off | as above |

```
ffsolve improve barrel --window 18 --out solutions\barrel.lua
ffsolve improve hole --window 24 --stride 2
```

**No time limit** — it runs to fixpoint, which can take hours on a long level.

## `verify` — replay a move string

```
ffsolve verify <level>              # the level's bundled reference solution
ffsolve verify --all                # every reference solution (the regression check)
ffsolve verify <level> --moves S    # an arbitrary move string
```

Every solution the solver produces is already verified before it is reported;
this is for checking one by hand, or for confirming a solution against a
*different* level (see below).

## `results` — the scoreboard

```
ffsolve results              # every level, with the hall of fame alongside
ffsolve results --unsolved   # only what is still open, and how close the bound got
ffsolve results --rebuild    # refresh the file from the corpus, without searching
ffsolve results --rebuild --separator culture   # rewrite it for the local Excel
```

The record is **[`docs/results.csv`](docs/results.csv)**, written by the solver
itself — one row per level, in the order the game is played, grouped by branch:

| column | |
| --- | --- |
| `name`, `derivedFrom` | the level; the real level it was simplified from, if any |
| `branch` | the game's own section name (`Ship Wrecks`, `Treasure Cave`…) |
| `best`, `bestAuthor` | the hall of fame (`docs/worldfame.lua`) |
| `bundled` | the solution shipped in `legacy/solution` |
| `our` | the shortest we have found |
| `width`, `height`, `items` | room size, and models excluding fish and fixed scenery |
| `proven` … `recorded` | the run: method, bound, expanded, stored, seconds, status |

Only the run columns are recorded data — everything else is recomputed from the
levels and the hall of fame on every write, so the table cannot drift, and
editing those columns in a spreadsheet changes nothing. `--rebuild` forces that
refresh after `worldfame.lua` changes or a level is added.

In the printed table `*` marks proven shortest, and `~` marks a level where the
**bound alone reaches the hall-of-fame number**, which proves that number optimal
even though we never found the moves.

### Separator

A Czech, German or French Windows expects a CSV to use **semicolons**, and opens
a comma-separated one as a single column. So the file follows the local setting
(Region ► Additional settings ► *List separator*), and pairs it with the local
decimal separator — `171,6` seconds under `;`, `171.6` under `,`:

| `--separator` | |
| --- | --- |
| `culture` | whatever Windows is set to — the same one Excel reads with |
| `comma`, `semicolon`, `tab` | pin it explicitly |
| *omitted* | keep what the file already uses |

**The choice sticks.** Only a brand-new file follows the culture; after that,
writes preserve whatever separator the file has, so a solve run on a machine in
another locale never reflows the committed record. Reading detects the separator
from the header line and accepts a decimal comma or point either way, so a file
written in Prague opens correctly on an English machine and back again.

**Writes are protected.** They take an exclusive lock (`.results.lock`) across
read-modify-write, so two solvers running at once cannot drop each other's work;
the previous table is kept as `results.csv.bak`; the new one is written to a temp
file and renamed over the target, so a crash cannot leave a half-written record.
Merging keeps the best of each dimension independently — shortest solution, and
separately highest bound — so re-running is always safe. If the CSV is open in
Excel the rename fails; the solver says so and leaves the new table as
`results.csv.tmp` rather than throwing the run away.

## `reduce` — what the analysis can freeze

```
ffsolve reduce <level>
ffsolve reduce --all
```

Reports how many models the automatic analysis proves immobile. The `mobile`
count is exactly what goes into the state key, so it is the number to watch when
simplifying a level by hand.

## `info` — level facts

```
ffsolve info gems
```

Room size, model counts by weight, the move symbols and branching factor, goals,
and whether the freshly-loaded room is settled and solvable.

## `bench` — simulation throughput

```
ffsolve bench map --rounds 200
```

Replays a reference solution repeatedly and reports moves/sec and bytes
allocated. Allocation should stay at zero — if it doesn't, something in the hot
path regressed.

---

# Working with modified levels

The editor writes an ordinary level JSON, so **a modified level is just another
level name**:

```
ffsolve solve corals-simple --seconds 600
```

If you saved it outside `solver/levels`, point the solver at that directory:

```
ffsolve solve corals-simple --levels D:\experiments\levels
```

Modified levels are deliberately *not* added to `levels/index.json`, so `--all`
and the test sweep ignore them.

**The important part:** simplifying changes the physics — a frozen item cannot
fall, a deleted one cannot be stood on — so a solution found on a modified level
is not automatically legal in the real one. Saved levels record where they came
from, and `solve` re-checks its answer against the original automatically:

```
solved: expanded 9,625, stored 15,629, f=34, 0.2 s
source     DOES NOT solve 'start' (move 4 ('r') is not possible) - this answer is
           only valid for the simplified room
solution   34 moves - verified, PROVABLY SHORTEST
```

That example is real: freezing one pillar in `start` gave a 34-move answer
against the real optimum of 54 — and it was worthless. Treat a modified level as
a way to explore; the `source` line is what says whether you found anything.

When the answer **does** replay on the original, it is recorded against that
level too — a move string that solves `gems` is a solution to `gems` whichever
room it was found in. The proof is not transferred, though: the simplified room
has a different state space, so the row is marked unproven, keeps the bound the
real level earned on its own, and names the room it came from in `source`. Save
the moves with `--out solutions\<real level>.lua`; they are not kept otherwise.

To check a solution against any level by hand:

```
ffsolve verify start --moves <the move string>
```

# Level editor

```
dotnet run --project src\FishFillets.Editor -c Release
```

A complete automatic reducer is not feasible — deciding whether an item can ever
move is as hard as solving the level ([`docs/004`](docs/004-2026-08-14-from-scratch-solver.md)).
But a person can see at a glance that a crate is scenery, and `ffedit` lets that
judgement be applied.

| action | how |
| --- | --- |
| select an item | click it, or pick it from the list |
| move it | arrow keys, or drag |
| **freeze into the wall** | **F** — cells stay solid, model leaves the state space |
| delete it | **Delete** |
| draw walls | toggle **Draw walls**, then click/drag; right-drag erases |
| save | **Save as…** — writes a level JSON and stamps the source level |

The status bar runs the real `LevelReduction` and builds the level through the
actual engine after every edit, so it shows the live **mobile-model count** and
reports overlapping models exactly as the solver would reject them. Watching
"mobile 7 → 5" is watching the search space shrink.

# Tests

`dotnet test` is the regression suite — 204 tests, about a minute. Run it after
any change to `FishFillets.Physics`. It covers five things (see
[`docs/002`](docs/002-2026-08-14-test-project.md), [`docs/004`](docs/004-2026-08-14-from-scratch-solver.md)):

- **the corpus**, one case per level: all 80 recorded solutions must replay to
  *Solved*. This is what proves the port matches the real game.
- **invalid solutions**, that wrong answers are rejected — and at the right place.
  A validator that accepts every real solution is worthless if it also accepts bad
  ones.
- **individual rules**, in hand-built rooms: pushing by power, turning, falling,
  automatic escape, death by stress.
- **the level reduction**, checked against all 80 solutions — nothing it freezes
  may ever move during real play.
- **the results record**, that a run survives the CSV round trip, that a merge
  never loses a better result, and that the world-map parse still yields a branch
  for every level.

`ffsolve verify --all` does the corpus half on its own, which is what
`build-solver.ps1 -Publish` uses to smoke-test the AOT binary.

# Level data

`solver/levels/*.json` is generated from the game's own Lua by the browser port's
loader, and checked in so this project builds from a clean clone with no web
toolchain. Regenerate it (needs Node and a working `web/` setup) only if level
content changes:

```
scripts\export-levels.ps1        # from the repo root
```
