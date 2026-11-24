# Fish Fillets solver

A console app that solves Fish Fillets levels — finds a solution for a level, or
proves the shortest one. No UI, no rendering, no dialogs, no scripting: just the
puzzle simulation (moving, pushing, falling, dying, escaping) and a search over
it.

Separate from the browser port in `web/`, which is a different effort with a
different goal. The two share the game's rules and its level content, nothing
else. See [`docs/`](docs) for this tree's log, and the repo-root `docs/` for the
port's.

**Status:** the physics port is verified (all 81 recorded reference solutions
replay to *Solved*). Nine levels are solved to **proven** optimality, all matching
the hall of fame; four more have solutions shorter than the ones bundled with the
game. `ffsolve results` is the live scoreboard.

## Layout

```
solver/
  src/FishFillets.Physics/   the rules: Level, Room, ApplyMove, IsSolved
  src/FishFillets.Search/    A*, the window optimiser, the level reduction, the smoother
  src/FishFillets.Cli/       the `ffsolve` console app
  src/FishFillets.Editor/    `ffedit`, a WPF editor for simplifying levels
  tests/                     MSTest suite (dotnet test)
  levels/                    exported level geometry (checked in, 82 levels)
  solutions/                 solutions shorter than the bundled ones
  docs/                      milestone log, hall of fame, results.csv, window-results.csv
```

## Getting started

