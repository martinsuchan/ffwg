# 012 - Pruning unsolvable states: what is provable, and what the heuristic really costs

2026-08-16

Following docs/011, which measured four A\* optimisations and found only one paid. The
question this time came from the other direction: rather than making the loop faster, stop
generating states that cannot lead anywhere. Three worked examples were on the table -
`cabin1` (two routes out, the short one blocked by items), `society` (opening with the big
fish going right ruins the level), and `city` (seven crabs that must be delivered, and a
dropped one is fatal).

Nothing here is committed to `src/`. This is a measurement pass plus a diagnosis; the
findings that warrant code changes are listed at the end.

## The number that frames everything

The bound recorded in `docs/results.csv` is the heuristic's estimate at the starting state.
Compared against the best known solution:

| | levels | bound / best |
|---|---|---|
| solved | 16 | **1.00** (every single one) |
| never solved | 72 | 0.02 - 0.92 |

Every level the solver has ever cracked had an *exact* heuristic. Not one level with a bound
below the true answer has been solved directly - the two exceptions in the table, `cabin1`
(0.96) and `gems` (0.97), were solved through hand-simplified variants, not on their own.

The cliff is that sharp. And the misses are not near misses: `propulsion` estimates 47 moves
for a 1964-move solution, `city` estimates 85 for 481. At 18% of the truth A\* is not being
guided at all, it is running breadth-first with extra steps.

So the framing in the request is right, and stronger than it looks: **the heuristic is not
one of three levers, it is the only one that has ever decided an outcome.**

## Where the current bound comes from, and what it cannot see

`ExitHeuristic` sums, over every model whose goal is to leave, the distance to the border in
a room stripped to its immovable walls. Fish distances add; item distances take the max.

Two properties matter here. First, it is a *walls-only* relaxation - every movable item is
deleted. Second, and less obviously: **in all three example levels, no item has a goal at
all.** `city`'s crabs, `society`'s steel, `cabin1`'s blocks are all `goal=No`. The heuristic
tracks two fish and nothing else.

That means the solver's entire notion of progress is "how far are the two fish from the
border in an empty room". Every move spent pushing an item somewhere, and every detour taken
to let the other fish past, is invisible to it. On levels where the fish mostly just swim
out, the bound is exact and the level solves. On levels that are really about rearranging
furniture, it is 20% of the truth.

## What is provable: re-deriving immobility per state

`LevelReduction` already computes, once per level, which models can never move - a growing
fixpoint seeded from "nothing moves", where a model joins the mobile set if something
supporting it might move or a fish strong enough can reach a spot that pushes it.

The idea tested here is to run that same fixpoint **from a live state instead of the level's
starting positions**, then treat everything it proves immobile as wall and ask whether each
fish can still reach an exit. If one cannot, the state has no solution, whatever the search
does next.

This is sound in the same way the existing relaxation is: a model that provably cannot move
again is scenery forever, so calling it wall is exact rather than optimistic, and deleting
the models that *can* still move only ever helps a fish, which swims rather than climbs.

### It reproduces the `society` example exactly

Opening `RR` pushes the heavy steel bar right; unsupported, it drops into the one-wide shaft
at column 7 and lands on the small item already at the bottom.

```
  7 ##..BBBB..#########...##          7 ##..BBBB..#########...##
  8 ##..BBBB..............##          8 ##..BBBB..............##
  9 #####..d..............##   ==>    9 #####..#..............##
 10 #######d...#######....##         10 ########...#######....##
 11 #######d##.####.......##         11 ##########.####.......##
 12 #######l##.###........##         12 ##########.###........##
        after RR                        with everything provably stuck as wall
```

Both items are then provably immobile: the bar cannot go sideways (wall at row 11 both
sides), cannot go down (floor), and can only go up if a fish gets underneath it - but
underneath is the small item, which is itself in a one-wide dead-end shaft with wall below,
so it can never move either, and no fish can ever occupy that cell.

With the shaft sealed, the big fish - four cells wide, two tall - has no route to the exit at
the bottom right. It would have to pass rows 7-8 on the right side, where only three columns
are clear. **`CAN REACH EXIT = False`**, proved from the geometry.

The solver today sees none of this: `IsSolvable()` is true (nothing died) and the walls-only
distance is finite (both items are deleted in the relaxation).

## Two real bugs in `LevelReduction`, found on the way

