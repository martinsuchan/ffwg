# 008 — Measuring what a parked fish is actually for

2026-08-15

`MacroSolver`'s parking rule was justified by two mechanisms that **do not exist
in this game**. Measuring them against the engine collapsed the rule from "line of
sight to anything mobile" to "standing under something", and with it the state
count on `start` by a factor of 150.

## The claim under test

`InertRouter.IsParking` decided whether a fish that has swum somewhere and done
nothing is worth a search state of its own. docs/005 argued it wide:

> A fish that has parked matters only because it is solid, and it can only be
> solid in someone's way if it is either touching something now, or waiting
> underneath something that could later fall onto it.

That is two mechanisms: **catching a falling item**, and **stopping a push**. Both
are testable in a few lines against the ported engine, and neither survives.

## What the engine says

`FishAsObstacleTests`, six cases:

- **A falling item kills whatever it lands on** — any weight, either fish. A
  *light* item falling one cell onto the **big** fish kills it. So a fish cannot
  catch anything, ever.
- **A fish cannot be pushed** — not directly, and not as the far end of a chain.
  An item pushed into a waiting fish with three empty cells behind it simply does
  not move; the whole push is refused. So a fish cannot be a backstop either.
- **Resting is a different rule from landing.** An item already at rest on a fish
  is fine, and there weight decides: heavy kills the small fish, the big fish
  holds it. Impact kills; carrying does not.

Both mechanisms therefore fail, and for different reasons:

- a fall onto a parked fish kills it, and every fish carries a goal, so the state
  is pruned as unsolvable regardless;
- a push into a parked fish is refused, so the fish only forbids a move that was
  otherwise available — which never helps.

**One case survives.** Support can change hands with no fall: a wide item held up
by one fish, the other slides in under a different part of its footprint, the
first leaves, and the item never moves. That is a real maneuver, and the search
has to be able to express it.

So `IsParking` is now exactly `IsHoldingSomethingUp` — *I am standing under
something* — and `SeesMobile` (plus the already-dead `IsMobileAt`) is gone.

## What it bought, and what it cost

Macro search, 60-second budget, before and after:

| level | optimum | wide rule | narrow rule |
| --- | --- | --- | --- |
| `start` | 54 | unsolved, f=49, 658,262 stored | **54** — 0.7 s, **4,405 stored** |
| `submarine` | 83 | | **83** — 0.6 s |
| `wreck` | 91 | | **91** — 54 s |
| `noground` | 44 | | **44** — 38 s |
| `cannons` | 39 | | **39** — 55 s |
| `wc` | 100 | | **120** ✗ |
| `library` | 111 | | unsolved at 120 s, f=77 |

Five of six known optima survive, and `start` goes from *not solved at all* to
solved in under a second on a 150× smaller state space. The parking rule was
doing essentially nothing except storing states.

`MacroSolverTests` now pins `start` and `submarine` so this cannot drift
unnoticed — the previous `wc` regression (106 against 100, docs/005) was caught by
hand, which is why it could come back.

## What `wc` proves

`wc` at 120 is the useful failure. Handover alone is not enough, so there is a
third reason to park, and it is the one the user proposed independently:

> expand into dir udlrUDLR if it allows the other fish to move to a previously
> blocked location

A fish is an obstacle to *movement* even though it is not an obstacle to being
pushed: the other fish cannot swim through it, and an item cannot be pushed into
it. So a fish sometimes has to move aside for no other reason, and under the
narrow rule no successor is generated for that at all — the fish simply never
gets out of the way.

The old line-of-sight rule covered this case by accident, because the *other
fish* counts as mobile, so almost every placement with a clear line to it was
kept. That is also why it barely reduced anything.

## Trying to close the `wc` gap — three rules, all bad trades

Each of these was implemented and measured rather than argued about. Times and
state counts are from a single machine, same budget, `--macro`:

