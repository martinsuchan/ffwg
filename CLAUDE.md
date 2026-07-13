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
- `scripts/` — PowerShell helpers (`setup.ps1`, `publish.ps1`, `start.ps1`, `new-doc.ps1`) for the web port.
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
- Briefcase level: fullscreen movie + auto-play tutorial
  (`docs/031-2026-07-11-briefcase-movie-and-auto-play-tutorial.md`): the `briefcase` tutorial
  level was unplayable — pushing the briefcase down (`kufr.faze==8`) called the unbound
  `level_newDemo`, crashing the live Lua engine. Fixed both of the level's special scripted
  sequences. **Phase 1 (movie):** new `web/src/lua/demoScript.ts` (a persistent-wasmoon runner
  for `demo_briefcase.lua` — the port's `DemoMode` equivalent: `game_planAction` slideshow of
  `demo_display` frames + `model_talk` voice + `kufrik` music) and `web/src/scenes/DemoScene.ts`
  (fullscreen 720×555 movie player, **Esc-only** skip, level input fully locked out). `level_newDemo`
  now delegates to a new `HostActions` callback that pauses `LevelScene` and `scene.launch`es the
  demo overlay; a `RESUME` handler restores canvas/input/music. Shared sound helpers extracted to
  `web/src/lua/dialogSound.ts`. **Phase 2 (auto-show):** `level_planShow`/`level_isShowing` are
  real now (a separate `showActions` FIFO = legacy `CommandQueue m_show`), and
  `level_action_move/save/load/restart` drive the engine unattended while `isShowing()` (player
  input + R/Space/P/F1/F2/F3 all locked, only Esc leaves). Save/load use an **in-memory demo
  snapshot**, never a player save slot (docs/026); restart/load reset physics only
  (`resetPhysicsOnly`/`buildAnimators`), keeping the one persistent wasmoon engine + show queue
  alive across show-restarts — the key trick avoiding the docs/008 async-reentrancy hazard, valid
  because a level's own per-round logic is suppressed while showing. `demo_help.lua` is a *runtime*
  `file_include` (the only one in the game — `extractRuntimeIncludes` classifies by indentation):
  wrapped as a callable at bootstrap and run deferred on the trigger (`runPendingIncludes`, outside
  the host callback), so it doesn't queue its whole show at load. A show command that hits an
  impossible move (physics divergence) is caught and gracefully ends the show. The full
  ~200-command walkthrough is only reachable from a mid-solve position (its trigger cell is
  item-occupied at start), so the graceful-abort path is the safety net for the acknowledged
  fragility. Verified: movie E2E (push briefcase → 720×555 movie → Esc → resume), show mechanism
  (trigger/queue/drive/abort/save-load-restart), all-levels sweep 79/80 with no level pre-running
  its show, docs/026 save/load green. **Follow-up fixes (same-day play-testing):** (1) dialog
  audio played ~0.5-1s late (lazy sprite load) and could overlap — added `AudioManager.preload()`
  (+ `levelSoundSpriteDirs`) called from `LevelScene`/`DemoScene` to warm the cache non-blocking,
  and `playDialogVoice()` (via `addAudioSprite`) so a new line cuts the previous; (2) level audio
  kept playing on the world map after Esc — `AudioManager.destroy()`/`reset()` now `stopAll()`
  (music + voice + `scene.sound.stopAll()`); (3) movie frames replaced instead of layering —
  `demo_display` is now an append-only draw log rendered as stacked Image GameObjects (opaque
  covers, transparent layers; Phaser 4 `RenderTexture.draw` rendered black); (4) the Phase-2
  tutorial save is now a **real persistent slot** (`saveTutorialGame`/`loadTutorialGame`,
  `SavedGame.tutorial`) loadable after the tutorial and shown as a distinct amber dot, upserted
  onto one slot, never touching the player's own saves. **Follow-up 2 — dialog audio still lagged
  its subtitle:** instrumented timing found it's Web Audio (steady-state plays in 2-10ms, not
  network — one concatenated `sprite.mp3` per dir), but the first line on a big-sprite level waited
  ~2s while Web Audio decoded the whole sprite (briefcase's is 2.78MB). Fixed by (a) making
  `AudioManager.ensureLoaded` track a **per-key** promise (+ `whenLoaded()`) so a loaded sprite
  plays instantly and a loading one waits only for itself, and (b) **gating the level's dialog
  logic** — `LevelScene` awaits `whenLoaded(levelDialogVoiceDir(level))` (loaded in parallel with
  the Lua bootstrap, `Promise.race`-capped at 4s) before `this.levelScript` goes live, so no dialog
  fires until its audio is decoded (first line in sync with its subtitle; level stays interactive
  during the ~2s warm-up). **Follow-up 3 — the briefcase MOVIE still cropped each voice line's
  tail** (levels were fine): the movie waits per line via `waitForTalker()`/`model_isTalking`, but
  `demoScript`'s `model_talk` sized the line duration in cycles using `ROUND_MS` (130ms, the level
  rate) while `DemoScene` ticks the movie at `DEMO_CYCLE_MS` (100ms) — so it went "done" at ~77% of
  the clip and the next line cut the tail. Fixed by making `DEMO_CYCLE_MS` (in `demoScript.ts`, now
  exported) the single source of truth for both the tick and the duration math. Lesson: a
  real-time→cycles conversion must use *its own* tick interval.
