# 029 — Window optimiser on the main solver's machinery

The window optimiser (docs/003, `ffsolve improve`) had not changed since August.
It still worked, but it used none of what the main solver gained since:

- one thread;
- a full room snapshot restored eight times per expansion;
- the old `StateSet` with the wide key;
- no reversal skipping;
- identical items told apart;
- no time limit.

This entry moves its search onto the main solver's parts and makes it parallel.

## What changed

- **The search inside a window.** It now uses the main A*'s machinery:
  - the level reduction's mobile models;
  - the bit-packed key in a `StateStore`;
  - a `BucketQueue`;
  - the undo restore between the moves of one expansion (docs/011, docs/027).

  The heuristic is unchanged: the fish's Manhattan distance to the target
  state, plus a provable turn.
- **Rounds, in parallel.**
  - Each pass searches every window of the current solution side by side. Every
    worker has its own room, table and queue.
  - The shortcuts found are then spliced in left to right, and one that
    overlaps a shortcut already taken is skipped. Non-overlapping splices are
    independent, since each keeps both of its endpoint states.
  - The result, the node count and the windows searched are the same on any
    number of threads.
- **Windows known to be barren are not searched again.** A window's outcome
  depends only on its start state, its target and its length. One that found
  nothing is remembered by those three, so a later pass re-searches only the
  windows a splice changed. The old optimiser re-searched the whole solution
  on every pass.
- **Identical items merged at the target** (docs/022). The target is matched on
  the canonical key, so a window may end with identical items swapped. That
  is the same room to the physics. Every splice is replayed before it is kept,
  and must end in the same final room, solved.
- **Reversal skipping, with a window's own rule.**
  - After a fast turn, or a fast vertical step, the opposite symbol returns to
    the predecessor and is not tried (docs/028).
  - After a fast horizontal step, docs/028 drops the turn round, since a
    shortest solution can always postpone it. That argument needs a next
    horizontal move or the end of the path. In a window the end is a fixed
    target, and the target can require exactly that facing. So the turn is
    simulated, but kept only when it is the target itself.
- **`--threads`, `--seconds`, `--no-merge` and `--no-skip-reversals`** on
  `improve`. `--out` is rewritten after every pass that shortens the solution,
  and only with a replayed solution. A stopped run therefore leaves the best
  so far on disk.
- **`StateStore.Clear`.** A nearly empty table now zeroes only the chains it
  used. A window store is cleared thousands of times, mostly with a handful
  of states in a bucket array grown by an earlier, bigger window.

## Measured

Window 18, the same shortcuts found as in docs/003, process wall time:

| level | before | now, 1 thread | now, 12 threads | nodes before -> now |
|---|---|---|---|---|
| hole (465 -> 463) | 35.0 s | 8.4 s | 3.3 s | 1,909,727 -> 952,606 |
| linux (656 -> 652) | 51 s (Aug.) | 3.7 s | — | — -> 573,234 |

- **Reversal skipping** saves 10-13 % of the nodes and 6-14 % of the time:
  - hole: 1,061,605 -> 952,606 nodes;
  - linux: 657,317 -> 573,234 nodes.
- **Merging identical items** changes nothing on hole or linux, which have no
  identical items.
- **On 12 threads hole gains only 2.6x.** Its passes are short, and one
  expensive window holds up the end of each. Bigger windows parallelise
  better (barrel, below).

## Tests

`WindowRegressionTests`, 9 tests:

- **The corpus shortcuts.** linux 656 -> 652 and hole 465 -> 463 at window 18:
  - the length is pinned exactly;
  - the nodes are pinned as an upper bound;
  - the result, nodes and windows searched must be identical on 1 and 8 threads.
- **Reversal skipping.** The same answer with fewer than 95 % of the nodes.
- **The window rule.** On `exit-detour`, "step left, turn right" is the only
  2-move path to its target, and must be found. docs/028's rule alone would
  never try the turn.
- **Identical items.** On `identical-blocks`, breadth first finds a room
  reached sooner with the blocks the other way round. The window must find
  the shorter way to it only when merged, and the optimiser must splice it in.
