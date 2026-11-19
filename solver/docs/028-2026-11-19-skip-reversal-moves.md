# 028 — Skipping the opposite of a fast move

After a move that changed nothing but the fish, its opposite is not worth
trying:

- **turning straight back** after a turn;
- **stepping straight back down** after swimming up into open water, or up
  after swimming down;
- **turning round right after a horizontal step** into open water.

The first two return to the predecessor state. The third reaches a new state,
but one no shortest solution needs. Until now the solver simulated all of them.

## How often

A temporary counter in the serial loop, before the change. It counted children
that reach the store (after physics and the dead-end tests) and are exactly the
parent's predecessor:

| level | such children | made by a fast move |
|---|---|---|
| society | 9.9 % (890 k of 8.9 M) | 95 % |
| library | 10.2 % | 96 % |
| wreck | 8.9 % | 82 % |
| cannons | 11.3 % | 99 % |

The store already dropped all of those as dearer: g + 1 against at most g - 1.
They only cost time. The turn after a horizontal step is different. It is a new
state, so the store kept it and searched on from it.

## Why the third case is safe

This came out of a review question, and it is right. A fish facing right steps
right into open water. A left symbol now only turns it, and that turn can always
be left out of a shortest solution:

- **Facing matters to nothing but that fish's own left/right symbols.** In the
  physics, `IsLeft` is read only to decide between turn and step. Shapes do not
  mirror. Falls, pushes, deaths and exits ignore it.
- **So the turn commutes with every other move.** Those are vertical moves and
  the other fish's moves. It can wait until just before the fish's next
  horizontal move: right, left, up and right, up, left are equally long and end
  in the same state.
- **If nothing lies between the step and that next horizontal move**, the
  sequence is not shortest:
  - the next move turns back, and the pair cancels;
  - or it steps back left: step, turn, step = a single turn from the
    predecessor, two moves cheaper;
  - or the path ends with the turn, which never solves anything.
- **Duplicate detection does not break this**, although the rule depends on the
  edge a state was stored with. From any state on a shortest path, if every
  shortest continuation began with the skipped turn, postponing the turn gives
  one that begins with an unskipped move.

The step has to be a fast one, moving only the fish. A step that pushed an item
does not come back by stepping back, and then the argument fails.

## What changed

- **`Room.LastMoveWasTurn`.** It says whether a fast move was a turn on the
  spot rather than a step into open water.
- **The store's move byte.** The symbol index takes the low 6 bits. Two flags
  above it record a fast turn or a fast step that moved only the fish. That
  costs no memory: the bytes per state are unchanged on every bench level.
  `Reconstruct` masks the flags off.
- **The symbol layout.** The constructor checks it: four symbols per fish,
  up/down/left/right, so the opposite of index `s` is `s ^ 1`. After a fast move
  the solver skips `s ^ 1` before the physics runs.
- **`SolveOptions.SkipReversals`.** It is on by default. The CLI turns it off
  with `--no-skip-reversals` on `solve`, the batch and `bench`.
- **Where it applies.** The serial loop and parallel phase 1, which only reads
  the store. Plain A* only: under `--partial-order` a skipped symbol would
  change the sleep sets of later siblings.

## Tests

- **`ReversalSkipTests`.** Every micro-level plus start, wc, noground, cannons,
  wreck and submarine, run on 1 and 6 threads. With the skip and without it, the
  status, answer length and proven bound must match, and the answer must
  replay. On wreck it must generate under 97 % of the children and expand fewer
  nodes.
- **`micro-baseline.csv` rewritten.**
  - 126 of 267 cells changed their counts. No status and no answer length
    changed.
  - Every A* variant expands 4-6 % fewer nodes.
  - Corpus levels:

    | level | expansions | change |
    |---|---|---|
    | start | 217,606 -> 206,435 | -5.1 % |
    | wreck | 727,311 -> 682,864 | -6.1 % |
    | cannons | 157,588 -> 149,973 | -4.8 % |
    | wc | 76,266 -> 75,326 | -1.2 % |
    | submarine | 104,435 -> 103,877 | -0.5 % |
    | noground | 93,900 -> 93,313 | -0.6 % |

- **The matrix.** Its Optimal check compares every micro-level against the
  full-state BFS optimum, and it passes. 1097 tests pass, and the same 9
  `KnownBug` cells fail as before.
- **Deep fuzz, 20,000 candidates per run.** The Optimal matrix is checked
  against the BFS oracle on every room. No unexplained failures; only the known
  PO-PAIRWISE cell, under `--partial-order pairwise`, shows up.

  | seed | mode | usable rooms | solvable |
  |---|---|---|---|
  | 77 | normal | 5,235 | 2,393 |
  | 9001 | normal | 5,251 | 2,412 |
  | 77 | identical items | 4,614 | 1,913 |
  | 4242 | identical items | 4,601 | 1,954 |

## Measured

Interleaved A/B on one binary, toggling `--no-skip-reversals` (process wall
time, startup included). 8 rounds serially, 6 on 12 threads:

| level | serial, min | serial, median | 12 threads, min |
|---|---|---|---|
| wreck (to the proof) | 1.13x | 1.13x | 1.09x |
| cannons (to the proof) | 1.09x | 1.08x | 1.19x |
| library (500 k exp.) | 1.08x | 1.08x | 1.05x |
| society (400 k exp.) | 1.05x | 1.05x | 1.04x |

- **Serially: 5-13 % faster.** That is roughly twice the gain of skipping only
  the exact returns (an earlier cut of this change: 3-8 %), because the turns
  after a horizontal step are now pruned states, not just saved lookups.
- **On 12 threads: +4-19 % by minimum.** The medians are within this laptop's
  noise.
- **Memory.** It falls with the stored states, and the bytes per state are
  unchanged. At a fixed 3 M expansions on 12 threads:
  - society stores 4.24 M states instead of 4.44 M (-4.5 %), at the same
    bound (f = 82);
  - library 3.398 M instead of 3.401 M, and its bound rises from 104 to 105.

## Open

- Longer detours are still caught only by the store: a fish swimming away and
  back over several moves, with the other fish's moves in between. Pruning
  them needs the move-ordering reasoning that measured a net loss in docs/011.
