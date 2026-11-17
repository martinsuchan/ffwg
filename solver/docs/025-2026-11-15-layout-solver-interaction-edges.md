# 025 — Layout solver: the idle fish inside an event

2026-11-15

docs/024 left `--layouts` failing its gate: `wc` exhausted after 32 layouts,
because the idle fish was never allowed inside an event's footprint. It now
**solves `wc` (104 against an optimum of 100)** - the first decomposed search
here to solve it at all - and `start` and `heavy-gate` come back at their
optimum.

## What changed

1. **Interaction edges.** After a clean event, the other fish is tried on the
   few squares where it would be in the item's way - the cells the moved items
   end on or sweep through, plus the cells directly under where they come to
   rest - cheapest first. Two kinds of child, at most one of each per fish:
   - the layout comes out **different** (the fish stopped or carried something);
   - the layout is the **same** but the fish ends up inside the event,
     committed there. This is what `wc` needs: the bowl comes down onto the
     small fish's back while also resting on walls, so no item ends anywhere
     new - only the fish's square differs, and the generic child forbids it.
2. **The footprint margin is narrower**: each raw cell plus the cell directly
   below it, not all four neighbours. The four-way margin masked the square
   right beside a drop, where `start`'s small fish waits; that alone was the
   56 against 54. Checked on all micro-levels and along the recorded solutions
   of six corpus levels; with no margin at all it fails on five micro-levels.
3. **A third physics retry** (the other fish clear of where the push chain
   moves) and **cheaper planning for a committed fish** (it moves first; the
   composed and joint fallbacks are gone for those edges).

## How the candidate rule was chosen, and what was wrong with the measurement

The alternative was to try the other fish everywhere. Measured first, per clean
event: 100-755 reachable placements, 20-28 squares touching the dilated
footprint, 2-7 squares inside "ends on or sweeps through". A probe then said
every *different* outcome came from those inside squares, 1-7% of events.

That probe overcounted. It did not check that the arrangement was settled
before the key press, and the implementation's stricter probe finds **no**
different layout at all on `wc` or `cannons`. What `wc` needs is in the
category the probe had filed under "same outcome". The squares were right; the
reason was not. The same-layout kind and the under-cells were added by the
implementation, not by the measurement.

## Measured

`--layouts`, serial, 60 s limit (`broom` 120 s):

| level | optimum | docs/024 | now | interaction presses (of all) |
|---|---|---|---|---|
| `start` | 54 | 56, 1.9 s | **54**, 1.9 s | 64,601 of 136,132, 0 edges |
| `submarine` | 83 | 83, 1.8 s | 83, 1.6 s | 9,482 of 164,404, 12 edges |
| `noground` | 44 | 44, 23.9 s | 44, 31.6 s | 999,054 of 2,150,079, 1,167 edges |
| `cannons` | 39 | 41, 22.7 s | 41, 19.6 s | 355,086 of 1,001,655, 321 edges |
| `wc` | 100 | exhausted | **104**, 1.2 s | 20,687 of 72,203, 64 edges |
| `broom` | (193 bundled) | f=106, 6,711 layouts | f=104, 7,031 layouts | 2,041,382 of 4,452,065 |

Reconstruction rejected nothing; every answer replays. Suite: 1,036 passed.
Micro matrix: `heavy-gate` 28 → 24 (its optimum); `pairwise-unsound` still
tagged `LAYOUTS-STAGE-B`.

## Reading it

- **Interactions bought `wc` and nothing else on these levels**, and they are
  not cheap: about half of all key presses on `noground` and `broom`, zero
  children on `start`. With the interaction step off, `noground` took 14 s.
- **`cannons` at 41 and `wc` at 104 are unexplained.** `--items` finds 39 on
  `cannons`.
- **`broom` is no closer.** Layout count is about the same and f is not a bound.

## Open

- **The interaction property is pinned by one row.** On the micro-levels that
  half of the footprint check is vacuous - none of them has such an outcome -
  so it rests on `wc`'s recorded solution. A micro-level with an item settling
  onto a waiting fish is needed.
- **Cost of the probes**: a per-layout memo of "this event has no interaction"
  would remove most of them (stage C).
- **Support handover** (`pairwise-unsound`): no event, so nothing here covers it.
- The 2-4 move gaps on `cannons` and `wc`.