The per-state analysis is the same computation as the existing one, so it inherits whatever
that gets wrong. Checking it against all 80 reference solutions - a frozen model that a real
solution demonstrably moves is a proof of error - found **7 of 80 levels contradicted**.

### 1. A push is a chain, and the fish is not always the body that lands

`CanPush` asks whether the *fish's own shape*, stepped one cell, lands on the target. The
class comment argues chains need no special case, because once the item in between is known
mobile it is deleted, the fish reaches the vacated square, and pushes directly.

That argument fails whenever the shapes differ. In `magnet`, move 336 is `L`:

```
 18 ###########..............ee......##
 19 ###########...............eBBBB..##     fish pushes 'e' left;
 20 ###########.....j.........eBBBB...#     'e' is 7 cells, and its own
 21 ###########.....j.........ee......#     bottom cell lands on 'h'
 22 ###########.....j........he##.....#
 23 ###########jjjjjj........h###f..###     right of 'h' at rows 23-24 is
 24 ###########.....j........h###f..###     solid - a 2-tall fish never fits
```

The fish can never occupy a square from which it touches `h`. Only `e`'s body can. `corals`
is the same defect one level deeper: fish -> `b` -> `c` -> `d`, and `d` only becomes
pushable 122 moves in, once other items have moved next to it - something a start-state
induction cannot see at all.

The fix is to let **any model already known mobile** be a pusher, not just a fish, bounding
where an item might stand by "every placement that fits" and its available strength by the
strongest fish.

### 2. Declared positions mixed with settled positions

The analysis reads shapes and builds its wall grid from `ModelDef.X/Y` - what the level file
declared - while querying occupancy through `room.GetModel`, which reflects the settled
opening state. On any level that floats a model in its file these disagree.

The same confusion is in `Verified`, which compares each frozen model against `ModelDef.X/Y`
after every move. `society` declares item 11 at (7,2); the opening settle drops it to
(7,12). `Verified` reads that first fall as "a frozen model moved" and throws the entire
reduction away, on a level where the analysis was right.

### The effect of fixing both

| | models frozen | levels contradicted by a real solution |
|---|---|---|
| `LevelReduction.Compute` today | 149 | **7** (hanoi, captain, map, city, pavement, magnet, warcraft) |
| with both fixes + generalised pushers | 125 | **0** |

Fewer models frozen - the corrected analysis is properly more cautious - but consistent with
every one of the 80 recorded solutions, and so **no longer dependent on the `Verified`
fallback at all**. That matters beyond tidiness: a per-state detector runs on millions of
states with no reference solution to check itself against, so it cannot inherit an analysis
that is only correct because a fallback catches it.

## Measured value

Soundness first, since a dead-end test that is wrong silently loses solutions. Replaying all
80 reference solutions and running the detector at every state along them:

**32,031 states checked, 0 false positives.**

Then how often it fires. States were sampled by random legal play - with a heuristic at 20%
of the truth A\* explores near-uniformly, so this approximates what it visits - rejecting any
move the solver's own `IsDeadEnd` would already have discarded, so what is measured is purely
the *marginal* gain over what exists.

| level | newly dead | heuristic gain | items the walk disturbed | ms/state |
|---|---|---|---|---|
| society | **72.0%** | 0.00 | 5.69 | 4.5 |
| steel | 11.0% | 0.00 | 1.87 | 3.9 |
| duckie | 5.0% | 0.00 | 4.24 | 1.3 |
| city | 0.0% | 0.00 | 1.33 | 2.7 |
| warcraft | 0.0% | 0.00 | 1.87 | 6.4 |
| cabin1 | 0.0% | 0.00 | 2.20 | 0.5 |
| corridor | 0.0% | 0.00 | 2.79 | 12.1 |
| columns | 0.0% | 0.00 | - | 185 |

Three things to read out of this.

**The detection is real but concentrated.** 72% of the live states a random walk reaches in
`society` are provably unsolvable and the search stores every one of them. That is a far
bigger prune than anything in docs/011, where duplicate detection did 62-80% and everything
else was under 2%.

**The heuristic gain is exactly zero, everywhere.** Adding provably-stuck items to the wall
set never lengthened the relaxed path on a state that survived. In hindsight this is what
should happen: if a stuck item sat on the fish's shortest route, the state would be *dead*,
not merely longer. The mechanism is all-or-nothing, so it is a dead-end test and not a
heuristic improvement. **`cabin1` is therefore not helped by this** - its 162-vs-168 gap is
not caused by anything provably immobile.

