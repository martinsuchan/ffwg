# 009 — Searching over item moves

2026-08-15

A third search (`ffsolve solve <level> --items`, `ItemSolver`) that records a
state only when **something other than a fish has changed**. It works, it is not
finished, and the way it fails is informative.

## The idea

A solution contains very few real events. `wc`'s optimum is 100 symbols, of which
**13 move an item**; the other 87 are fish swimming into position. `wc` has three
movable items in a 23x22 room, so the space of item configurations is tiny -
while the macro search, which also stores where each fish parked, holds
65,000-200,000 states on that level.

So: edges are item moves, and a state is kept only if an item moved or a fish
left the room. Fish travel is priced into the edge and recomputed on demand.

**Fish positions stay in the key.** Walking costs moves here, so the price of the
next edge depends on where the fish are; Sokoban's trick of normalising the
player away needs a free player (docs/005). What changes is that the only fish
positions ever *stored* are "wherever a push left them".

**Getting out of the way stops being a state and becomes part of an edge.** This
is the real prize. `MacroSolver` needs a rule for guessing where a fish might
usefully park, and every version of that rule measured in docs/008 was either
incomplete or ruinously expensive. Here a fish only moves because some item needs
it to, and a fish in the way is moved aside while costing the edge - so there is
no parking rule at all.

## Where it got to

| level | optimum | `--items` (item-directed) |
| --- | --- | --- |
| `start` | 54 | **54, verified** — 6,719 expanded, 28,949 stored, 15.5 s |
| `wc` | 100 | frontier closes at f=112, 1,783 stored |
| `submarine` | 83 | closes at f=100, 9,190 stored |
| `noground` | 44 | closes at f=110, 5,200 stored |

**The size claim holds**: 5,000-11,000 stored states against the macro search's
65,000-200,000. And `start` comes out at the known optimum, so the machinery -
routing, clearance, reconstruction, the physics - is sound end to end.

**But three levels exhaust**: the edge graph closes without ever reaching a
solved state, which means the expansion is missing edges rather than running out
of budget.

## Three guesses, all wrong

Recording these because each looked convincing - and because the probe below
later showed why none of them could have worked:

1. **Escapes were not recognised as events.** `IsOut` (left the room) and
   `IsLost` (removed) are separate flags and only the second was checked; the
   test also has to compare against the parent, or every move after the first
   escape counts as an event. Real bug, genuinely fixed - and it changed nothing.
2. **The clearance cap was too tight.** Raising the number of resting places
   tried for a fish that must step aside from 4 to 32 changed no outcome, only
   the running time.
3. **Unroutable border placements.** The router refuses to route *through* a
   placement it believes a fish would escape from, and that test is deliberately
   generous (docs/005). If a fish moves there and does not escape, nothing
   happened, the state is discarded, and no route can ever put the fish there
   again. Recording those positions as events fixed nothing and made `start` five
   times slower, so it was reverted.

The clearance variants themselves are unconditional now, which *was* a real fix
in kind: the obvious version only moves the other fish when it blocks the
traveller's *route*, which misses the case this solver exists for - a fish
standing where an item needs to be pushed blocks nothing a route would notice,
because the traveller can swim around it.

## The item-directed rewrite

The first version kept the macro search's shape - enumerate every fish placement,
try three actions - and bolted clearance onto it. That drifts from the design in
a way that matters: nothing ever knows *which squares need to be free*, so the
fish that has to step aside is chosen by "cheapest four placements" rather than
by what it is blocking. And because `InertRouter` treats a turn as a placement
costing 1 in the same square, the cheapest four are typically *turn in place*,
up, down, and one step forward. Backing away costs 2 and was never tried.

Rebuilt item-directed, which is what the design called for:

- **each item x direction is checked with both fish removed first** - only wall or
  a provably immobile model rules it out, which is cheap and prunes hard;
- **push placements are computed, not discovered**: for each item square and each
  square of the fish's own shape, the anchor that puts them in contact on the far
  side of the push;
- **arrangements are deduplicated by square, not by placement**, so a turn no
  longer eats half the clearance budget - the next edge re-derives facing at the
  same price anyway;
- **carries, drops and exits are their own families**, and the squares a fish can
  be carried out from are enumerated once per level since they depend only on the
  walls.