- Death auto-restart + demo restart/load render fix
  (`docs/032-2026-07-12-death-auto-restart-and-demo-restart-render-fix.md`): two play-test
  bugs. (1) **All levels now auto-restart when both fish can no longer move** — the port only
  showed "A fish died - press R" and waited forever, but the original's `LevelCountDown`
  (`legacy/src/level/LevelCountDown.cpp`) counts `getCountForWrong()` = 75 cycles once
  `Room::cannotMove()` (no unit `willMove()` — both dead/wedged, *not* `isSolvable()`), then
  `Level::finishLevel()` calls `action_restart(1)`. Added `WRONG_RESTART_ROUNDS = 75` +
  `wrongCountdown` to `LevelScene`, and a `cannotMove()` branch in `tick()`'s win/lose block
  (after `isSolved()`, so a real win — both fish `isLost` also makes `cannotMove()` true — is
  caught by the solved branch first); it latches a message, counts down, then calls the
  existing `restart()`, on its own counter so it fires even when `gameOver` was already
  latched by the single-death branch. (2) **Briefcase Phase-2 demo restart/load rendered
  stale positions** (items sliding around, room at start "with no fish" until the load
  settled): `tick()` captured `renderModels` *before* `levelScript.tick()`, but an auto-play
  show's `level_action_restart/load` runs *inside* that call and swaps the `GameEngine` +
  rebuilds animators (`resetPhysicsOnly`→`buildAnimators`) at fresh positions — so the
  post-`tick()` sync drove the freshly-built sprites with the stale death-position snapshot.
  Fixed by splitting: `preStepModels` (read before, for the show's `moveXY`→`getLoc()`
  decision) vs. a re-read `renderModels` (after, for the render/sync loop); identical for the
  normal non-show path. Both verified in a real browser.