**The zeroes are not evidence of a miss.** The last-but-one column is why that column exists:
`city` disturbs 1.33 items in 250 random moves, `society` 5.69. The detection rate tracks how
much the walk actually rearranged the room. Random play never gets around to mishandling
`city`'s crabs, so its 0% says nothing about whether the detector would catch one. Measuring
that properly needs states sampled from a real search on a level it can get deep into, which
random play is a poor stand-in for.

## Why `city` is a different problem anyway

The crabs are the clearest case that this mechanism has a ceiling. A crab dropped on the
floor is usually **not immobile** - it can still be shoved along the floor. What it has lost
is the ability to go back *up*, because lifting needs a fish underneath it and there is no
cell there. The level is then unwinnable not because something is stuck but because something
is in the wrong *place*.

Nothing in the model can express that. The crabs have `goal=No`; the solver has no notion
that they must end up anywhere in particular. Detecting a dropped crab needs a mechanism that
knows an item is *required* somewhere - which is also exactly the missing term in the
heuristic, since delivering seven crabs is most of `city`'s 481 moves and the bound accounts
for none of it.

## The cost problem

At 0.5-185 ms per state against a budget of roughly 6 microseconds per expansion (docs/011),
this is between 100x and 30,000x too slow to run as built. It is only viable cached, and the
shape of the cache is favourable: the analysis depends only on **item positions**, and fish
moves - the overwhelming majority of edges - leave those untouched. Keyed on the item
sub-key, with the augmented wall grid and the per-fish distance grids as the cached value,
the per-state cost falls back to the two array lookups it already does.

A cheaper approximation is also available and would have caught `society` on its own: for
each item, precompute per placement whether it can be pushed in any direction *given
permanent walls only*, ignoring other items. That is a static per-level table, one lookup per
item per state, and it is what proves the steel bar and the small item stuck in the example
above.

## Where this leaves the three levers

- **Dead-state pruning** is worth building, sound, and much larger than anything in docs/011
  where it fires - but it fires on some levels and not others, gives nothing to the
  heuristic, and needs the caching work to be affordable at all.
- **Not generating useless states** was measured in docs/011 (both partial-order variants:
  correct, no gain) and is not revisited here.
- **The heuristic** remains untouched and remains the only lever with a demonstrated
  relationship to whether a level gets solved. The specific missing term is now identifiable:
  the bound counts distance to the exit and nothing else, so every move spent rearranging
  items - which is what the hard levels consist of - is unaccounted for.

## Follow-ups

1. **Fix `LevelReduction`** independently of any of the above. The chain-push and
   declared-vs-settled defects are live bugs: they cost the reduction entirely on 7 levels
   (larger state keys, weaker bound) and the `Verified` fallback is masking them rather than
   catching them. Verified fix in the scratchpad; 0/80 contradicted after.
2. **Make `Verified` compare against the settled start**, not `ModelDef.X/Y`.
3. **Cache the per-state analysis by item sub-key**, or implement the static per-placement
   pushability table, before wiring any of it into the search.
4. **Measure the detector on states from a real search** rather than random play, to find out
   what the zeroes in the table actually are.
5. **The heuristic's missing term.** Nothing here addresses it and nothing in docs/011 did
   either.

## Methodology

docs/011 ended on "when a result has no mechanism, distrust the measurement before believing
it". This pass produced four wrong numbers before producing a right one, all caught the same
way:

- 65%/100% dead rates on the first run - too good, and correctly so: the analysis was
  unsound, firing on `city`'s and `warcraft`'s *starting* states.
- Heuristic "gains" **below zero**, which adding walls cannot produce. Comparing a per-state
  result against production's `Verified` reduction was comparing two different analyses.
- Random walks that killed a fish within a few moves, so the sample was almost entirely
  states A\* would never have stored.
- A frozen-model check comparing against declared rather than settled positions, which
  invented five counter-examples that were not real - and, as it turned out, is the same
  mistake production makes.

The one measurement that carried real weight was the cheapest: replay the 80 known solutions
and see whether the detector ever contradicts one. It caught the unsoundness immediately, and
it is what the "items moved" column is for as well - a rate means nothing without knowing
whether the sample contained the situation being measured.
