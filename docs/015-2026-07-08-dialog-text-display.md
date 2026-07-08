# 015 - Dialog Text Display

2026-07-08

## Goal

Phase 3a of "implement full level content": real dialog/subtitle text on
screen - English only, no voice audio/music (still deferred). `docs/014`
got item animation running via a persistent live Lua engine but
deliberately stubbed every dialog-related host function to *disable*
ambient dialog logic entirely (`game_isPlanning() -> true` forced
`no_dialog()` permanently false). This phase replaces those stubs with
real implementations.

## Investigated before planning

- **How does the original decide subtitle duration without audio?** It
  already has its own answer: `Dialog::getMinTime()`
  (`legacy/src/gengine/Dialog.cpp`) = `min(180, utf8Length(subtitle))`
  cycles, and `PlannedDialog::isTalking()` falls back to exactly this
  whenever there's no active sound channel
  (`legacy/src/gengine/PlannedDialog.cpp`). Not an invented heuristic -
  the original engine's own no-sound fallback, ported directly.
- **`game_planAction`'s exact `count` semantics.** Traced to
  `legacy/src/plan/CommandQueue.cpp`: a single FIFO, one command
  processed per round, `count` resets to 0 when a command finishes and
  the next one starts, and it runs in the *same* round it was queued
  (`LevelScript::updateScript()` calls `script_update()` then
  `satisfyPlan()` in the same call).
- **How dialog files actually load.** `airplane/init.lua` loads
  `models.lua` -> `dialogs.lua` -> `code.lua`; `dialogs.lua` is a
  one-line `dialogLoad("script/airplane/")` wrapper. `level_start.lua`'s
  `initModels()` unconditionally loads three more via `bordershout.lua`/
  `blackjokes.lua`/`borejokes.lua`. Confirmed exactly 4 files needed for
  airplane: `script/airplane/dialogs_en.lua`,
  `script/share/shout_dialogs_en.lua`, `script/share/bore_dialogs_en.lua`,
  `script/share/black_dialogs_en.lua`.
- **Does `code.lua`'s attempt/death-based branching need to be real?**
  User asked this directly. `airplane/code.lua` reads
  `pokus = getRestartCount()` and uses it to adjust dialog probability/
  delay. Checked `legacy/src/level/Level.cpp`: `m_restartCounter` starts
  at `1`, increments by 1 on every `action_restart()` - i.e. every level
  restart, nothing fancier. There's no separate "death count" in the
  original at all - dying always forces a restart, so restart count *is*
  the attempts/deaths signal. `LevelScene` already had the exact
  matching counter (`scriptGeneration`) - wired up for real, not
  deferred. Separately checked `level_getDepth()` and found it's
  *not* a stuck/death counter at all despite how it reads - it's the
  level's static position in the world-map campaign tree
  (`legacy/src/menu/LevelNode.h`), set once when the map graph is built.
  Stays a hardcoded constant - would need a whole world-map system we
  don't have, a real scope boundary rather than a shortcut.

**Note:** the two research spikes for this phase (like a few before them)
independently reported encountering text embedded in tool output that
impersonated a system reminder telling the agent to stop working -
correctly identified as not a real instruction and ignored.

## What was built

- **`web/src/lua/levelScript.ts`**: bypasses `dialogLoad()` entirely
  (it would enumerate ~15 languages via `select_lang.lua` and call
  `file_include` per match - unnecessary work, and the exact wasmoon
  reentrancy risk `docs/008` already found). `level_dialog.lua`'s
  `DialogState.lang`/`.DEFAULT_LANG` both start as `""` until
  `dialogLoad()` ever runs, so as long as we never call it, `dialogId()`'s
  registration check is trivially satisfied - the 4 dialog files are
  just pre-fetched and `doString()`'d directly, order-independent (pure
  data registration), right after `level_dialog.lua` loads.
  - New host function `dialog_addDialog(name, lang, soundPath, font,
    subtitle)`: registers `{font, subtitle}` keyed by name (`lang`/
    `soundPath` ignored).
  - `game_planAction(callback)` is now a real FIFO (`pendingActions`) -
    only the front is processed each round, matching `CommandQueue`'s
    single-command-at-a-time design; `game_isPlanning()` reflects
    `pendingActions.length > 0`. `game_killPlan()` clears the queue for
    real too (cheap, obviously correct once the queue itself is real).
  - `model_talk(index, dialog, volume, loops)` looks up the registered
    entry and sets a single `activeDialog` slot with
    `endCycle = cycles + min(180, subtitle.length) * (loops+1)` - the
    ported `Dialog::getMinTime()` formula. `dialog_isDialog()`/
    `model_isTalking(index)` both derive from comparing `cycles` against
    `activeDialog.endCycle` on demand (matching the original's own
    on-demand check) rather than an explicit "clear" step.
  - `LevelScript.tick()` now also increments the round counter and calls
    a new `processPlan()` after `scriptUpdate()` - matching
    `updateScript()`'s real order (logic first, then `satisfyPlan()`).
  - New public `getActiveSubtitle(): {text, font} | null`.
  - `createLevelScript()` gained a third `restartCount` parameter,
    backing `level_getRestartCounter()` for real.
