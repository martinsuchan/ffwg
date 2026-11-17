# 023 — Item solver: exact support, and the edges it was losing

2026-11-15

`ffsolve solve <level> --items` (docs/009) exhausted on three of the four levels
it was tried on. docs/009 named the main cause and left it open; a review found
more. `submarine` and `noground` now solve at their known optimum. `wc` still
exhausts and `broom` is still unsolved, and what is left is one thing only: where
the other fish goes.

## What changed

1. **Exact support.** `InertRouter` refused every step from a placement with
   anything mobile overhead. For the item search that is fatal: the step is not
   travel, and when nothing falls it is not an event either, so a fish under an
   item that is also propped up elsewhere could never move again.
   - `Room.WouldDisturbWithFishAt(fish, x, y)` asks the engine, with the fish at
     a hypothetical place, whether anything would fall **or any fish would be
     crushed**, and applies nothing. The second half matters: a heavy bar across
     both fish drops nothing when the big one steps out, and kills the small one.
   - `InertRouter` takes `exactSupport`. The coarse test stays as the pre-filter,
     so the engine is asked only when something really is overhead.
   - Only `ItemSolver` turns it on. `MacroSolver` keeps the coarse test, where a
     false positive only costs a node; its baseline cells are unchanged.
2. **Room state leaked between edges in `ExpandUnder`.** Every `TryEdge` leaves
   the room wherever its key press took it, and the next item's position, the
   "could this move at all" test and the carry/drop block all read the room
   without putting the arrangement back. Item positions now come from the
   parent's snapshot and the arrangement is restored before each grid read.
   On its own this took `wc` from 1,783 stored states to 5,385.
3. **Turning on the spot counted as stepping aside.** The blocker's own square
   was never marked as used, so the first of the four clearance tries was the
   turn there, which clears nothing.
4. **Drops come from the router.** A drop was only tried from the square the
   fish started on, facing the way it faced. A wide fish can slide along under
   an item before it lets go, and a turn first is a placement of its own. The
   router now reports every step into open water it refused because of what the
   fish is holding (`InertRouter.Drops`), and each is an edge. No extra physics
   calls.
5. **Down is a push after all.** "An item below a fish is supported, or it would
   have fallen" is false when the fish is what supports it: `wc`'s bowl lies on
   the small fish's back with part of itself underneath, and the fish lowers it
   by pushing down. Down is now a push direction, tried only where the fish is
   holding something up.

## Tests

- **`ExactRouter_AgreesWithTheEngine`** (micro property, every level): in every
  state that can still be finished, routing leaves the room untouched, every
  step the router calls travel changes only that fish under `ApplyMove`, and
  every reported drop is a legal move that disturbs something.
- **`heavy-shared`** (new micro-level, 15 moves): a heavy bar across both fish.
  With the crush test removed the property fails on it, which is what it is for.
- **`ItemExpressibilityTests`** now diagnoses with the exact router.

## Measured

`--items`, serial, 60 s limit (`broom` 90 s). Times vary by about a quarter
between runs on this machine; the state counts do not.

| level | optimum | before | after |
|---|---|---|---|
| `start` | 54 | 54 — 28,949 stored, 10.0 s | 54 — 26,407 stored, 10.0 s |
| `submarine` | 83 | exhausted, 9,190 stored | **83** — 20,384 stored, 16.3 s |
| `noground` | 44 | exhausted, 5,200 stored | **44** — 27,779 stored, 57.6 s |
| `cannons` | 39 | not measured | 39 — 95,618 stored, 37.9 s |
| `wc` | 100 | exhausted, 1,783 stored | exhausted, 11,113 stored |
| `broom` | (193 bundled) | unsolved, 64,459 stored | unsolved, 64,879 stored, f=88 |

`ItemExpressibilityTests` (successors of a known optimum the expansion never
builds): `wc` 8 → 6, `submarine` 4 → 1, `noground` 4 → 0. **Every remaining gap
reports "every step in the segment is inert"**: both fish can make the trip, and
the only thing missing is the other fish's destination. It sits at cheapness rank
4 to 69 among that fish's reachable squares, against four tries; the rank-4 case
(`wc` event 15) is a tie at the cap.

Micro baseline: item columns only, plus the new level's rows. Weight-1 lengths
are unchanged; three `items-w2` answers are longer (24 → 27, 27 → 33, 35 → 37)
because the weighted search has more edges to be greedy with - that column
promises a valid solution, not a short one. The physics digest is unchanged.

## Why this matters: layouts, not fish positions

Counted over the states plain A\* stores (serial, default options):

| level | stored states | item layouts | states right after an item moved |
|---|---|---|---|
| `wc` | 76,705 | 60 | 1,501 |
| `start` | 302,388 | 142 | 12,140 |
| `cannons` | 263,438 | 464 | 15,825 |
| `broom` (60 s, f=96, unsolved) | 5,440,050 | 1,143 | 123,206 |
| `gems` | 948,357 | 105,819 | 201,793 |

On `broom` one layout holds 4,759 states on average and 162,955 at worst: the
search is enumerating fish positions. `gems` is the counter-case at 9 per layout.
The `broom` row is what was reached by f=96 against a bundled 193, not the size
of the level.

## Open

- **The step-aside model** is the whole remaining incompleteness the probe can
  see, and it is why `wc` exhausts. Four nearest squares is the wrong ranking,
  not too small a cap (docs/009).
- **Cost per expansion** is 0.4-5 ms. Every state re-derives which pushes exist,
  though that depends only on the item layout, and a layout is entered 25-108
  times on the first four levels above (about twice on `gems`).
- **Proposed next:** a two-level table - item layout first (identical items
  merged, as in the main solver), then per layout the fish configurations it was
  entered with and their cost - with the push catalogue computed once per
  layout. Three cases keep fish from being separable from the layout and have to
  be designed for: a fish holding an item (and the handover), a fish in the path
  of a push or a fall, and fish blocking each other's travel.
- `ItemSolver` still lacks what `Solver` has: stale-node skipping, stuck
  detection, identical-item merging.
- `pairwise-unsound` still fails in the `items` columns.
