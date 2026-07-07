# 009 - Fish Animation System

2026-07-07

## Goal

Phase 1 of "implement full level content" (dialogs, animation, per-level
item behavior - discussed after `docs/008`): real fish body/head
animation (swimming, turning, idling, blink/pushing expressions) and the
frame-discovery infrastructure phase 2 (item animation) and phase 3
(dialogs) will also need. Explicitly out of scope, by design: sound/
music, per-level `code.lua`-driven item animation (needs the per-round
script update loop - phase 2), dialogs/talking (phase 3), and
`model_setEffect`'s per-level shaders (disintegrate/mirror/reverse/zx -
excluded by the user). Planned in plan-mode with the user before
implementation; see the "Key design decision" below for the one real
architecture fork that came up.

## Key design decision: animation timing is decoupled from physics

The original engine ties movement speed to animation: a move/turn is only
applied to a fish's grid position once its animation finishes playing
(`PhaseLocker`/`Controls::getNeededPhases` - a turn takes
`countAnimPhases("turn")` cycles, a swim move 2-6 depending on how many
consecutive moves you've made). Confirmed by tracing `Application::run()`
(`legacy/src/game/Application.cpp`): the whole game loop is a single
`while (!quit) agents->update()`, and `TimerAgent::own_update()`'s
`SDL_Delay` inside that same loop means *drawing* (via `VideoAgent`, same
iteration) is bottlenecked to the same ~100ms cadence as game logic - i.e.
the original runs both logic and rendering at a flat ~10Hz.

Given the user's explicit choice (asked via `AskUserQuestion` before
planning): **keep `docs/007`'s physics round loop completely unchanged** -
no `PhaseLocker` port, no coupling animation duration back into round
pacing. Animation lives entirely in the presentation layer
(`web/src/scenes/`), triggered by physics events but timed independently
on a fixed ~300ms window per triggered anim. Trades the original's
"speeds up the longer you hold a direction" feel for zero risk to the
tested Room/round-loop code.

## What was built

- **`web/tools/build-image-manifest.mjs`** + **`scripts/build-image-manifest.ps1`**
  (same style as `check-lua-compat.mjs`/`.ps1`): recursively lists every
  real file under `legacy/images/` into `web/public/lua/image-manifest.json`
  (gitignored, regenerable - `scripts/start.ps1` now builds it
  automatically if missing). This is what lets `file_exists` in
  `web/src/lua/levelLoader.ts` finally return real answers instead of
  always `false` - `imgList()` in `level_creation.lua` can now actually
  discover every numbered frame of an anim, not just phase 0. Verified:
  `images/fishes/small/left/body_swam_*.png` -> 6 entries, `body_turn_*`
  -> 3, matching the real directory listings.
- **`levelLoader.ts`**: `LevelModel.picture: string | null` (one resolved
  frame, docs/006's approach) is gone, replaced by the full per-anim,
  per-side frame lists (`anims: Record<string, {left, right}>`) plus the
  model's *initial* anim/phase/facing. The `docs/008` modulo-phase-wrap
  workaround is gone too - real frame discovery means it's no longer
  needed.
- **`web/src/game/UnitAnimator.ts`** (new, in the physics-adjacent `game/`
  layer, not `scenes/`): `computeBodyAnim`/`computeHeadAnim`, a direct
  TypeScript port of `legacy/script/share/level_update.lua`'s
  `animateFish`/`animateHead` - pure functions over plain
  `{isAlive, action, state}` values (not physics objects), so the exact
  same logic works against a real `Cube` in tests or a `GameEngine`
  render snapshot in the browser. Same "port fixed game logic directly,
  don't round-trip through Lua" reasoning as `Rules`/`Landslip`
  (`docs/007`) - this is engine behavior, not level content. The
  original's "busy"/"talking" branches (which override the *body* anim,
  not just add a head overlay) are intentionally not ported - unreachable
  without dialogs (nothing in this port ever sets `busy`/`readyToActive`),
  documented inline for phase 3 to revisit.
- **`web/src/game/GameEngine.ts`**: `RenderModel` gained `action`/`state`
  (straight from `Rules.getAction()`/`getState()`, already faithful ports
  - no physics changes needed) and dropped `picture` (rendering no longer
  resolves through GameEngine at all).
