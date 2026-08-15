# 010 — Editor erase tools, and a pre-check for modified levels

2026-08-15

Three additions to `ffedit` (docs/006), all of them about making a level smaller
with less ceremony.

## What was missing

The editor could **add** wall and **remove whole models**, but the inverses were
either awkward or absent:

- **erasing wall** existed only as a right-*drag* while Draw walls was on. There
  was no `MouseRightButtonDown` handler at all, so a single right-click did
  nothing - you had to press and move to erase one cell;
- **cutting part of an item** was impossible. An item was all or nothing: freeze
  the whole thing, delete the whole thing, or leave it. A four-cell crate that is
  only in the way at one corner had no middle option;
- **freezing** needed a select-then-press-F round trip per item, which is the most
  common operation in the whole tool.

## What it does now

The single `Draw walls` toggle became four mutually exclusive tools, because every
one of them is destructive and **there is still no undo** - so exactly one click
behaviour is live at a time, and Esc leaves the tool before it touches the
selection.

| tool | click does |
| --- | --- |
| *(none - default)* | select an item; drag or arrows move it |
| **Draw walls** | add a wall cell; right-click or right-drag erases |
| **Erase walls** | clear a wall cell |
| **Erase tile** | cut one cell out of an item |
| **Freeze on click** | merge the clicked item into the room shape |

**Erase tile is scoped by the selection.** With an item selected, only that item
is cut, so a click that strays onto a neighbour does nothing instead of quietly
damaging it; with nothing selected the first click picks up whatever it lands on.
Cutting the last cell removes the model outright.

## The part that needed care

`LevelDocument.RemoveTile` leaves the model's **anchor where it is**. Marks are
stored relative to the anchor, so moving it - which is the tempting thing to do
when the top-left cell is the one being cut - would shift every remaining cell to
a new absolute position. Instead `Serialise` pads the vacated rows and columns
with `.`, and the shape parser skips them.

That is the one invariant worth checking rather than assuming, so it was: driving
the real built `LevelDocument` through reflection over `start.json`, cutting a
cell, and asserting that the remaining nine cells are the same nine absolute
positions as before, that the level still builds through the actual engine, and
that cutting every cell removes the model. Painting and erasing wall round-trip
exactly, and freeze-on-click merges the item's cells while leaving the original
wall intact. All of it green, plus the window itself constructing with its four
tool buttons.

Also fixed while in there: `_Delete item` and `_Draw walls` both claimed Alt-D.
Walls take W now.

## `solve` checks the original solution first

Alongside the editor work, `ffsolve solve` on a level with a `sourceLevel` stamp
now replays the **original** level's own solution against the simplified room
before searching, and says whether it still solves it.

This is the natural companion to the check that already ran *after* the search
(does my answer replay on the real level?). Asking the question in the other
direction, up front, is what tells you whether the edit left the same puzzle -
worth knowing before spending an hour on a search rather than after.

**A failure is not an error and does not stop the search.** Breaking the original
path is frequently the point of the edit, and the simplified room can still be
solvable by a shorter route. What it predicts is that an answer found here is less
likely to replay back on the real level.

It discriminates, which is the useful property - of the three simplified levels in
`solver/levels`, `stairs-simple` still admits `stairs`' 163-move solution while
`gems-simple` and `cabin1-simple` do not:

```
source     'stairs' - its 163-move solution still solves this room, so the edit
           kept the original path (and that is an upper bound)
source     'gems' - its 59-move solution NO LONGER solves this room
           (move 18 ('L') is not possible); the edit changed the puzzle
```

Single-level `solve` only. Batch mode prints one line per level from parallel
workers, so an extra line per level would interleave into noise - and `--all`
draws from `LevelsWithSolutions()`, which a simplified level is not in anyway.

## Open

- Still no undo, which is what makes the one-tool-at-a-time rule matter.
- The editor has no coverage in the test project: it is WPF, so `net10.0-windows`,
  and `FishFillets.Physics.Tests` is plain `net10.0`. The checks above were a
  scratch harness, not something that runs with `dotnet test`. A small
  `net10.0-windows` test project over `LevelDocument` alone would fix that - the
  document operations are pure and have no UI dependency.
