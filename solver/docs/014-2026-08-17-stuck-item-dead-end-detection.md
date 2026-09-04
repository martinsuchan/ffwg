# 014 - Dropping states where an item is stuck forever

2026-08-17

Built the detector designed and measured in docs/012, to the specification asked for:

> - on every expansion check each item if it can be moved, or it's stuck forever. This check
>   must be solid - check if this item is stuck even if every other item or fish has moved
> - if this specific item is stuck, handle it like the solid background and use it for
>   computation of shortest path to edge of the map. If there is no longer any path, the level
>   is not longer solvable and this state must be dropped.

That is exactly right, and the "even if every other item or fish has moved" clause turned out
to be the load-bearing part - twice.

## What shipped

`LevelReduction`'s growing fixpoint, lifted out into `StuckAnalysis` so it can run on any
state rather than only a level's opening position. One implementation now serves both;
`LevelReduction.Compute` is a thin wrapper over it, which matters because both bugs docs/013
fixed were in this analysis and a second copy would have needed fixing twice.

Per expansion, when `--stuck` is on:

1. Grow the set of models that *might* move, from none. Whatever is left cannot move again.
2. Treat those cells as wall, unioned with the level's permanent walls.
3. Ask, per fish, whether any border is still reachable. If not, drop the state.

`IsDeadEnd`'s three existing rules run first - they are orders of magnitude cheaper and catch
their own cases.

Cached per **item arrangement**, not per state: a fish move leaves the items alone, which is
nearly every edge, so the fixpoint runs far less often than once per expansion. A one-entry
front check in front of the dictionary skips even the key construction when the arrangement is
unchanged.

## It catches the case it was built for

`society`, opening `RR`: the big fish pushes the heavy bar off its ledge and gravity drops it
into the one-wide well at column 7, on top of an item already walled in on three sides.

```
  9 #####..d..............##          9 #####..#..............##
 10 #######d...#######....##   ==>   10 ########...#######....##
 11 #######d##.####.......##         11 ##########.####.......##
 12 #######l##.###........##         12 ##########.###........##
        after RR                        with everything provably stuck as wall
```

Neither can ever move again - the bar has walls both sides at row 11, floor below, and can
only go up if something gets underneath it, where the only cell is occupied by an item in a
dead-end shaft that nothing can ever reach. With the well sealed, the big fish (four cells
wide, two tall) has no route to the exit at the bottom right: it would have to pass rows 7-8
on the right, where only three columns are clear.

`IsSolvable()` is true - nothing died - and the walls-only relaxation deletes both items, so
the distance to the border stays finite. **Every test the solver had before this one passes
the state**, and it then explores the entire dead subtree. Pinned as
`SocietyIsUnsolvableOnceTheBarIsInTheWell`.

## The bug the specification caught

The first version measured each fish's reach *from where that fish actually stands* - a fish
sealed in a pocket cannot push what is outside it, which is the stronger analysis and is right
for a one-off look at a single position. But the result was then cached on the item
arrangement alone, and it does not depend on the items alone. Two states with identical items
and different fish positions shared an answer that was only correct for whichever of them
computed it first.

It surfaced as an impossibility rather than a wrong answer: changing nothing but the cache
size changed the number of states stored, 2,164,036 against 2,164,994, in a search that is
deterministic. A search whose result depends on a cache is wrong somewhere by definition.

The fix is the specification: assume a fish can stand anywhere it fits. That makes the answer
a function of the items alone, so caching on them is sound, and it is strictly the more
cautious reading - a fish that might be anywhere can only make more models look mobile, never
fewer. Proven by the same test that found it: cache limits of 512 and 262,144 now give
byte-identical results (2,164,856 both), differing only in time (31.4 s against 11.2 s).

## Soundness

A false positive silently costs a solution, so this is the half that matters. Every state
along a recorded solution demonstrably has a solution.

- `NeverFiresAnywhereAlongAReferenceSolution` - all 80 levels, sampled every eighth state
  with a per-level offset. Sampled because the fixpoint would otherwise dominate a 3-second
  suite; docs/012 ran the exhaustive version, all 32,031 states, and it was clean.
- `StartingPositionsAreNeverCalledDead` - all 80.
- Both run `fishAnywhere: true`, the configuration that actually ships.

472 tests green.

## What it is worth: not much

Fixed budget of 1.5 M expansions, so both runs do identical work and the numbers compare
directly:

| level | stored, plain | stored, `--stuck` | fewer | time | slower |
|---|---|---|---|---|---|
| duckie | 2,226,373 | 2,059,985 | **7.5%** | 9.2 → 10.6 s | 1.15x |
| steel | 2,204,962 | 2,059,455 | 6.6% | 13.2 → 16.1 s | 1.22x |
| society | 2,222,009 | 2,164,856 | 2.6% | 10.2 → 10.8 s | 1.06x |
| city | 2,251,777 | 2,237,703 | 0.6% | 11.4 → 13.0 s | 1.14x |
| cabin1 | 1,689,986 | 1,689,986 | 0.0% | 6.9 → 7.6 s | 1.10x |

0 to 7.5% fewer states for 6 to 22% more time. **A net loss, on every level measured.** It is
close to break-even where memory rather than CPU is what runs out - the solver's practical
limit - but not close enough to switch on, and on two of five levels it saves nothing at all.
Off by default, behind `--stuck`, kept because it is sound and cheap to re-measure.

Also worth recording: the 4096-entry cache was thrashing badly, and that alone was most of the
cost - `duckie` went from 24.8 s to 11.1 s at 256 k entries with no change in result. The
first measurements said 1.6x to 2.8x slower and would have led to abandoning it.

## Why 72% became 7%

docs/012 measured 72% of sampled `society` states as provably dead and this pass gets 2.6%.
Both numbers are right; they are about different states.

docs/012 sampled by random legal play, 60 to 250 moves deep, which shoves items all over the
room - and the detection rate tracked how much a walk had actually disturbed (`society` moved
5.69 items on average, `city` 1.33, and `city` scored 0%). **A\* does not go there.** It
spends its effort near the start, on states reached by a few dozen moves where the items are
mostly where they began, and an undisturbed item is almost never stuck.

The lesson is narrower than "random walks are a bad proxy". It is that a rate measured over a
sample says nothing until you also know the sample contained the situation being measured -
which is what the "items moved" column in docs/012 was for, and it was already saying this.

## Follow-ups

- The detector answers "is a fish walled in". It does not answer "is an item that must still
  be *delivered* now undeliverable" - `city`'s crabs, where a dropped one is fatal but is not
  stuck, just misplaced. Items have no goals, so nothing in the model can express it.
- `LayoutCacheLimit` is a fixed 262,144 entries. It should scale with the room size, since the
  cached grids are `w*h` per fish.
