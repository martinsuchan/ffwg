# 026 - Solution & Save Persistence

2026-07-10

## Context

Steps 4 and 5 of the solution/save/replay roadmap (move recording →
headless validator → replay mode → **solved-solution persistence** →
**mid-level save/load**), combined into one pass at the user's request:
"When user opens the web page, it should be possible for him to continue
where he left." Revised twice during planning:

1. **Storage choice**: `localStorage` - confirmed directly with the user.
   Synchronous string key/value store, ~5-10MB per origin (moves are
   bytes to low hundreds of bytes each), no setup. `IndexedDB` was named
   as the 2026 alternative but isn't needed here - nothing stored is
   large or structured enough to justify it.
2. **Multi-slot save, not one**: the user asked for save/load modeled on
   *Fish Fillets 2*'s mission-screen UI (manual supplied as `ff2/Fish
   Fillets 2 Manual.pdf`) - a row of small dots in the bottom-left corner,
   one per save. Per the user's own simplification of that screen's
   slightly fussier select-then-load flow: **left-click a dot to load,
   right-click to delete**. Original FF NG only ever had one save per
   level (`saves/<codename>.lua`) - multi-slot is a deliberate upgrade
   beyond it, not a port of anything that existed.
3. **What actually needs saving**: the user asked directly whether the
   original saves full level/object state, not just moves, since "in many
   levels some NPCs or textures change depending on player moves." Yes -
   confirmed by reading `Level::saveGame()`/`action_load()`
   (`legacy/src/level/Level.cpp`), `LevelLoading::nextLoadAction()`, and
   `legacy/script/share/Pickle.lua`/`prog_save.lua`. This materially
   changed the plan: a pure move-string replay (this plan's first draft)
   would silently drop any level-specific Lua state a level's own
   `code.lua` had accumulated.

## How the original actually saves/loads

Real key bindings (`legacy/src/level/LevelInput.cpp`): **F2 = save, F3 =
load** (`KEY_SAVE`/`KEY_LOAD`). Unused anywhere in this port - reused
verbatim. `action_save()` only saves `if (room->isSolvable())`.

It's a **hybrid**, not one format. `Level::saveGame()` writes two Lua
globals: `saved_moves` (text) and `saved_models` (a Lua table *literal*,
not a quoted string - the pickled output of `getModelsTable()`).
`getModelsTable()`'s model objects (`level_creation.lua`'s
`createObject()`) only have `index`/`talk_phase` by default - **position
is never cached on the model table**, always fetched live via `getLoc()`.
So `saved_models` carries only whatever extra plain-data fields a level's
own `code.lua` stashed onto a model - dialogue-progress counters,
"have I shown this yet" flags, decoration state it tracks itself.

Load is genuinely **two independent phases**. `LevelLoading::
nextLoadAction()` replays `saved_moves` through pure C++ physics only
(`room()->loadMove(symbol)`, 5-50 moves/cycle) - no Lua/`script_update()`
at all during catch-up, so no historical dialog/sound/anim re-fires. Only
once that's fully drained does `Level::nextLoadAction()` call
`script_loadState()` (`prog_save.lua`) - *one* one-shot call unpickling
`saved_models` and merging its fields onto the freshly-rebuilt
`getModelsTable()` (`assignModelAttributes()`). This mapped cleanly onto
this port's existing physics/Lua-decoration split (`GameEngine`/`Room`
vs. `LevelScript`, docs/007/014) - physics reuses the existing
`loadMove()`/`settleAll()` (docs/022) unchanged; Lua state needed a new
one-shot capture/restore pair.

`prog_save.lua` also defines undo/redo, confirmed safe to load anyway -
those functions are only ever called by a separate C++ path
(`Level::saveUndo()`) this port has no equivalent of and never triggers.

## Sync capture/restore, not async - avoiding a reentrancy risk

`script_loadState()` expects `saved_models` to already be a *table*
(the original gets this for free - `scriptInclude(file)` executes the
save file as Lua source, evaluating the `{...}` literal). This port
stores the pickled *string*, so materializing it needs
`loadstring("return "..s)()` first. Doing that via async `lua.doString()`
calls at save/load time could race a same-tick `LevelScript.tick()` call
into the same live wasmoon engine - the exact class of corruption
docs/008 already hit once. Instead, two tiny glue functions are defined
once during `createLevelScript()`'s existing bootstrap (own literal, not
fetched):

```lua
function ffwg_captureModelState()
    return pickle(getModelsTable())
end
function ffwg_restoreModelState(serialized)
    saved_models = loadstring("return " .. serialized)()
    script_loadState()
