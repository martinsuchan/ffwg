# 003 — Window optimiser, and where the slack actually is

2026-08-14

First search work. Adds the state layer, a windowed re-optimiser, and
`ffsolve improve` — and, more usefully, establishes **which levels still have
room to improve and what kind of search will be needed to get it**.

Net result so far: **42 moves saved across 4 levels**, all replayed and verified.

## Where we stand against the hall of fame

The bundled `legacy/solution/*.lua` are by various authors and are not always the
best known. `solver/docs/worldfame.lua` (the hall of fame, a slightly newer copy
than `legacy/script/worldfame.lua` — 10 entries differ) gives the record for each
level. Comparing:

**68 of 80 match the record exactly. 12 are longer**, by 830 moves in total:

| level    | ours | best | gap |       | room  | models | record holder        |
| -------- | ---- | ---- | --- | ----- | ----- | ------ | -------------------- |
| corals   | 252  | 242  | 10  | 4.1%  | 28×25 | 8      | David Glass          |
| nowall   | 420  | 413  | 7   | 1.7%  | 30×34 | 15     | Bertram Felgenhauer  |
| hanoi    | 432  | 402  | 30  | 7.5%  | 57×21 | 20     | David Glass          |
| hole     | 465  | 459  | 6   | 1.3%  | 50×40 | 11     | Masaaki Irie         |
| city     | 485  | 481  | 4   | 0.8%  | 50×23 | 18     | Gabor Braun          |
| pavement | 512  | 500  | 12  | 2.4%  | 47×36 | 24     | Erik Hajduk          |
| atlantis | 572  | 531  | 41  | 7.7%  | 50×35 | 20     | Uoti Urpala          |
| linux    | 656  | 596  | 60  | 10.1% | 29×32 | 16     | Zmiter Nikitin       |
| floppy   | 946  | 890  | 56  | 6.3%  | 51×35 | 15     | Aleš Zimmermann      |
| barrel   | 1237 | 1229 | 8   | 0.7%  | 50×39 | 20     | Zmiter Nikitin       |
| grail    | 1691 | 1681 | 10  | 0.6%  | 51×36 | 43     | Zmiter Nikitin       |
| map      | 2127 | 1541 | 586 | 38.0% | 48×33 | 26     | Gabor Braun          |

## How good the input already is

Before building anything, a probe replayed every bundled solution and hashed the
state after each move, looking for a repeat. If `state(i) == state(j)`, every
move between them is pure waste and can be spliced out with no search at all.

**Zero revisited states, across all 80 solutions and 32,031 moves.** A
hand-recorded playthrough would essentially never manage that. So there is no
cheap win anywhere in this corpus, and any improvement has to come from a real
search. (It also exercised the state key over 32k states before a line of search
code existed, which was worth the twenty minutes on its own.)

## What was built

**State layer** (`Room.State.cs`). Two observations make it small and fast:

- The grid is *derived*, never authoritative — `Reset()` already rebuilds it by
  clearing and re-masking. So a snapshot is only `ModelState[]` (~300 bytes),
  not the grid too (~2.5 KB more), and `RestoreFrom` re-stamps it.
- A *settled* state carries no in-flight bookkeeping: after `SettleAll()` the
  round pipeline has cleared `Dir`, `Pushing`, `LastFall` and every `ReadyTo*`,
  and `Weight` is back to its declared value. So the key is just position, facing
  and the alive/out flags, over `Level.MutableModels` (models that provably can
  never move, die or leave are excluded — mainly each level's room shape).

`TouchDir` is excluded, as docs/001 §3 required: `SetTouched()` records it even
on a **rejected** move, so including it would change the key of the state you are
standing in merely by probing a successor.

**Window optimiser** (`FishFillets.Search`). For each window `[i, j]`, A\* from
the state at step *i* to the state at step *j*, admitting only paths shorter than
`j - i`; anything shorter is spliced in and the sweep repeats to a fixpoint.

The reason a window is much easier than a level: it has a fixed start **and a
fixed goal state**, which admits a far sharper heuristic than "get both fish
out". Since every accepted move steps exactly one fish by at most one cell (a
turn steps none), the summed Manhattan distance of the fish to their target cells
is admissible, plus a provable turn where the fish must move against its facing.
Items are deliberately not counted — one move can push several at once and
gravity moves them free, so their displacement is not additive and counting it
would break admissibility.

