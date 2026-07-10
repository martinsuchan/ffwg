# 028 - World Map Smoke-Test Fixes

2026-07-10

## Context

The user smoke-tested docs/027's World Map and found three problems: no
music on the map itself, no dialog audio in the first level they tried
(only music), and a serious level-loading bug - closing one level and
opening a second showed the *first* level's background with the *second*
level's item layout. All three turned out to be real, and two of them
(dialog audio, level loading) are specifically *because* the World Map
now makes every level reachable - this port had only ever been exercised
against `airplane`/`viking1` before, so gaps invisible for those two
levels were invisible everywhere.

## Bug 1: Level-loading texture collision (the serious one)

**Root cause**: `ModelAnimator.textureKey()`/`preloadModelFrames()` and
`LevelScene`/`ReplayScene`'s background image both used level-*agnostic*
Phaser texture keys - a plain `"bg"`, and `model-${index}-${animName}-
${side}-${phase}` (no level identifier at all). This was invisible before
docs/027 because `LevelScene` used to be constructed once, for one
hardcoded level, for the whole session - the key never needed to
distinguish between levels because there was only ever one. Once the map
made `LevelScene` dynamically re-launchable for *different* levels
(docs/027's `init(data)` refactor), two different levels' model 0 (say)
collided on the exact same texture key. Phaser's loader treats a
`load.image()` call against an already-registered key as a no-op (keeps
whichever image loaded first) - so the *second* level's sprites kept
showing the *first* level's textures, even though the physics/position
data (from the correctly-loaded fresh `LevelData`) was genuinely the new
level's own layout. Exactly matches what the user saw.

**Fix**: every texture key is now prefixed with `levelName` -
`textureKey(levelName, index, animName, side, phase)`, `${levelName}-bg`.
Threaded through `ModelAnimator` (`textureKey`/`resolveTextureKey`/
`preloadModelFrames`, plus a new `levelName` constructor field used by its
two internal `resolveTextureKey` call sites), `sceneUtils.ts`'s
`resolveInitialTextureKey`, and both `LevelScene`/`ReplayScene`'s call
sites. Verified directly: loaded `start`, then `briefcase` - background
texture keys (`start-bg` vs `briefcase-bg`) and the actual underlying
image data now differ, and model 0's live texture key is provably scoped
to whichever level is currently active.

## Bug 2: No dialog audio beyond `airplane`/`viking1`

Two separate, compounding causes - both are "this was invisible when only
two levels were ever tested" gaps, not new bugs introduced by docs/027:

1. **The audio manifest was never regenerated after conversion.**
   `docs/018`'s original small-batch conversion had a matching manifest
   build step, but docs/027's asset-conversion run (needed so images
   render for all ~80 levels, not just 2) never re-ran
   `scripts/build-audio-manifest.ps1` - so `web/public/lua/audio-
   manifest.json` still only listed `airplane`/`viking1`/`share` sound
   paths. Since `dialog_addDialog`'s sound-path resolution is gated by
   `audioManifest.has(...)` (unlike music, which never depended on the
   manifest at all - it just points Phaser's loader straight at
   `/assets/music/<track>.mp3` and lets a 404 fail silently), every other
   level's dialog lines silently resolved to "no voice," while music kept
   working fine. Exactly matches "I don't hear dialogs, only music."
   Fixed by re-running the manifest builder (1458 → 3702 sound paths, 3 →
   82 level directories covered).

2. **The asset-conversion batch itself had silently stopped partway
   through**, only converting sound for the first ~30 levels
   alphabetically (through `elevator1`) before crashing. Root cause:
   `legacy/sound/elevator1/nl/zd1-m-cesta.ogg` (and, found on the retry,
   `gems/nl/zav-v-sto.ogg`) are essentially-zero-length placeholder clips
   (~3.5ms) that normalize down to an empty WAV with no measurable
   duration - `ffprobe` reports the literal string `"N/A"` instead of a
   number, and `scripts/build-audio-sprite.ps1`'s `[double]$durText` cast
   threw, aborting the *entire* batch (`$ErrorActionPreference = "Stop"`,
   no per-file recovery). This had never been hit before because no prior
   conversion run had ever attempted the full, unfiltered legacy corpus.
   Fixed with a defensive check: an unparseable duration is now treated as
   a 0-length clip (a degenerate but harmless sprite region) with a
   warning, instead of aborting the whole run. Re-ran the full batch to
   completion (all 82 level directories, `warcraft`→`wc`→`windoze`→
   `wreck` at the very end of the alphabet, confirming it now runs
   through to completion) and rebuilt the manifest again (3702 paths, 82
   levels).

Verified directly: `briefcase`'s dialog registry now resolves real sound
paths for all 130 registered dialogs (was 0), and `AudioManager` actively
loads the `briefcase/cs` sprite during play.

## Bug 3 (found during verification, not reported): missing live-engine
## Lua host bindings for every level except `airplane`/`viking1`

While verifying bug 2's fix, `briefcase`'s live script still failed to
load - a *different* problem: `level_isShowing` wasn't bound in
`web/src/lua/levelScript.ts` at all (`attempt to call a nil value`). This
is the same shape of gap `docs/024` already found and fixed once for the
*static* loader (`levelLoader.ts`) - a host binding that only gets
exercised by a level's real per-round `script_update()` closures, which
the original docs/014 spike never triggered because it was only ever
checked against `airplane`/`viking1`.

