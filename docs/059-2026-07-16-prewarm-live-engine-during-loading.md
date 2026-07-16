# 059 - Prewarm the live Lua engine during the world-map "Loading" phase

_2026-07-16_

Follow-up to docs/058's "Open" item. The port runs two Lua engines (see the
docs/006 -> docs/008 -> docs/014 lineage): a throwaway *loader* that extracts a
static `LevelData` snapshot, and a persistent *live* engine (`levelScript.ts`)
that runs `script_update()` each round. The live engine boots asynchronously
*inside* `LevelScene`, so for ~380ms-1s after the room appears there's no live
engine and the room renders purely from the loader's snapshot. That gap is what
forced docs/058 to bake each model's first-`updateAnim`/`setEffect` result into
the snapshot (the parrot / party2's limbs).

This closes the gap the way the original never has it: the original has one Lua
engine whose first `script_update()` runs *before the first draw*. Here, the
world map boots the live engine **during its own "Loading" phase**, so it's ready
when `LevelScene`'s first frame draws.

## What changed

### 1. World map prewarms (`WorldMapScene.launchLevel`)
After `loadLevelModels()`, it now also builds the `GameEngine` and boots the live
script, then hands both to `LevelScene`:

```ts
const engine = new GameEngine(levelData);
const script = await createLevelScript(codename, engine.getRenderModels(), 1,
                                       undefined, this.makeEngineControl(engine), depth);
this.scene.start("level", { levelData, poster, depth, engine, script: Promise.resolve(script) });
```

The engine is built here (not just in the scene) for two reasons: the bootstrap
seeds from its render models, and it reads the engine back via `engineControl`
(creatures/cancan/turtle query `model_equals` in `prog_init`, docs/054). The
`engineControl` passed here is **temporary** - it closes over this local engine,
correct for the bootstrap, and `LevelScene` swaps in its own version on adoption.

### 2. LevelScene adopts + attaches its own callbacks
`init()` takes optional `engine`/`script`. `startEngine()` adopts them on the
first fresh start (never a restart or save-resume - those build fresh). Because
the world map has no scene / engine-swap context, the prewarmed script was
booted **without** this scene's `this`-closured `hostActions`/`engineControl`;
`LevelScene` attaches them on adoption via new `LevelScript.setHostActions()` /
`setEngineControl()`. Safe because neither is invoked between boot and the first
`tick()`. `hostActions`/`engineControl` moved onto `LevelScriptState` so the
setters can mutate what the host bindings read.

### 3. Assignment decoupled from the dialog-audio gate
Previously `this.levelScript` was assigned only *after* the audio gate (~1s),
which would have made prewarming pointless. Now the script is assigned as soon as
it's booted, so the render loop reads its bootstrap init-state immediately; a new
`scriptTicking` flag gates only the live per-round `script.tick()` (dialogs /
animation advance) on the audio, preserving docs/031's "first line in sync with
its subtitle".

### 4. The live engine now applies init-anims at bootstrap too
Assigning the script early surfaced a real coupling: the live engine, exactly
like the loader before docs/058, does **not** apply a deferred `afaze` until its
first `script_update()`. So `getScriptAnim(parrot)` returned frame 0 (colourful)
in the window between assignment and the first (audio-gated) tick - a ~800ms
regression. Fixed by running the same one-`updateAnim()`-pass at the end of
`createLevelScript`'s bootstrap (`APPLY_INITIAL_ANIMS_SOURCE`, the live-engine
twin of the loader's docs/058 pass, minus the loader-only `ffwg_hasAnims` guard).
Now the live script's pre-tick state is correct, matching the original's
first-update-before-first-draw.

## What this does and does NOT change

- **Does**: the live engine is genuinely booted during "Loading" and assigned at
  `LevelScene` frame ~1 (measured 110ms, 0 frames of null vs. ~990ms before). Its
  bootstrap init-state (item anim phase + `setEffect`) is the render source from
  frame 1, so the docs/058 loader bake is now a **fallback**, not the sole
  mechanism.
- **Does NOT remove the docs/058 loader fix.** `buildAnimators()` runs
  synchronously *before* the script promise resolves, so the very first frame(s)
  still come from `LevelData.initialAnim`/`initialEffect`; and non-prewarmed
  entries (replay->Esc->level, the ending, a save-resume) still boot in-scene.
  The two now work together.
- **Does NOT change when per-round animation / dialogs start.** `script.tick()`
  is still audio-gated (~900ms), so decorative item animation and dialogs begin
  at the same time as before - the gate exists to keep a first line in sync with
  its voice, and ticking early would break dialog *cycle* timing (script_update
  advances a dialog's clock even if its audio isn't ready). The win here is
  architectural correctness + correct init-state from the live engine, not a
  faster first dialog.

## Cost
The node-click "Loading" phase is ~380ms longer, because the world map now awaits
the script boot before the scene transition (previously it awaited only
`loadLevelModels`). Honest - the level is fully ready when it appears - and
faster on repeat visits (HTTP-cached Lua). If it ever feels sluggish, passing the
in-flight promise instead of awaiting keeps the transition snappy at the cost of
the engine being live at ~380ms-into-scene instead of frame 1 (the loader fix
covers that gap either way).

## Verification (real browser)
- **cabin2 prewarm**: script assigned at 110ms / 0 frames-of-null (was ~990ms);
  parrot never colourful; scriptTicking releases at ~913ms (audio-gated).
- **party2**: limbs hidden across the whole first ~600ms (live bootstrap's
  `setEffect` now applies from frame 1, no pre-live window left).
- **viking1**: dialogs still fire (subtitle appears) after ticking releases.
- **Adoption-sensitive levels** windoze (engineControl at tick), briefcase
  (hostActions), creatures/turtle (model_equals at bootstrap), cabin1: all adopt
  + play ~3s with zero errors.
- **Lifecycle**: restart (R), save+load resume (F2/F3, the resumeModelState
  path), and non-prewarmed replay->Esc->level re-entry all boot live + ticking,
  zero errors.
- All-81 loader sweep clean (164 fish, 1543 items); negative-phase + docs/058
  checks green; e2e **7/7** (incl. the 80-level live-tick sweep, which exercises
  the new bootstrap pass); `tsc -b` clean.

## Files
- **Modify:** `web/src/lua/levelScript.ts` (`hostActions`/`engineControl` on
  state + `setHostActions`/`setEngineControl`; `APPLY_INITIAL_ANIMS_SOURCE` run
  at end of bootstrap), `web/src/scenes/LevelScene.ts` (adopt prewarmed
  engine/script; `scriptTicking` gate; assign-immediately), `web/src/scenes/
  WorldMapScene.ts` (`launchLevel` prewarms; `makeEngineControl`).
