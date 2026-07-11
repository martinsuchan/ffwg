# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Fish Fillets - Next Generation (fillets-ng), a C++/SDL 1.2 puzzle game (moving two fish
around underwater rooms), originally by Ivo Danihelka, based on a 1998 ALTAR Interactive
game released under GPLv2 in 2004. Homepage referenced in code: http://fillets.sf.net.

**Active work in this repo is porting the game to the browser** (TypeScript, reusing the
original Lua level content) — see the next section. Repo layout:

- `legacy/` — the original C++/SDL1.2/Lua game source (autotools build). Reference material
  and the source of Lua content to port; see "Legacy game" below.
- `web/` — the new browser port: TypeScript + Phaser + Vite. Early stage.
- `docs/` — dated, numbered dev log of the port (`docs/README.md` has the convention).
  **This is the source of truth for current status/open questions** — this file covers
  stable architecture and won't be kept in sync with day-to-day progress.
- `scripts/` — PowerShell helpers (`start.ps1`, `build.ps1`, `new-doc.ps1`) for the web port.
- `README.md` — human-facing overview and run instructions.

## Web port (`web/`) — start here for new work

Goal: reuse the legacy Lua content (level layouts, dialogs/translations, scripted room
behavior under `legacy/script/`) unmodified where possible, rewrite the puzzle
physics/rules (`Field`, `Cube`/`Unit`, `Rules`, `Landslip`, `FinderAlg` — see "Legacy game"
below) in TypeScript, and re-implement the C++ "host API" the Lua scripts call into
(`*-script.cpp` files) as TypeScript bindings. Target inputs: keyboard, mouse, gamepad,
touch.

Current stack (see `docs/001-2026-07-06-legacy-review-and-phaser-spike.md` for the
reasoning; check for later numbered entries too — decisions here can change):

- Rendering/input: **Phaser 4** (`phaser@^4.2.0`), chosen for its built-in input manager
  unifying keyboard/mouse/touch/gamepad. TypeScript + Vite.
- Lua runtime: **wasmoon** (`wasmoon@^1.16.0`, Lua 5.4 via WASM). POC in
  `web/src/lua/luaPoc.ts` + `web/public/lua/sample.lua` confirms host callbacks work both
  ways (scalar return values and table round-trips) — see
  `docs/002-2026-07-06-wasmoon-lua-poc.md`. Checked against the real legacy corpus too:
  `web/tools/check-lua-compat.mjs` (`scripts/check-lua-compat.ps1`) parses all 1,469
  `legacy/script/**/*.lua` files with wasmoon's actual Lua 5.4 — 1,468 parse clean; the one
  real incompatibility category found (`table.getn`, `loadstring`) is fixed via a loaded-once
  shim, `web/public/lua/lua50-compat.lua`, not per-file edits — see
  `docs/005-2026-07-06-lua-5-4-compatibility-check.md`. `fengari` fallback is no longer
  needed.
- First real level render: `web/src/lua/levelLoader.ts` runs a level's actual, unmodified
  `legacy/script/<level>/models.lua` (+ `legacy/script/share/level_creation.lua`) through
  wasmoon, and `web/src/scenes/LevelScene.ts` draws the result in Phaser — background, wall
  overlay, items and both fish, all at their real grid positions (`x*15, y*15`, see
  `legacy/src/level/View.h`'s `SCALE`). Dev-only: legacy `.lua`/image files are fetched
  straight off disk via Vite's `/@fs/` route (`server.fs.allow` in `web/vite.config.ts`),
  not copied — production packaging of Lua content is still open. Scoped to "models only"
  (no dialogs/code.lua/animation/sound) — see
  `docs/006-2026-07-07-level-models-rendering-poc.md`.
- First playable puzzle logic: `web/src/game/` is a line-for-line TS port of the physics/
  rules classes (`Field`, `MarkMask`, `Rules`, `Landslip`, `Cube`, `Goal`, `Unit`,
  `ModelFactory`, `Room` — see "Legacy game" below for what each does), wired to the parsed
  Lua level via `web/src/game/GameEngine.ts`. `airplane` is playable end-to-end in
  `LevelScene` on a fixed round tick: WASD drives the big fish, IJKL the small fish (their
  real legacy key bindings, from `ModelFactory::createUnit`) — no animation/sound/save/undo.
  Verified against the real ported code via synthetic-room tests (push/fall/death/escape) and
  an extended real-browser playthrough, not just read-through — see
  `docs/007-2026-07-07-game-logic-port-and-playable-poc.md`, which also documents the actual
  gameplay rules (who can push what, the three ways a fish can die, win/lose conditions).
- `levelLoader.ts` also runs each level's `code.lua` now (not just `models.lua`), since 9
  levels — each world's final level (`grail`, `barrel`, `floppy`, `atlantis`, `gods`,
  `linux`, `map`, `propulsion`, `turtle`) — reassign goals there: both fish get `goal_alive`
  instead of `goal_escape`, and a specific item gets `goal_out` instead (push *that* out of
  the room). The physics port needed zero changes for this — `Rules::actionOut()` already
  works on any model with `shouldGoOut()`, not just fish. Also loads `level_plan.lua` and
  `prog_goanim.lua` (pure-Lua utilities these depend on) and resolves each level's own
  `file_include(...)` calls via a pre-scan (not a live host binding — calling back into a
  running wasmoon engine from a host callback corrupts its WASM state) — see
  `docs/008-2026-07-07-final-level-goals-and-file-include.md`.
