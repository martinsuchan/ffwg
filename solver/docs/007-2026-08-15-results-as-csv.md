# 007 — The results record as a CSV, and crediting simplified levels

2026-08-15

Two changes to the record docs/006 introduced: it is a spreadsheet now, and a
solution found on a hand-simplified room is credited to the real level when it
earns it.

## Why CSV

`results.json` was written for a program to read, but nothing reads it except the
`results` command. What it is actually used for is *comparison* — our length
against the hall of fame, room size against how far the bound got, which branch
the unfinished levels cluster in — and that wants a table you can sort, not an
object graph you have to scroll.

`solver/docs/results.csv` therefore carries the facts about each level alongside
the run:

```
name,derivedFrom,branch,best,bestAuthor,bundled,our,width,height,items,
proven,method,source,bound,expanded,stored,seconds,status,recorded
```

**Only the run columns are recorded data.** Branch, hall of fame, size and item
count are recomputed from the corpus on every write, so the table cannot drift
from the levels the way a hand-maintained one would, and editing them in a
spreadsheet changes nothing. `ffsolve results --rebuild` forces that refresh
without searching, for after `worldfame.lua` changes or a level is added.

Two consequences of holding the level facts:

- **Every level has a row**, including the ones never searched. The file is a
  scoreboard for the whole game, not a log of what happened to run.
- **Rows are in play order, grouped by branch**, which is how the game is
  actually thought about. That needed the game's own metadata: branch names come
  from `worlddesc.lua`'s fourth argument (read in English, so the table does not
  change meaning with a language setting) and the order from `worldmap.lua`'s
  declaration order, both via a new `Corpus.WorldMap`. A simplified level sorts
  directly after the level it came from.

The printed `ffsolve results` table gained the same grouping, and reads from the
CSV, so the file and the command can no longer disagree.

### Protecting it

The record accumulates weeks of search and nothing can reconstruct it, so writes
are guarded four ways: an exclusive `.results.lock` held across read-modify-write
(two solvers recording at once used to silently drop each other's work — the
docs/006 bug); the previous table kept as `results.csv.bak`; a temp file renamed
over the target so a crash cannot leave half a record; and merge-only semantics,
where a weaker run can never displace a stronger one.

CSV added one failure mode worth handling explicitly: **Excel holds an exclusive
lock on an open CSV**, so the rename fails. The solver now says exactly that and
leaves the new table as `results.csv.tmp` rather than throwing the run away. The
file is written with a UTF-8 BOM for the same audience — half the hall of fame
has accented names, and Excel reads a BOM-less UTF-8 CSV as the local codepage.

### The separator follows Windows

A comma-separated CSV opens as a single column on a Czech, German or French
Windows, where the list separator is a semicolon. So the file follows the local
setting, and pairs it with the local decimal separator — `171,6` under `;`,
`171.6` under `,`. `ffsolve results --rebuild --separator culture|comma|semicolon|tab`
chooses; the repository's own copy is semicolon-separated.

Three details make that safe rather than merely convenient:

- **The choice sticks.** Only a brand-new file follows the culture; later writes
  preserve whatever the file already uses. Otherwise a solve run on a machine in
  another locale would reflow all 83 lines of a committed file.
- **Reading detects it** from the header line, and accepts a decimal comma or a
  point regardless, so the file crosses machines intact. The decimal is
  unambiguous once the field has been split, which is what makes that work.
- **The counts stay invariant.** A thousands separator would be noise in a file
  whose numbers exist to be compared, and a hazard in a comma-separated one.

Reading the Windows setting needed a Win32 call rather than `CultureInfo`
(`WindowsLocale.cs`). This project builds with **`InvariantGlobalization`** —
deliberately, for Native AOT: no ICU dependency, faster startup, identical
formatting everywhere — and under it every culture behaves as the invariant one,
so `TextInfo.ListSeparator` always answers `,` however Windows is configured.
`GetLocaleInfoEx(LOCALE_SLIST/LOCALE_SDECIMAL)` costs one P/Invoke, keeps the AOT
posture, and has the bonus of honouring a *user override* — someone on a Czech
machine who has set the separator to a comma gets a comma, which the culture
tables would not have said either.

## Crediting a simplified level's answer

`gems-simple` had been solved to a proven-shortest 59, and `gems` — the real
level — still showed unsolved. That is the wrong answer to the question "have we
solved gems?".

The rule now: when a solve on a simplified room produces an answer, the CLI
already replays it against the source level (docs/006). **If that replay
succeeds, the result is recorded against the source level too**, because a move
string that solves `gems` is a solution to `gems` regardless of which room it was
discovered in.

What deliberately does *not* carry across is the proof:

- **`proven` is false** on the transferred row. The simplified room has a
  different state space — a frozen item cannot fall, a deleted one cannot be
  stood on — so "shortest here" is only an upper bound there.
- **the bound is not transferred at all.** It was proved about the wrong level.
  The merge keeps whatever bound the real level had earned on its own (`gems`
  still shows 57, from its own 900-second run).

The `source` column records where the answer came from, so the row says
`found on gems-simple` rather than pretending the search ran on the real level.
`gems` now reads: our 59, hall of fame 59, bundled 59, bound 57.

The move string itself lives in `solver/solutions/`, named after the **real**
level (`gems.lua`) — it had been lost entirely, since the original run wrote no
file, which is what prompted the CLI to print the `--out` line to use.

## Two bugs the new tests caught

`ResultsFileTests` covers the round trip, the backup, the merge rule, the
separator (all three, both directions, and that it sticks) and the world-map
parse. Writing it found two things:

- **Lua comment lines were parsed as calls.** Both `worldmap.lua` and
  `worlddesc.lua` open with a commented-out signature
  (`-- branch_addNode(parent, codename, ...)`), which the scanner happily read as
  a real node called `codename` — shifting every level's play order by one.
- **A null slipped through where a `TryGetValue` had failed.** `(int, string)`
  and `LevelPlace` both leave their string *null*, not empty, when the lookup
  misses — which `ending` (no hall-of-fame entry, not a map node) hits. The old
  escaping happened to tolerate it, because `((string)null).AsSpan()` is legal
  and yields an empty span; the separator-aware version calls `IndexOf` on the
  string and threw. Fixed at the source, in both places.
- **The merge preferred a proof over a shorter solution.** docs/006's rule was
  "a proven-optimal result always wins", so a proven 61 would displace a verified
  59. Now length wins and the proof only breaks ties at equal length. Every
  recorded solution has been replayed to *Solved*, so a shorter one is real; a
  proof at a greater length can only mean the two runs were not about the same
  room.

## Open

- The transfer is one-way and only fires on `solve`. Nothing walks the existing
  `solutions/` files and re-checks them against other levels.
- `LevelFacts` caches level geometry per process, keyed by name only. A run that
  pointed `--levels` at two different directories in one process would read the
  first one's facts — not reachable from the CLI, which takes `--levels` once.
- The row for a simplified level borrows its source's `best` and `bundled`
  numbers so the comparison is meaningful; `derivedFrom` is what says they are
  borrowed. It is still worth remembering when reading the file.