`ffsolve improve <level> [--window N] [--stride N] [--nodes N] [--out F]`.
Every result is replayed through `SolutionValidator` before it is reported or
written; the command refuses to emit a solution that doesn't solve the level.

## Results, and the limit this ran into

At window 18, sweeping all 12 gap levels:

| level  | ours | → | best known | note                    |
| ------ | ---- | - | ---------- | ----------------------- |
| hole   | 465  | **463** | 459  | 2 of 6 closed           |
| linux  | 656  | **652** | 596  | 4 of 60                 |
| barrel | 1237 | **1231** | 1229 | 6 of 8 — two off the record |
| map    | 2127 | **2097** | 1541 | 30 of 586               |

The other eight found nothing. Saved solutions are in `solver/solutions/`.

**`corals` is the instructive failure.** Smallest room, fewest models, shortest
solution, a 10-move gap — the most promising target on paper. Windows were pushed
right up:

| window | nodes      | time  | found |
| ------ | ---------- | ----- | ----- |
| 12     | 22,831     | 0.4 s | —     |
| 20     | 386,175    | 3 s   | —     |
| 30     | 4,830,921  | 37 s  | —     |
| 40     | 61,746,330 | 496 s | —     |

Cost grows roughly 12× per +10 window, and nothing is found. That is not a
tuning problem, it is structural: **splicing preserves both endpoints**, so it
can only ever find a shortcut between two states that our solution already
passes through. If the 242-move route diverges from ours for more than a window's
worth of moves — which the window-40 result says it does — no window size
recovers it.

So the read on the 12 levels is:

- **hole, linux, barrel, map** have *local* slack, and windowing gets at it.
- **corals, nowall, hanoi, city, pavement, atlantis, floppy, grail** do not.
  Their improvements need a globally different route, i.e. search from scratch.

## Open

- **Bigger windows on the four that responded.** `barrel` is 2 moves off the
  record and `hole` 4; both are plausibly reachable at window 24–30. Not yet run.
- **A from-scratch search** for the other eight. This is the macro-move
  reformulation from docs/001 §6 stage C — the only thing that can find a
  different route. `corals` is the right first target (28×25, 8 models).
- **Optimiser performance.** `RestoreFrom` runs 8× per expansion and re-fills the
  whole grid each time; an undo journal would avoid that and is likely worth
  several×. The heuristic is also weak whenever only item positions differ, which
  is exactly the case that matters most.
- **`--stride` is 1 by default**, so a sweep is `n` windows. Fine at these sizes,
  wasteful on `map` (2,127 windows, 475 s).

## Tests

`dotnet test` is 112 now, up from 101 (docs/002).

`StateLayerTests` checks the claim the whole search rests on — that equal keys
mean interchangeable states — directly rather than through a search: restore
mid-solution and require the *rest* of the solution to still replay and solve;
require the restored grid to match a freshly built room cell for cell; require
all 236 keys along a solution to be distinct; and require a **rejected** move to
leave the key untouched, which is the `TouchDir` caveat made executable.

`WindowOptimizerTests` covers both directions. That it never breaks a solution is
easy to check on real input. That it actually *finds* savings needs input with a
known detour — and since the corpus has none, the test builds one: turning
against your facing and back moves the fish nowhere (`Unit::goLeft/goRight` only
flips the side when you are not already facing that way), so `"rl"` prepended to
a solution is a guaranteed two-move no-op. The optimiser is required to remove
exactly those two.