- Fish animation (body swim/turn/vertical/idle poses + head blink/pushing overlays) is done:
  `file_exists` in `levelLoader.ts` is a real lookup now (`web/public/lua/image-manifest.json`,
  built by `scripts/build-image-manifest.ps1`), so every real anim frame gets discovered, not
  just phase 0 — `LevelModel.picture` (one resolved frame) is gone, replaced by the full
  per-anim/per-side frame data. `web/src/game/UnitAnimator.ts` is a direct TS port of
  `level_update.lua`'s `animateFish`/`animateHead` (which anim to play); `web/src/scenes/
  ModelAnimator.ts` owns *when* frames actually change on screen plus position-slide tweening,
  deliberately decoupled from `docs/007`'s physics round loop (confirmed with the user first —
  see `docs/009` for the tradeoff). See
  `docs/009-2026-07-07-fish-animation-system.md`, which also covers why texture atlases
  (`docs/004`) are still deferred.
- Position-slide timing bug fixed: the slide tween duration must stay under the physics
  round interval or consecutive rounds stack tweens on top of each other — visible as a
  continuously-driven fish's sprite lagging further and further behind its true grid cell,
  or as a pushed item appearing to move diagonally when a horizontal push transitions into
  a vertical fall. `web/src/game/timing.ts` (`ROUND_MS`) is now the single source of truth
  for the round interval, shared by `LevelScene`'s round timer and `ModelAnimator`'s slide
  duration; `ModelAnimator.sync()` also kills any in-flight tween and snaps to the last
  grid-aligned pixel position before starting a new one, since a cube only ever moves one
  axis per round (`Rules.dir` is a single value) — see
  `docs/010-2026-07-07-position-slide-timing-fix.md`.
- Corpse disintegration/removal ported: the original doesn't unmask a dead fish from the
  Field on death — it stays solid for ~14 game cycles (`EffectDisintegrate`'s
  `DISINT_START`/`DISINT_SPEED` math) before `Rules::changeState()` unmasks it and calls
  `change_remove()` (the same removal an escaped/`goal_out` model gets). `web/src/game/
  Rules.ts` now ports this as a round-counted timer (`DEATH_REMOVE_ROUNDS = 14`); until it
  fires, a corpse still fully supports whatever rests on it, matching the original. Also
  fixed a related bug found while verifying this: `LevelScene.tick()` was halting the round
  loop entirely the instant a fish died (level unsolvable), which would have prevented the
  new countdown from ever completing in real play — the loop now always keeps running,
  `gameOver` only latches the status text once. See
  `docs/011-2026-07-07-corpse-disintegration-and-removal.md`.
- Restart sprite leak fixed: `ModelAnimator.destroy()` only removed its two `TimerEvent`s,
  never the `bodySprite`/`headSprite` `Image` GameObjects it owns — every `R` restart left
  the previous run's sprites on screen (frozen at their old texture/position) and added a
  whole new set on top, compounding on every restart. Fix was one line: `destroy()` now also
  calls `.destroy()` on both sprites. See `docs/012-2026-07-07-restart-sprite-leak-fix.md`.
- Death-reaction visual timing fixed: compared `Rules.ts`'s death checks line-by-line against
  `legacy/src/level/Rules.cpp` and confirmed the physics is faithful — the original's crush
  detection is predictive by design (adjacent + already moving, not literal cell overlap,
  since two cubes can never occupy the same Field cell). The bug was presentation-only:
  `ModelAnimator` swapped a dying fish to its skeleton texture instantly, while the killer
  item's own position slide (`SLIDE_MS`, `docs/010`) was still gliding into place, making the
  fish look dead before its killer visibly arrived. Fix: the skeleton-pose swap is now
  delayed by `SLIDE_MS` via `scene.time.delayedCall`, landing back in sync with the killer's
  slide. See `docs/013-2026-07-07-death-reaction-visual-timing.md`.
- Item animation (grail's aura pulse, airplane's eye blink, etc.) is done via **live Lua**,
  not a TypeScript port — item animation is ~66 levels' worth of one-off hand-written
  `code.lua` state machines (not one shared algorithm like physics/fish-animation), so
  porting each would mean translating and re-verifying dozens of scripts, against this
  project's own goal of reusing legacy Lua content unmodified. `web/src/lua/levelScript.ts`
  runs each level's real bootstrap chain (`level_start.lua`'s `script_update()`, previously
  never executed post-load) in a *persistent* wasmoon engine kept alive for the play
  session — confirmed via research spikes that wasmoon's per-round Lua calls are
  synchronous and ~0.1ms/round (no `async` restructuring needed), and that this repeated-call
  pattern is safe (the `docs/008` reentrancy bug is specific to calling `doString()` from
  inside a host callback, a different pattern). Host bindings read live model state from the
  same `RenderModel[]` snapshot `LevelScene` already computes each round; `model_setAnim`/
  `runAnim`/`useSpecialAnim` write into an override map `ModelAnimator` applies for
  **non-fish models only** — fish stay entirely TS-owned (`docs/009`/`013`) even though the
  real `script_update()` also drives fish anim internally via the same calls; those writes
  are just never read. See `docs/014-2026-07-08-item-animation-via-live-lua.md`.
- Dialog/subtitle text is real now (English only, no voice audio/music yet). Built on
  `docs/014`'s live Lua engine: `game_planAction`/`game_isPlanning` are real now (a FIFO
  matching `legacy/src/plan/CommandQueue.cpp`'s single-command-at-a-time design, not the
  previous docs/014 no-op stub), `model_talk`/`dialog_isDialog`/`model_isTalking` are real,
  and `dialogLoad()` is deliberately bypassed (would enumerate ~15 languages via
  `select_lang.lua` and re-trigger `docs/008`'s reentrancy risk) in favor of pre-fetching
  each level's English `dialogs_en.lua` files directly. Subtitle duration is
  `Dialog::getMinTime()`'s own no-sound fallback formula (`min(180, textLength)` cycles,
  ported from `legacy/src/gengine/Dialog.cpp`) — not an invented heuristic, the original's
  own answer for exactly this case. `level_getRestartCounter()` is also real now, backed by
  `LevelScene`'s existing per-restart counter — so `code.lua`'s attempt-based dialog
  probability/delay (`pokus`) genuinely varies across restarts. See
  `docs/015-2026-07-08-dialog-text-display.md`.
- Controls now match the original's real scheme instead of the earlier "WASD always
  drives big fish, IJKL always drives small fish" POC: one fish is "active" at a time
  (small fish first, matching `ModelFactory::createUnit`'s `startActive`), arrow keys
  always drive whichever fish is active, WASD/IJKL still drive their own fish directly
  and silently make it active, and Space explicitly switches the active fish — which
  triggers a brief "greet" animation (a held turn-pose frame) via `Rules.actionActivate()`,
  a code path `docs/007`/`docs/009` had already ported but left unreachable pending this
  feature. `web/src/game/Controls.ts` (new) ports the relevant subset of
  `legacy/src/level/Controls.cpp`. See `docs/016-2026-07-08-active-fish-control-scheme.md`.
- Mouse controls added on top of `docs/016`'s scheme: click a fish to select it (same
  greet flash as Space), click-and-hold the left button to path the active fish around
  obstacles toward the cursor (new `web/src/game/FinderAlg.ts`, a plain BFS recomputed
  fresh every round — port of `legacy/src/level/FinderAlg.cpp`, minus its explicit
  `w*h`-bounded closed array, unneeded since `Field.getModel` already returns the border
  Cube for any out-of-bounds probe), and hold the right button to push straight toward the
  cursor instead (new `web/src/game/MouseControl.ts`, port of `MouseControl.cpp` — included
  after confirming with the user, since only left-click behavior had been described). Mouse
  is only tried when keyboard produces no move that round, matching the original's real
  precedence. Also added a visual-only "swims faster" effect after several tiles of
  continuous movement (`Rules.moveStreak`, `ModelAnimator`'s `speedStepsFor`) — the
  original ties speedup to shortening the physics round's own real-world duration
  (`PhaseLocker`), which conflicts with this project's fixed `ROUND_MS` (`docs/010`), so
  after confirming with the user this only speeds up the swim animation/position-slide
  tween, not actual grid-cell traversal rate. See
  `docs/017-2026-07-08-mouse-controls-and-swim-speedup.md`.
- Real audio: per-level looping background music (some levels stop it mid-game, e.g.
  `viking1`'s musician-band gag — `sound_playMusic`/`sound_stopMusic` are real host
  bindings now), dialog/NPC voice (`model_talk`'s sound arg actually plays, with subtitle
  duration driven by the real clip length when one resolves, text-length formula
  otherwise), built-in impact/death sounds (`Room.lastImpact`, ported since `docs/007` via
  `Landslip.getImpact()` but unread until now — no Lua call site, resolved through the same
  `sound_addSound`-populated registry Lua uses), and Lua-driven ambient sound (bubbles).
  New `web/src/scenes/AudioManager.ts` owns lazy-loaded Phaser playback (asset needs aren't
  known until the async Lua bootstrap runs). **Default dialog language switched from
  English to Czech** (text and audio both — supersedes `docs/015`'s "English only"):
  Czech has near-universal voice-over coverage across levels vs. English's ~30/82.
  `scripts/convert-assets.ps1`/`convert-music.ps1` (built in `docs/003`, unused until now)
  converted a small verification set (`airplane`, `viking1`, the shared sound pool, all
  music) — the full ~80-level batch is a documented follow-up. New `web/tools/
  build-audio-manifest.mjs` mirrors the image-manifest tooling, but reads the *converted
  web output* (`web/public/assets/sound/**/sprite.json`) rather than `legacy/sound/`
  directly, since sound is sprite-packed. See `docs/018-2026-07-09-sound-and-music.md`.
- Keyboard input reliability fixed: `LevelScene`/`Controls.ts` only polled `heldKeys` once
  per round (`ROUND_MS`), so a discrete tap shorter than one round interval could land
  entirely between two polls and vanish - measured ~65-70% drop rate for a realistic fast
  tap, confirmed with timestamped traces. The original avoids this via
  `Controls::controlEvent()`/`m_strokeSymbol` (`legacy/src/level/Controls.cpp`), a single-
  slot buffer that captures the raw keydown edge independent of round timing and is
  guaranteed to be consumed by the next round - a mechanism `docs/016` had deliberately
  dropped when porting `Controls.cpp`. Ported as `InputProvider.takeQueuedKey()` +
  `LevelScene.queuedKey` + `Controls.driving()` trying the queued key before falling back
  to held-state polling; 100% tap reliability after (36/36 vs. ~30% before). The initial
  level-load input freeze (while unsupported items settle) and mouse click-and-hold
  pathing both turned out to be faithful/working already, not bugs. See
  `docs/019-2026-07-09-keyboard-input-reliability-fix.md`.
- Immediate slide start on decide, not apply: traced the original's real animation timing
  (`PhaseLocker`/`Controls::lockPhases()`/`View::getScreenPos()`) rather than assuming it -
  it computes screen position from the just-decided direction (`Cube::getLastMoveDir()`)
  plus a growing shift, never from the committed grid location, so its slide starts the
  instant a move is decided with no dead zone, even though it has the same decide-this-
  round/apply-next-round split internally as this port. `ModelAnimator.ts` used to wait
  until it *saw* `model.x`/`model.y` change (one round after the move was decided) before
  starting a tween, leaving the fish standing still replaying its swim texture for one
  extra `ROUND_MS`. Fixed with `MOVE_OFFSETS`: `ModelAnimator.sync()` now predicts the
  slide target from `RenderModel.action` (e.g. `"move_right"`) immediately, falling back to
  the official `model.x`/`y` whenever it disagrees (so a missed prediction can never leave
  a sprite stuck) - applies uniformly to pushed/falling items too, not just fish. Worst-case
  time to visually arrive at the next cell measured 173-274ms after this (was up to 362ms).
  See `docs/020-2026-07-09-immediate-slide-start-and-original-comparison.md`.
- Move recording (step 1 of turning the POC into a real game - solution validation/replay/
  save are the planned next steps): every successful move or turn now appends a symbol to
  `Controls`' recorded move string - lowercase `udlr` for `fish_small`, uppercase `UDLR` for
  `fish_big` (`ControlSym`, matching `ModelFactory::createUnit()` exactly), regardless of
  whether it came from a held key, `docs/019`'s queued-key edge trigger, or mouse - all paths
  record through the same mechanism. A turn records the same symbol as the eventual move
  (legacy's `Unit::goLeft()` does this too), so replaying a string reproduces turn-then-move
  splits without a separate "turn" marker. `GameEngine.getMoves()`/`getStepCount()` expose it.
  This is also the flat-string format `legacy/solution/*.lua`'s `saved_moves` already use.
  See `docs/021-2026-07-09-move-recording.md`.
- Headless solution validator (step 2): `Unit.driveOrder()`/`Controls.makeMove()`/
  `Room.loadMove()`/`settleAll()` port legacy's `Room::loadMove()` - settle pending falls,
  apply exactly one move symbol (throwing if invalid), repeat. New
  `web/src/game/SolutionValidator.ts`'s `validateSolution(engine, moves)` replays a whole
  move string against a fresh `GameEngine`, no Phaser/rendering at all. First run against
  all 81 `legacy/solution/*.lua` files: only 32 validated cleanly - see `docs/023` for why
  that number was misleading and jumped to 69 after one bug fix. See
  `docs/022-2026-07-09-headless-solution-validator.md`.
- Initial facing direction bug: `GameEngine.buildCube()` never applied the level-parsed
  `isLeft` onto a newly built `Cube` - every model in every level always spawned facing left
  (`Cube`'s own default), regardless of a level's `addFishAnim(model, LOOK_RIGHT, ...)`.
  Invisible until now because `airplane`/`viking1` (the only two levels ever exercised before
  docs/022's validator) both specify `LOOK_LEFT`, where the bug's effect happens to match the
  correct spec. Confirmed the blast radius *before* fixing: 44 levels request `LOOK_RIGHT` for
  a fish, and that list is an exact match for docs/022's 37 "solution fails partway" failures -
  none of the 32 passing levels use it. One-line fix (`cube.isLeft = modelData.isLeft;`,
  mirroring the existing `cube.goal = ...` line beside it) took the validation pass rate from
  32/81 to **69/81** - every one of those 37 levels now solves correctly, including 1691- and
  2127-move solutions. The remaining 12 failures are entirely the pre-existing, unrelated
  missing-Lua-binding/`fish_extra`/no-such-level gaps from docs/022. See
  `docs/023-2026-07-09-initial-facing-direction-bug.md`.
- Closed the remaining 10 level-load gaps: `levelLoader.ts`'s goal-extraction-only loader
  stubbed `initModels()` as a complete no-op, correct for `airplane`/`viking1` (whose
  `code.lua` only touches per-model animation state inside deferred per-round closures) but
  wrong for `alibaba`/`bathroom`/`briefcase`/`chest`/`city`/`elevator1`/`elevator2`/
  `experiments`/`gems`/`music`, whose `code.lua` calls things like `:updateAnim()`
  *synchronously* in `prog_init()`. Fixed with `INIT_MODELS_SOURCE` (a faithful subset of the
  real `initModels()` from `level_start.lua`, minus its trailing sound/font-loading calls
  this loader doesn't need) plus running two more real shared files (`prog_finder.lua`,
  `prog_compatible.lua`) and a handful of new no-op stubs (`game_addDecor`,
  `level_planShow`, `game_planAction`) and one real one (`model_getLoc`). Also fixed
  `math.mod` (Lua 5.0's integer modulo, missed by `docs/005`'s compat checker since that
  only verifies parsing, not execution) in the compat shim. Found two wasmoon marshaling
  quirks along the way worth remembering for future host bindings: returning `null` crashes
  `PromiseTypeExtension`, and `undefined` marshals as *zero* Lua return values rather than
  one `nil` - `options_getParam` returns `""` instead. Full batch re-run: **79/81 passed** -
  only `windoze` (`fish_extra`, unsupported) and `redhat` (no matching level in this repo)
  remain, both explicitly out of scope. See
  `docs/024-2026-07-09-closing-the-level-load-gap.md`.
- Replay mode (step 3): a new `ReplayScene` plays back a level's recorded move string
  (docs/021's symbol format) round-by-round in real time, launched from normal play via `P`
  (reads `legacy/solution/<level>.lua` for now - the same reference solutions docs/022-024
  validated - since solved-level persistence, step 4, isn't built yet). Deliberately better
  than the original's own replay (`LevelLoading::loadReplay()`: one fixed fast pace, no
  pause/step/speed control at all): starts at normal speed immediately, shows a step counter,
  and has Pause/Step/Play/Fast-forward buttons (media-player Unicode glyphs). Only fish
  animation and background music play - no subtitles, no item decorative animation, no sound
  effects/dialog voice - though the live Lua engine still runs every round regardless, since
  music commands (including mid-level stops like `viking1`'s musician gag) come from it. New
  `Room.replayRound()`/`GameEngine.tickReplay()` mirror `nextRound()`'s round-by-round shape
  exactly (unlike the fast, instantly-settling `loadMove()` validator path from docs/022) -
  verified to reach the same solved outcome the validator already confirmed. Shared scene
  helpers moved to new `web/src/scenes/sceneUtils.ts`. See
  `docs/025-2026-07-09-replay-mode.md`.
- Solution/save persistence (steps 4+5, combined): `localStorage`-backed, new
  `web/src/storage/levelStorage.ts`. Solved-solution persistence (step 4) - `P` now prefers
  the player's own best (shortest) solved solution over the `legacy/solution/` reference file,
  "keep only if shorter" ported from `LevelStatus::writeSolvedMoves()`. Mid-level save/load
  (step 5) is **multi-slot**, modeled on *Fish Fillets 2*'s mission-screen dot row (not
  original FF NG, which only ever had one save per level) per the user's request: new
  `web/src/scenes/SaveSlotUI.ts` draws a row of clickable dots bottom-left - left-click loads,
  right-click deletes, the dim trailing dot (or `F2`) saves a new slot (`F3` loads the latest
  slot) - real key bindings, `legacy/src/level/LevelInput.cpp`'s `KEY_SAVE`/`KEY_LOAD`. A save
  is more than a move string: reading `Level::saveGame()`/`action_load()`/
  `LevelLoading::nextLoadAction()` confirmed the original also snapshots each level's own
  Lua-side model state (`getModelsTable()`, pickled) - the only way per-level custom state
  (NPC dialogue counters, decoration flags a level's `code.lua` tracks itself - e.g. `viking1`'s
  musician-band gag) survives a load, since physics position is *never* cached on the model
  table (always fetched live). Ported faithfully: physics still replays via the existing
  `loadMove()`/`settleAll()` (docs/022); `web/src/lua/levelScript.ts` now also loads
  `Pickle.lua`/`prog_save.lua` verbatim and exposes `LevelScript.captureModelState()`/
  `restoreModelState()` as plain **synchronous** calls (two tiny glue functions loaded once,
  fetched as function references exactly like `scriptUpdate` - avoids the async-`doString()`
  reentrancy risk docs/008 already hit once). See
  `docs/026-2026-07-10-solution-and-save-persistence.md`.
- World Map: the game now boots into a real level-select hub (`web/src/scenes/
  WorldMapScene.ts`) instead of one hardcoded level, in **sandbox mode** (every node,
  including normally-hidden secret branches, is unlocked - `SANDBOX_MODE` in
  `web/src/game/worldMapState.ts`, a single flag away from real progression-gating). New
  `web/src/lua/worldMapLoader.ts` parses the real `legacy/script/worldmap.lua`/
  `worlddesc.lua`/`worldfame.lua` (80 nodes, Czech names for whole-game language
  consistency) via a one-shot wasmoon engine, same pattern as `levelLoader.ts`.
  Solved/unsolved node coloring is derived fresh from `localStorage` every time the map is
  shown (`computeNodeStates()`, a pure function - not the original's in-place tree mutation)
  rather than replicating the original's push/pop-with-paused-state-underneath stack model;
  this port keeps using its own already-proven `scene.start()` full-teardown pattern
  (docs/025) both ways. `LevelScene`/`ReplayScene` are now launched dynamically
  (`init(data)`, mirroring `ReplayScene`'s existing pattern) instead of `LevelScene` taking
  fixed data in its constructor; the canvas resizes per scene now too
  (`this.scale.setGameSize(...)`) since the map (640x480) and each level's own room size
  differ. Clicking a solved node shows the original's real **Pedometer** screen (new
  `web/src/scenes/PedometerUI.ts`, an in-scene overlay, not a separate scene) - Run/Replay/
  Cancel at the original's exact panel/button positions, move count via a simple count-up
  tween (not the original's per-digit slot-machine animation). `ReplayScene` gained a
  `returnTo: "level" | "worldmap"` distinction, since Escape's destination now genuinely
  depends on whether replay was launched via P from a live level or via the map's Pedometer.
  The map's 4 large corner buttons (Intro/Exit/Credits/Options) are analyzed in depth but
  deliberately left inert this pass (real destinations - attract-mode movie, a from-scratch
  settings screen, a poster scroller, and Exit's browser-tab reinterpretation - don't exist
  in this port yet) - an explicit follow-up. Also ran the **first full, unfiltered asset
  conversion batch** (previously only `airplane`/`viking1`/shared pool were converted) since
  the map makes every level genuinely reachable now. See
  `docs/027-2026-07-10-world-map.md`, which also documents 3 real bugs found/fixed during
  verification (a Phaser hit-area coordinate-space bug that silently broke all node hover/
  click, a pulse-timer-outliving-its-scene crash, and an uncaught-JSON-parse crash for any
  level beyond the two previously exercised).
- World Map smoke-test fixes (`docs/028-2026-07-10-world-map-smoke-test-fixes.md`): user
  smoke-testing docs/027 found 3 more real bugs, all "invisible until every level became
  reachable." (1) **Level-loading texture collision** (the serious one): every Phaser
  texture key (`ModelAnimator.textureKey()`, `LevelScene`/`ReplayScene`'s `"bg"`) was
  level-*agnostic* - fine when one hardcoded level lived for the whole session, but once
  the map made `LevelScene` dynamically relaunchable (docs/027), two different levels'
  model 0/background collided on the same key, and Phaser's loader silently keeps
  whichever image loaded first - closing level A and opening level B showed A's stale
  textures with B's correctly-loaded position data. Fixed by prefixing every texture key
  with `levelName` throughout `ModelAnimator.ts`/`sceneUtils.ts`/`LevelScene.ts`/
  `ReplayScene.ts`. (2) **No dialog audio beyond `airplane`/`viking1`**: two compounding
  causes - `web/public/lua/audio-manifest.json` was never regenerated after docs/027's
  broader asset conversion (dialog sound-path resolution is gated by it; music never was,
  which is why music alone kept working), and that conversion had itself silently
  *stopped partway through* (~30 of 82 levels) because two genuinely-real, essentially-
  zero-length placeholder `.ogg` clips make `ffprobe` report `"N/A"` for duration,
  crashing `scripts/build-audio-sprite.ps1`'s unguarded `[double]` cast with no per-file
  recovery. Fixed the script to treat an unparseable duration as 0-length instead of
  aborting, reran the full batch to actual completion, rebuilt the manifest (82 levels,
  3702 sound paths). (3) **World map had no music at all** (not reported broken, just
  missing) - `WorldMapScene` never got an `AudioManager`; added one `applyMusicCommand`
  call in `create()`, mirroring the original's `WorldMap::own_resumeState()`. Also found
  (not user-reported, surfaced while fixing #2): 7 more Lua host bindings missing from
  `web/src/lua/levelScript.ts` (the *live* per-round engine) that only ever mattered for
  levels beyond the two this port had ever exercised - `options_getParam`/`game_addDecor`/
  `level_planShow` were copy-pasted from `levelLoader.ts`'s already-safe stubs;
  `level_isShowing`/`model_setViewShift`/`game_setScreenShift`/`game_setFastFalling` are
  new no-op stubs (each confirmed cosmetic-only, or - for `game_setFastFalling` -
  windoze-only and already out of scope, before stubbing); `model_isAtBorder` got a *real*
  implementation (the computation already existed internally, `Rules.isAtBorder()` since
  docs/007, just was never exposed to Lua - added to the `RenderModel` interface);
  `model_equals` got a documented approximation (single-anchor-position match, since this
  engine deliberately has no access to the real multi-cell `Field` grid). Found all 7 via
  a from-scratch sweep script (`createLevelScript()` + a few ticks, all 80 levels, no
  Phaser/UI at all) rather than fixing them reactively one at a time - went from 62/80 to
  79/80 levels loading cleanly (only `windoze`'s already-known-unsupported `fish_extra`
  remains).
- Fish talking animation & canvas stretching (`docs/029-2026-07-10-fish-talking-animation-
  and-canvas-stretching.md`): two more user-found bugs. **Canvas stretching**: `LevelScene`/
  `WorldMapScene`/`ReplayScene` all resized the game canvas via `this.scale.setGameSize()`
  (docs/027) - wrong call for this port's Scale Manager mode. Phaser's own docs say
  `setGameSize()` is for `FIT`-style modes (updates only the internal backing resolution);
  `.resize()` is the one for `NONE` mode (this port's actual, implicit mode - no `mode` is
  ever set, only `zoom`), and it's the one that also updates the canvas's real CSS display
  box. Confirmed directly: `library` (21x37 cells) had its internal resolution correctly
  become 315x555, but the CSS box stayed frozen at the map's 960x720 from boot - a
  0.568-ratio image stretched into a 1.333-ratio box. Switched all three call sites to
  `.resize()`. **Fish talking animation**: `UnitAnimator.computeHeadAnim()`'s own doc
  comment had flagged this gap since docs/009, written before dialogs existed - "revisit
  when dialogs land" never actually happened when they did (docs/015). Ported the real
  `animateHead()` (`legacy/script/share/level_update.lua`): talking beats pushing beats
  occasional blink, using 3 real `head_talking_00/01/02` frames every fish already has
  (confirmed the assets exist, this was a pure logic gap). New `LevelScript.isModelTalking()`
  wires up the `TALK_INDEX_BOTH` (-1) "narrator line, every fish talks" case alongside a
  model's own dialog slot. `talk_phase` cycling is owned by `ModelAnimator` itself (ticked
  on its existing ~100ms head-check timer), not read from Lua, matching this port's
  established "fish stay entirely TS-owned" split (docs/009/013) rather than the original's
  Lua-side bookkeeping.
- Post-solve auto-return, F1 help popup, window title + favicon
  (`docs/030-2026-07-11-post-solve-return-help-popup-and-window-title.md`): three
  quality-of-life items, each cross-checked against the original. (1) **Auto-return to the
  world map after solving** — ports legacy's `LevelCountDown` (`Level::own_updateState`
  counts down `getCountForSolved()` cycles once `isSolved()` — 10 normally, 30 if a dialog
  is still running — then `quitState()`s back to the still-alive `WorldMap`). Implemented as
  a round-counted countdown in `LevelScene.tick()` (`SOLVED_RETURN_ROUNDS`/`_DIALOG`, in
  `ROUND_MS` rounds, the per-cycle proxy), reusing `getActiveSubtitle()` for the dialog case;
  the win/lose tail no longer early-returns on `gameOver` so it can drive the countdown to
  `scene.start("worldmap")` (~1.4s no-dialog, identical in effect to Esc). (2) **F1 help
  popup** — `statusText` no longer holds a permanent controls wall; new
  `web/src/scenes/HelpOverlay.ts` (owned-UI overlay like `PedometerUI`, content-measured
  layout) toggles on F1, closes on Esc/OK, and is a true modal (movement gated via a no-op
  `engine.tick()` input, discrete keys via a new `whenPlaying()` guard); `statusText` is kept
  `setVisible(false)` while empty (an empty Text still renders its background box otherwise).
  (3) **Window title + favicon** — the original's per-level caption is `Level::initScreen()`'s
  `findDesc + ": " + findLevelName` = `<section>: <name>` (e.g. "Vrakoviště: Výška: -9000
  stop"); `worldMapLoader.ts` now also captures the 4th `desc` arg into `WorldMapData.sections`,
  and *all* `document.title` writes live in `WorldMapScene` (`create()` = the map title,
  `launchLevel`/`launchReplay` = `titleFor(codename)`), so every return path restores the map
  title with zero plumbing through `LevelScene`/`ReplayScene`. The whole-game name is a
  `GAME_TITLE` constant = "Fish Fillets - **Web** Generation" (this port's own name, not the
  original's "Next Generation"; per-level section/level names are unchanged). Favicon: the
  game's own 32×32 `legacy/images/icon.png` copied to `web/public/favicon.png`, linked from
  `index.html`.

Commands (from repo root):

```
scripts\start.ps1              # installs deps if needed, runs the Vite dev server, opens a browser
scripts\build.ps1              # tsc -b + vite build (add -Preview to serve the build)
scripts\new-doc.ps1 "<slug>"   # scaffold the next numbered docs/ entry
```

Equivalent by hand, from `web/`: `npm install`, `npm run dev`, `npm run build`,
`npm run preview`. No test suite yet.

**Workflow convention:** whenever a notable feature/decision lands (not small edits), add
the next `docs/NNN-YYYY-MM-DD-slug.md` entry via `scripts/new-doc.ps1` summarizing what
changed, why, and what's open — don't wait to be asked.

## Legacy game (`legacy/`) — reference for porting

### Build

Autotools-based C++ project (no CMake, no package.json). From `legacy/`:

```
./configure
make
make install
```

Requires libSDL 1.2, SDL_mixer, SDL_image, SDL_ttf, and Lua 5.0 (`liblua50`/`liblualib50`,
or pass `--with-lua=PREFIX`). Optional: FriBidi (bidi text), Boost.Filesystem (non-POSIX
systems only, `--with-boost=PREFIX`), SMPEG, X11.

There is no `make check` / test suite in this project.

Regenerating `configure` from `configure.in` requires autoconf/automake (`aclocal.m4`,
`ltmain.sh`, etc. are checked in) — normally unnecessary unless `configure.in` changes.

Run the built game with the data package (not included in this repo — see
`legacy/README`) placed at a system dir:

```
./src/game/fillets systemdir=$datadir
```

### Source layout (`legacy/src/`)

Static libraries built bottom-up in this dependency order (see `src/Makefile.am`), each
its own subdir with its own `Makefile.am`:

```
SDL_gfx -> gengine -> effect -> widget -> plan -> option -> state -> level -> menu -> game
```

- `SDL_gfx` — vendored SDL_gfx primitives (C).
- `gengine` — the core "GenGine" engine: agents, messaging, scripting glue, resource
  packs, exceptions, input handling. Everything else depends on it.
- `effect` — drawing/pixel-level effects (Picture, Font, LayeredPicture, disintegrate/
  mirror/reverse/wavy effects on sprites).
- `widget` — simple UI widgets (boxes, buttons, sliders, labels) built on `effect`.
- `plan` — planner/state-machine glue: `GameState`, `StateManager`, key bindings, console.
- `option` — options/help menus built on `widget`+`plan`.
- `state` — top-level game states (demo mode, movie/poster states).
- `level` — the puzzle simulation itself: `Level`, `Field`, `Cube`/`Unit` (the fish and
  movable objects), `Room`, physics/rules (`Rules`, `Landslip`, `FinderAlg`), and the
  Lua binding layer for levels (`level-script.cpp`, `game-script.cpp`).
- `menu` — world map / level-select screens (`WorldMap`, `LevelNode`, `Pedometer`).
- `game` — `main.cpp`, `Application`, `GameAgent`; produces the `fillets` binary.

#### Engine architecture (agents)

The engine (GenGine) is built around **agents** — see the doxygen block at the top of
`src/game/main.cpp` for the canonical description. Key points:

- `AgentPack` owns all agents, calling `init()`/`update()`/`shutdown()` on each, ordered
  by name (see `Name.h`/`Name.cpp`, e.g. `"10script"`, `"20option"`, `"30video"`,
  `"90game"`). Lower-named agents init before higher-named ones.
- Every agent derives from `BaseAgent` (`init()`/`update()`/`shutdown()` template methods
  calling protected `own_init()`/`own_update()`/`own_shutdown()`).
- Rule: an agent may only reference lower-named agents (and itself) from `own_init()`,
  and only higher-named agents (and itself) from `own_shutdown()`.
- The `AGENT(TYPE, NAME)` macro (in `BaseAgent.h`) generates a static
  `TYPE *TYPE::agent()` accessor that looks the singleton instance up from
  `AgentPack`, e.g. `OptionAgent::agent()->getAsInt("screen_width")`.
- Agents: `MessagerAgent` (always present; pub/sub messaging via `BaseListener`/
  `BaseMsg`), `ScriptAgent` (Lua), `OptionAgent` (global options), `VideoAgent`,
  `SoundAgent`, `TimerAgent` (fixed-FPS pacing), `InputAgent`, `SubTitleAgent`,
  `GameAgent` (drives the actual game via `StateManager`).
- `GameAgent` owns a `StateManager`, a stack of `GameState`s
  (`pushState`/`popState`/`changeState`) — this is how the app moves between world map,
  level play, demo mode, menus, etc.

#### Scripting (Lua)

- `Scripter`/`ScriptState`/`ScriptAgent` wrap the embedded Lua 5.0 interpreter.
  C++-to-Lua bindings live in files named `*-script.cpp` (e.g. `level-script.cpp`,
  `game-script.cpp`, `dialog-script.cpp`, `worldmap-script.cpp`, `options-script.cpp`,
  `def-script.cpp`), each exposing a set of `script_*` functions callable from Lua.
- Global/shared Lua sits directly under `legacy/script/`: `init.lua` (startup/locale
  setup), `labels.lua` (UI strings), `worlddesc.lua` (per-level, per-language title/
  description registered via `worldmap_addDesc`), `worldmap.lua`, `worldfame.lua`,
  `level_funcs.lua`, `select_lang.lua`, `select_speech.lua`, and shared helpers/dialog
  fragments under `script/share/`.
- Every level is its own directory `legacy/script/<levelname>/` containing `init.lua`
  (level entry point), `models.lua` (room object/unit layout), `code.lua` (puzzle
  scripted behavior/hints), and localized `dialogs_<lang>.lua` files.
- Level-name directories are mirrored across three trees that must stay in sync:
  `legacy/script/<name>/`, `legacy/images/<name>/`, `legacy/sound/<name>/`. When adding
  or renaming a level, update all three plus its `worldmap_addDesc(...)` entries in
  `worlddesc.lua`.

## Licensing

GPLv2 (`legacy/COPYING`, root `LICENSE`). Game data (images/sound/fonts/levels) and translations
are credited per-contributor in `legacy/AUTHORS`.