- Live-Lua unbound host-function freezes
  (`docs/033-2026-07-12-live-lua-unbound-host-fn-freezes.md`): `cabin1` froze a few seconds
  into play. Root cause is a whole class: the live per-round engine (`levelScript.ts`) runs
  each level's real `code.lua`, and any host function it calls that isn't bound throws "nil
  value", which propagates out of `levelScript.tick()` and kills the round-loop timer — but
  only when a delayed/random/position-gated branch first fires (so the level plays fine
  first, and the load-time sweeps missed it). `cabin1`: a ~1%/round gag arms `room.mov`, then
  calls `big:getTouchDir()` every round → the unbound `model_getTouchDir`. Fixed by porting
  legacy `Rules::setTouched`/`getTouchDir`/`m_touchDir` (reset in `occupyNewPos`, set in
  `actionMoveDir`'s blocked branch — windoze-only `touchSpec` still skipped; write-only, so
  physics is byte-identical), adding `RenderModel.touchDir`, and binding `model_getTouchDir`.
  Then swept it generically: collected every host-style call in the runtime Lua (61 names) and
  queried a **live** engine's globals — 9 nil. Two more were the same freeze class and got
  fixed: `game_changeBg` (`corridor`/`rotate`/`steel` runtime background swap — bound for real
  via `LevelScript.takeBgChange()` + `LevelScene.applyBgChange()`, on-demand texture load using
  the **key-specific** `filecomplete-image-<key>` event since the scene loader is shared with
  `AudioManager`; also fixes replay of those levels) and `model_getViewShift` (`pyramid` parallax
  — returns `(0,0)`, consistent with the already-stubbed no-op setter). The other 6 nils are
  confirmed dead in this port (windoze-only `game_checkActive`; `level_save`/`level_load`/
  `model_change_set*`/`model_getExtraParams` are the C++ save/**undo** path the port never
  drives). All verified in a real browser (cabin1 no-freeze + real non-zero `getTouchDir`,
  corridor bg actually swaps, pyramid viewShift, 4-level smoke drive).
- "Both fish are stuck" auto-restart misfiring on a win
  (`docs/034-2026-07-12-stuck-restart-misfires-on-win.md`): solving `cabin1` showed docs/032's
  death message. Cause: `cannotMove()` is true the instant both fish are `isLost` (a fish is
  `isLost` the moment it crosses the exit), but `isSolved()` also requires `isFresh()` (room
  settled) - and on the round the second fish walks out, `fallout()` makes the round non-fresh,
  so there's a one-round window where `cannotMove()` is true but `isSolved()` isn't. docs/032's
  branch fired "stuck" and latched `gameOver`; the next (solved) round was then gated out by
  `!gameOver`, hanging the win. Fixed by gating the stuck branch on `&& this.engine.isFresh()`
  (new `GameEngine.isFresh()`) - aligning it with `isSolved()`'s own freshness requirement, so
  during the escape neither fires and the settled round resolves as a win; a genuine loss still
  settles to fresh and restarts. Also made the solved branch gate on `solvedCountdown < 0` (not
  `!gameOver`) and clear `wrongCountdown`, correcting any stray latch. Verified in a real browser
  (transient repro shows no "stuck" then "Solved"; cabin1 reference solution reaches Solved;
  docs/032 genuine auto-restart still fires).
- The `windoze` level — nested "bonus" child level + extra fish
  (`docs/035-2026-07-12-windoze-level.md`): the last skipped level. A **second pair of fish**
  (`fish_EXTRA-WXYZ`/`fish_extra-wxyz`, "the old couple") lives in a bonus sub-window that must
  be solved before the normal fish can finish, and it's the only level with an **extended replay
  alphabet** (`w/x/y/z`/`W/X/Y/Z`). Ported the physics primitives faithfully: extra fish in
  `ModelFactory` (no dedicated keys - driven only when active via arrows, symbols parsed from the
  kind string), `output_left`/`Rules.touchSpec` + `Cube` out-plug fields (windoze's `spuntik`;
  touchSpec fires only when blocked by a lone `output_*` cube, so no other level is affected),
  `busy` (already existed) wired to Lua, `game_checkActive` → `Room.checkActive`, and
  `game_setFastFalling` → a fast-settle loop reusing `fastForwardSettle`. The one new coupling:
  a small opt-in `EngineControl` bridge (`setBusy`/`checkActive`/`setFastFalling`) passed to
  `createLevelScript` (from `LevelScene`/`ReplayScene`, closing over `engine`), since windoze is
  the sole level whose `code.lua` drives physics (docs/014 otherwise keeps live Lua physics-free).
  Rendering needed no changes (extra fish use the generic fish-animator path; the anim-less
  invisible `spuntik` is skipped by `buildAnimators`). **One real gotcha**: `Unit.driveOrder` (the
  recorded-symbol path for replay/validation/demo) was gated on `canDrive()` (incl. `busy`), so
  the watchable replay stalled mid-way when the live Lua toggled `busy` a round off from recording
  - changed it to gate on `willMove()` instead (a recorded symbol is an already-decided,
  deterministic move; `busy` only gates interactive driving, and is windoze-only so nothing else
  changes). Verified: reference solution validates headlessly (525 moves, all 4 fish out); all 81
  solutions now **80 SOLVED** (was 79; only `redhat` fails, having no level content); interactive
  control-swap + drive-extra-fish + save/load (F2/F3) + full replay to Solved, all in a real
  browser.
- viking1 musician band silent (`docs/036-2026-07-12-viking1-musician-songs.md`): when the music
  stops and the vikings should sing/whistle, no sound played. The band's notes come from
  `model_talk(actor, "d1-z-p1"/"d1-z-b1"/"d1-z-v1"...)` (empty-subtitle *sound-only* dialogs), but
  those are defined **only in `dialogs_en.lua`** (not `dialogs_cs.lua`) and their `.ogg`s live only
  in `sound/viking1/en/`. The original registers every dialog under its first-seen (DEFAULT_LANG)
  definition, so these language-agnostic instrument clips always come from English - but this port
  bypasses `dialogLoad()` and loaded only the `cs` file (docs/015/018), so `d1-z-*` were never
  registered and `model_talk` no-op'd. Fixed by reproducing the DEFAULT_LANG fallback: after the
  localized dialog file, also run the level's `dialogs_en.lua` with `currentSoundPrefix =
  sound/<level>/en/` - `level_dialog.lua`'s `dialogId` no-ops for already-primed dialogs (and the
  localized file's `dialogId` uses the same English default subtitle, so no override/warnings), so
  only the en-only clips get added, with en sounds; plus added `<level>/en` to `fetchSoundDurations`
  so the empty-subtitle clips get a real duration (else `minTime` would be 0 → never play).
  General (all 81 levels), faithful, non-regressing (verified: normal cs dialogs unchanged, 81/81
  `createLevelScript` clean, `viking1/en` sprite actually plays). **Known limitation**: single
  `activeDialog` slot + `playDialogVoice` cuts the previous, so only one instrument is audible at a
  time (whistle dominates) - a true simultaneous trio needs multi-channel dialog audio (deferred).
- Full-featured subtitle system (`docs/037-2026-07-12-subtitle-system.md`): replaced docs/015's
  placeholder (one white line at a time) with the original's real behavior - each speaker's own
  **color**, **stacking** subtitles (scroll up), each **self-dismissed** on its own timer. The
  original decouples `SubTitleAgent` (visual subtitles) from `DialogStack` (running dialogs/sound);
  colors are per-font via `dialog_addFont(name,r,g,b)` (`level_fonts.lua`'s `loadFonts()`), each
  `dialogId` names a font. Ported: `dialog_addFont` (was a no-op) now stores `state.fontColors`;
  `model_talk` still sets the single `activeDialog` (talking-state/sound/plan-gating, unchanged)
  but ALSO pushes `{text,color}` to `state.pendingSubtitles` (new `takePendingSubtitles()` drain,
  empty "sound-only" dialogs add nothing); new `web/src/scenes/SubtitleStack.ts` (port of
  `SubTitleAgent`/`Title`) holds colored outlined `Phaser.Text` lines, newest at the bottom,
  gliding up, each living `utf8len*TIME_PER_CHAR + TIME_MIN` ticks on its own ~100ms timer (not the
  round loop). `LevelScene` drains into it, clears on restart, destroys on shutdown; the post-solve
  countdown uses `subtitleStack.hasVisible()`. Verified: 27 colors registered, viking1 intro shows
  orange small-fish + cyan big-fish stacked lines (screenshot), self-dismissal, no Text leak across
  restarts, 81/81 `createLevelScript` clean. **Deferred**: concurrent dialog *sound* (multi-actor
  `DialogStack`, docs/036) and the original's wavy-text TODO.
- World-map corner buttons (`docs/038-2026-07-12-world-map-corner-buttons.md`): the 4 inert map
  corners (docs/027) now work - TL **Intro**, TR **Exit**, BL **Credits**, BR **Options** (legacy
  `WorldMap.cpp`), each with a hover highlight. Hover uses the original's mask system: `map_mask.png`
  is read once via an offscreen canvas (`getImageData`); the 4 image-corner colors define the
  buttons, and hovering reveals the prelit `map_lower.png` masked to the hovered button's *exact
  mask shape* (a per-corner canvas texture, so e.g. only the "OPTIONS" glyphs light, not a
  rectangle - see docs/039). **Intro** (new
  `IntroScene`) plays the real intro movie - `intro.mpg` transcoded once to `assets/video/intro.mp4`
  (browsers can't play MPEG-1) via a Phaser `Video`; **Credits** (new `CreditsScene`) scrolls the
  game's own `credits.png`; **Exit** = `window.close()` (does nothing if the browser blocks it);
  **Options** (new
  `OptionsOverlay`, HelpOverlay shape) has language (cs/nl), music+sound volume sliders, and a
  subtitles toggle (no speech selector per the user). New `web/src/storage/settingsStorage.ts`
  (`ffwg:settings`: lang/musicVolume/soundVolume/subtitles) is wired live: `AudioManager` volume
  reads the setting (+ `refreshMusicVolume` for the playing track); `LevelScene` gates the docs/037
  subtitle stack on `settings.subtitles`; `levelScript.ts`'s `DIALOG_LANG` const became
  `getDialogLang()` (setting-driven, cs/nl for both text+voice, snapshotted per level load;
  `demoScript.ts` reads it too). Verified in a real browser (all 4 corners hover+dispatch, intro
  video plays, options persist, language switches voice dir, subtitles toggle gates the stack,
  81/81 sweep clean).
- World-map mask fidelity, edges/hover, real pedometer
  (`docs/039-2026-07-12-world-map-mask-fidelity-and-pedometer.md`): follow-up polish, all
  cross-checked against `legacy/src/menu/`. (1) **Lossless masks** - `map_mask.webp`/
  `pedometer_mask.webp` are read pixel-by-pixel for button hit-testing + prelit-shape building;
  lossy WebP (the bulk `convert-images.ps1` default) smears the flat fills and breaks exact-color
  matching, so both were reconverted `ffmpeg -c:v libwebp -lossless 1` (map mask now decodes to
  exactly 5 unique colors). (2) **Shared masked-texture helpers** (`readTexturePixels`/`packRgb`/
  `buildMaskedTexture` in `sceneUtils.ts`) - docs/038's corner-reveal logic extracted so the
  pedometer reuses it; `WorldMapScene` refactored onto them. (3) **Thicker edges** - legacy
  `NodeDrawer::drawEdge` is solid yellow `0xdea500` ≈ 3px (5 overlaid aalines); port now
  `lineStyle(3, 0xdea500, 1)` (was a 1px dim gold line). (4) **Dot-sized hover highlight** -
  `NodeDrawer::drawSelect` tints the hovered dot with a translucent yellow disc *the dot's size*
  (`radius = max(dotW,dotH)/2 + 1`) drawn *over* it; port drew a 15px halo *behind* it - now
  radius from the `node-solved` texture (=11) at depth 4. (5) **Real Pedometer** (`PedometerUI.ts`
  rewrite) - `pedometer.png` rack art with `pedometer_mask.png` button regions (sampled at the
  original's panel-relative Run 86,100 / Replay 128,100 / Cancel 170,100) and `pedometer_lower.png`
  prelit-on-hover (per-button `buildMaskedTexture`), replacing text buttons; the step count is
  drawn from `numbers.png` (spritesheet of digits 9..0, each 19x24, at absolute 275,177, digit `d`
  -> frame `9-d`) replacing the text counter. Scene-level corner handlers now guard on
  `isModalOpen()` (pedometer/options showing). Verified in a real browser (mask 5 colors, ring
  radius 11 depth 4, pedometer 3 masked buttons + 5 digits reading `00012` for 12 moves + Run
  hover lights `pedo-run`; screenshot).
- Pedometer clean-map presentation + browser Back
  (`docs/040-2026-07-12-pedometer-clean-map-and-browser-back.md`): three main-screen fixes. (1)
  **Pedometer hides the node graph, no dark overlay** - the original Pedometer is a separate
  state whose `prepareBg()` draws only `map.png` + level name + solver text (no `NodeDrawer::
  drawPath`, so no dots/edges, no tint). This port renders it as a world-map overlay, so it now
  reproduces that: backdrop rectangle is `alpha 0` (still interactive for click-absorb + button
  hit-testing), and `WorldMapScene.setNodesVisible(false)` hides all dot images + the edges
  `Graphics` while shown (restored on close). Cancel routes through a new `onCancel` callback (to
  restore the dots); **Esc** also closes it. (2) **Real localized best-solution text** - the info
  line is now the original `SolverDrawer`'s `solver_better`/`solver_equals`/`solver_worse` label
  from `script/labels.lua` (chosen by `LevelStatus::compareToBest()` logic, `%1`/`%2` = best move
  count + author), no background box, centered at screen `h-150`. `worldMapLoader.ts` now also
  runs `labels.lua` (captures those 3 labels per language) -> `WorldMapData.solverLabels`;
  `PedometerUI` picks the `settings.lang` row (cs/nl, en fallback). (3) **Browser Back returns to
  the world map** - new `web/src/navigation.ts`: `pushSubView()` pushes a history entry on each
  world-map sub-view launch (level/replay/intro/credits) and a `popstate` listener
  (`initHistoryNav` in `main.ts`) routes Back to the map instead of unloading the SPA, keying off
  active scenes (robust to history drift). Verified in a real browser (backdrop alpha 0, dots+edges
  hidden, real cs solver text no-bg, Cancel restores; Back from a level returns to the map, page
  still loaded; screenshot).
- Build + publish scripts, production Lua packaging
  (`docs/041-2026-07-13-build-and-publish-scripts.md`): two user-facing PowerShell scripts at the
  repo root so a fresh clone works with no AI tooling (Windows 11 only for now). **Enabling code
  change**: `LEGACY_ROOT` (`web/src/lua/levelLoader.ts`) now branches on `import.meta.env.DEV` - dev
  keeps the `/@fs/` literal, **prod** resolves to `<site>/legacy/` (via `import.meta.env.BASE_URL`),
  closing the docs/005/006 "production Lua packaging still open" gap (everything fetched is under
  `legacy/script/**` + `legacy/solution/**`). Added `web/src/vite-env.d.ts`. **`build.ps1`**:
  checks tools (`node`/`npm`/`ffmpeg`/`ffprobe`, prints `winget install` hints for missing ones via
  new `scripts/lib/common.ps1`), `npm install`, converts all assets (`convert-assets.ps1` + the
  `intro.mpg`->`intro.mp4` step + both manifests), then runs the dev server; flags `-NoRun`/
  `-SkipAssets`/`-Force`/`-Install`/`-Port`. **`publish.ps1`**: runs `build.ps1 -NoRun`, `npm run
  build`, then assembles a self-contained `publish/` (built site + `legacy/script`+`solution` under
  `/legacy/` + `staticwebapp.config.json` + `web.config` + `README.txt`) - a pure static site,
  ~176MB. Verified by serving `publish/` and driving the **production** build in a real browser:
  world map + a level both load from `/legacy/`, no errors.
- Texture atlas migration (`docs/042-2026-07-13-texture-atlas-migration.md`): finished the
  atlas goal first scoped in docs/003/004 - model sprites now render from Phaser **texture
  atlases** (one `load.atlas` per level dir + one per shared fish variant) instead of a
  `load.image()` per frame, and the individual per-sprite `.webp` files are no longer shipped
  for atlased dirs (docs/004's packer existed but was never wired in - only a stray
  `airplane/atlas.*` test artifact remained, double-shipping that one level). Two atlas
  families packed by `convert-assets.ps1`: per-level `images/<level>/**` (items + background)
  and shared `images/fishes/{small,big,ex_small,ex_big}/**` (loaded every level, one atlas key
  each so it caches across levels). New `web/src/scenes/atlas.ts` maps a Lua picture path to
  `{atlasKey, frame}` where the frame name is the path relative to the atlas dir minus ext -
  the same string `build-atlas.mjs` writes; `ModelAnimator` resolves + `setTexture(atlasKey,
  frame)` from it (`resolveTextureKey`->`resolveFrame`, `preloadModelFrames`->`collectAtlasKeys`
  + `preloadAtlases`), and `applyBgChange` (docs/033) simplified to a synchronous atlas frame
  swap since changeBg targets are already in the level atlas. `build-atlas.mjs` now recurses
  subdirs + names frames by relative path (for the nested fish tree; also guards `sharp.trim()`
  on the extra fish's sub-3×3 placeholder heads). Only `menu/` (world-map UI + lossless masks)
  and `demo_briefcase/` (movie frames) stay individual - both load through their own pathways
  and fail single-page packing. Result: **3732 image files -> 510**, `assets/images` 24MB->18MB,
  a level fetches one atlas instead of up to ~188 requests. Verified: tsc + vite build clean;
  every atlas's frame set proven to exactly equal its source-PNG set (airtight resolution proof
  since the runtime derives frame names by the same rule); atlas URLs served 200. **Not yet
  done**: interactive real-browser drive (no browser-automation tool this session) - see the
  doc's "Open for next time". Also fixed a **separate pre-existing docs/041 bug** found while
  verifying: the dev world map failed with a Lua `unexpected symbol near '<'` because docs/041's
  `/* @vite-ignore */` on `LEGACY_ROOT`'s dev branch disabled Vite's `/@fs/` rewrite (fetched
  `.lua` as the SPA `index.html`); replaced the whole mechanism with a dev-only `serveLegacyDev`
  middleware (`vite.config.ts`) serving repo-root `legacy/` at `/legacy/`, so dev+prod share one
  `/legacy/` path and `LEGACY_ROOT` needs no `import.meta.url`/`@vite-ignore` (prod JS bundle
  byte-identical).
- Polyphonic audio + Web Audio buffer engine
  (`docs/043-2026-07-13-polyphonic-audio-and-web-audio-engine.md`): fixed two audio defects by
  mirroring the original's in-memory multi-channel model. **(1) No simultaneous playback** - the
  port collapsed legacy's `DialogStack` (FIFO lists of concurrent `PlannedDialog`s, each on its
  own mixer channel; `m_activeDialog` only the blocking one) into a single `activeDialog` slot
  that cut the previous voice, so viking1's whistle+bass+voice trio played one note at a time
  (docs/036). **(2) 1-2s latency** - each dir's concatenated `sprite.mp3` was decoded whole by
  Phaser on first use (can't partial-decode an MP3); un-warmed dirs hit cold mid-game decodes.
  New `web/src/scenes/audioEngine.ts` (Web Audio, shares Phaser's AudioContext/`destination`):
  decode each dir's sprite **once** into one `AudioBuffer` (module-global cache = decode-once-
  per-session across scenes), play regions by offset via independent `AudioBufferSourceNode`s →
  instant + unlimited concurrency; per-actor grouping backs `killSound`. `AudioManager` routes
  voices+effects through it (`preloadAll`/`playDialogVoice(...,actor,loop)`/`playSoundEffect`
  overlap); music stays on Phaser. `levelScript.ts` ports `DialogStack`: `activeDialog` →
  `talkers[]` + `activeBlocking` + `killedActors`; `model_talk` honors its real 5th `dialogFlag`
  arg (`planDialog`=blocking, `object:talk`=non-blocking band) and **pushes** talkers;
  `isModelTalking` scans all talkers; new `takePendingVoices`/`takeKilledActors` drains replace
  the `getActiveDialogId`/`lastDialogId` single-voice diff. `LevelScene` pre-decodes the level's
  voice + shared pools + `<level>/en` fallback (viking instruments) at load and plays each round's
  talkers concurrently; `DemoScene` movie stays sequential (one group, cut-previous). Verified:
  tsc + vite build clean, modules transform + app boots, sprite.json/`viking1/en` band regions
  present. **Not yet done**: interactive real-browser drive (no automation tool this session) -
  viking1 polyphony, first-line sync, overlap, teardown; see doc's "Open for next time".

Commands (from repo root):

```
scripts\setup.ps1              # ONE-COMMAND local build+run: checks tools, converts assets, runs (docs/041)
scripts\setup.ps1 -SkipAssets  # fast restart once assets are already built
scripts\publish.ps1            # produce a deployable static-site 'publish/' folder (docs/041)
scripts\start.ps1              # just (re)start the Vite dev server (assumes assets built)
scripts\build.ps1              # low-level tsc -b + vite build only (add -Preview to serve)
scripts\new-doc.ps1 "<slug>"   # scaffold the next numbered docs/ entry
```

Equivalent by hand, from `web/`: `npm install`, `npm run dev`, `npm run build`,
`npm run preview`. Asset conversion (needed before a hand build works) is what `setup.ps1`
orchestrates via `scripts/convert-assets.ps1` + the manifest scripts. No test suite yet.

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
