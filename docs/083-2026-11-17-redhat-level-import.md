# 083 - The redhat level: imported from upstream data, secret branch off emulator

2026-11-17

## What was done

`redhat` was the one world-map level this repo couldn't play: `legacy/solution/redhat.lua`
existed, but no `legacy/script/redhat/` (docs/022/023; BACKLOG §C8). Its content came in
the upstream data archive `fillets-ng-redhat.zip` (repo root), which holds:

- `script/redhat/` - `init.lua`, `models.lua` (33x31 room, 12 items, both fish),
  `code.lua` (intro/idle dialogs, music `rybky02.ogg`), `dialogs.lua`, `dialogs_cs.lua`,
  `dialogs_en.lua`;
- `images/redhat/` - the 14 pictures `models.lua` references (all present);
- newer `script/worlddesc.lua` (= ours + the two `redhat` desc lines),
  `script/worldfame.lua` (adds `redhat` 297 moves, and 10 better records elsewhere),
  `script/worldmap.lua` (= ours + `redhat` hung off `keys` at 580,330).

No `sound/redhat/` - the level has no voice-over (text-only, like a speechless dialog: the
subtitle time comes from Dialog::getMinTime()'s text-length rule).

Imported into `legacy/` at the user's explicit request (the only sanctioned edit there so
far - noted in `legacy/CLAUDE.md`):

- `script/redhat/*` and `images/redhat/*` verbatim, except one Czech typo fixed in
  `dialogs_cs.lua` ("kalceláři" -> "kanceláři").
- `worlddesc.lua` and `worldfame.lua` replaced by the archive's versions (user's choice:
  whole newer hall of fame, not just the redhat line; `worldfame.lua` was then replaced
  again by the current upstream file - see the end of this entry). The section stays the archive's
  "Branch of the New Generation" / "Větev Nové Generace", so the window title reads
  "Větev Nové Generace: Red Hat".
- `worldmap.lua`: **not** the archive's placement. Per the user, `redhat` is a branch off
  `emulator` heading up-right ("1 o'clock"): `branch_addNode("emulator", "redhat",
  "script/redhat/init.lua", 245, 283, true)` - ~39 px from emulator (226,317), clear of
  warcraft/broom. `hidden = true` like every other side-branch entry, so in the standard
  game it stays invisible until emulator is solved, then opens (docs/027/074). Depth 12.
  The ending's "all solved" condition now includes it (81 nodes).

Regenerated: `scripts\convert-assets.ps1 -Level redhat` (atlas: 14 sprites, 587x932) and
`build-image-manifest.ps1`; the audio manifest is unchanged (no sound). For the solver:
`scripts\export-levels.ps1` into a scratch dir, then only `solver/levels/redhat.json` and the
one-line `index.json` addition copied in (every other export was byte-identical to the
checked-in files).

Web change: a level may lack its `dialogs_<lang>.lua` (redhat has no nl). The original's
dialogLoad() just loads whichever language files exist; `createLevelScript` now treats the
level's localized file and the `dialogs_en.lua` fallback as optional (`optionalLegacyFile`),
so in nl redhat shows the en subtitles instead of failing to load.

Mentions updated: README (81 levels), BACKLOG (§1 table, §C8 rewritten as an open item,
resolved entry), `web/CLAUDE.md` + `web/tests/README.md` + e2e 06's comment (only
`cabin1-simple` is still "no-level"), `legacy/CLAUDE.md`, solver README/CLAUDE.md counts,
`ReferenceSolutionTests` (80 -> 81, comment), level-count comments in `levelLoader.ts`
/`WavyBackground.ts`. Older dev-log entries are left as written (history).

## Verification

- The bundled solution (287 moves, Mattia Onofri's record per `solver/docs/worldfame.lua`)
  replays to Solved: e2e 06 "81 solved, 1 skipped (no-level): cabin1-simple"; a watchable
  ReplayScene run in the browser reached Solved; the solver's independent port
  (`ffsolve verify redhat`) agrees.
- `scripts\test.ps1` 10/10 (01: 81 nodes; 05: 81 levels + 58 host fns bound; 07: standard
  open=1 gated=80, sandbox open=81).
- Browser probe: standard mode hides the node until emulator is solved, then it's open at
  245,283; the level renders (screenshot checked) and plays the cs intro ("Kde to jsme?",
  the fixed "kanceláři" line) with no console errors; in nl it falls back to en subtitles.
- Solver: suite green without KnownBug (1046 passed). `Physics_RandomWalkDigestsUnchanged`
  only lacked the new level - `UPDATE_BASELINE=1` added exactly one row
  (`corpus:redhat;6000;7ee0f7bb44e87edb`) to `physics-digest.csv`.

## Follow-up: `cabin1-simple` solution removed

`legacy/solution/cabin1-simple.lua` (added in `ceaf4e4` by the solver editor work, a
leftover with no level content - docs/078/082, BACKLOG §C10) is deleted at the user's
request. Nothing depended on it: the solver enumerates `solver/levels/index.json`, which
never listed `cabin1-simple`, so its corpus/reference tests are unaffected. The solver's own
simplified room `solver/levels/cabin1-simple.json` and its historical `results.csv` rows stay
(solver data, like the other `*-simple` rooms). e2e 06 keeps its generic "no-level" skip, but
nothing is skipped any more: 81 solutions, 81 solved.

## Open for next time

- No voice-over and no nl/other translations for redhat (upstream data has none).
- `fillets-ng-redhat.zip` is still in the repo root (untracked) - delete or keep as
  provenance.
- Hall of fame: superseded the same day - both `legacy/script/worldfame.lua` and
  `solver/docs/worldfame.lua` are now the current upstream file
  (SourceForge `code-fillets_data`, `script/worldfame.lua`, master; incl. duckie 96 moves by
  Martin Suchan = `solver/solutions/duckie.lua`, verified, and redhat 287). Re-fetch from
  there when records change; SourceForge answers plain curl with 403.
