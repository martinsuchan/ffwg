# 030 - Post-solve auto-return, F1 help popup, window title + favicon

2026-07-11

## Context

Three quality-of-life improvements requested after the World Map landed
(docs/027-029), each cross-checked against the original FF NG for
faithful behavior:

1. Solving a level should auto-return to the world map after a beat
   (it just latched a "Solved!" message and sat there before).
2. The full controls list was painted permanently across the top of
   every level - move it into an on-demand F1 popup instead.
3. Show the level's real "section: name" caption in the browser
   tab/title (the original sets the OS window caption), and use the
   game's own fish icon as the favicon.

## Bug/feature 1: auto-return to the world map after solving

The original does this via `LevelCountDown` (`legacy/src/level/
LevelCountDown.cpp`, driven from `Level::own_updateState()`): once
`Room::isSolved()`, it sets a countdown of `getCountForSolved()` game
cycles - **10** normally, **30** if a dialog is still running
(`Level::getCountForSolved()`), `0` when loading, `-1` when undoing -
decrements one per `own_updateState` cycle (~`timeinterval` = 100ms/
cycle), and at zero calls `finishLevel()` -> `saveSolution()` +
(`createNextState()` ? `changeState` : `quitState()`). For a normal
level `createNextState()` is NULL, so it's a plain `quitState()` pop
back to the still-alive `WorldMap` - i.e. exactly this port's
`scene.start("worldmap")`.

Ported into `LevelScene.tick()`'s existing round loop as a round-counted
countdown (`SOLVED_RETURN_ROUNDS = 10` / `SOLVED_RETURN_ROUNDS_DIALOG =
30`, counted in `ROUND_MS` physics rounds - this port's per-cycle proxy,
same substitution docs/011 used for the disintegration timer). The
dialog-still-running case reuses `LevelScript.getActiveSubtitle()` (the
existing docs/015 dialog-active check, no new state) to pick the longer
count. Restructured the win/lose tail so it keeps evaluating after
`gameOver` latches (the old early `if (this.gameOver) return;` was
removed - render/audio already ran above it, matching docs/011's
"round loop keeps running" design) and drives the one-shot countdown to
`scene.start("worldmap")`. `solvedCountdown` resets to `-1` in
`startEngine()` so a restart clears any pending return. Measured ~1.4s
in the no-dialog case (10 x 130ms + overhead), i.e. "a few seconds" as
asked, and identical in effect to pressing Esc.

## Feature 2: F1 controls popup instead of always-on help text

`startEngine()` used to stuff the entire controls string into
`statusText` (top-left, permanently visible). Now `statusText` is kept
**hidden while empty** (`.setVisible(false)` at creation and on reset,
`.setVisible(true)` only when a solved/died/unsupported message is set)
and used only for that result line - an empty Text still renders its
background+padding box otherwise, which showed as a stray top-left
smudge. The controls live in a new modal `web/src/scenes/HelpOverlay.ts`
(same owned-UI shape as `PedometerUI`/`SaveSlotUI` - backdrop + panel +
two aligned monospace columns + OK button, laid out from *measured* text
extents so nothing overlaps at any room size).

- **F1** toggles the popup (`keydown-F1`, added to `addCapture` so the
  browser doesn't open its own help).
- **Esc** closes the popup if it's open, otherwise leaves to the map
  (the existing `keydown-ESC` handler now checks `helpOverlay.isShowing`
  first) - and the **OK** button closes it.
- While the popup is open it's a true modal: movement is gated by
  feeding `engine.tick()` a no-op input (held keys/mouse read as
  unpressed, queued key drained to null) in `tick()`, and the discrete
  gameplay keys (R/Space/P/F2/F3) + left-click-select run through a new
  `whenPlaying()` guard that no-ops while the modal is up. The round
  loop itself keeps running (item anims/audio unaffected).

## Feature 3: window title (section: name) + favicon

The original's caption is `Level::initScreen()`:
`findDesc(codename) + ": " + findLevelName(codename)`, where (confirmed
via `LevelDesc(lang, levelname, desc)` -> `Dialog(lang, "", desc)`)
`findLevelName` = `worldmap_addDesc`'s 3rd `levelname` arg and
`findDesc` = its 4th `desc` (section/house) arg - so e.g.
`"Vrakoviště: Výška: -9000 stop"`. The map's own caption is
`WorldMap.cpp` -> `findDesc("menu")` = `"Fish Fillets - Next
Generation"`.

- `worldMapLoader.ts` now also captures the 4th `desc` arg into a new
  `WorldMapData.sections` map (MAP_LANG only), alongside the existing
  `names`.
- All `document.title` writes live in `WorldMapScene` (the only screen
  holding the names/sections data): `create()` sets the **map title**;
  `launchLevel()`/`launchReplay()` set `titleFor(codename)` =
  `<section>: <name>` right before `scene.start`. Every return path
  (Esc, the new auto-return, replay's `returnTo: "worldmap"`) re-runs
  `create()` and restores the map title, so no title plumbing is needed
  through `LevelScene`/`ReplayScene` at all.
- The map title is this port's own name, a `GAME_TITLE` constant =
  **"Fish Fillets - Web Generation"** (not the original's "Next
  Generation" from the "menu" desc row) - only the whole-game name
  changes; per-level `<section>: <name>` titles still use the real
  worlddesc.lua names. `index.html`'s static `<title>` matches.
- Favicon: the game's own 32x32 `legacy/images/icon.png` (the original's
  `SDL_WM_SetIcon` source, a yellow fish) copied to
  `web/public/favicon.png` and linked from `index.html`.

## Verification

- `npx tsc -b` clean.
- Real-browser (Playwright, temporary `window.__game` hook, removed
  after - `main.ts` is back to no hook): 15-assertion suite covering all
  three - favicon link + map title on boot; `sections`/`names` captured;
  level title becomes "Vrakoviště: Výška: -9000 stop" on launch;
  `statusText` hidden during play; F1 opens, Esc closes it *without*
  leaving the level, fish doesn't move while the modal is open, F1
  toggles it closed, Esc then returns to the map with the map title
  restored; and a full-solution solve latches the "Solved in 235 moves -
  new best!" line then auto-returns to the map in ~1.4s with the node now
  rendering solved.
- Screenshot of the open popup confirms clean two-column layout with the
  OK button clear of the last row.
- docs/026 save/load regression suite re-run green (the F2/F3 keys now
  route through `whenPlaying`, unaffected when the modal is closed).

### Follow-up fixes (same day)

User smoke-tested and reported two visual leftovers plus a naming ask,
all folded into this entry rather than a new doc:

- The bottom-right **"F1: Help" hint was removed** entirely - the F1
  popup is enough, the extra on-screen label was clutter.
- The stray **top-left box** was the empty `statusText` still rendering
  its background+padding - fixed by keeping it `setVisible(false)` while
  empty (see "Feature 2" above), verified via a clean-level screenshot
  and `statusText.visible === false` during play.
- The port's displayed name is now **"Fish Fillets - Web Generation"**
  (`GAME_TITLE` + `index.html` `<title>`), per the user's request.

## Open for next time

- The dialog-extended return (30 rounds) is exercised only by the logic
  path, not a scripted end-to-end test (hard to deterministically solve
  a level *while* a dialog is mid-playback); the shared countdown code
  is the same either way.
- The auto-return currently always goes to the map; the original's
  final-level-of-a-world case (`createNextState()` -> a poster/recap
  screen) is still out of scope, consistent with docs/027's deferral of
  posters.
