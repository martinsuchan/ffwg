# 085 - The sandbox replays the best known solutions

2026-11-20

## What was done

- **Shorter solutions replace the game's own.** The solver (solver/docs/003,
  solver/docs/029) found shorter solutions for 9 levels. They now sit in
  `legacy/solution/<level>.lua`, which is what `/sandbox` replays (045) and
  what `publish/` ships:

  | level | original | now | hall of fame |
  |---|---|---|---|
  | barrel | 1237 | 1223 | 1229 |
  | duckie | 98 | 96 | 96 |
  | floppy | 946 | 944 | 890 |
  | hole | 465 | 463 | 459 |
  | key | 524 | 522 | 524 |
  | linux | 656 | 652 | 596 |
  | map | 2127 | 2075 | 1541 |
  | propulsion | 1964 | 1962 | 1964 |
  | redhat | 287 | 283 | 287 |

  barrel, key, propulsion and redhat beat the hall of fame. duckie matches
  it, and A* proved 96 optimal.
- **The originals are kept.** Each replaced file moved unchanged to
  `legacy/solution/old/<level>.lua`. This is the one deliberate edit of
  `legacy/`, made on the user's request. Root CLAUDE.md records the exception.
- **The web needs no code change.** It fetches `solution/<level>.lua` by
  name, and the e2e check (`06-solutions-validate`) lists only the top-level
  `*.lua`. It replays all 81, the new ones included. `publish.ps1` copies
  `solution/**`, so `old/` ships too, unused.
- **The solver keeps measuring against the originals.**
  - `Corpus.TryReadOriginalSolution` prefers `old/`. The results' `bundled`
    column and the test helper `TestCorpus.Solution` use it, so pinned node
    counts and shortcuts stay put.
  - `verify`, and `improve` without `--moves`, use the best solution.
  - A new test checks every file in `old/`: it must still reach Solved, and
    its replacement must be strictly shorter.

## Open for next time

- **Only the solver replays the originals in `old/`.** The web suite does not.
- **A later improvement has to be promoted by hand.** Copy it from
  `solver/solutions/` and move the current file to `old/`, unless `old/`
  already holds the original.