This is a straight win on cost - `start` went from 62 s to 15.5 s - and it makes
the expansion narrow enough to reason about. **It did not fix the exhaustion.**

Not yet built: retrying an edge that kills a fish with the victim cleared out of
the item's swept path. The engine reports the death and both the item's before and
after positions are known, so the region to vacate is available without predicting
the fall - but it is unwritten, and the levels here exhaust before reaching it.

## The probe, and the answer

Rather than guess a fourth time, `ItemExpressibilityTests` asks the question
directly. A known optimum is cut at every point where an item moved or a fish
left - exactly the states this solver records - and each consecutive pair is
handed to the **real expansion** (`ItemSolver.ProbeSuccessors`, which runs
`ExpandItemMoves` with an observer on every edge) with one question: do you
produce this successor from this predecessor, and if not, what stopped you.

Every edge reports its fate, so "never built" is distinguished from "built and
then filtered". When a successor is missing, the probe reconstructs the optimum's
own version of that edge - replay to just before the event move and both fish are
exactly where the edge needed them - and asks each stage whether it could have put
them there. It checks its own replay against the recorded checkpoints first, so a
wrong reading cannot pass unnoticed.

The result is unambiguous. `start` has **no** gaps, which is why it is the level
that solves. The other three:

| level | events | successors never built |
| --- | --- | --- |
| `start` | 21 | **0** |
| `wc` | 17 | 8 |
| `submarine` | 33 | 4 |
| `noground` | 10 | 4 |

Costs are right wherever an edge exists at all - no transition is generated
*above* the optimum's price - so this is purely missing edges.

## The cause: a coarse predicate meeting a different consumer

Every unreachable case in all three levels reports the same thing:

```
first non-inert step: move 78 ('R') fish_big (16,7)->(17,7)
    refused because it is holding something up
```

`InertRouter.IsFreeStep` refuses **every** step from a placement where
`IsHoldingSomethingUp` is true, and that test is coarse on purpose - anything
mobile directly overhead counts, with no question of whether it is *also* propped
up elsewhere. Its own comment says guessing wrong in this direction "only costs a
search node".

That is true of `MacroSolver`, where a false positive turns travel into an action,
and an action is a node. **It is fatal here.** In this solver a false positive
turns travel into an *event* edge - and `TryEdge` then discards it, because
`SomethingHappened()` is false: the item never fell. So there is no edge at all.
A fish that walks under a wide item supported elsewhere can no longer travel (the
router refuses every step) and its carry/drop edges produce no event (nothing
falls), so **it can never move again**. The frontier closes. That is the
exhaustion, and it is why all three earlier guesses moved nothing.

It also explains `start`: its fish never end up under something that is propped up
elsewhere, so the predicate never lies there.

The fix is to make the question exact - would this actually fall if the fish left
- rather than "is something overhead". `Room.Landslip`'s support fixpoint already
computes precisely that, and the coarse test is a sound cheap pre-filter for it,
so the exact version only runs when something really is overhead. Under-reporting
would be the dangerous direction (a route that silently drops an item), which is
what makes the fixpoint the right answer rather than a tighter guess.

## The second cause: stepping aside is not what the other fish is doing

The remaining gaps are the clearance model. In a real optimum the non-acting fish
does not step aside - it travels to where **its own next job** is, up to 14 moves
away, at cheapness rank 22, 35, 54 among its reachable squares. The expansion
offers it the four nearest resting places.

This is why raising `ClearanceTries` from 4 to 32 (guess 2 above) changed nothing:
the cap is not the problem, the **ranking** is. Cheapest-first is the wrong order
for a fish that is not getting out of the way but going somewhere.

One case (`wc` event 4) needs something further still: the destination is
unreachable from the parent but reachable if the actor is ignored, so the two fish
have to **interleave** - and the expansion moves one, then the other.

## Open

- **The holding predicate** (above) - the exhaustion, and the thing to fix first.
- **The clearance ranking** (above) - costs moves rather than solvability, so it
  is second.
- `start` takes 15.5 s where the macro search takes 1 s. Per-expansion cost is
  high - a route per clearance variant per fish - and nothing has been done about
  it, because correctness comes first.
- `--items` records as method `items`, with no lower bound, for the same reason
  `--macro` does not: an incomplete expansion's `f` bounds only the solutions it
  can express.
