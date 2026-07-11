# 031 - Briefcase level: fullscreen movie + auto-play tutorial

2026-07-11

## Context

The `briefcase` level (the game's tutorial, "Briefcase Message") was
**unplayable**: a headless probe confirmed the level loads and plays fine
until the briefcase (`kufr`) is pushed down, at which point its Lua animation
state machine (`code.lua` `prog_init_kufr`, `kufr.faze==8`) calls
**`level_newDemo("script/briefcase/demo_briefcase.lua")`** - unbound in the
port's live engine (`levelScript.ts`), throwing `attempt to call a nil value`
and crashing the round loop. Pushing the briefcase is the level's core
mechanic, so the level was dead.

The level is special (original C++): two scripted, no-player-control sequences.

1. **Fullscreen movie** (`level_newDemo` → C++ `DemoMode`, a state pushed over
   a *paused* Level): `demo_briefcase.lua` plays a 296-frame slideshow
   (`demo_display`) over a `kufr256.png` background, with Czech voice
   (`model_talk`) and music (`kufrik.ogg`), sequenced by `game_planAction`.
2. **Auto-play tutorial** (`demo_help.lua` via `level_planShow`): auto-drives
   the fish through a ~200-step scripted walkthrough demonstrating moves,
   deaths, and unattended save/load/restart, with player input disabled while
   `level_isShowing()`.

Implemented **both, movie first** (per the user), with extra control-lockout
requirements the user called out: during the movie only Esc skips (no save/
load/F1/replay); during the show only Esc leaves (no user save - it could
corrupt the demo), and the demo's own save/load must **not** touch the player's
multi-slot saves (docs/026).

Assets were already converted (296 `demo_briefcase` webp frames incl.
`kufr256`, `kufrik.mp3`, `sound/briefcase/cs/`).

## Phase 1 - Fullscreen movie

- **`web/src/lua/demoScript.ts`** (new): a persistent-wasmoon runner for the
  movie script - the port's `DemoMode`/`demo-script.cpp` equivalent, with only
  the bindings `demo_briefcase.lua` needs (no physics/models): `game_planAction`
  plan FIFO, `demo_display` (records `{path,x,y}` pictures, accumulated by
  position like `DemoMode`'s persistent surface buffer), `model_talk`/
  `model_isTalking` (subtitle+voice, reusing the same dialog-registry/timing as
  `levelScript`), `sound_playMusic`, `options_getParam("lang")→"cs"`,
  `file_exists`. `dialogLoad()` is bypassed (docs/015 reentrancy reasoning) -
  `brief_dialogs_cs.lua` is fetched and run directly with `soundPrefix =
  "sound/briefcase/cs/"`.
- **`web/src/scenes/DemoScene.ts`** (new): the movie player - resizes the canvas
  to 720×555 (`kufr256`), preloads the frame set, ticks `demoScript` on a 100ms
  cycle (matching the original `TimerAgent` cadence the `planDelay` counts were
  tuned to), draws the current picture(s) + subtitle + music/voice. **Esc-only**
  skip (per user, unlike the original's Space/click). On finish (Esc or plan
  drained) it resumes the paused level.
- **`web/src/lua/dialogSound.ts`** (new): `resolveSoundPath`/`fetchSoundDurations`/
  `ResolvedSound` extracted from `levelScript.ts` so both engines share them.
- **Launch/resume** (`LevelScene`): `level_newDemo` delegates to a new
  `HostActions.newDemo` callback that sets `pendingDemo`, read after
  `levelScript.tick()`; `launchDemo()` stops level music, **disables level input**
  (`this.input.enabled = false` - a paused scene still receives input in Phaser),
  `scene.launch("demo")` + `scene.pause()`. A `RESUME` handler re-enables input,
  restores the room canvas size, and re-applies the level's last music command.
  The level's engine/levelScript/positions are untouched by the pause, so play
  continues exactly where it left off.

## Phase 2 - Auto-play tutorial

**Key enabling insight:** during a show, the level's own per-round logic is
suppressed (`code.lua`'s closures all guard on `not level_isShowing()`), so a
mid-show `level_action_restart`/`load` only needs to reset **physics**, not
re-run `code.lua`. This lets the port keep its single persistent wasmoon engine
(and the show queue) alive across show-restarts, avoiding the async-`doString`-
from-a-host-callback reentrancy hazard (docs/008) a faithful C++-style
"re-run the script on restart" would hit.

- **Show queue** (`levelScript.ts`): `level_planShow(fn)` pushes to a separate
  `showActions` FIFO (legacy's `CommandQueue m_show`); `level_isShowing()` =
  `showActions.length>0`; `runShowStep()` (run inside `tick()` before
  `script_update`, matching `nextShowAction`→`updateLevel`) drains one command/
  round; `abortShow()` clears it.
- **Host actions**: `level_action_move/save/load/restart` delegate to
  `HostActions`. `LevelScene.hostActions` closes over `this` so a restart
  swapping `this.engine` stays consistent. `move` → `engine.showMove` (new
  `Room.showMove`, legacy `Room::makeMove` fresh-gate + throw-on-impossible);
  `beginShowRound` = `Room.beginFall`. **Save/load use an in-memory
  `demoSnapshot`** (`{moves, modelState}`), never a player save slot -
  `demoRestart`/`demoLoad` reset physics only (new `resetPhysicsOnly` +
  `buildAnimators`, extracted from `startEngine`) keeping the Lua engine alive.
- **Show-driven round** (`LevelScene.tick()`): when `isShowing()`, player
  movement is blocked (no-op input, reusing the help-modal pattern), the round
  is `beginShowRound()` + `runShowStep()`, win/lose evaluation is skipped (the
  demo deliberately dies/restarts), and **all user controls except Esc are
  locked out** - `whenPlaying()` now also short-circuits while showing, so
  R/Space/P/F1/F2/F3 no-op; only Esc leaves (to the map). A show command that
  throws (physics divergence → impossible move) is caught and gracefully ends
  the show (`abortShow`) rather than crashing.
- **Runtime `file_include`** (`levelScript.ts` + `levelLoader.ts`): `demo_help.lua`
  is `file_include`d at *runtime* (inside a closure), the only such include in
  the whole game (new `extractRuntimeIncludes` classifies by indentation - every
  other code.lua include is a top-level `prog_border`/etc. that still pre-runs at
  bootstrap). Runtime includes are wrapped as callable Lua functions at bootstrap
  (not run), and `file_include(path)` defers running the matching one to
  `runPendingIncludes()` (after `script_update` returns, a plain TS→Lua call, not
  a reentrant host-callback call). Without this, making `level_planShow` real
  would have queued the entire show at load.

## Verification

- `npx tsc -b` clean throughout.
- **Phase 1**: headless probe reaches `kufr.faze==8` and confirms `level_newDemo`
  fires the host action (no crash); `demoScript` produces the `kufrik` music +
  `kufr256` picture + Czech subtitle-with-voice. Full real-browser E2E (11
  assertions): pushing the briefcase (first ~22 solution moves) launches the
  DemoScene at 720×555, it draws layered pictures, **Space does NOT skip** but
  **Esc does**, the level is paused with input disabled during the movie, and on
  skip the canvas/input restore and the level is playable again. Mid-movie
  screenshot confirms the correct art (briefcase + TV with the animating gold
  ring + Czech subtitle).
- **Phase 2**: the show mechanism is verified end-to-end - forcing the trigger
  (fish at 25,23/27,21 via a fake snapshot) queues the ~200-command show,
  `isShowing()` gates input, `runShowStep`/`showMove` apply moves, and a physics
  divergence gracefully aborts (returning control); `save`/`load`/`restart` work
  and use the in-memory snapshot, not `localStorage`. The full clean walkthrough
  from the exact position wasn't exercised because the trigger cell (25,23) is
  occupied by an item at level start (the walkthrough is a mid-solve tutorial),
  so it's only reachable after partly solving - the acknowledged fragile part,
  which the graceful-abort covers.
- **Regression**: all-levels `createLevelScript` sweep = **79/80** clean (only
  `windoze`/`fish_extra`, unchanged), and **no level is "showing" at load** (the
  runtime-`file_include` change correctly keeps `demo_help` from pre-running).
  docs/026 save/load slot suite re-run green (the `buildAnimators`/`startEngine`
  refactor); Phase 1 movie E2E re-run green after the Phase 2 changes.

## Open for next time

- The full `demo_help.lua` walkthrough runs to completion only if the port's
  physics matches the original at every one of ~200 steps *and* the fish reach
  the exact (25,23)/(27,21) trigger. Neither is easy to exercise in a test
  (the trigger is a mid-solve position); if it diverges in real play it
  gracefully aborts and returns control. Validating a full run would need
  driving the level to that exact mid-solve state.
- `DemoScene.preload()` loads all 293 frames up front (fine for a one-time
  cutscene); lazy per-frame loading is a possible refinement.

## Follow-up fixes (same day, from user testing)

Four issues found play-testing the landed feature:

- **Dialog audio download latency + overlap.** The first line of a level's
  dialog (incl. briefcase) played ~0.5-1s late because `AudioManager` lazy-loads
  each sound sprite on first use (docs/018), so the first `model_talk` waited on
  a network fetch - and a late line could overlap the next. Fixed by **preloading**
  each level's sound-sprite dirs up front (new `AudioManager.preload()` +
  `levelSoundSpriteDirs(levelName)` = its own `<level>/cs` voice, the built-in
  `share` impact/death pool, and the shared joke/border pools), called from
  `LevelScene.create()` and `DemoScene.create()` - non-blocking, so the level
  still starts instantly but the clip is cached by the time it's needed.
  Preferred this over gating level-start on audio-ready (which adds a visible
  wait). Also made dialog voice **tracked**: `AudioManager.playDialogVoice()`
  uses `addAudioSprite` (a controllable instance) so a new line **cuts the
  previous** one (legacy `Dialogs::killSound`, no overlap), and a dying fish's
  line is cut too.
- **Level audio kept playing on the world map after Esc.** `AudioManager.destroy()`
  only stopped *music*, not the dialog voice / one-shot effects (which
  `playAudioSprite` fires untracked, and the Sound Manager is game-global,
  docs/025). New `AudioManager.stopAll()` (music + dialog voice +
  `scene.sound.stopAll()`) now runs on `destroy()`/`reset()` and when launching
  the movie, so nothing bleeds into the next scene.
- **Movie frames replaced instead of layering.** `DemoScene` kept one image per
  display position and swapped its texture, so a *transparent* frame meant to
  overlay the previous one instead revealed the background (the original never
  clears its surface buffer - later frames draw on top). Fixed by making
  `demo_display` an append-only **draw log** (`takePendingDraws`) that
  `DemoScene` renders as **stacked Image GameObjects** (later drawn on top:
  opaque frames cover, transparent frames layer) - verified visually (the bird
  "snapshot" + island composite over the persistent briefcase/TV background).
  (Phaser 4's `RenderTexture.draw` rendered black here, so stacked images are the
  robust choice for a one-time cutscene.)
- **Phase-2 tutorial save was virtual.** Per the user, the auto-play tutorial's
  save is now a **real, persistent slot** (new `saveTutorialGame`/
  `loadTutorialGame` in `levelStorage.ts`, `SavedGame.tutorial` flag) the player
  can **load after the tutorial**, shown as a distinct **amber** dot vs. their
  blue saves (`SaveSlotUI` `TUTORIAL_COLOR`). It's a single **upserted** slot
  (the walkthrough saves repeatedly onto the one slot, never spawning many) and
  never overwrites the player's own saves. Replaces the earlier in-memory
  `demoSnapshot`.

## Follow-up 2: dialog audio *still* delayed (root cause found)

The preloading above didn't fully fix it - the user still saw each early dialog
lag its subtitle and get its tail cut off. **Instrumented the real timing**
(subtitle/`playDialogVoice` entry → the sound's `PLAY` event) and found:

- Backend is **Web Audio** (`WebAudioSoundManager2`), so playing a sprite marker
  is normally instant - steady-state lines measured **2-10 ms**. Not network,
  not per-line files (it's one concatenated `sprite.mp3` per dir, played by
  marker).
- The delay was the **one-time Web Audio decode of the whole sprite**:
  briefcase's voice sprite is **2.78 MB** (~2 s to decode). A dialog firing
  before that finished sat in `playDialogVoice`'s load-wait and then played ~2 s
  late; because the subtitle's duration clock started on time, the next line cut
  it off. Measured: the first briefcase line waited **2088 ms**.
- Compounding it, `ensureLoaded` returned the **whole `loadingChain`**, so even
  an already-loaded sprite could block on unrelated in-flight loads.

Two fixes:

1. **Per-sprite load promises** (`AudioManager`): `ensureLoaded` now tracks a
   promise **per key** (via a `loadPromises` map, replacing the `attempted`
   set), so `playDialogVoice` for a loaded sprite resolves immediately and a
   loading one waits only for *itself*, never the whole chain. New public
   `whenLoaded(spriteDir)`.
2. **Gate the level's dialog logic on its voice sprite** (`LevelScene`): the
   `createLevelScript().then(...)` now `await`s
   `audioManager.whenLoaded(levelDialogVoiceDir(level))` (the sprite is already
   loading in parallel with the Lua bootstrap) before setting `this.levelScript`,
   so **no dialog can fire until its audio is decoded** - the first line plays in
   sync with its subtitle. Time-boxed (`Promise.race` with a 4 s cap) so a
   stuck/failed load can never brick the level. The level stays fully interactive
   (fish move) during the brief warm-up; only the script's dialog/item-anim
   layer waits. Verified: `levelScript` goes live at exactly the sprite-decoded
   time (briefcase +2028 ms, airplane +526 ms), and the movie plays `kufrik`
   music + the `briefcase/cs` voice with only one voice at a time (no overlap).

## Follow-up 3: movie still cropped each voice line's tail

Standard levels were now in sync, but the **briefcase movie** still cut the
last ~1-2 s off each voice line. Cause: a **cycle-rate mismatch** in the movie.
The original waits for a line via `waitForTalker()` (`game_planAction(() => not
model_isTalking(actor))`), and the port has that too - but `demoScript`'s
`model_talk` sized a line's duration as `ceil(clipMs / ROUND_MS)` **cycles**
using `ROUND_MS` (130 ms, the *level* round rate) while `DemoScene` ticks the
movie at `DEMO_CYCLE_MS` (**100 ms**). So `model_isTalking` reported the line
done after `N x 100 ms` where `N` was sized for 130 ms cycles -> **~77 %** of the
real clip -> `waitForTalker` advanced early and the next line's
`stopDialogVoice` cut the tail. (Levels are self-consistent because there both
the tick and the duration math use `ROUND_MS`.) Fix: `DEMO_CYCLE_MS` moved into
`demoScript.ts` as the single source of truth (exported, `DemoScene` imports it)
and used for the duration math, so a line lasts `ceil(clipMs / DEMO_CYCLE_MS)`
cycles = exactly its real length. Verified per line: `kd-uvod` (10.8 s clip)
played 10.86 s, `kd-ufo` (19.9 s) played 19.98 s - ratio ~1.00, both play to
`complete` instead of being cut.
