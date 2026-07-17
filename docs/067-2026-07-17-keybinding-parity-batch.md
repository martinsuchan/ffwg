# 067 - Keybinding parity batch (Backspace restart, F5/F6/F10, Space demo-skip)

_2026-07-17_

From a keybinding audit comparing the port to legacy FF NG's input layer
(`legacy/src/level/LevelInput.cpp`, `plan/StateInput.cpp`, `menu/WorldInput.cpp`,
`state/DemoInput.cpp`). Implemented five differences the user picked.

## Changes

- **Level restart -> `Backspace`** (legacy `KEY_RESTART` = `SDLK_BACKSPACE`),
  replacing the port's `R`. Backspace is safely capturable: modern browsers
  dropped Backspace-as-history-back years ago, and Phaser `addCapture` calls
  `preventDefault` on it anyway (verified: no navigation, no reload).
- **`F5` toggles the step counter** (legacy `show_steps`) - new
  `settings.showSteps` (persisted, default on); `LevelScene` reads it to set the
  counter's visibility and flips it on F5.
- **`F6` toggles subtitles** (legacy `KEY_SUBTITLES`) - flips the existing
  `settings.subtitles` and clears the visible `SubtitleStack` when turning off;
  the per-round drain already reads the setting.
- **`F10` opens the settings panel in-level** (legacy `KEY_MENU` / game menu).
  Before this, `OptionsOverlay` was created **only** in `WorldMapScene` - not
  reachable from a level. `LevelScene` now owns its own `OptionsOverlay`
  instance, wired to *this* level: `onVolumeChange` -> `audioManager
  .refreshMusicVolume()`, `onGameSizeChange` -> `applyRenderScale(this, roomW,
  roomH)` (so volume + game size apply live in-level; verified camZoom 1.5->2
  live). It's a true modal like the F1 help - movement gated (`blockInput`),
  discrete keys gated (`whenPlaying`), F1/pointer gated, Esc closes it (its own
  window listener) instead of leaving the level. `hide()` is called on scene
  SHUTDOWN so its Esc listener never leaks. Note: changing **language** in-level
  only fully applies on the next level load (dialogs are pre-loaded per language).
- **`Space` skips the demo/movie** (legacy `DemoInput` registers `SDLK_SPACE` as
  quit), alongside the existing `Esc`, in `DemoScene`.

Browser-default suppression: `LevelScene`'s `addCapture` list gained
`BACKSPACE,F5,F6,F10` (F5=reload, F6=address bar, F10=menu bar) - all confirmed
prevented (page not reloaded). `DemoScene` captures `SPACE` too.

The F1 help text (`HelpOverlay`) was updated: restart shows `Backspace`, plus new
rows for `F5 / F6` (step counter / subtitles), `F10` (settings), `F11` (fullscreen).

## Deliberately not changed

- Undo/redo (`-`/`+`), debug console (`` ` ``): no such feature in the port.
- World-map `Tab`/`Enter` keyboard navigation: still mouse-only (not in this batch).
- `ReplayScene`'s `R` still restarts the *replay* playback (a port-only feature,
  distinct from level restart) - left as-is.

## Verification (real browser)

- F5 toggles the counter + persists `showSteps`; F6 toggles + persists
  `subtitles`; F10 opens the panel (Game-size row present, screenshot) and Esc
  closes it while staying in the level; live game-size change in-level (backing
  1012x607 -> 1350x810). No page reload from any function key.
- Restart: `Backspace` calls `startEngine` (restart), `R` no longer does; neither
  navigates away.
- e2e 7/7; tsc clean.