- **A time limit already passed.** Nothing is searched, the input comes back
  unchanged, and the run is marked as cut short.
- **A time limit inside one long window.** corals at window 60 with no node
  limit must still stop within seconds of a 300 ms limit.
- **Reporting and barren windows.**
  - Every reported solution replays, and each is shorter than the last.
  - Later passes search only the windows a splice touched.
- **`StateStore.Clear`.** Full and sparse clears both forget everything.

The existing `WindowOptimizerTests` and the micro `Window_RemovesTheDetour`
rows pass unchanged. The suite: 1113 pass, and the same 9 `KnownBug` cells
fail as before.

## barrel

The run started from `solutions/barrel.lua` at 1231 moves, two moves above the
hall-of-fame 1229 (docs/003, docs/006). Settings: 12 threads, 5M nodes per
window, at most 1200 s per window size. Each size started from the best
solution so far:

| window | windows searched | nodes | time | result |
|---|---|---|---|---|
| 20 | 1,230 (all) | — | 19.6 s | 1231 |
| 24 | 1,230 (all) | — | 81.5 s | 1231 |
| 28 | 1,230 (all) | 59.5M | 297 s | 1231 |
| 32 | 1,230 + 203 in 4 more passes | 225.6M | 1138 s | **1223** |
| 36 | 250 of 1,222 | 243.5M | 1263 s, time limit | 1223 |
| 40 | 177 of 1,222 | 249.0M | 1298 s, time limit | 1223 |

- **The new record.** **1223 moves**, 6 below the hall of fame and 14 below
  the bundled 1237. It is verified and saved in `solutions/barrel.lua`.
- **One spot paid four times.** Window 32 found a 32 -> 30 shortcut at
  [357..389]. Each splice opened a new one next to it, and the barren cache
  meant passes 2-5 searched only 61, 44, 49 and 49 windows (about 15 s each).
  The old optimiser would have re-searched all 1,230 windows on every pass, at
  about 18 minutes per pass.
- **Cost grows ~4x per +4 moves of window.** Window 36 and above cannot cover
  the whole solution in 20 minutes; they only got through its first ~20 %.
- **The barren cache does not survive a run.** A longer run at 36-40 would
  have to start again from window 1. Persisting the cache, or stepping
  `--seconds` in chunks, is listed under Open.

## corals and duckie: no shortcut at any window size

- **corals.** From the bundled 252 moves (record 242), windows 8-48 searched
  every window and found nothing. Window 40 now takes 82 s, against 496 s in
  docs/003. Window 44 took 6 min and window 48 took 19 min. Window 52
  searched 76 of 251 windows.
- **The time limit did not hold.** Window 52 ran 2.6 h on a 40-minute limit:
  - the limit was checked only between windows;
  - 50M nodes per window on 12 threads also outgrew the RAM.

  Since this entry, the window search polls the limit every 4,096 expansions.
  A window cut short is not marked barren.
  `ATimeLimit_CutsALongWindowShort` pins this.
- **duckie.** From the bundled 98 moves, windows 8-40 found nothing, although
  A* proved 96 optimal. A scratch probe compared the two move strings by
  canonical room state. They share only the start and the first 4 moves, and
  even their final rooms differ. A splice keeps both of its endpoints, so no
  window can turn the 98 into the 96.

## The branch-final levels

The levels that end a branch on the world map, the ones with a poster, in one
detached run. Each started from `solutions/` if it had a file there, else from
the bundled solution. Windows were 16, 20, 24, 28 and 32, each from the best
so far, with 5M nodes per window, 12 threads and 600 s per window size:

| level | from | to | hall of fame | time |
|---|---|---|---|---|
| gods | 533 | 533 | 533 | 2 min |
| atlantis | 572 | 572 | 531 | 7 min |
| propulsion | 1964 | **1962** | 1964 | 18 min |
| turtle | 620 | 620 | 620 | 10 min |
| grail | 1691 | 1691 | 1681 | 18 min |
| floppy | 946 | **944** | 890 | 57 min |
| linux | 652 | 652 | 596 | 2 min |
| map | 2097 | **2075** | 1541 | 32 min |

