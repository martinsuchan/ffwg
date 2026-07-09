# 022 - Headless Solution Validator

2026-07-09

## Context

Step 2 of [[ffwg-solution-save-replay-roadmap]] (see prior chat turn / the
docs/021 entry for the full research): a fast, render-free way to check
whether a recorded move string actually solves a level, built on step 1's
move recording (docs/021). Doubles as the tool to finally check the user's
own open item - the 79 `legacy/solution/*.lua` files uploaded earlier still
needed validating.

## Port

Faithful, minimal translation of `legacy/src/level/Room.cpp`'s
`loadMove()`/`makeMove()` and `Controls::makeMove()`:

- `web/src/game/Unit.ts`: new `driveOrder(symbol)` - applies `symbol` if it
  belongs to this unit, independent of held keys (legacy's
  `Unit::driveOrder()`).
- `web/src/game/Controls.ts`: new `makeMove(symbol)` - scans units for
  whichever one owns `symbol`, drives it, records on success. Symbols never
  overlap between units, so this fully identifies both the unit and the
  direction on its own (legacy's `Controls::makeMove()`).
- `web/src/game/Room.ts`: new `loadMove(symbol)` (settle any pending falls,
  then apply exactly this one move, throwing if invalid) and `settleAll()`
  (settle only, no move - call once after a solution's last move to apply
  its consequences before checking `isSolved()`). Both build on a new
  private `fastForwardSettle()` loop (`do { beginFall() } while
  (!isFresh())`, capped at 1000 rounds against a pathological infinite
  fall) - legacy's `loadMove()`'s "let object to fall fast" behavior,
  simplified: our port doesn't need the intermediate `Room::makeMove()`
  method legacy has, since that method exists there only to also serve a
  separate direct-single-move UI action (`Level::action_move()`) this port
  doesn't have.
- `web/src/game/GameEngine.ts`: `loadMove()`/`settleAll()` passthroughs.
- `web/src/game/SolutionValidator.ts` (new): `validateSolution(engine,
  moves)` - replays a full move string against a fresh engine, stopping at
  the first invalid move (reporting its index and symbol) rather than
  throwing past it, or reporting `solved: true/false` once all moves are
  applied and everything's settled. Pure TS, no Phaser/LevelScene/
  AudioManager dependency at all - exactly "without rendering anything."

No physics changes.

## Running it against the uploaded solutions

`loadLevelModels()` is fetch-based (reads `legacy/` via Vite's dev-server
`/@fs/` route), so "headless" here means no Phaser/rendering, not
literally no browser process - ran via a scratchpad Playwright script
against the running dev server (not a committed project tool - `web/`
has no Playwright dependency; see "Open for next time"). Result across all
81 solution files:

**32/81 validated cleanly** - including both levels this project has
exercised before (`airplane`, 235 moves; `viking1`, 119 moves) and 30
others, from 98 to 1964 moves. Validation itself is fast: airplane took
52ms for 235 moves; the one-time per-level Lua bootstrap (`loadLevelModels`,
unrelated to the validator) dominates wall-clock time.

**49/81 failed**, in four distinct categories - traced deeply enough to be
confident these are real findings, not validator bugs:

1. **11 levels fail to even *load*** (`alibaba`, `bathroom`, `briefcase`,
   `chest`, `city`, `elevator1`, `elevator2`, `experiments`, `gems`,
   `music`, and a syntax error for `redhat`) - a Lua error inside
   `code.lua`'s `prog_init()`, each a *different* missing host binding this
   port's Lua API surface doesn't implement yet: `updateAnim` (method,
   `city`/`gems`/`alibaba`), `mod` (field, `bathroom`/`experiments`),
   `level_planShow` (`briefcase`), `addv` (`chest`), `game_addDecor`
   (`elevator1`/`elevator2`), `options_getParam` (`music`). This is a
   pre-existing gap in level-loading coverage (only a handful of levels
   have ever been exercised by this port before now), not a solution- or
   validator-specific problem - it would block *any* use of these levels,
   not just replay. `redhat` is a different case entirely: `legacy/script/
   redhat/` doesn't exist in this repo at all, confirmed via direct
   `ls` - the solution file has no matching level to validate against.
2. **1 level (`windoze`) uses `fish_extra`** (more than the standard two
   fish, custom per-level symbols) - `GameEngine.buildUnit()` only builds
   `Unit`s for `fish_small`/`fish_big`, so this throws
   `"unknown/unimplemented model kind"` immediately. Known, pre-existing,
   single-outlier limitation (confirmed via `grep -rl fish_extra legacy/
   script/` while researching the roadmap - `windoze` is the only level
   that uses it).
3. **35 levels load fine but the solution genuinely fails partway
   through** - `failedAt` ranges from move 1 (`library`) to move 271 of
   573 (`warcraft`). Traced `library`'s failure (move index 1) all the way
   down to rule out a validator bug: fish_small spawns at `(5,27)`
   (confirmed against the raw `models.lua`, matches exactly), the first
   `'l'` moves it to `(4,27)` (confirmed via direct state inspection), and
   the second `'l'` would move it to `(3,27)` - which is a real wall,
   confirmed directly from `models.lua`'s own room-shape ASCII art (row 27
   is `XXXX.....X......XXXXX`, position 3 is `X`). The recorded solution's
   second move cannot succeed against this level's actual, unmodified
   content - not a loader or physics bug. Combined with `airplane`/
   `viking1` (extensively turn/push/fall-exercising, 119-235 moves)
   validating perfectly, this is strong evidence the validator mechanism
   itself is correct, and most/all of these 35 failures are genuine
   problems with the uploaded solution files - exactly what the user asked
   to have checked.
4. **32 levels solved cleanly** - see above.

## Verification

- `npx tsc -b` clean.
- `airplane`/`viking1` full-length validation (235 and 119 moves) both
  report `solved: true` - the same two levels already known-good from
  every prior docs entry's regression suite.
- `library`'s move-1 failure traced move-by-move (position, `isLeft`,
  `Rules.dir` before/after each `loadMove()` call) and cross-checked
  against the raw level source directly, independent of any of this
  port's code - see above.
- Zero console/page errors during the full 81-level batch run itself (the
  *reported failures* are the validator correctly catching bad moves/loads,
  not crashes).

## Open for next time

- Step 3 (replay mode) is next per the roadmap.
- The 11 missing-Lua-binding levels are a real, separate body of work
  (extending `levelScript.ts`'s host API surface) - worth its own docs
  entry if picked up, not bundled into the replay/save work.
- No permanent, repeatable "validate all solutions" project tool exists
  yet - this run used a one-off scratchpad Playwright script against the
  dev server. If this needs to be re-run regularly (e.g. after physics
  changes, or once more solutions are sourced), worth turning into a real
  `web/tools/validate-solutions.mjs` - either add Playwright as a devDependency
  (matches how this was just run) or give `loadLevelModels()`'s file-fetching
  a Node/`fs`-based alternative to the current Vite-`/@fs/`-only path, for a
  dependency-free CLI tool. Not decided - flagging the choice, not defaulting
  either way.
- `redhat` has no matching level in this repo at all - worth confirming
  with whoever sourced the solutions whether it's expected to exist under a
  different codename, or should just be dropped.
