# 050 - Final-level posters + the ending level

_2026-07-15_

## Why

Two original-game features BACKLOG #11 had deferred ("parsed but never
rendered"): (1) finishing a world-final level shows a fullscreen recap **poster**
cutscene; (2) once every level is solved, a special **ending** level ("both fish
at home") auto-runs. User asked to implement both.

## How the original does it (traced)

- **Poster** = a `DemoMode` movie (the same mechanism as briefcase's tutorial
  movie, docs/031). Each world-final level declares one as the 7th arg of its
  `branch_addNode(...)` in `worldmap.lua` (`script/<level>/demo_poster.lua`) - 9
  levels: linux, gods, map, atlantis, propulsion, turtle, grail, barrel, floppy.
  A `demo_poster.lua` is tiny: `demo_display("images/<level>/poster.png",0,0)` +
  a couple of narrated `planTalk(...)` lines (`prog_demo.lua`). Flow:
  `Level::finishLevel()` -> `LevelCountDown::createNextState()` ->
  `LevelStatus::createPoster()` -> `new DemoMode(demo_poster.lua)`, then back to
  the map. **Also shown after a replay**: the Pedometer's Replay runs
  `loadReplay()`, and `own_updateState()` runs the solved-countdown every round
  regardless of replay mode, so a replay that drives the room to solved hits the
  same `finishLevel -> createPoster` path.
- **Ending** = `branch_setEnding("ending", "script/ending/init.lua",
  "script/ending/demo_poster.lua")` - a normal playable 2-fish level with **no
  map position** (never a clickable dot). `WorldMap::checkEnding()` auto-runs it
  when you return to the map after solving the final leaf that completes the game
  (`wasRunning && isComplete && areAllSolved`). It has its own final poster.

## Port implementation

- **worldMapLoader.ts**: capture the per-node `poster` (7th arg) into
  `WorldMapData.posters` and the ending node (`branch_setEnding`) into
  `WorldMapData.ending` (both previously dropped).
- **demoScript.ts**: `createDemoScript(demoFile, levelName, opts)` - `opts`
  distinguishes the briefcase movie (`brief_dialogs_`, no prog_demo) from a
  poster (`demo_dialogs_`, `includeProgDemo: true` since a `demo_poster.lua`
  `file_include`s `prog_demo.lua`, which defines planTalk/planStop over the
  already-bound game_planAction/model_talk).
- **DemoScene.ts**: generalized to `mode: "movie" | "poster"`. Poster mode sizes
  the canvas 640x480, loads the single poster frame **from the level atlas**
  (docs/042 - `poster.png` lives inside its atlased level dir, not as an
  individual file: `pictureToAtlas("images/<level>/poster.png")`), and finishes
  by starting the world map (`returnTo: "worldmap"`) instead of resuming a paused
  level.
- **LevelScene.ts**: takes a `poster` in its launch data; the solved-countdown
  (docs/030) now calls `leaveToMapAfterSolve()` - plays the poster
  (`scene.start("demo", { mode:"poster", returnTo:"worldmap", returnData:{fromLevel:true} })`)
  if the level has one, else goes straight to the map with `{fromLevel:true}`.
- **ReplayScene.ts**: on solved, if `returnTo === "worldmap"` and the level has a
  poster (the Pedometer path), play the poster before returning - faithful to the
  original. An in-level (P) replay stays a review tool (pause + Esc/R).
- **WorldMapScene.ts**: `launchLevel`/`launchReplay` pass the codename's poster.
  New `setupEnding(solved)`:
  - **Standard mode**: if we arrived with `{fromLevel:true}` (a solve just
    returned us), the ending isn't already solved (checked via its own
    `loadSolvedMoves`, since the ending isn't in the node-derived `solved` set),
    and every real node is solved -> auto-run the ending. Deferred one tick via
    `time.delayedCall(0, ...)` - the original triggers from its update loop, and
    launching mid-`create()` crashes (Phaser Text render not ready).
  - **Sandbox mode**: nothing is genuinely "solved", so the ending is instead a
    small top-centre clickable button (corners are taken by Intro/Exit/Credits/
    Options) so it and its final poster stay testable.
  `launchEnding()` loads the ending level and starts it with its poster.

## Verification (real browser)

- **Poster after replay**: seeded gods' solved moves, ran its Pedometer replay to
  solved -> transitioned to the gods poster (atlas frame `gods/poster`) -> Esc
  returned to the map. No errors.
- **Poster render**: launched the linux poster directly - 640x480, `linux/poster`
  frame, prog_demo/planTalk ran clean, Esc -> map (screenshot: the full recap
  letter art).
- **Ending, sandbox**: the top-centre button loads the ending level - both fish,
  its poster attached (screenshot: both fish at their table "at home").
- **Ending, standard**: seeding every node solved then returning with
  `{fromLevel:true}` auto-launches the ending; NOT when a node is unsolved; and
  NOT re-triggered once the ending itself is solved (the bug the storage-vs-set
  check fixed - otherwise an ending->poster->map->ending loop).
- e2e suite 7/7; `tsc -b` clean.

## Notes / deviations
- A P-launched (in-level) replay does **not** show the poster (it's a review
  tool); only the Pedometer (worldmap) replay does, matching where the original's
  poster actually fires.
- The replay-triggered poster returns to the map without a `{fromLevel:true}`
  ending check (a replay is a review, not a fresh solve) - a minor, deliberate
  simplification vs. the original, which would also re-run finishLevel.
- Poster subtitles use the DemoScene's single white style (no per-font colour);
  same simplification as the briefcase movie.

## Files
- **Modify:** `web/src/lua/worldMapLoader.ts`, `web/src/lua/demoScript.ts`,
  `web/src/scenes/DemoScene.ts`, `web/src/scenes/LevelScene.ts`,
  `web/src/scenes/ReplayScene.ts`, `web/src/scenes/WorldMapScene.ts`.