- **propulsion, 1962.** This is 2 below the hall of fame, which is also the
  bundled solution. Window 24 found it at [858..882].
- **map, 2075.** 22 moves shorter, from 9 shortcuts at windows 20-28. It is
  still far above Gabor Braun's 1541, which takes a different route.
- **floppy, 944.** Its window sizes ran 767-1538 s on a 600 s limit, at about
  1/15 of the usual speed. The same stage re-run the next morning took 68 s,
  so the cause was outside the solver: the machine was loaded or throttled.
  Its window searches are short, under 6 s each on one thread.
- **The three are recorded.** They are in `solutions/` and in `results.csv`
  with method `window`, the run's nodes and seconds. All three are verified.

## Six more long levels: nothing to find

The same run on the six longest levels not windowed before. In all six the
bundled solution is already the hall of fame's:

| level | moves | time | windows 28-32 |
|---|---|---|---|
| rush | 852 | 1 min | all searched |
| experiments | 706 | 23 min | 350 and 335 of 705, time limit |
| hardware | 629 | 2 min | all searched |
| emulator | 620 | 16 min | window 32: 409 of 619, time limit |
| cancan | 578 | 1 min | all searched |
| warcraft | 573 | 1 min | all searched |

No window shortened any of them. These solutions look locally tight, and only
a different route could beat them. The 600 s limit held each time,
600.1-600.3 s, now that it is checked inside a window too.

`results.xlsx` now also greens a `bundled` cell when the bundled solution is
at least as short as the hall of fame's: 68 levels.

## The rest of Branch of the New Generation, electromagnet and redhat

The branch's remaining levels, plus electromagnet and redhat, in the same
run. Hanoi, nowall, hole and linux had been windowed in docs/003, and rush
above. All six start from a bundled solution that is already the hall of
fame's:

| level | from | to | time |
|---|---|---|---|
| fdto | 243 | 243 | 9 min |
| rotate | 213 | 213 | 1 min |
| key | 524 | **522** | 2 min |
| keys | 281 | 281 | 15 min, window 32 cut by the limit |
| electromagnet | 458 | 458 | 9 min |
| redhat | 287 | **283** | 2 min |

- **Two new records.**
  - key: 522, 2 below David Glass's 524. Window 32 found it at [279..311].
  - redhat: 283, 4 below Mattia Onofri's 287. Windows 16 and 28 found it at
    [225..241] and [217..245].
- **Both are recorded.** They are verified, saved in `solutions/` and
  recorded in `results.csv`.

## The five levels short of the hall of fame that only the old optimiser had seen

Of the 11 levels where no solution as short as the hall of fame's was known,
6 were windowed above. These are the other 5. Windows were 16-40, with 5M
nodes per window and 600 s per window size. A reboot cut the run during city;
it was resumed at city's window 36.

| level | from | to | hall of fame | windows 36 / 40 |
|---|---|---|---|---|
| hole | 463 | **441** | 459 | all searched |
| city | 485 | 485 | 481 | all searched |
| nowall | 420 | 420 | 413 | 230 and 58 of 419, time limit |
| pavement | 512 | 512 | 500 | 274 and 147 of 511, time limit |
| hanoi | 432 | 432 | 402 | all searched, 8 s and 16 s |

- **hole, 441.** This is 18 below the hall of fame (Masaaki Irie) and 24
  below the bundled 465. Window 28 found nine 2-4 move shortcuts around moves
  113-232, and window 32 found one more. The old optimiser had stopped at
  window 18. The solution is verified, saved in `solutions/hole.lua` and
  recorded in `results.csv`.
- **hanoi is cheap.** Window 40 took 16 s for the whole solution, so much bigger
  windows are within reach there.
- **nowall and pavement hit the time limit** from window 36 on. The
  `--window-log` probes above show why: those windows run into the node limit.

## Later fixes (2026-11-21)