Needs the [.NET 11 SDK](https://dotnet.microsoft.com/download). From the repo
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
`src\FishFillets.Cli\bin\Release\net11.0\<rid>\publish\`. It starts fast (no JIT
warm-up: `verify --all` takes 70 ms instead of 355 ms), but **for long searches the
JIT build is faster** — about 15-20% on `wreck`/`society`, thanks to dynamic PGO
([docs/016](docs/016-2026-11-13-dotnet-11-upgrade.md)). So use the exe for quick
commands and `dotnet src\FishFillets.Cli\bin\Release\net11.0\ffsolve.dll` (or
`dotnet run -c Release`) for anything that runs minutes. Publishing needs the
**"Desktop development with C++"** workload in the Visual Studio Installer;
nothing else here does.

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
| `--weight W` | 1.0 | inflate the heuristic. >1 drops the optimality proof. Rarely helps on its own — see `--work` |
| `--work N` | 0 | charge N extra per cell of a movable item on a fish's way out ([docs/013](docs/013-2026-08-17-reduction-fixes-and-work-heuristic.md)). Reaches levels A\* cannot; drops the optimality proof. Try 3, then 8 |
| `--no-stuck` | detection on | keep states where something is stuck for good. By default they are dropped: a fish that has to leave walled in, or a goal_out item that can never move again ([docs/020](docs/020-2026-11-13-stuck-detection-by-default.md)). Always sound; 61–63% of what plain A\* stores on wc and society. Switches itself off after ~1M expansions if it has neither dropped a state nor raised an estimate |
| `--no-merge` | merging on | keep identical items apart. By default physically identical items (same kind, shape, weight, facing, no goal) have their positions sorted in the state key, so rooms where they only swapped roles are stored once ([docs/022](docs/022-2026-11-14-merging-identical-items.md)). Changes no answer; gems proves its optimum in 0.49 M expansions instead of 2.93 M |
| `--no-skip-reversals` | skipping on | also simulate the opposite of the fast move a state was reached by. By default, after a turn or a step that moved only the fish, the opposite symbol is skipped before the physics runs: turning or stepping straight back returns to the predecessor, and turning round right after a horizontal step is never needed by a shortest solution ([docs/028](docs/028-2026-11-19-skip-reversal-moves.md)). Changes no answer's length; 1-6 % fewer expansions |
| `--no-stuck-dist` | distances on | keep the dead ends, but leave the estimate at the walls-only distances. By default the fish's distances out are measured with everything stuck counted as wall ([docs/021](docs/021-2026-11-14-stuck-walls-in-the-heuristic.md)) — admissible, and on library-type rooms, where an item wedges a shortcut, much tighter |
| `--macro` | off | search over decisions (travel, then one thing that changes the world) instead of key presses — see below |
| `--threads N` | all cores | expand nodes in parallel ([docs/019](docs/019-2026-11-13-performance-p1-p2-p4.md)). At weight 1 the answer and every count are identical to `--threads 1`, stuck detection included. `--work` and `--partial-order` run serially |
| `--out FILE` | — | write the solution as `saved_moves = '…'` |
| `--progress N` | 1 | progress line every N seconds (`0` silences) |
| `--quiet` | off | no progress lines |
| `--no-record` | off | don't merge the run into `docs/results.csv` |

```
ffsolve solve start                                  # solves in ~1.5 s
ffsolve solve stairs --nodes 60000000 --seconds 600  # needs a bigger budget
ffsolve solve cabin1 --work 3 --seconds 300          # give up optimality for reach
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

**Memory is what runs out first**, not CPU — roughly 40-60 bytes per stored state
(`bench --suite` prints the exact figure), with about 1.35 states stored per
expansion, so 20 M nodes is about 1.5 GB. Raise `--nodes` only as far as RAM allows.
`society` ran 70 M expansions (95 M states, ~4.3 GB) in 110 s on 12 cores.

### `--layouts`

A search over *item layouts* rather than fish positions: a node is a layout plus
which fish acted last, and the fish that did not act is carried as a **field** —
the cost of it standing at each square — that it pays from only when it next
acts, instead of being parked somewhere at every event. Edges are pushes, drops
and exits; every answer is replayed concretely and checked before it is reported.

**It is a finder, not a proof**, like `--macro` and `--items`: no lower bound is
recorded, and the length can exceed the model's own cost. A fish waiting *inside*
an event is tried only where a moved item ends, sweeps or comes to rest on it
("interaction" edges, which is what solves `wc`); a support handed over without
anything moving is not expressible yet. The counters it prints after the result
(layouts, entries, travel plans, bound and interaction edges, reconstruction
rejects, physics calls) are explained in `docs/024`.

```
ffsolve solve noground --layouts --no-record      # 44 moves (the optimum) in ~30 s
```

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
| `--work N` | 0 | as above |
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
It searches the windows of each pass in parallel with the main solver's state
table, skips windows already known to hold nothing, and matches a window's end
up to swapped identical items ([`docs/029`](docs/029-2026-11-19-window-optimizer-rework.md)).
Without `--moves` it starts from `legacy/solution/`, which holds the best solution known (root docs/085).

| option | default | meaning |
| --- | --- | --- |
| `--window N` | 16 | window length in moves. Bigger reaches further and costs exponentially more |
| `--stride N` | 1 | gap between window starts |
| `--nodes N` | 2,000,000 | per-window search budget (each thread holds one window's table) |
| `--threads N` | all cores | windows searched side by side; the result does not depend on it |
| `--seconds N` | none | stop after N s, keeping what was found |
| `--no-merge` | off | match a window's end state exactly, not up to swapped identical items |
| `--no-skip-reversals` | off | also simulate moves that undo a fast move |
| `--grow N`, `--max-window N` | off | after a full round at one size, grow the window by N and go again; stops after the first size at which a window hits a per-window limit, or at `--max-window`. The stride then defaults to N. That loses nothing: a window of the last size, starting anywhere, lies inside one of this size starting on the stride, and a window finds every shortcut inside it. In general a stride s at window W covers every stretch of up to W - s + 1 moves |
| `--grow-per-start` | off | with `--grow`: a window that hits a per-window limit blocks only its own start room, not the whole run. Larger windows from that room are skipped (logged as `blocked`); the others go on growing until every window of a size is blocked, or `--max-window`/`--seconds` ([docs/030](docs/030-2026-11-22-incremental-windows.md)) |
| `--window-seconds N`, `--window-memory GB` | none | per-window limits, like `--nodes`: a window that reaches one gives up and reports how far it got. `--nodes 0` drops the node limit. The memory is the worker's state table and queue |
| `--order sequential\|spread` | sequential | the order windows are searched in: from the start of the solution to its end. `spread` starts one window per thread at 1/(t+1), 2/(t+1)... of the solution, then always the middle of the widest stretch not yet searched, so a run stopped early has sampled the whole solution evenly. A finished round gives the same result in either order |
| `--heuristic full\|walls\|manhattan` | full | the window search's lower bound. `walls`: each fish's distance to its target around the walls, facing included (the walls-only BFS of the main solver's exit bound). `full`: that, or the item furthest from its target if more (sideways and up it moves only when pushed, one cell a move; down is free; sliding out over the border is free). `manhattan`: straight lines, as before docs/029's rework. All are admissible |
| `--state FILE` | — | every window searched, appended as it ends. A rerun skips each window it covers: one ruled out, or one stopped by a limit no smaller than the current one. Windows are known by their two rooms and length, so they stay known after a splice elsewhere |
| `--resume` | off | start from the solution in `--out`, if there is one. `--out` is rewritten as soon as a window finds a shortcut, not only after a round, so stop and rerun the same command at any time |
| `--window-log FILE` | — | one line per window searched, to the console and appended to FILE as CSV (`level;window;pass;start;end;length;outcome;bound;nodes;stored;seconds`). `outcome` is `shortcut`, `none` (ruled out), `node-limit` or `time-limit`; `bound` is how far the search got: no path between the window's two rooms is shorter |
| `--moves S` | the bundled solution | improve this string instead |
| `--out FILE` | — | write the result; rewritten after every pass that shortens it |
| `--quiet` | off | as above |
| `--no-record` | off | don't put a shortened solution into `docs/results.csv`. A run that shortens nothing never goes there |
| `--no-window-record` | off | don't add the rounds to `docs/window-results.csv` (below) |

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

## `smooth` — reorder a solution so it reads like a person's

```
ffsolve smooth <level>                 # legacy/solution against legacy/solution/old
ffsolve smooth <level> --from F.lua [--reference R.lua | --no-reference] [--out F.lua]
ffsolve smooth --all [--write]         # every level whose original was replaced
```

A shortcut from `improve`, or an A* solution, is shortest but often switches fish on
nearly every move (`DdDdDdDd`). `smooth` keeps the length and changes only the order:

1. Where the solution and the reference (the game's original) pass through the same
   two states, and the reference needs no more moves between them, it takes the
   reference's moves (`luuu` → `uuul`) and locks them.
2. In what is left, it swaps a run of one fish with the next run of the other, or
   shifts one move by up to 8 places. A change is kept when the replay through
   it ends in the same state and the trace then passes through more of the
   reference's states, or the same number of them with fewer fish switches.

Both steps repeat until nothing changes. It is sound for the reason the window
splice is: equal state keys are interchangeable. As in A*, the keys merge identical
items that traded places, so such states still count as the same. It does no search and takes well
under a second on any level. `--write` puts a changed result into
`legacy/solution` and, where the level has one, `solver/solutions`. Details and
the first numbers: [`docs/031`](docs/031-2026-11-23-smooth.md).

## `results` — the scoreboard

```
ffsolve results              # every level, with the hall of fame alongside
ffsolve results --unsolved   # only what is still open, and how close the bound got
ffsolve results --rebuild    # refresh the file from the corpus, without searching
ffsolve results --rebuild --separator culture   # rewrite it for the local Excel
```

### The window optimiser's record

`improve` writes every round, one level at one window size, stride and bound,
to **[`docs/window-results.csv`](docs/window-results.csv)** and its workbook
`window-results.xlsx`. They are kept apart from `results.csv`, which records
solutions and bounds. A round run again replaces its row. A resumed round
counts the windows its `--state` file already knew.

| column | |
| --- | --- |
| `level`, `window`, `stride`, `heuristic` | the round |
| `from`, `to` | solution length before and after it |
| `windows`, `ruledOut`, `shortcuts`, `limited`, `notSearched` | its windows: proven to hold no shortcut, shortened, stopped by a per-window limit, not reached before the run's time limit |
| `covered` | the longest stretch the round proved has no shortcut anywhere: `window - stride + 1`. Only when every window was searched and none was limited |
| `nodeLimit`, `windowSeconds`, `windowMemoryGb` | the per-window limits |
| `nodes`, `seconds`, `note`, `recorded` | the cost, and what to know about the row |

The workbook's **levels** sheet sums up each level: its length now (green when
it matches or beats the hall of fame), the longest stretch covered, the largest
window whose round finished, and the largest window tried with how many of its
windows hit a limit. The **rounds** sheet lists every row of the CSV.

The rows before 2026-11-21 were taken from the run logs. Those runs did not
log which windows hit their node limit, so they have no `limited` or
`covered`. Their note says so.

### The results record

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

Every write also produces **[`docs/results.xlsx`](docs/results.xlsx)**, the same
table as an Excel workbook: counts as numbers with thousands separators, seconds
with one decimal, `recorded` as a real date, a frozen bold header row, an
AutoFilter on every column for sorting and filtering, and fitted column widths.
Its `bound %` column gives the proven bound as a share of the shortest known
solution (the hall of fame's or ours), so sorting by it shows which unproven
levels are closest to a proof.
It is a view only — the CSV stays the record that is read back and merged, and
what diffs in git — so editing the workbook changes nothing. Its bytes depend only
on the table, so it changes in a commit only when the results do. If it is open in
Excel, the solver prints a warning, leaves the new copy as `results.xlsx.tmp` and
carries on; the run is not lost.

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

## `ladder` — incremental windows, measured

```
ffsolve ladder linux --from solutions\linux.lua --starts 400,460,500 --window 32 --max-window 64 --store-pruned
ffsolve ladder key --every 40 --window 32 --max-window 56 --threads 2 --log ladder.csv
```

For each start, grows the window by `--grow` (4) from `--window` to `--max-window` twice: a fresh search per
size, and one state table kept across the sizes (the ladder, re-scoring every stored state for each new target).
`--store-pruned` also keeps the children over the limit in the table. Prints both per size and the totals
(expansions, skipped, re-scored, seconds, table size); exits with 1 if outcome, bound or shortcut length ever differ.
Limits as in `improve` (`--nodes`, `--window-seconds`, `--window-memory`), charged to the whole ladder. An experiment,
not yet used by `improve` ([docs/030](docs/030-2026-11-22-incremental-windows.md)).

## `bench` — simulation throughput

```
ffsolve bench map --rounds 200
```

Replays a reference solution repeatedly and reports moves/sec and bytes
allocated. Allocation should stay at zero — if it doesn't, something in the hot
path regressed.

```
ffsolve bench --suite --out before.csv          # on the old build
ffsolve bench --suite --compare before.csv      # on the new one
```

Wall clock of fixed, node-limited A* searches (start, wc, cannons, wreck,
society, library; `--quick` runs only start and cannons), best of `--reps` (3).
Prints expansions/s, bytes per stored state (from the solver's own array sizes,
so exact) and the bound f reached, and with `--compare` the per-expansion
speed-up, the wall-clock ratio and the memory ratio against an earlier run —
flagging any level whose expansion count or bound changed, since then the search
itself changed, not just its speed. `--no-stuck` measures without stuck
detection. Short 12-thread runs vary by ±15%; use `--reps 7` before trusting a
small difference. Use it for every performance change
([docs/017](docs/017-2026-11-13-regression-suite-phase-2.md)).

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

**Before it searches**, `solve` replays the *original* level's own solution
against the modified room, which says whether you are still looking at the same
puzzle:

```
level      stairs-simple (40x30)
source     'stairs' - its 163-move solution still solves this room, so the edit
           kept the original path (and that is an upper bound)
```

A pass means the room is definitely solvable and you already have an upper bound.
A failure is **not** an error and does not stop the search — breaking the original
path is often the whole point of the edit, and the simplified room may well have a
different, shorter route. What it does predict is that an answer found here is
less likely to replay back on the real level, which is the check below.

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
| draw walls | toggle **Draw walls**, then click/drag; right-click or right-drag erases |
| erase walls | toggle **Erase walls**, then click/drag |
| **cut one cell off an item** | toggle **Erase tile**, then click cells of the selected item |
| **freeze by clicking** | toggle **Freeze on click**, then click any item |
| leave a tool | **Esc** (again to deselect) |
| save | **Save as…** — writes a level JSON and stamps the source level |

The tools are mutually exclusive and none of this is undoable, so only one click
behaviour is ever live at a time. **Erase tile** is scoped by the selection: with
an item selected only that item is cut, so a click that strays onto a neighbour
does nothing; with nothing selected the first click picks up whatever it lands
on. Cutting the last cell removes the model outright, and the anchor never moves,
so the cells that remain stay exactly where they were.

The status bar runs the real `LevelReduction` and builds the level through the
actual engine after every edit, so it shows the live **mobile-model count** and
reports overlapping models exactly as the solver would reject them. Watching
"mobile 7 → 5" is watching the search space shrink.

# Tests

`scripts\build-solver.ps1` runs the regression suite (`dotnet test` with
`--filter "TestCategory!=KnownBug"`) — ~580 tests, a few seconds. Run it after
any change to `FishFillets.Physics` or `FishFillets.Search`. It covers six things (see
[`docs/002`](docs/002-2026-08-14-test-project.md), [`docs/004`](docs/004-2026-08-14-from-scratch-solver.md)):

- **the corpus**, one case per level: all 81 recorded solutions must replay to
  *Solved*. This is what proves the port matches the real game.
- **invalid solutions**, that wrong answers are rejected — and at the right place.
  A validator that accepts every real solution is worthless if it also accepts bad
  ones.
- **individual rules**, in hand-built rooms: pushing by power, turning, falling,
  automatic escape, death by stress.
- **the level reduction**, checked against all 81 solutions — nothing it freezes
  may ever move during real play.
- **the results record**, that a run survives the CSV round trip, that a merge
  never loses a better result, and that the world-map parse still yields a branch
  for every level.
- **micro-levels** (`tests/.../Micro/Levels/*.lvl`, ASCII rooms of 5-30 moves,
  [`docs/015`](docs/015-2026-11-13-micro-level-regression-suite.md)): every solver
  variant against an independent full-state BFS, the heuristic and the pruning
  rules checked in *every* reachable state, and a node-count baseline
  (`Micro/micro-baseline.csv`, which also pins the exact effort on six proven
  corpus levels) that fails on regressions. Accept deliberate changes with
  `$env:UPDATE_BASELINE=1` and commit the CSV diff.
- **the physics fingerprint** (`Micro/physics-digest.csv`,
  [`docs/017`](docs/017-2026-11-13-regression-suite-phase-2.md)): a seeded
  6,000-move random walk per level, off any solution path, hashed - any change to
  what any move does, from any visited state, shows up.
- **fuzzing**: a fixed-seed smoke run of random rooms against the oracle in the
  normal suite; violations are attributed to open findings by mechanism, anything
  unexplained fails. For hunting, run more rooms - each new kind of failure is
  minimised into a ready-to-commit `.lvl` under `%TEMP%\ffwg-fuzz\`:
  `$env:FUZZ_COUNT=20000; dotnet test solver\FishFillets.Solver.slnx -c Release --filter TestCategory=Fuzz`

Checks pinned to open findings carry `[TestCategory("KnownBug")]` and are
expected to fail until fixed: `scripts\build-solver.ps1 -KnownBugs` runs only
those. When a fix turns its rows green, delete the level's `known-bug:` entry.

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
