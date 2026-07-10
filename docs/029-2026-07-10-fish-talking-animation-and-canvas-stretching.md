# 029 - Fish Talking Animation & Canvas Stretching

2026-07-10

## Context

Two more bugs found smoke-testing docs/027/028's World Map: no mouth
animation while a fish talks (the original animates the mouth during
dialog), and every level rendering into the same window size regardless
of its actual room dimensions, visibly stretching levels whose aspect
ratio differs from the world map's 640x480.

## Bug 1: Canvas stretching

`LevelScene.create()` (and `WorldMapScene.create()`) called
`this.scale.setGameSize(width, height)` to resize the game canvas per
level (docs/027). This turned out to be the wrong Scale Manager call:
Phaser's own docs are explicit that `.setGameSize()` is meant for use
with the `FIT` scaling mode (or similar) - it updates only the *internal*
backing-store resolution, expecting some other automatic mechanism to
handle the CSS display size. `.resize()` is the one meant for `NONE` mode
("You should only use this if you are using the NONE scale mode, it will
update all internal components completely") - which is what this port's
Game config actually is, since it never sets an explicit `mode`, only
`zoom`.

Confirmed directly before fixing: loading `library` (21x37 cells = 315x555
internal px) showed `canvas.width/height` correctly updated to 315x555,
but `canvas.getBoundingClientRect()`/`canvas.style.width/height` stayed
frozen at `960x720` (the world map's size at zoom 1.5, set once at boot) -
the browser was stretching a 0.568-aspect-ratio image into a 1.333-aspect-
ratio box. Switched all three call sites (`WorldMapScene`, `LevelScene`,
and `ReplayScene` - the last one needed it too, since the Pedometer's
"Replay" button launches it directly from the map without ever passing
through `LevelScene` first) from `setGameSize()` to `resize()`. Verified:
internal and CSS aspect ratios now match exactly (both 0.568 for
`library`), and a screenshot confirms proper proportions - a tall,
narrow bookshelf room instead of a squashed-wide one.

## Bug 2: No fish talking-mouth animation

`web/src/game/UnitAnimator.ts`'s `computeHeadAnim()` already had this
gap flagged in its own doc comment since docs/009 (written before dialogs
existed at all): "The original's 'busy'/'talking' branches... doesn't
exist in this port yet... revisit this function's signature when dialogs
(phase 3) land." Dialogs landed in docs/015, but nobody came back to
finish this - exactly matching the user's report.

Read the real `animateHead()` (`legacy/script/share/level_update.lua`)
rather than guessing: talking beats pushing beats the occasional blink
(strict priority order), using a per-model `talk_phase` (0-2, matching
the 3 real `head_talking_00/01/02` frames every fish already has
registered via `addFishAnim()` - confirmed the converted assets exist,
this was purely a missing-logic gap, not a missing-asset one) that's
freshly randomized when talking starts and randomly steps to a different
frame roughly every other round while it continues, resetting the moment
talking stops. The "talking" trigger itself is `"talking" == state or
model_isTalking(TALK_INDEX_BOTH)` - `Cube::isTalking()` (the model's own
dialog slot) *or* `TALK_INDEX_BOTH` (`-1`, `level_creation.lua`), a real
actor value some narrator-style `model_talk()` calls use for lines not
tied to one specific fish, in which case *every* fish talks at once.

Ported faithfully, but with the physics/presentation split this project
already established rather than the original's Lua-side bookkeeping:
- **`LevelScript.isModelTalking(index)`** (new, `web/src/lua/
  levelScript.ts`): the actor-index-or-TALK_INDEX_BOTH check, reading the
  dialog state this class already tracked for subtitle display (docs/015)
  - genuinely just wiring, no new dialog-tracking needed.
- **`computeHeadAnim()`** (`UnitAnimator.ts`) gained `isTalking`/
  `talkPhase` parameters, staying a pure function - talking now checked
  first, ahead of pushing/blink, matching the original's priority order.
- **`ModelAnimator`** owns and cycles `talkPhase` itself (new `lastIsTalking`/
  `talkPhase` fields, updated in the existing `checkHead()` timer) rather
  than reading a Lua-side `model.talk_phase` - fish stay entirely TS-owned
  (docs/009/013), same reasoning as body animation already uses. Ticks on
  this class's existing ~100ms head-check timer, not tied to physics
  rounds - a deliberately simpler cadence than the original's
  `game_getCycles() % 2`-gated one (docs/009's established "decoupled
  timing" philosophy), not an attempt at exact parity.
- The original's `"busy"` action branch (which also overrides the *body*
  anim with a talk pose, not just the head) is still skipped - nothing in
  this port ever sets the `"busy"` action (no fish-switching/dialog-
  busying), unchanged from the pre-existing documented scope.
- `LevelScene.tick()` computes `isModelTalking()` for fish models only and
  passes it into `ModelAnimator.sync()`'s new third parameter;
  `ReplayScene` doesn't pass it (defaults to `false`), consistent with
  its existing "no subtitles either" simplification (docs/025).

## Verification

- `npx tsc -b` clean throughout.
- Real-browser (Playwright, temporary `window.__game` hook, removed
  after): loaded `library` (extreme aspect ratio) before/after the resize
  fix - CSS display box now matches the internal resolution's aspect
  ratio exactly; screenshot confirms correct proportions.
- Sampled `airplane` every 100ms over a 20-second window: confirmed a
  real dialog fired, `isModelTalking()` was true for a fish while its
  subtitle was showing, and that fish's actual head sprite texture key
  became a real `head_talking_*` frame during that window (not just "the
  function returned the right anim name" - the texture genuinely bound).
  Screenshot captured mid-dialog for a visual sanity check.
- Full regression: world map hover/click/escape/resize, solve→pedometer→
  cancel/run/replay, P-triggered replay `returnTo` distinction, and the
  full save/load slot suite (docs/026) all re-verified clean after both
  fixes - the save/load test script itself needed updating to navigate
  through the world map first (it predated docs/027 and assumed a direct-
  to-level boot).

## Open for next time

- The talking-phase cadence (~100ms, tied to the existing head-check
  timer) is a guess, not measured against the original's real ~260ms
  (`game_getCycles() % 2` at `ROUND_MS`) cadence - easy to retune if it
  looks too twitchy/slow in practice.