- **Only full-length windows.** A window used to start at every move, so the
  last `window - 1` were shorter tail windows, like pavement's 511 windows of
  32 in 512 moves instead of 481. Each of them lies inside the last full
  window, which finds the shortest path to the end, so they found nothing new.
  They cost up to 9 % of a pass at window 48. Now the last window ends at the
  end of the solution, and with a stride the last full window is added off
  the stride. `EveryWindow_IsReported_WithHowFarItGot` and
  `AStride_StillCoversTheEnd` pin both.
- **`--window-log FILE`.** It prints one line per window to the console and
  appends it to a CSV: outcome (`shortcut`, `none`, `node-limit`,
  `time-limit`), the bound reached, nodes, stored states and seconds.
  `WindowOptions.WindowDone` reports it.
- **Windows' power throttling.** A detached run has a hidden window and below-normal
  priority. Windows 11 ran it in efficiency mode: 12 workers "Ready" but
  sharing about 3 cores. `ffsolve` now opts out at start
  (`PowerThrottling.cs`). City at window 36 for 30 s went from 11.6M to
  25.4M nodes, the same as a foreground run. The machine was also held at
  41 % clock by its firmware, because the battery is worn to 19 % of its
  capacity. That is a hardware limit, outside the solver.
- **Pavement probes, 20M nodes per window.** The bound reached grows about
  1.36x per move:

  | window | where | outcome | bound reached | nodes | stored |
  |---|---|---|---|---|---|
  | 48 | 0-48 | none | 48/48 | 1.9M | 1.9M |
  | 48 | 64-112 | none | 48/48 | 11.3M | 11.3M |
  | 48 | 128-176 | cut | 44/48 | 13.6M | 24.2M |
  | 64 | 0-64 | cut | 55/64 | 16.7M | 28.6M |
  | 100 | 0-100 | node limit | 58/100 | 20M | 33.2M |

  A window of 100 would need around 10^12 nodes. A* reached bound 149 on the
  whole level, but that only says that no solution is shorter than 149. A
  window has to reach its own length to rule a shortcut out, which is as hard
  as proving a level that long optimal. The window heuristic sees only the
  fish, so this is what an item-aware heuristic would change.

- **Growing windows with per-window limits, resumable (`improve --grow`).**
  - `--grow N` repeats the rounds at window + N, + 2N, and so on. It stops
    after the first size at which some window hit a per-window limit: that
    size is as far as the limits reach.
  - `--window-seconds` and `--window-memory` are per-window limits, like
    `--nodes` (`--nodes 0` drops the node limit). The memory is what the
    worker's table and queue hold.
  - `--order spread` (the default at first, sequential again since; the user
    found the log easier to follow in order) searches one window per thread at 1/(t+1),
    2/(t+1)... of the solution first. After that it always takes the middle of
    the widest stretch not yet searched. A run stopped early has therefore
    sampled the whole solution evenly, instead of only its start.
  - `--state FILE` appends every window as it ends. A rerun skips the windows
    it covers: ruled out, or stopped by a limit no smaller than the current
    one. The id is the two rooms and the length, so a window stays known
    after a splice elsewhere moves it.
  - `--out` is now rewritten the moment a window finds a shortcut, with the
    round's shortcuts so far spliced into a replayed copy. `--resume` starts
    from it. Stopping and rerunning the same command loses only the windows
    in flight.
  - Tests: `SpreadOrder_SeedsEvenly_ThenSplitsTheWidestGap`,
    `PerWindowLimits_StopAWindow_NotTheRun` and
    `KnownWindows_AreSkipped_AndAShortcutIsSavedAtOnce`. A finished round gives
    the same moves, nodes and windows in either order.

