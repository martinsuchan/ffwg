# 030 — Incremental windows and per-start growth (2026-11-22)

Two ideas for `improve --grow`, both measured next to the running window queue
(docs/029). The queue itself still uses the old build.

1. **The ladder.** A window of 36 moves from start *i* re-finds every state that the
   window of 32 from the same start stored. The ladder keeps one state table per start
   and moves the target from `S[i+W]` to `S[i+W+4]` to the next one after that. The g
   values do not depend on the target, so they stay valid. On each rung, every stored
   state gets a new estimate for the new target, and it goes back into the queue if
   g + h ≤ limit.
2. **Per-start growth.** So far, one window that hit a per-window limit stopped the
   whole level from growing. On linux, 5 of the ~150 windows of size 68 kept the other
   ~145 from reaching 72. With `--grow-per-start`, a limit blocks only its own start.

## What was built

- **`WindowSearch.Ladder`** (`FishFillets.Search/WindowOptimizer.cs`). It keeps a flag
  on each state:
  - *complete*: the state was expanded with all its children stored, so a pop skips it;
  - *open*: the state is expanded again when popped, and only the children it pruned
    before are generated;
  - *target-only*: the state was stored only as a possible goal.

  The time and memory limits are charged to the whole ladder, from its start. The
  existing `Search()` is unchanged, so the micro baseline is unchanged.
- **`--store-pruned`** keeps the children that are over the limit, or kept only as
  targets, in the table without queuing them. A later rung then finds them there
  instead of generating them again.
- **`ffsolve ladder <level> --starts a,b | --every N --window W --max-window M`.** For
  each start it runs a fresh search per rung and the ladder side by side, and prints
  both. It exits with 1 when the outcome, bound or shortcut length differ.
- **`improve --grow-per-start`** (`FishFillets.Cli/GrowPolicy.cs`).
  - A window that hits a limit blocks its start room, which is the first part of the
    window id.
  - Larger windows from that room are skipped and logged as `blocked`.
  - Growth stops only when every window of a size is blocked, or at `--max-window` or
    `--seconds`.
- **Tests.**
  - `LadderTests`: the ladder must agree with fresh windows on hole, cannons, start
    and briefcase, in both variants. Every shortcut must splice in and solve, and the
    ladder must never expand more than the fresh windows. The tests also cover the
    policy and its window count.
  - The full suite passes: 1224 tests.

## Experiment A: the ladder against fresh windows

One thread, 600 s and 1 GB per window. The ladder gets the same limits for the whole
start. Only rungs that both sides reached are counted.

| level, rungs | variant | expansions | time | largest table |
|---|---|---|---|---|
| hole, 16–32, every 40 | plain | 89 % | 77 % | 100 % |
| hole | store-pruned | 67 % | 68 % | 185 % |
| key (522), 32–56, 13 starts | plain | 80 % (55.5M vs 69.1M) | 83 % (728 s vs 882 s) | 102 % |
| key | store-pruned | 56 % (38.5M) | 67 % (598 s vs 887 s) | 154 % |
| linux (650), 32–64, 6 starts in 400–520 | plain | 73 % (128.6M vs 176.2M) | 75 % (1070 s vs 1430 s) | 100 % |
| linux | store-pruned | 43 % (68.1M vs 158.9M) | 53 % (672 s vs 1264 s) | 123 % |

- **There were no mismatches.** Every rung gave the same outcome, bound and shortcut
  length.
- **The saving grows with the window.**
  - linux, start 500, rung 60: 3.8M expansions instead of 10.3M, and 41 s instead of 90 s.
  - linux, start 400, rung 64: 0.8M instead of 5.8M.

  On linux the store-pruned saving of 47 % is close to the 56 % ceiling estimated from
  the queue's logs, which is the share of time spent on the smaller sizes of the same
  start.
- **The variant that keeps pruned children costs memory.** Its table is 1.2–1.9 times
  the largest fresh table. With the memory limit charged to the whole ladder, it hits
  that limit sooner.
  - linux, start 500: it hit the memory limit at rung 64, where the fresh window
    finished.
  - linux, start 520: it hit it at rung 60, one rung before the fresh window.

  The ladder is faster in time, but at the same memory limit it does not get as far.
- **Re-scoring is not free.** Each rung re-estimates every stored state: up to 16M
  states a rung on linux. This is already included in the times above.

## Experiment B: barrel with per-start growth

`improve barrel --grow-per-start --max-window 48 --window-memory 1.5`. It resumes from
a copy of the queue's state file, which had stopped at window 40 because of one window
that hit the time limit. It ran on one thread from 15:02 to 17:05, then on two threads
until its 4 h `--seconds`. In all, it searched 1,800 windows and 2.27 G nodes in ~6 h.

- **The run found shortcuts at window 44.** They are at moves 244–288 and 252–296,
  bringing barrel from 1219 to 1217.
- **Each splice changed the moves around it, and the windows there were new.**
  Smaller windows over them found more: 1217 → 1215 at window 16, 1215 → 1213 at
  window 28, and on down to 1205. Window 44 then found one more, 1205 → 1203.
  15 passes improved 6 times in all.

  Barrel is now **1203** (1223 shipped, 1219 after the queue). Each step passed both
  checks (`ffsolve verify` and the web replay) and is promoted to `solver/solutions`
  and `legacy/solution`.
- **20 starts hit 1.5 GB at window 44 and were blocked; none hit the time limit.**
  The other starts went on growing. Window 48 had begun (21 windows) when the 4 h ran
  out; the queue's barrel rerun goes on from this state file.

## Recommendation

- **`--grow-per-start`: use it for the queue.** It changes nothing in the search
  itself, and barrel alone gained 16 moves from it (1219 → 1203).

  The cost is longer runs: a level no longer stops at its first limit. Bound each run
  with `--max-window` and `--seconds`. A level such as experiments (stopped at 32 by
  3 windows) or hardware (stopped at 64 by 2 windows) would keep growing everywhere
  else.
- **The ladder: worth putting into `improve`, but not as a drop-in change.** The
  store-pruned variant halves the time on large windows. However, `improve` works in
  passes: every window of one size, then splices, then the next size. The ladder works
  one start at a time, which needs a different structure:
  - a worker takes one start and climbs until it finds a shortcut or hits a limit;
  - after a splice, the starts it touched begin again;
  - the memory limit belongs to the ladder, and it should be larger than one window's,
    or the ladder loses reach.

  This is the next step if the queue keeps spending its hours on the largest windows.

## Deployed: the queue on `--grow-per-start` (2026-11-22, 18:05)

The window queue (docs/029) moved to the JIT build with `--grow-per-start`, 5 threads
× 3 GB, 600 s per window, at most 4 h a level. It now reruns the levels it finished,
each from its own state file, then goes on to the levels it had not reached.

- Turtle was stopped at window 60 under the old settings, after it had found 620 → 618
  (promoted).
- Experiments had stopped at 32 because of 3 windows. It now ran on through 36, 40
  and 44: 485 windows, 21 at the time limit, 24 starts blocked, no shortcut yet. The
  queue was paused in window 48 so that corals could run (3 threads × 5 GB: corals'
  windows store up to 82M states in 600 s).

## Open

- A per-start B run on key and hole, which stopped at 60 and 52. It needs memory that
  the 6-thread queue is using now.
- Ladder integration in `improve`, as described above.
