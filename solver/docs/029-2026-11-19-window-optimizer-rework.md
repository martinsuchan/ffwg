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
- **A recorded window run overwrites the row's counts.** It replaces the
  `expanded`, `stored` and `seconds` that belonged to the run that set the
  bound (seen on hole, linux, map and barrel). Separate columns for the
  bound's run would fix that.
- **Without `--moves`, `improve` starts from the bundled solution**, not from
  the shorter one in `solutions/`.