- **A stronger window bound (`--heuristic`, default `full`).**
  - **The fish, around the walls.** Each fish's distance to its target cell
    and facing is the walls-only BFS of the main solver's exit bound
    (`RelaxedDistance.ToCell`), not the straight line. The proven-immobile
    items count as walls.
  - **The items.** The bound also takes the item furthest from its target, if
    that is more. Sideways and up an item moves only when a fish move pushes
    it, one cell per move, and down for free. With identical items merged, an
    item may take its twin's target. The fish are summed, the items are
    combined by their maximum, and the two by the larger.
  - **Two traps, caught by the new tests.**
    - An item touching the border slides out over it for free. linux moves
      one 10 cells up in 9 moves, so the items are measured only inside the
      room.
    - windoze's output plug lets a fish leave anywhere in the room, so there
      a fish's way out gets no bound. The old straight-line bound had this
      fault already, at 31 against 29 real moves.
  - **Tests (`WindowHeuristicTests`).**
    - On all 81 corpus solutions, the bound between any two steps up to 40
      apart, merged and kept apart, may not exceed the moves the solution
      takes between them.
    - On cannons, hole, start, briefcase and barrel, every window finds
      exactly what a search with no bound finds, and each stronger bound
      searches no more nodes.
  - **Pavement, the same probe windows** (20M nodes, 240 s, 2 threads):

    | window | where | Manhattan | full |
    |---|---|---|---|
    | 48 | 0-48 | none, 1.89M nodes, 20 s | none, 1.02M nodes, 15 s |
    | 48 | 64-112 | none, 11.3M nodes, 183 s | none, 5.55M nodes, 117 s |
    | 48 | 128-176 | bound 44 | bound 46 |
    | 48 | 192-240 | bound 36 | bound 38 |
    | 64 | 0-64 | bound 55 | bound 56 |
    | 100 | 0-100 | bound 58 | bound 58 |

    - **Nodes:** proving a window empty takes half the nodes.
    - **Time:** a node costs about 30 % more, because the item bound loops over
      every item. So the time saved is 25-35 %, and a time-limited window
      gets 1-2 moves further.
    - **Overall:** a real gain, but not the step change that windows of 100
      would need.

- **With `--grow N`, the stride is N.** A window of W at any start lies
  inside a window of W + N that starts on a multiple of N. A window finds
  every shortcut inside it, so a round at W + N on a stride of N covers the
  whole round before it, with 1/N of the windows. In general, stride s at
  window W covers every stretch of up to W - s + 1 moves.
  `AStrideOfS_AtWPlusS_FindsWhatEveryWindowOfW_Finds` pins it on hole and
  linux.
  - **What it saves depends on how fast a window's cost grows** with its
    length. On hole, window 32 on stride 4 took 24M nodes and found 441.
    Window 28 on stride 1 took 29M nodes and found 443.
  - **The guarantee holds only for windows that finish.** A window that hits
    a limit leaves the smaller windows inside it uncovered. That happens only
    at the size where `--grow` stops anyway.

- **The per-window memory limit counts the window's own states.** At first
  it compared the worker's held capacity with the limit. A worker's table
  keeps whatever a bigger window grew it to, so after one big window every
  later window on that worker stopped at its first check. That happened to
  21 of the 22 corals windows at size 56 and 2 GB, after 4,096 nodes and
  0.02 s each. Now a window is charged its stored states times
  key + 29 bytes: the store's fields, plus its share of the buckets and of
  the queue. `TheMemoryLimit_ChargesAWindowOnlyForItsOwnStates` fails on the
  old accounting.
- **The log names the windows that hit a limit.** The line is printed when
  it happens, and the summary lists them, for example
  `[80..136] memory-limit at bound 52/56`. A size where a window hit a limit
  is still searched to its end. Only the next size is not started.

- **The per-window time limit is processor time.** The 10 minutes are
  charged in the window thread's own CPU time (`ThreadClock`,
  `GetThreadTimes`), not by the wall clock. Pavement's windows of 44 were
  running when the machine went into Modern Standby from 17:42:49 to
  17:56:27. They woke with 14 minutes on the clock and about 2 of real work,
  and four of them were cut within the same second, at bounds 38-42 of 44.
  Those four were taken out of the state file and searched again.
  `TheWindowClock_CountsWork_NotWaiting` pins it. The whole run's
  `--seconds` stays wall-clock.

