# 022 — Merging identical items

Every mobile model has its own slot in the state key. Two physically identical
items, such as gems or plain 1x1 blocks, that trade places therefore produce two
keys for one room, which is stored and searched twice. The question came up
while chasing the gems loss in docs/021.

## How much

Each stored state was rewritten with the positions sorted within every class of
interchangeable items, and the distinct results counted (300 k expansions):

| level | interchangeable | stored | distinct | duplicates |
|---|---|---|---|---|
| gems | 88 in 1 class | 679 k | 140 k | **79.4%** |
| party1 | 9 | 448 k | 386 k | 13.9% |
| alibaba, experiments, cannons, chest, pearls, library, society | 4-66 | | | ~0% |

On most levels identical items never get past one another, so a swap cannot
happen. Where they can, it happens on a large scale.

## What changed

- **`ItemSymmetry.Classes`.** Two mobile items are interchangeable when they
  match in everything the physics reads from a model definition: kind, shape
  (the same marks in the same order), weight, power, declared facing. They must
  also have no goal, no plug, and not be a fish. Frozen models are not in the
  mobile set at all.
- **`StateKeyCodec.Canonical`.** After encoding, each class's 2-byte positions
  are sorted in place.
  - A child differs from its canonical parent by a move or two, so a class is
    nearly always still sorted. One pass checks; only an unsorted class is
    insertion-sorted.
  - A plain `Sort` here cost experiments 30%. The check-first version costs 5%.
  - Decoding hands the sorted positions out in slot order. The result is a room
    with identities swapped, which is the same room to the physics.
- **`StuckDetector.Canonical`.** The layout key is sorted the same way, so
  layouts that differ only by a swap share one analysis. This is what removes
  most of gems's analyses.
- **Options.** `SolveOptions.MergeIdenticalItems` (default on), CLI and bench
  `--no-merge`.

## Why it is sound, and how that is checked

Merging assumes that which identical item stands where never matters. Item
identity could leak in through the order a round processes models. For
example, when two items could fall into one cell, the lower index wins, and
swapping their positions would then change the outcome.

The new check `SymmetrySound` tests this directly:

- In every state, rotate the positions within each class.
- Apply every move to both rooms.
- Require the same outcome: the move allowed or not, solved, solvable, able to
  move, and the same canonical key.

It runs on every micro-level, on every fuzz room, and in a new fuzzer mode where
all goal-free items are identical (`FUZZ_IDENTICAL=1`, and in the default suite
as `Fuzz_Smoke_IdenticalItems`). In that mode 42% of rooms have interchangeable
items, against 4.5% in the normal mode.

Deep runs, 20,000 candidate rooms each:

- normal mode: seeds 77 and 9001;
- identical mode: seeds 77, 4242 and 9001.

No violation was found. The Optimal matrix ran on all of those rooms too, so the
merged search still returns exactly the oracle's optimum.

## Tests

- **Micro-level `identical-blocks` (12).** Four identical blocks, found by the
  identical-items fuzzer. Expansions 44 → 42; it keeps a micro-level with a
  non-empty symmetry check in the suite.
- **`ItemSymmetryTests`:**
  - gems forms one 88-member class;
  - goal items are never merged;
  - gems at 100 k expansions stores under three quarters of the states
    (248,525 → 171,036) with no lower bound.
- **Matrix column `astar-nomerge`.**
- **`Symmetry_IdenticalItemsAreInterchangeable`** runs on every micro-level.
- **Results:** 880 tests pass; KnownBug shows the 7 pinned cells; `verify --all`
  80/80.

## Measured

**gems** (12 threads, proving the 59-move optimum):

| | expansions | time |
|---|---|---|
| no stuck rule, no merge (before docs/020) | 7.42 M | 28.3 s |
| stuck rule, no merge (docs/020/021) | 2.93 M | 72.2 s |
| merge, no stuck rule | 1.00 M | 4.9 s |
| **merge + stuck rule (default)** | **0.49 M** | **6.1 s** |

The gems loss from docs/020 is gone: the default is now 4.6x faster than before
the stuck rule.

**Whole corpus, 1.5 M expansions per level** (deterministic):

- gems is now solved and proven within that budget;
- the bound went up on corridor, keys, party2 and rotate (+2 each), and on
  bathroom and reactor (+1);
- it went down nowhere;
- 71 levels are unchanged;
- 7 levels solved instead of 6, and the batch took 2.7 min instead of 4.7.

**Cost where nothing merges:**

- `bench --suite`: within ±3%, cannons −6%;
- at 1.5 M expansions serially: experiments +5% (66 interchangeable items),
  map +2%.

## Open

- The stuck rule still makes gems slower per expansion (4.9 s vs 6.1 s to the
  proof with merging). Per-analysis cost on rooms with many items is the
  remaining lever: an incremental fixpoint that re-checks only near a model that
  just became mobile.