- **`web/src/scenes/ModelAnimator.ts`** (new): the presentation-layer
  piece. `preloadModelFrames`/`resolveTextureKey`/`textureKey` centralize
  "which Phaser texture does (anim, side, phase) map to" behind one
  lookup point (deliberately, so a later atlas swap - see below - doesn't
  touch anything else). `ModelAnimator` per model handles:
  - **Position sliding** (all kinds, not just fish): tweens screen
    position over ~300ms instead of snapping - closes the `docs/007`
    "Open for next time" item.
  - **Real per-side art, no flipping**: since both `left`/`right` frames
    are now genuinely loaded (`addBodyAnim`/`addHeadAnim` always register
    both), fish use the actual right-facing artwork instead of a mirrored
    left-facing sprite.
  - **Body anim** (fish only): every physics tick, `computeBodyAnim` picks
    an anim name; a same-named trigger extends a rolling ~300ms window
    (so a held key keeps the swim cycle going smoothly), a different name
    restarts it at phase 0, and expiry falls back to `rest`. A separate
    ~100ms timer advances the phase while "running". Dead fish permanently
    lock to `skeleton`, bypassing the window.
  - **Head overlay** (fish only, second sprite): an independent ~100ms
    timer calls `computeHeadAnim` (pushing pose, ~6%/tick blink chance),
    matching the original's per-cycle re-roll cadence.
- **`LevelScene.ts`**: preloads every frame of every model (not just one),
  creates a second (initially hidden) sprite per fish for the head
  overlay, and feeds each `ModelAnimator` the latest `RenderModel` after
  every physics tick. `restart()` now explicitly destroys all animators
  (stopping their timers) before rebuilding, instead of the old
  reuse-existing-sprites approach - necessary once sprites own live
  timers, not just static textures.

## Texture atlases (`docs/004`) - confirmed still deferred, not needed here

Raised by the user before the plan was finalized: does animation change
when atlas-packing (`docs/004`, never wired in) needs to happen? Checked
`scripts/asset-tools/build-atlas.mjs` directly - it only reads files
*directly* inside `--source` (non-recursive) and names frames by bare
filename, so it can't pack fish assets today (`left`/`right`/`heads/left`/
`heads/right` are nested, and duplicate filenames like `body_rest_00.png`
collide across them). Decided to keep that as separate, self-contained
follow-up work rather than bundle a packer rewrite into this change - the
texture-key indirection above means swapping it in later won't touch the
animator or `UnitAnimator` at all. Worth doing soon, given animation takes
each fish from ~1 loaded frame to ~60+.

## Verification

- `npx tsc -b` and `npx vite build` both clean throughout (build output:
  30 modules, still zero `.lua` files bundled - the `docs/008` fix
  holds).
- Unit-style tests (Playwright + dynamic `import()`, same technique as
  `docs/007`/`docs/008`): `computeBodyAnim` exercised for every reachable
  action (`move_up/down/left/right`, `turn`, `rest`, dead) plus the
  unreachable `busy`/`activate` fallback, all matching
  `animateFish`/`animateHead`'s original branches exactly;
  `computeHeadAnim` for pushing/blink/none/dead.
- Manifest + real frame discovery confirmed end-to-end through the actual
  loader (not just the raw manifest file): loading `airplane` for real
  now reports `swam`/`vertical_*` = 6 frames, `turn`/`rest`/`talk` = 3,
  for both fish, plus the full `head_*` anim set - all previously
  invisible to `imgList()`.
- Real browser, real airplane level (Playwright screenshots): held a
  movement key and captured frames ~150ms apart - the swim pose visibly
  cycles between distinct frames, not a static image. Triggered a turn
  (pressed right while facing left) and confirmed a multi-frame turn
  pose plays, ending with the fish now rendered from genuine right-facing
  artwork (not a mirrored flip) with the swim/rest cycle continuing
  correctly afterward.
- Regression: re-ran `docs/007`'s 7 synthetic-room tests and `docs/008`'s
  10-level goal-loading test completely unmodified - identical results
  both times, confirming the physics/goal layers are untouched.
- Extended mixed-key stress play (~20s) and repeated restart-mid-motion
  cycles (checking `ModelAnimator.destroy()` actually tears down its
  timers rather than leaking/duplicating them across restarts) - zero
  console errors, zero failed requests in both.

## Open for next time

- Item animation (grail's aura pulse, airplane's eye blink, ...) needs
  the per-round `code.lua` update loop - phase 2, not started.
- Dialogs/talking - phase 3. `computeHeadAnim`'s "talking" branch and the
  original `animateHead`'s body-anim-overriding "busy" branch are
  unported; revisit `UnitAnimator.ts`'s signature then rather than
  guessing now.
- Blink specifically wasn't visually reconfirmed in a real browser this
  session (a ~6%-per-100ms event in a small on-screen sprite region isn't
  practical to catch reliably via screenshot sampling) - the decision
  logic is unit-tested and it renders through the same texture pathway
  already proven for body animation, but worth an eyeball check next time
  the level is open interactively.
- Texture atlas extension (recursive source + collision-safe frame
  naming) for `build-atlas.mjs`, then wiring `LevelScene`/`ModelAnimator`
  to load `.atlas` pairs instead of N individual images - see the
  "Texture atlases" section above.
- Round pacing is still a flat timer (`docs/007`); the original paces by
  animation-phase count. Revisit only if the current feel turns out to be
  a real problem, not preemptively - see the "Key design decision" above.
