# 006 — A results record, and a level editor

2026-08-15

Two things that are about *working on* the solver rather than the solver itself: a
record of what it has achieved, and a WPF editor for simplifying levels by hand.

## The results record

Results lived in prose and terminal scrollback, which meant they went stale the
moment anything was re-run. `solver/docs/results.json` is now the record, written
by the solver itself:

```
ffsolve results              # the table, with the hall of fame alongside
ffsolve results --unsolved   # what is still open, and how close the bound got
```

Every `solve` and `improve` merges into it, keeping the best of each dimension
**independently**: the shortest solution, and separately the highest lower bound,
with a proven-optimal result always beating an unproven one. Re-running is
therefore safe - a weaker later run cannot overwrite a stronger earlier one.

**Failed runs are recorded too**, and that turned out to be the most useful part.
The `bound` field is how far the search proved no solution can be shorter than,
and the gap to the record says *why* a level is unfinished.

### Where things stand

Nine levels proven shortest, every one matching the hall of fame:

| | | | |
| --- | --- | --- | --- |
| cannons 39 | noground 44 | start 54 | snowman 66 |
| submarine 83 | wreck 91 | wc 100 | library 111 |
| stairs 163 | | | |

Four shorter than the bundled solution, from the window optimiser (docs/003):
`hole` 463, `linux` 652, `barrel` 1231, `map` 2097.

And a third category the record made visible: **`bathyscaph` has a bound of 82
against a record of 82.** We never found the moves, but every bucket below 82 was
drained without a finish, so no solution shorter than 82 exists - which proves the
hall-of-fame entry optimal without reproducing it. `ffsolve results` marks these
with `~`. It is a cheaper win than solving, needing only the bound to reach the
record.

### A correction worth recording

docs/005 sorted the unfinished levels into "budget-bound" and "heuristic-bound"
by how close the bound got at 90 s, and put `snowman` (f=42 against 66) firmly in
the second group. That was wrong: at 900 s it solved outright in 172 s. **A bound
at a given time limit is a much weaker signal than that classification implied.**
Bounds did improve across the board with 10x the budget - `gems` 55→57 (record
59), `cabin1` 154→162 (168), `steel` 145→153 (185), `duckie` 74→85 (98) - so
several more are probably within reach of memory rather than a better heuristic.

### Two bugs found by using it

- **Concurrent writes.** Running a batch and an `improve` at once meant two
  processes doing load-merge-save on the same file, silently dropping each
  other's work. `ResultsFile.Update` now does the read-modify-write under an
  exclusive lock file and renames a temp file over the target, so a reader never
  sees a half-written record either.
- **Progress reporting was useless where it mattered.** Single runs reported
  every 500,000 *expansions* - minutes apart on a slow level - and batch runs
  passed no callback at all, so a 40-minute run printed nothing until levels
  finished. Now time-based (`--progress SECONDS`, default 1 single / 10 batch),
  showing elapsed, bound, expanded, stored, rate and memory. The clock is only
  read every 4096 expansions, because a timestamp is a system call and at over a
  million expansions a second that is a real tax.

Batch results also save per level now rather than at the end, so a long run that
gets interrupted keeps what it finished.

One process lesson: a background job was filtered with
`Select-String "solved|solution"`, so when `stairs` failed it produced **zero
output** and the failure looked exactly like success. Filter for the failure
signatures too, or don't filter.

## The level editor

`solver/src/FishFillets.Editor` (`ffedit`), a small WPF app.

The motivation is docs/004's conclusion: a complete automatic reducer is not
feasible, because deciding whether an item can ever move is as hard as solving
the level. But a person looking at a room can see instantly that a crate is
scenery. The editor lets that judgement be applied directly.

Open a level from `solver/levels`, then:

- **click an item** to select it, **arrows** or drag to move it;
- **F** freezes it into the room's shape - the cells stay solid, but the model
  leaves the state space entirely. This is the main tool;
- **Delete** removes it;
- **Draw walls** paints barriers cell by cell (right-drag erases), for sealing off
  a region a fish has no business exploring;
- **Save as** writes a new level JSON alongside the others, so
  `ffsolve solve <name>` picks it up with no extra plumbing.

The status bar continuously reports **mobile model count** from the real
`LevelReduction`, and validates by building the level through the actual engine -
so overlapping models are reported exactly as the solver would reject them.
Watching "mobile 7 → 5" is watching the search space shrink.

### The part that matters most

**Simplifying a level changes the physics**, so an answer found on the simplified
room is not automatically legal in the real one - a frozen item cannot fall, and a
deleted one cannot be stood on. Saving therefore stamps
`LevelJson.SourceLevel`, and `ffsolve solve` re-verifies its answer against the
original automatically.

That guard earned its place on the first test. Freezing one heavy pillar in
`start` and solving gave:

```
solved: 34 moves - verified, PROVABLY SHORTEST
source  DOES NOT solve 'start' (move 4 ('r') is not possible)
```

34 moves against the real level's optimum of 54, found in 0.2 s instead of 1.3 s -
and completely invalid. Without the automatic re-check that reads like a triumph.

So the editor is a tool for *exploring* - for finding out whether a level becomes
tractable when a distraction is removed, and for building intuition about which
models actually matter. A simplified level whose solution also replays on the
original is a genuine result; one that does not is a hypothesis.

## Open

- The editor has no undo, and no test coverage beyond a headless round-trip probe
  of its document operations (load → freeze → save → reload through the engine).
- Edited levels land in `solver/levels` but are not in `index.json`, so
  `--all` and the test sweep ignore them. That is the intent, but it does mean
  they are easy to forget about.
- `improve` has no time limit at all and will run to fixpoint for hours; it was
  backgrounded next to a memory-hungry batch once, and the recorded timing for
  `map` (19,005 s) is contaminated by that contention rather than real.