Rather than fix these one at a time as they surfaced per-level, wrote a
one-off sweep (`createLevelScript()` + 5 ticks, for all 80 levels,
via dynamic `import()` inside the page - no Phaser/UI involved at all) to
find every remaining gap in one pass. Found and fixed 7 total missing
bindings in `levelScript.ts`:

- **`options_getParam`**: already a real, working stub in `levelLoader.ts`
  (returns `""`, docs/024) - just never carried over to this file. Copied
  verbatim.
- **`game_addDecor` / `level_planShow`**: same story - real no-op stubs
  already existed in `levelLoader.ts`, just missing here. Copied.
- **`level_isShowing`**: not previously stubbed anywhere. Traced to
  `Level::isShowing()` (`legacy/src/level/Level.cpp`) - true only while a
  `level_planShow()` action is queued. Since `level_planShow` is already a
  no-op that never queues anything, `() => false` is exactly consistent,
  not just a guess.
- **`model_isAtBorder`**: real gameplay state (`Rules::isAtBorder()`),
  used by several "final level" goal-reassignment scripts
  (`atlantis`/`gods`/`map`/`propulsion`/`turtle`/`barrel`/`floppy`). Traced
  to confirm this port *already* has the real computation ported and
  working internally (`Rules.ts`'s `isAtBorder()`, since docs/007) - it
  just wasn't exposed to Lua. Added `isAtBorder` to the `RenderModel`
  interface (`GameEngine.getRenderModels()`) and bound it directly - a
  real implementation, not an approximation.
- **`model_equals`**: real pathfinding-helper logic (`isWater()`/
  `isFreePlace()`-style checks in the shared `prog_compatible.lua`/
  `prog_finder.lua`, used by a few "programmed"/scripted-unit levels -
  `stairs`/`rush`/`creatures`/`cancan`). The original compares against a
  real `Field` occupancy grid this engine deliberately doesn't have
  access to (`levelScript.ts` stays physics-decoupled by design, docs/014).
  Implemented as a documented approximation using each model's single
  anchor position from the existing `RenderModel[]` snapshot, rather than
  the original's full multi-cell shape mask - correct for the common
  case, doesn't distinguish "empty water" from "room border" at the exact
  edge. Affects only those 4 non-final levels' scripted-unit pathfinding,
  not save-breaking or crash-causing.
- **`model_setViewShift` / `game_setScreenShift`**: confirmed purely
  cosmetic (camera/render-offset effects, `View::getScreenPos()` only, no
  physics/goal state) before stubbing as no-ops - matches this file's
  existing `model_setBusy`/`model_setEffect` precedent.
- **`game_setFastFalling`**: a real physics-pacing effect, but its only
  caller (`windoze/code.lua`) is already unsupported for an unrelated
  reason (`fish_extra`, docs/022) - stubbed anyway so the live script
  itself doesn't *also* fail to load, though `windoze` remains unplayable
  regardless (confirmed: it still fails later, on `model_getTouchDir`,
  another `fish_extra`-only binding not worth chasing).

Full sweep re-run after fixing all of the above: **79/80 levels load
their live script cleanly** (only `windoze` remains, explicitly
out-of-scope). Also re-verified `windoze`'s own separate, already-fixed
docs/027 failure mode (unsupported model *kind*, thrown during
`GameEngine` construction, not Lua loading) is unaffected by any of this.

## Bug 4 (found and fixed as part of Bug 1's investigation): world map
## music

Not reported as broken, but genuinely missing - `WorldMapScene` had no
`AudioManager` at all, unlike every other scene. The original's
`WorldMap::own_resumeState()` unconditionally plays `music/menu.ogg`
every time the map becomes active. Since this scene is already fully
torn down and recreated on every visit (docs/027), a single call in
`create()` has the same effect. `AudioManager` (docs/018) is scene-
agnostic and needed zero changes - just instantiated and given one
`applyMusicCommand({type: "play", track: "menu"})` call, plus the
matching `SHUTDOWN` cleanup. `menu.ogg`/`menu.mp3` were already converted
(part of docs/018's original small verification set).

## Verification

- `npx tsc -b` clean throughout.
- Direct sweep (`createLevelScript()` + 5 ticks, all 80 levels, no UI):
  79/80 clean, only `windoze` remaining (known out of scope).
- Real-browser (Playwright, temporary `window.__game` hook, removed
  after): world map music confirmed actually playing (`AudioManager`'s
  live state, not just "no error"); `briefcase`'s dialog registry
  resolves real sound paths for all 130 dialogs and `AudioManager`
  actively loads its sprite during play; loading `start` then `briefcase`
  back-to-back produces genuinely different background texture keys/image
  data and correctly-scoped model textures; full regression re-run of
  docs/027's own verification suite (hover/click/escape/resize, solve→
  return→pedometer→cancel) still clean.

## Open for next time

- `model_equals`'s anchor-position approximation could misclassify a
  probe exactly at the room's outer edge (treating border as empty water)
  - only matters for `stairs`/`rush`/`creatures`/`cancan`'s scripted-unit
  pathfinding, not confirmed to actually cause a visible issue in any of
  those four.
- `windoze` remains fully out of scope (`fish_extra`, multiple missing
  bindings beyond just `game_setFastFalling`/`model_getTouchDir`) -
  unchanged from docs/022's original scoping decision.
- Sound conversion for the full ~80-level corpus is now real, but only
  Czech (`cs`)/English (`en`, where it exists)/Dutch (`nl`) got converted
  (the languages already handled by the existing sprite-building script) -
  other localizations aren't wired up, matching this port's existing
  Czech-default scope (docs/018).
