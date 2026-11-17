# 084 - The mirror and zx draw effects; lossless WebP policy

2026-11-17

Both remaining legacy draw effects are now drawn. They are cosmetic only, with no
effect on physics, saves, replays or solutions: `model_setEffect("zx")` (emulator's
walls) and `model_setEffect("mirror")` (submarine's mirror) had been recorded but drawn
normally (docs/051). Both are set in `prog_init`, so they reach ReplayScene through
`initialEffect` as well, and the **MP4 export** shows them too (checked by decoding an
exported submarine frame).

## `zx` (emulator) - `web/src/scenes/zxEffect.ts`

A line-for-line port of `legacy/src/effect/EffectZx.cpp`:

- **Cycle:** `phase` cycles 0..499, one step per draw. The original draws once per game
  cycle (100 ms), so one cycle lasts 50 s.
  - Phases 1-51: wide stripes (38.5 px, jittered by `randomReal(0.06)`) in the ZX1/ZX2
    colours (top-left / bottom-left corner pixel).
  - From phase 52: narrow stripes (3.4 px, jitter `randomReal(0.1)`) in ZX3/ZX4
    (top-right / bottom-right corner).
- **Crawl:** the row counter and the current colour carry across draws, which is what
  makes the stripes crawl. Each draw is `blit()` then `updateEffect()`, the original's
  order.
- **Rendering:** each cycle the rows are filled into a sprite-sized `CanvasTexture`
  (runs of equal rows go into one `fillRect`). The result is cut to the sprite's shape
  with `destination-in` against a binary mask (original: "not the colour key"; here
  alpha >= 128). That is one 705x540 texture upload per cycle and nothing per frame.
- **Hook:** `ModelAnimator.sync()` switches the effect on and off. The body sprite shows
  the canvas (key `fx-zx`, shared and never removed - docs/056), and the atlas frame
  becomes the mask source. The CYCLE_MS body timer steps the effect.
- **Randomness:** the jitter uses `Math.random`, like the original's
  `Random::randomReal`. It is cosmetic and is not the level's Lua `random`.

## `mirror` (submarine) - `web/src/scenes/mirrorEffect.ts`

Legacy `EffectMirror::blit` draws the picture straight onto the screen:

- The glass colour is the colour of the centre pixel.
- Each glass-coloured pixel with `px > MIRROR_BORDER` (3) gets the screen pixel at
  `(x - px + 3, y + py)`. That mirrors the ~27 px strip left of the mirror, so the glass
  shows whatever was drawn before it: the wavy background, fish and items. Hence
  "mirror should be the last item" in `models.lua`.
- Every other pixel is drawn only if it is fully opaque.

The mirror is an `item_light` and can be pushed (in the solution it ends up one floor
lower and reflects the wall).

**Approach: `CaptureFrame` + `Shader`, not a camera filter.** A camera filter
(`BaseFilterShader` on `camera.filters.internal`) was probed first. It works, but it
post-processes the finished frame. It would therefore also reflect subtitles and HUD, and
punch the reflection through any overlay (options, help, save slots) that covers the
mirror. Phaser 4's `CaptureFrame` has exactly the original's semantics: it grabs the
framebuffer at its own position in the display list. Layout:

- **Display order:** `… models … mirror body sprite (alpha 0) → CaptureFrame → Shader → UI (depth 1000)`.
  The capture and the shader are moved to right after the body sprite
  (`children.moveTo`) at the same depth. In submarine the mirror is the last model, so
  the capture holds background and models but no UI, and overlays drawn later cover the
  mirror normally.
- **Shader:** a Shader game object the size of the picture draws the whole mirror. The
  picture is classified once on the CPU into a `CanvasTexture` (`fx-mirror-picture`):
  - alpha 255: opaque non-glass pixel, shown;
  - alpha 128: glass, which reflects;
  - alpha 0: not drawn (transparent or semi-transparent, as in the original).
  The glass test is an exact RGB match like `PixelTool::colorEquals`.
- **Glass pixels:** sample the capture at world `(x + 3 - localX, y + localY)`, converted to
  framebuffer px with the camera's scroll and `drawingContext.width / (camera.width /
  camera.zoom)`. Orientation details: `outTexCoord` is bottom-up, and the capture is a
  framebuffer texture with row 0 at the bottom. The picture is sampled at texel centres,
  so linear filtering at 150/200 % keeps the codes exact.
- **Camera framebuffer:** `CaptureFrame` needs one, so the effect turns on
  `camera.setForceComposite(true)` and restores it on destroy. Cameras are recreated on
  scene start anyway (the map stays non-composite). The cost is two extra full-frame copies
  per frame, and only in submarine.
- **Teardown:** the capture texture key `fx-mirror-capture` is reused. The previous
  instance's texture and framebuffer are freed when the next mirror is created, not during
  SHUTDOWN (docs/056). An R restart leaves exactly one capture.
- **Fallback:** without WebGL the effect is skipped and the mirror is a plain picture.

Checked in the browser:
- level at 100/150/200 %;
- replay with the big fish's nose reflected, and the mirror pushed down reflecting the wall;
- R restart, then back to the map;
- MP4 export frame (16:9 layout with scroll offset and zoom).

## Lossless WebP policy (now in one place)

The reasons for lossless WebP were scattered over docs/039, docs/062 and comments in three
scripts. They are now listed in one block, **"Lossless WebP policy" in
`scripts/convert-assets.ps1`**, next to `$losslessLevelAtlases`. `web/CLAUDE.md` points
there. The policy: lossy by default; lossless only where pixels are read back exactly,
or where it is also smaller:

1. hit-test masks (docs/039);
2. fish atlases (docs/062);
3. demo_briefcase frames (docs/062);
4. the level atlases **emulator** (exact zx colours), **submarine** (exact glass match),
   **tetris** and **warcraft** (flat pixel art, smaller).

Measured on all 82 atlased image dirs (`build-atlas.ps1`, q90 vs `-Lossless`):

| | lossy | lossless |
|---|---|---|
| all 82 atlases | 12.21 MB | 36.47 MB |
| emulator | 29.6 KB | **4.0 KB** (0.14x) |
| tetris | 122.5 KB | **20.8 KB** (0.17x) |
| warcraft | 241.4 KB | **105.2 KB** (0.44x) |
| submarine | 33.0 KB | 133.6 KB (4.05x) |

Lossless is smaller only for emulator, tetris and warcraft. The next closest are windoze
(1.03x) and nowall (1.12x); the rest are 1.25x to 6.6x (redhat 6.6x, noground 5.9x,
airplane 5.1x). Net change of the four switches: about -160 KB. The lossy emulator atlas
had shifted the zx corner colours (128,0,0 came out as 103,7,8). The lossy submarine
atlas blurred the glass edge and highlight, because near-cyan pixels missed the
exact-match mask.

## Tests

- e2e `11-zx-effect`:
  - exactly one model sits on the effect canvas (705x540);
  - its colours are the exact ZX colours, which also catches a stale lossy atlas;
  - the effect advances. The test waits for progress rather than asserting a rate,
    because timers lag right after load.
- e2e `12-mirror-effect`:
  - display order: capture and shader directly after the mirror, the mirror last among
    the models, no UI before them;
  - the body sprite is hidden and the camera framebuffer is on;
  - on a real one-frame snapshot (`renderer.snapshotArea`), the glass centre pixel equals
    its mirrored source left of the mirror (got 9,46,8 = 9,46,8).

## Open for next time

- Nothing for the effects. If a future level put a model *after* its mirror, that model
  would not be reflected and would draw over the mirror, as in the original.
- Corpse removal is still an alpha fade, not `EffectDisintegrate` (BACKLOG §B.5). It
  could use the same CanvasTexture technique as `zx`.