- **The window optimiser's own record (`docs/window-results.csv`, `.xlsx`).**
  - Every round goes there, one level at one window size, stride and bound:
    windows ruled out, shortened, limited or not reached, the stretch it
    covered, the limits, and the cost.
  - The workbook's *levels* sheet shows how far each level has got.
  - `results.csv` now gets a window run only when it shortened the solution.
    This closes the open point about window runs overwriting the counts of
    the run that set the bound.
  - The rows of this entry's earlier runs were filled in from their logs.
    Those runs did not log node-limit hits, so they leave `limited` and
    `covered` blank.
  - `results.xlsx` and the new workbook share one writer (`SheetWriter`), and
    `results.xlsx` comes out byte for byte the same.

## Growing runs with every change above (2026-11-22)

Settings: `--window 16 --grow 4` (stride 4), 4 threads, `--nodes 0`,
`--window-seconds 600 --window-memory 6`, `--heuristic full`.

- **redhat, regression check:** from the bundled original (287), it again finds 283.
- **map: 2075 → 2001** (74 shorter, 126 below the game's 2127; hall of fame 1541).
  - The run had 16 passes, searched 4,668 windows, found 28 shortcuts in 2.0G nodes and took 5.4 h.
  - Window 36 alone went from 2035 to 2001. The stretch at moves 1108-1144 shortened three times, 36 → 34 → 32 → 28.
  - 18 windows of 36 hit the time limit, around moves 444-528, 588-628 and 1372-1428, so window 40 was not started.
  - The result was replayed by `ffsolve verify` and by the web port's physics, and
    promoted to `solutions/map.lua` and `legacy/solution/map.lua`. The game's
    original stays in `old/`.
- **The queue.** The same settings then run level after level:
  1. the six levels the window optimiser had shortened before;
  2. every level not yet run this way and not proven optimal, longest known solution first.

  Verified shortenings are promoted as they come in.

  | level | from | to | last window |
  |---|---|---|---|
  | propulsion | 1962 | **1940** | 48, 2 windows at the time limit; 2.8 h |
  | barrel | 1223 | **1219** | 40, 1 window at the time limit; 37 min |
  | floppy | 944 | 944 | 40, 2 windows at the time limit; 35 min |
  | linux | 652 | **650** | 68, 4 windows at the time limit and 1 at 3 GB; 2.6 h |
  | key | 522 | 522 | 60, 2 windows at the time limit; 53 min |
  | hole | 441 | **439** | 52, 2 windows at the time limit; 34 min (bundled was 463) |
  | grail | 1691 | **1689** | 44, 2 windows at 3 GB (~94 B a state, 34M states, at 87-91 % of the time); 44 min |
  | rush | 852 | 852 | 92, 6 windows at 3 GB (57M states, at bound 86-88); 2.8 h |
  | experiments | 706 | 706 | 32, 3 windows at the time limit (moves 348-388); 20 min |
  | hardware | 629 | 629 | 64, 2 windows at the time limit (moves 332-400); 1.2 h |

  **Threads and memory.** The queue ran on 4 threads until linux's window 68. It then
  moved to 8 threads. Within 25 minutes those workers held 15 GB of the machine's 32 GB, with
  0.5 GB of commit left. A worker keeps its table at the size its biggest window grew it to.
  Windows of 68 reached 47M states, about 49 bytes each.

  The per-window limit therefore does not bound the whole process; threads times
  that limit does. The queue now runs 6 threads with `--window-memory 3`, at most
  about 18 GB.

  At 3 GB a window holds about 65M states. That is close to what the
  fastest windows reach in 10 minutes. linux's [416..484] stopped there at 567 s, bound
  66/68.

## Open

- **A last window that accepts any solved room.** At present the last window
  must end in the old solution's exact final room. That would not save
  duckie, whose routes split at move 4, but it frees the ending elsewhere.

- **Big windows on barrel cover only a part of the solution.** Window 36-40
  needs about 1.5-2 h per full pass on 12 threads. A run that resumes from a
  start index, or keeps its barren windows on disk, would let it finish in
  pieces.

- **The heuristic still ignores items.** A window where only items differ is
  pruned by the cost limit alone. That is where the remaining time goes.
- **Without `--moves`, `improve` starts from the bundled solution**, not from
  the shorter one in `solutions/`.