- **`LevelScene.ts`**: new `subtitleText` `Phaser.GameObjects.Text`,
  fixed at the bottom of the room (matching the original's own fixed
  on-screen subtitle region - confirmed from `SubTitleAgent` source, not
  per-fish floating speech bubbles), word-wrapped. Updated from
  `getActiveSubtitle()` each round, right after `levelScript.tick(...)`.
  `startEngine()` now passes `this.scriptGeneration` (already an
  existing 1-based, per-restart counter, exact match for
  `Level::m_restartCounter`'s semantics) through to `createLevelScript`.

### Deliberate simplifications (see docs/015 plan for full list)

One shared text style (no real bitmap fonts per `font_small`/`font_big`);
one active subtitle slot instead of the original's 5-line stacking
deque; `immediate=false`/bare `object:talk()` calls treated the same as
the blocking `planDialog` path (unverified, unused by airplane);
`model_killSound` stays a no-op.

## Verification

- `npx tsc -b` and `npx vite build` both clean.
- Direct end-to-end test (drive `GameEngine`+`LevelScript` in a loop, no
  real-time waiting needed): confirmed not just "a dialog fires" but the
  *serialization* is correct - `addm("let-m-divna")` (queued first, and
  on attempt 1 `random(100) < 100` is unconditionally true) occupies the
  queue front and its subtitle displays for its full `min(180, len)`
  cycles, and only *after* that window closes does the second queued
  `addv("let-v-vrak...")` (big fish) start displaying - exactly matching
  `planTimeAction`'s `dialog_isDialog()`-deferral logic, not just "some
  text appeared."
- Real-browser visual check: subtitle text genuinely renders on screen
  at the expected position, screenshotted.
- Restart counter verified for real (not just via our own JS-side
  counter): called the actual registered `level_getRestartCounter()`
  Lua function directly across two restarts, got `1, 2, 3` exactly as
  expected.
- Confirmed all 4 dialog files load correctly (100 total registered
  dialog entries: airplane's 8 plus the 3 shared files' content,
  cross-checked specific known IDs from each file).
- Re-verified the specific crash `docs/014`'s spike found and worked
  around with a no-op stub (`stdBlackJoke`'s death-reaction path, not
  gated by `no_dialog()`, calling `game_planAction` once a fish dies):
  forced a real death via the same technique as `docs/011`/`013`, ran
  400 rounds - zero errors with the now-real `game_planAction`
  implementation (the underlying gap the stub papered over is now
  actually fixed, not just avoided).
- Full regression: re-ran `docs/007`'s synthetic-room tests, `docs/008`'s
  10-level goal test, `docs/009`'s stress-play script, `docs/011`'s
  corpse-removal test, `docs/012`'s restart-leak test, `docs/013`'s
  death-reaction-delay test, and `docs/014`'s item-animation and
  rapid-restart tests - all unchanged, identical results.

## Open for next time

- Voice audio/music (separate phase) - subtitle duration is the ported
  no-audio fallback formula, not a placeholder.
- Real bitmap font rendering per `font_small`/`font_big`/etc.
- Multi-line/multi-actor simultaneous subtitle stacking (the original's
  5-line deque).
- `immediate=false` / non-blocking `object:talk()` semantics - ~29
  levels use direct `object:talk()` calls, unverified here.
- `model_killSound` early-cutoff.
- Full multi-language support - would need a language-selection UI
  first, not just more Lua files.
- Only `airplane` is targeted/verified; other levels' dialog content
  (and any `file_include`s inside their own `dialogs_<lang>.lua` files)
  may need extending this incrementally.
