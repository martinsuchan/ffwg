# Project log

Running record of the browser-port work, one file per notable milestone.

## Convention

`NNN-YYYY-MM-DD-short-slug.md`, numbered sequentially in the order the work
happened. The number gives a stable read order (multiple entries can share a
date); the date makes it easy to see what landed when.

Add a new entry whenever a bigger feature/decision lands (not for every small
edit) — e.g. "Lua runtime spike", "ported Field/Rules physics", "level loader
for one world". Keep entries short: what changed, why, what's still open.

Also add the entry's one-line pointer to the index below (`scripts/new-doc.ps1` does this
automatically). **Never** summarize milestones into `CLAUDE.md` — it loads into every Claude
Code session, so it holds only stable architecture and invariants. The older per-milestone
summaries that used to live there are preserved verbatim in [HISTORY.md](HISTORY.md).

Other standing files: [BACKLOG.md](BACKLOG.md) (open items + FF NG feature comparison),
[HISTORY.md](HISTORY.md) (pre-2026-11-15 CLAUDE.md milestone summaries).

## Index

- [001](001-2026-07-06-legacy-review-and-phaser-spike.md) — Legacy review and Phaser hello-world spike
- [002](002-2026-07-06-wasmoon-lua-poc.md) — Wasmoon Lua POC
- [003](003-2026-07-06-asset-conversion-pipeline.md) — Asset Conversion Pipeline
- [004](004-2026-07-06-texture-atlas-packing.md) — Texture Atlas Packing
- [005](005-2026-07-06-lua-5-4-compatibility-check.md) — Lua 5.4 Compatibility Check
- [006](006-2026-07-07-level-models-rendering-poc.md) — Level Models Rendering POC
- [007](007-2026-07-07-game-logic-port-and-playable-poc.md) — Game Logic Port And Playable POC
- [008](008-2026-07-07-final-level-goals-and-file-include.md) — Final Level Goals And File Include
- [009](009-2026-07-07-fish-animation-system.md) — Fish Animation System
- [010](010-2026-07-07-position-slide-timing-fix.md) — Position Slide Timing Fix
- [011](011-2026-07-07-corpse-disintegration-and-removal.md) — Corpse Disintegration and Removal
- [012](012-2026-07-07-restart-sprite-leak-fix.md) — Restart Sprite Leak Fix
- [013](013-2026-07-07-death-reaction-visual-timing.md) — Death Reaction Visual Timing
- [014](014-2026-07-08-item-animation-via-live-lua.md) — Item Animation Via Live Lua
- [015](015-2026-07-08-dialog-text-display.md) — Dialog Text Display
- [016](016-2026-07-08-active-fish-control-scheme.md) — Active-Fish Control Scheme
- [017](017-2026-07-08-mouse-controls-and-swim-speedup.md) — Mouse Controls and Swim Speedup
- [018](018-2026-07-09-sound-and-music.md) — Sound and Music
- [019](019-2026-07-09-keyboard-input-reliability-fix.md) — Keyboard Input Reliability Fix
- [020](020-2026-07-09-immediate-slide-start-and-original-comparison.md) — Immediate Slide Start and Original Comparison
- [021](021-2026-07-09-move-recording.md) — Move Recording
- [022](022-2026-07-09-headless-solution-validator.md) — Headless Solution Validator
- [023](023-2026-07-09-initial-facing-direction-bug.md) — Initial Facing Direction Bug
- [024](024-2026-07-09-closing-the-level-load-gap.md) — Closing the Level-Load Gap
- [025](025-2026-07-09-replay-mode.md) — Replay Mode
- [026](026-2026-07-10-solution-and-save-persistence.md) — Solution & Save Persistence
- [027](027-2026-07-10-world-map.md) — World Map
- [028](028-2026-07-10-world-map-smoke-test-fixes.md) — World Map Smoke-Test Fixes
- [029](029-2026-07-10-fish-talking-animation-and-canvas-stretching.md) — Fish Talking Animation & Canvas Stretching
- [030](030-2026-07-11-post-solve-return-help-popup-and-window-title.md) — Post-solve auto-return, F1 help popup, window title + favicon
- [031](031-2026-07-11-briefcase-movie-and-auto-play-tutorial.md) — Briefcase level: fullscreen movie + auto-play tutorial
- [032](032-2026-07-12-death-auto-restart-and-demo-restart-render-fix.md) — Death auto-restart + demo restart/load render fix
- [033](033-2026-07-12-live-lua-unbound-host-fn-freezes.md) — Live-Lua unbound host-function freezes (cabin1 + a whole class)
- [034](034-2026-07-12-stuck-restart-misfires-on-win.md) — "Both fish are stuck" auto-restart misfiring on a win
- [035](035-2026-07-12-windoze-level.md) — The `windoze` level: nested "bonus" child level + extra fish
- [036](036-2026-07-12-viking1-musician-songs.md) — viking1's musician band: no sound when the vikings sing/whistle
- [037](037-2026-07-12-subtitle-system.md) — Full-featured subtitle system (per-speaker colors, stacking, dismissal)
- [038](038-2026-07-12-world-map-corner-buttons.md) — World-map corner buttons: hover, Intro, Credits, Exit, Options
- [039](039-2026-07-12-world-map-mask-fidelity-and-pedometer.md) — World-map mask fidelity, thicker edges, dot-sized hover, real pedometer
- [040](040-2026-07-12-pedometer-clean-map-and-browser-back.md) — Pedometer clean-map presentation + browser Back to world map
- [041](041-2026-07-13-build-and-publish-scripts.md) — One-command build.ps1 + publish.ps1 (and production Lua packaging)
- [042](042-2026-07-13-texture-atlas-migration.md) — Texture atlas migration (atlas-only model rendering)
- [043](043-2026-07-13-polyphonic-audio-and-web-audio-engine.md) — Polyphonic audio + Web Audio buffer engine
- [044](044-2026-07-13-e2e-regression-suite.md) — Committed e2e regression suite
- [045](045-2026-07-13-standard-vs-sandbox-modes.md) — Standard game at `/`, sandbox at `/sandbox`
- [046](046-2026-07-13-phase-locked-animation-rework.md) — Phase-locked animation rework (shared clock, matching the original)
- [047](047-2026-07-13-in-level-step-counter.md) — In-level step counter
- [048](048-2026-07-14-atlas-trim-opaque-crop-fix.md) — Atlas trim cropping opaque images
- [049](049-2026-07-14-fast-falling-speed-fix.md) — Falling items too slow (phase-lock refinement)
- [050](050-2026-07-15-final-level-posters-and-ending.md) — Final-level posters + the ending level
- [051](051-2026-07-15-gods-battleships-easter-egg.md) — gods' battleships easter egg (view shift + draw effects)
- [052](052-2026-07-15-dialog-format-args.md) — Dialog format args (`name@arg`) - gods' spoken coordinates
- [053](053-2026-07-15-replay-no-accel-instant-falls.md) — Replay: no fish acceleration, instant falls
- [054](054-2026-07-15-lua-host-gaps-part1.md) — Lua host-API gaps, part 1 (depth, model_equals, isSolved, options)
- [055](055-2026-07-15-rope-decor-and-screen-shift.md) — Rope decor + screen shift (Lua host gaps, part 2)
- [056](056-2026-07-15-room-waves.md) — game_setRoomWaves: the underwater background ripple
- [057](057-2026-07-15-wavy-bg-edge-fix.md) — Wavy background: the edge strip is padded, not wrapped
- [058](058-2026-07-16-small-fixes-batch.md) — Small fixes: parrot flash, rope in replay, ending node, step counter
- [059](059-2026-07-16-prewarm-live-engine-during-loading.md) — Prewarm the live Lua engine during the world-map "Loading" phase
- [060](060-2026-07-16-localization-english-audio-fallback.md) — Localization: per-line English audio fallback (+ a decode race, + a Dutch parse crash)
- [061](061-2026-07-16-ending-flow-playtime-talk-pose.md) — Ending flow (pedometer), play-time counter, and the talking body pose
- [062](062-2026-07-16-demo-briefcase-lossless.md) — Lossless WebP for the flat sprite art (demo_briefcase movie + fish)
- [063](063-2026-07-16-fractional-anim-phase-freeze.md) — Fractional animation phase freeze (electromagnet, rotate)
- [064](064-2026-07-17-game-size-crisp-text-fullscreen.md) — Game size setting, crisp text, and F11 fullscreen
- [065](065-2026-07-17-fullscreen-fixes.md) — Fullscreen fixes: black screen, aspect ratio, crisp text, Esc-exit
- [066](066-2026-07-17-fullscreen-native-f11-esc-stays.md) — Fullscreen: native F11, Esc stays fullscreen
- [067](067-2026-07-17-keybinding-parity-batch.md) — Keybinding parity batch (Backspace restart, F5/F6/F10, Space demo-skip)
- [068](068-2026-07-17-worldmap-tab-enter-navigation.md) — World-map Tab/Enter keyboard navigation
- [069](069-2026-07-17-options-worldmap-and-narrow-room-fit.md) — F10 options on the world map + fit modals to narrow rooms
- [070](070-2026-07-17-held-key-release-extra-cell-fix.md) — Held-key release: one phantom extra cell fixed
- [071](071-2026-07-17-mouse-clicks-outside-level-ignored.md) — Mouse clicks outside the level no longer drive a fish
- [072](072-2026-07-17-progress-backup-restore.md) — Backup / restore game progress (JSON file)
- [073](073-2026-07-18-ui-localization-and-notification-cleanup.md) — UI localization + happy-path notification cleanup
- [074](074-2026-07-18-worldmap-progressive-branch-reveal.md) — World-map progressive branch reveal
- [075](075-2026-07-18-hide-language-en-sprite-gate-loading-screen.md) — Hide language switch, gate en-sprite fetches, world-map loading screen
- [076](076-2026-07-18-backlog-refresh-ffng-comparison-readme.md) — Backlog refresh, FF NG feature comparison, README rewrite
- [077](077-2026-11-15-claude-md-slimming.md) — Slim CLAUDE.md: nested per-tree files, history moved to docs
- [078](078-2026-11-15-replay-video-export.md) — Replay video export (MP4)
- [079](079-2026-11-16-replay-still-background-vector-icons.md) — Replay: still background, vector control icons
- [080](080-2026-11-16-export-aac-trial-encode-wasm-fallback.md) — Video export: AAC trial encode + WASM AAC fallback
- [081](081-2026-11-16-exit-swim-out-fidelity.md) — Leaving the room: swim out, do not fade at the edge
- [082](082-2026-11-17-review-fixes-and-load-speedups.md) — Code review fixes: plan interrupt, local Lua WASM, parallel loading, mono audio
- [083](083-2026-11-17-redhat-level-import.md) — The redhat level: imported from upstream data, secret branch off emulator
- [084](084-2026-11-17-mirror-and-zx-draw-effects.md) — The mirror and zx draw effects; lossless WebP policy