end
```

...then fetched as plain function references (`lua.global.get(name)`),
exactly like `scriptUpdate` already is - a proven-synchronous call
pattern (docs/014). `LevelScript.captureModelState()`/`restoreModelState()`
are plain sync methods - no `await`, no timing window, no new guard.

## What was built

- **`web/src/storage/levelStorage.ts`** (new): `localStorage` wrapper, all
  reads/writes try/catch-guarded. `SavedGame { id, moves, modelState }`,
  `loadSavedGames`/`addSavedGame` (capped at `MAX_SAVES = 6`, returns
  `null` when full)/`deleteSavedGame`, plus `loadSolvedMoves`/
  `saveSolvedMoves` (the "keep only if shorter" rule, ported from
  `LevelStatus::writeSolvedMoves()`).
- **`web/src/lua/levelScript.ts`**: `createLevelScript()`'s bootstrap gains
  two more fetches (`Pickle.lua`, `prog_save.lua`, loaded right after the
  existing `compatibleSource` so `getRestartCount()` already exists for
  `prog_save.lua`'s one top-level side effect) plus the glue snippet
  above. `LevelScript` gained `captureModelState()`/`restoreModelState()`.
- **`web/src/scenes/SaveSlotUI.ts`** (new): a row of small
  `Phaser.GameObjects.Arc` circles at the bottom-left - blue-filled per
  existing save, one dim "add new" circle while under `MAX_SAVES`.
  `refresh(saves)` rebuilds the row (cheap, ≤7 objects). Left-click a
  filled dot → load; right-click → delete; click the dim dot → save a new
  slot.
- **`web/src/scenes/LevelScene.ts`**:
  - `startEngine(resumeMoves?, resumeModelState?)` - replays `resumeMoves`
    via the existing `loadMove()`/`settleAll()` (try/catch, falls back to
    a fresh engine on failure - e.g. saved data no longer valid because
    level content changed since it was saved); applies
    `resumeModelState` via `script.restoreModelState()` inside the
    existing `createLevelScript(...).then(...)` callback (try/catch,
    warn-only - a corrupted decorative overlay doesn't invalidate the
    physics restore that already succeeded independently). `restart()`
    (R) still calls it with no args, matching the original never
    touching save data on restart.
  - `saveGame()` (F2 / dim dot): guarded by `engine.isSolvable()`
    (matches `action_save()`) and the level script being ready (matches
    `action_save()`'s `isRoom()` guard) - captures live Lua state, adds a
    slot, refreshes the dot row.
  - `loadGame(id)` (dot click): resumes from that slot.
  - `deleteGame(id)` (right-click): removes the slot.
  - `loadLatestGame()` (F3): loads the most-recently-created slot - a
    "quick load" stand-in for the original's "load whatever's currently
    selected," since this port has no selection concept.
  - `tick()`'s `isSolved()` branch now also calls `saveSolvedMoves()`
    ("new best!" wording when it actually replaced a prior one) - doesn't
    touch mid-level saves, matching the original (independent files
    there too).
  - `launchReplay()` (P) now prefers `loadSolvedMoves()` over fetching
    `legacy/solution/<level>.lua`, falling back to the reference solution
    only if the player hasn't solved it here yet.
  - New top-right `feedbackText` + `showFeedback()` (cancel-and-reschedule
    ~1.5s auto-hide) - mirrors the original's `displaySaveStatus()`
    transient flash.
  - Keyboard capture extended to include F2/F3 (avoids Firefox's F3
    quick-find popping up mid-game).

No changes to `Room`/`GameEngine`/`Controls`/physics.

## Verification

- `npx tsc -b` clean throughout.
- Direct engine-driven checks (Playwright + a temporary `window.__game`
  hook, removed after):
  - Full save→reload→click-to-load cycle on `airplane`: saved moves
    match engine state exactly, survive a real page reload (proving
    `localStorage`, not in-memory scene state), a fresh reload with no
    save action starts the level over from scratch (no auto-resume, per
    the user's explicit requirement), loading snaps to the saved
    position with a matching step count.
  - Filled all 6 slots, confirmed the 7th `addSavedGame` is rejected with
    the "all slots full" feedback and the dot row's dim "add" circle
    disappears; deleting one slot brings it back.
  - Corrupted a save's `modelState` string directly in `localStorage`,
    loaded it: physics position still restored correctly (independent
    try/catch), console warning fired, no crash.
  - **`viking1`** (has real accumulating state -
    `melodak1.afaze`/`.hrat`/`.mrk`/`.hlasky`/`.posl`, `room.dohrat`/
    `.blok`/`.startblok` - the musician-band gag, docs/018): captured
    state included these custom fields (not just the trivial
    `index`/`talk_phase` baseline every model starts with), changed
    across a wait window (proving it's live, not frozen), round-tripped
    byte-for-byte through `captureModelState → restoreModelState →
    captureModelState` when checked atomically (no interleaved round
    tick). A full F2-save → wait → F3-load cycle restored `room.dohrat`
    (a monotonic countdown) to within a few counts of its saved value,
    not a fresh `randint(300,700)` reroll - proof the real F2→load path
    genuinely restores level-specific decorative state end-to-end, not
    just physics.
  - Solved `cannons` (39-move validated solution, applied via the
    existing headless `loadMove()`/`settleAll()` engine path so the test
    exercises `tick()`'s real `isSolved()`-detection/save code, not
    keyboard-timing race conditions unrelated to this feature):
    `ffwg:solved:cannons` was written with the winning move string; a
    seeded shorter fake entry survived untouched (the "keep only if
    shorter" rule); pressing P launched the replay scene without ever
    fetching `legacy/solution/cannons.lua` (confirmed via network request
    monitoring), proving it used the stored solved solution instead.
  - Regression: normal play (movement, restart, Space-switch) on
    `airplane` after every change, zero console/page errors, save dots
    render correctly (screenshot-verified) alongside the existing status
    text.

## Open for next time

- No world map / level-select UI yet to show "which levels are solved" or
  auto-resume "last played level" - solved/save data is ready for one to
  consume whenever it's built.
- `MAX_SAVES = 6` and the dot layout/spacing are first guesses, not
  measured against any real level's screen width.
- The bottom-left save dots and the bottom-center subtitle text
  (docs/015) can visually overlap for a long subtitle line on a narrow
  room - cosmetic only.
- Loading doesn't preserve the original's "doesn't count as a restart"
  nuance for `level_getRestartCounter()` (dialog-probability flavor only,
  no functional effect) - `scriptGeneration` bumps on load same as on
  restart.
- Undo/redo (`-`/`+` in the original) still out of scope, not asked for.
