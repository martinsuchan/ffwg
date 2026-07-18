# 072 - Backup / restore game progress (JSON file)

_2026-07-17_

Game progress lived only in `localStorage`, so clearing browser data wiped it
with no recovery. Added **Backup game progress** / **Restore game progress**
buttons to the Settings panel so a player can export everything to a file and
import it back (or onto another device). User-chosen shape: **JSON**, back up
**everything**, restore **merges** (keep-best, never destructive).

## The whole backup surface

Four `ffwg:*` localStorage areas (no other game state exists):

- `ffwg:solved:<codename>` - best (shortest) solved move string per level (the
  progression backbone; world-map node states derive from it), incl. `ending`.
- `ffwg:saves:<codename>` - JSON array of `SavedGame {id,moves,modelState,tutorial?}`.
- `ffwg:settings` - lang/volumes/subtitles/showSteps/gameSize.
- `ffwg:playtime` - cumulative seconds.

## File format (JSON, pretty-printed, versioned)

```json
{ "format": "ffwg-progress", "version": 1, "exportedAt": "…Z",
  "solved": { "airplane": "…" }, "saves": { "briefcase": [ … ] },
  "settings": { … }, "playtimeSeconds": 12345 }
```

JSON over Lua deliberately: the data is already JSON-shaped and human-readable,
and it needs **no interpreter** to load - no code-execution surface on an
importable, user-editable file, which is the whole point of the hardening.

## New module `web/src/storage/progressBackup.ts` (no Phaser deps)

- `serializeProgress()` - collect all four areas -> `JSON.stringify(…, null, 2)`.
- `parseBackup(text)` - **pure/sync, no level loading.** Size cap
  (`MAX_BACKUP_BYTES = 5 MB`) -> `JSON.parse` guard -> `format`/`version` gate ->
  per-field coercion that **drops** anything malformed (move strings checked
  against the legal symbol alphabet incl. windoze's w/x/y/z; settings via the
  shared `sanitizeSettings`). Returns `{ok,value}` or `{ok:false,error}`.
- `restoreProgress(value)` - **async, hardened, merge-only.** Loads each
  referenced level once (`loadLevelModels`, try/catch -> unknown/failing level =
  rejected per-entry, never aborts the whole restore). **Every solved solution is
  re-validated by actually replaying it to a solved state** (`validateSolution` +
  a fresh `GameEngine`) before it's trusted - a tampered/incompatible solution is
  rejected. Saves must replay with no invalid move (a valid partial, same bar as
  save-resume, docs/026). Writes are merge/keep-best: `saveSolvedMoves`
  (keep-shorter), `mergeSavedGames` (union by id, cap `MAX_SAVES`, one tutorial
  slot), `saveSettings` (replace), `mergePlaytimeSeconds` (max). Returns a
  `RestoreReport` (accepted/rejected counts + reasons).

## Reused / extended existing storage

- `levelStorage.ts`: `allSolved()`/`allSaves()` (scan the two prefixes) for
  collection, `mergeSavedGames()` for restore; `saveSolvedMoves` (keep-shorter)
  unchanged.
- `settingsStorage.ts`: extracted `sanitizeSettings(raw)` (the field-by-field
  coercion already inside `loadSettings`), now shared by `loadSettings` and
  `parseBackup` - untrusted file input gets the exact same hardening as stored
  data, for free.
- `playtime.ts`: `mergePlaytimeSeconds(seconds)` = write `max(current, file)`.

## UI (`OptionsOverlay.ts`, shared by world map + level)

New "Game progress" row with **Backup** / **Restore** buttons; `PANEL_H` bumped
364 -> 430 (the docs/069 fit-scale container handles narrow rooms - verified
`library` scales to 0.7275, bounds 12..303 / 121..434 within [0,315]x[0,555]).
Backup builds a `Blob` -> temporary `<a download="ffwg-progress-YYYY-MM-DD.json">`.
Restore uses a reused hidden `<input type="file">` (removed in `hide()`, so a
cancelled dialog can't leak elements): size check -> `parseBackup` -> a status
line shows "Validating n/N…" -> the summary -> `location.reload()` (so the world
map re-derives node states and playtime/settings re-read; simplest guaranteed
refresh). Parse/size errors show an inline red message and do nothing.

## Verification

- **New e2e case** `web/tests/cases/08-progress-backup.mjs` via a dev-only
  `window.__progress` handle (`main.ts`, gated on `import.meta.env.DEV` like
  `__game`): serialize round-trips through `parseBackup`; restoring a file mixing
  a **valid** `airplane` solution, a **wrong** solution under `viking1`, and an
  **unknown** level accepts only `airplane` (persisted) and rejects the other two
  (not persisted); a valid save is restored; settings applied; non-JSON /
  missing-`format` / bad-`version` / oversized all rejected by `parseBackup`.
  (16 assertions.)
- **Real browser** (Playwright): F10 opens Options on the world map, both buttons
  render, clicking **Backup** downloads `ffwg-progress-2026-07-17.json` whose JSON
  is a valid backup containing the seeded progress; panel fits the narrow
  `library` room (screenshot).
- `tsc -b` clean; full e2e suite green.

## Open for next time

- Restore is targeted at cs/nl settings + the current move alphabet; if country
  speech variants or new symbols are ever added, extend `sanitizeSettings` /
  `MOVE_RE` accordingly.
- No explicit "are you sure?" before a restore reload - it's non-destructive
  (merge), so a mistaken import can't lose data, but a confirmation could still be
  friendlier.
