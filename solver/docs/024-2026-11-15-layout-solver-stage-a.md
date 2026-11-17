# 024 — Layout solver, stage A: built, valid, and short of its gate

2026-11-15

A third decomposed search, `ffsolve solve <level> --layouts` (`LayoutSolver`):
a two-level table - item layout first, then the fish configurations that
layout was entered with - in which the fish that is not acting has **no
committed position**. It works and every answer replays, but it **fails the
gate it was built for: `wc` still exhausts**, for a different reason than
`--items` does.

## Why it was tried

docs/023 left `--items` with one systematic hole: where the other fish goes
(four nearest squares, against rank 4-69 in real optima). And the main solver
spends its time on fish positions: `broom` stores 6.2 M states over 1,181 item
layouts.

Stage 0 measured the idea before any code, from plain A\*'s store:

| level | plain A\* states | layouts | states right after an event | distinct (layout, placement of the fish that acted) |
|---|---|---|---|---|
| `wc` | 76,705 | 60 | 1,501 | 112 |
| `start` | 302,388 | 142 | 12,140 | 442 |
| `noground` | 104,015 | 196 | 3,277 | 580 |
| `submarine` | 109,599 | 440 | 6,761 | 854 |
| `cannons` | 263,438 | 464 | 15,825 | 1,183 |
| `broom` (60 s, unsolved) | 6,223,732 | 1,181 | 131,119 | 4,158 |
| `gems` | 948,357 | 105,819 | 201,793 | 112,595 |

The last column is the floor for the second level if the idle fish costs
nothing to store.

## What was built

- **`LayoutTable`** - level 1: canonical item layout (identical items merged,
  fish only in/out). Level 2: entries (which fish acted, where each fish's
  home is) with their best cost.
- **`FishField`** - the idle fish as a cost field over placements instead of a
  position: relaxed through each layout (`InertRouter.Relax`, multi-source,
  exact support), masked by each event's footprint, paid only when the fish
  acts.
- **`LayoutFootprint`** - the cells an event touches (moved models before,
  after and in between, the actor, dilated by one). Outside it the other fish
  does not change the outcome - with one exception, below.
- **`SegmentPlanner`** - two-fish joint travel in a frozen layout, for when
  they are in each other's way. Optimal against a brute force over the real
  engine on a purpose-built corridor.
- **`LayoutSolver`** - A\* over entries; edges priced so that they are
  feasible when generated (step-aside, then composed routes, then the joint
  search); reconstruction that follows each fish's field backwards.

Two things the design had wrong, found by building it:

1. **The footprint is necessary, not sufficient.** An idle fish far from a
   push can be what keeps a load off the other fish; move it and the other
   fish dies, though nothing in the footprint is near it. Pinned by the new
   micro-level `support-bound` (16 moves). The fix is a second rule,
   `LayoutFootprint.NeedsIdleSupport`: when the room would not stay settled
   without the idle fish, that fish is bound where it stands.
2. **Relaxing the idle fish as if the other were not there was too
   optimistic.** `noground` replayed at 52 for g=44. Deferred fish are now
   relaxed with the other fish solid; replayed length equals g on every level
   measured.

## Measured

`--layouts`, serial, 60 s limit (`broom` 120 s):

| level | optimum | result | layouts | entries | s | `--items` (docs/023) |
|---|---|---|---|---|---|---|
| `start` | 54 | 56 | 262 | 4,849 | 1.9 | 54, 9 s |
| `submarine` | 83 | **83** | 612 | 3,867 | 1.8 | 83, 16 s |
| `noground` | 44 | **44** | 167 | 4,980 | 23.9 | 44, 58 s |
| `cannons` | 39 | 41 | 2,484 | 34,744 | 22.7 | 39, 38 s |
| `wc` | 100 | **exhausted** after 32 layouts | 32 | 472 | 0.6 | exhausted |
| `broom` | (193 bundled) | unsolved, f=106 | 6,711 | 181,607 | 120 | unsolved |

Reconstruction rejected nothing and every answer replays. Micro matrix: every
level passes in the `layouts` columns except `pairwise-unsound` (tagged
`LAYOUTS-STAGE-B`); `heavy-gate` comes back valid but long (28 against 24).

## Reading it

- **The gate failed.** `wc` needs the small fish to wait at an exact square
  *inside* an event's footprint (the bowl comes down around it). Stage A
  forbids every placement inside a footprint, so the search closes after 32
  layouts. `--items` fails `wc` on the step-aside ranking; this fails it on
  cooperation. Neither solves it.
- **Entries are far above the stage-0 floor**: `start` 4,849 against 442,
  `cannons` 34,744 against 1,183. The home of the idle fish is part of the
  entry key, and most edges end up **bound** (on `submarine` every one:
  23,921), which turns the field back into a position. The deferred fish pays
  off less often than the floor suggested.
- **It is faster than `--items` where both solve** (2-8x), and **2 moves
  longer on `start` and `cannons`**, where `--items` reaches the optimum.
- **`broom`**: 6,711 layouts in 120 s against plain A\*'s 1,181 in 60 s, so it
  does search layouts rather than fish positions - but it has not solved the
  level, and its f is not a bound.
- **Cost per expansion is still 1-7 ms.** A quarter of the physics calls are
  retries with the other fish somewhere else, and every step under an item
  asks the engine.

## Open

- **Stage B is what `wc` needs**: an idle fish deliberately placed inside an
  event (bridge, support, waiting under something), and the support handover
  (`pairwise-unsound`). Without it this is not a more complete search than
  `--items`, only a faster one.
- **Why 56 on `start` and 41 on `cannons`** is not explained; the merge of
  entries with different histories is the first suspect.
- `heavy-gate` at 28 against 24: not probed.
- Stage C (per-layout push memo, dominance, stuck detection) is untouched.
- `--layouts` records as method `layouts`, with no lower bound.