| parking rule | `wc` (opt 100) | `start` (opt 54) | `submarine` (opt 83) |
| --- | --- | --- | --- |
| see anything mobile (original) | **100** | unsolved @60 s, 658 k stored | — |
| **handover only** | 120 — 32 s | **54** — 1.0 s, **4.4 k** | **83** — 0.6 s |
| + in another fish's way | 116 — 278 s | 54 | 83 |
| + beside an item | 116 — 55 s | 54 — 43 s, 158 k | 83 — 9.9 s |
| + on an item's push lane | 114 — 185 s | unsolved @240 s, 1.0 M | 83 — 130 s |

**"In another fish's way"** is the principled one — two routes per fish per state,
one with the fish treated as absent, and any placement that appears only in the
second is somewhere this fish is shutting the other out of. It is kept in the code
behind `MacroSolver.UseReachabilityUnblocking`, off, because it costs 9x the time
for four moves.

The pattern is monotone and unhelpful: every widening buys `wc` a little and costs
every other level an order of magnitude. There is no cheap middle ground, and only
the original see-anything rule reaches 100 — while being far too slow to be worth
it. So **handover only** stands.

Why `wc` needs more is still unexplained. `MacroExpressibilityTests` replays a
known optimum and reports every point where one fish hands over to the other that
is not a parking place; `wc` has five, one of which (move 87) is the small fish
stopping in **open water with nothing adjacent at all**. But the same probe flags
`start` and `submarine` too, and the macro search still reaches their optima - it
only has to find *some* solution of that length, not that one. So the probe
narrows the suspects without convicting any of them.

## Running it as a finder

Which is the point: every level above is one plain A\* already solves. Six levels
it does *not*, 300 s each under the narrow rule, against 900 s of plain A\*:

| level | record | plain A\* (900 s) | macro (300 s) |
| --- | --- | --- | --- |
| **`gems`** | 59 | bound 57, unsolved | **solved, 59** — 205 s |
| `bathyscaph` | 82 | bound 82 | f=80, unsolved |
| `society` | 110 | bound 86 | f=84, unsolved |
| `duckie` | 98 | bound 85 | f=79, unsolved |
| `keys` | 281 | bound 61 | f=63, unsolved |
| `cabin1` | 168 | bound 162 | f=132, unsolved |

**`gems` is a real result**: solved on the actual level, at the hall-of-fame
length, where plain A\* had spent 900 s and stopped at 57. Until now this project
only had `gems` via the hand-simplified `gems-simple` room (docs/007).

The other five say the collapse is not a general answer. On `cabin1` the macro
frontier is well behind where plain A\* gets, because routing every fish at every
state costs more per node than it saves in nodes. `gems` looks like the shape that
suits it - a room where the fish do a great deal of open-water travel between a
few real decisions, which is exactly the waste a macro-move deletes.

So `--macro` is a second tool to try on a stuck level, not a replacement.

## Open

- **The `wc` gap is unexplained, not just unfixed.** Worth one more attempt with a
  sharper tool: search for the shortest solution *that the decomposition can
  express* and diff it against the real optimum move by move, rather than
  inspecting handover points.
- **Nothing here measures the case that matters.** Every level in these tables is
  one plain A\* already solves. The point of the collapse is the levels it cannot,
  and `library` — unsolved at 120 s even under the narrow rule — is a warning that
  the macro expansion's per-node cost may still dominate on bigger rooms.
- **The merge rule has no notion of provenance.** When the macro run solved `gems`
  natively at 59, the record kept the older 59 found on `gems-simple`, because
  merging ranks by length and then by proof only. The row said `found on
  gems-simple` while we had it on the real level; it was corrected by hand.
  Ranking should prefer a result found on the level itself at equal length.
- A macro run's `f` is no longer recorded as the level's `bound` - under an
  incomplete expansion it bounds only macro-expressible solutions, and the table
  uses `bound` to argue a hall-of-fame number is optimal. Fixed while running the
  finder, but it means every macro row now carries whatever bound plain A\* had
  earned separately, which is the honest reading.
