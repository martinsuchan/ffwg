# 064 - Game size setting, crisp text, and F11 fullscreen

_2026-07-17_

Three related presentation features, plus a rendering-model change that makes
them possible.

## The problem

The port rendered the whole game at native resolution and let the browser
**CSS-stretch** the canvas to 150% (`main.ts`'s `zoom: 1.5`). The Scale Manager
ran in NONE mode, and `zoom` there only scales the canvas's CSS box - the WebGL
framebuffer (`canvas.width/height`) stayed at the room's native pixel size. So
*everything*, text included, was rasterized at native resolution and then
bilinearly upscaled by the browser. Sprites and photographic backgrounds are
low-res source art and can't be sharpened, but **text and vector graphics are
resolution-independent and looked needlessly soft**.

## Why `setResolution` alone can't fix it (the false start)

The first plan was to leave the CSS-stretch alone and just raise each Phaser
`Text`'s `resolution` (renders the glyph texture at higher DPI). Verified
empirically that this **does not work** under CSS zoom: the framebuffer is
native-res (a probe confirmed `canvas.width` stays 640 at zoom 1.5), so a
higher-DPI glyph texture is still composited into a native-res framebuffer and
then CSS-upscaled - an A/B of `resolution:1` vs `resolution:6` moved backing
anti-aliasing only marginally (397 -> 493 lit px) and the CSS blur dominated
regardless. **Crisp text fundamentally requires a display-resolution
framebuffer.**

## The fix: camera-zoom rendering (not CSS-zoom)

New `applyRenderScale(scene, nativeW, nativeH)` in `sceneUtils.ts`, called from
every scene's `create()` (replacing the old `this.scale.resize(nativeW,
nativeH)`):

- Framebuffer = `native * factor` (`scale.resize(...)`) -> renders at **display
  resolution**, so text/vector graphics are sharp.
- Main camera zoomed by `factor` (`cam.setZoom(factor)`, `cam.setOrigin(0, 0)` so
  world (0,0) stays at screen top-left) -> the game-unit world fills that
  framebuffer. Pixel-art sprites/photo backgrounds upscale as before (expected).
- **World coordinates are unchanged**, so all gameplay/click math is untouched -
  the port already reads camera-aware `pointer.worldX` (`LevelScene.toFieldPos`),
  so camera zoom is transparent to input, rope decor, screen-shift, etc.
- Phaser quirk handled: in NONE mode `resize()` only rewrites the CSS box when
  `_resetZoom` is set, so we `resize()` then `setZoom(1)` to force CSS ==
  framebuffer (1:1, no stretch). The Scale-Manager `zoom` is now always 1; the
  size factor lives entirely on the camera.

`crispText(style)` (a thin wrapper applied at all ~22 `add.text` call sites) sets
each Text's `resolution` to `max(factor, 3)` = 3, which now genuinely helps: text
drawn by a `factor`-zoomed camera into the high-res framebuffer is crisp when the
glyph texture DPI >= factor (3 covers 1/1.5/2 and fullscreen). This is the piece
that would have been a no-op under the old CSS-zoom.

## 1. Game size setting (Standard / Large / Huge)

`settingsStorage.ts` gains `gameSize: 1 | 1.5 | 2` (default **1.5**, preserving
the port's original on-screen size). New **Game size** row in `OptionsOverlay`
(same button-row shape as Language). Applies **live** via a new `onGameSizeChange`
callback -> `WorldMapScene.applyGameSize()` -> `applyRenderScale(this, MAP_W,
MAP_H)`; every other scene re-reads the setting in its own `create()`, so the
factor persists across navigation with no reload.

## 2. Crisp text at non-Standard sizes

Falls out of the camera-zoom change above: at 150/200% the framebuffer is
960x720 / 1280x960 and text/outlines render at that density. Verified a 2x step
counter renders as a sharp orange glyph with a clean outline (screenshot), and
the framebuffer measured `room*factor` at every size. Vector map edges are crisp
too now (a free bonus of the high-res framebuffer, beyond the "text only" scope).

## 3. F11 fullscreen (aspect-preserving)

New `fullscreen.ts` (`initFullscreen`, wired in `main.ts`). The **first attempt**
captured F11 (`preventDefault` + `scale.startFullscreen()`) and switched to FIT
before requesting fullscreen - and showed a **black screen in Edge**. Two causes:
(1) F11 is a browser-reserved key and `preventDefault()` does *not* reliably
suppress the browser's own native fullscreen (Edge especially), so our
element-fullscreen request and the browser's native F11 fullscreen fought; and
(2) FIT was applied *before* entering fullscreen, fitting the canvas to the small
windowed container so it stayed tiny.

Final approach - **ride the browser's own F11, don't capture it**: we listen to
the `(display-mode: fullscreen)` media query, which matches for BOTH native F11
*and* the Fullscreen API (the API's `fullscreenchange`/Phaser fullscreen events
fire only for element fullscreen and miss F11). On its `change`:

- **Enter:** size the game container to the viewport (`position:fixed; inset:0;
  100vw/100vh; flex-centered; black backdrop`) so FIT has a full-screen parent to
  scale into, then `fitToParent()` = switch to FIT (+ `displaySize.setAspectMode(
  FIT)`, since the manager only syncs aspect mode from `scaleMode` at boot) and
  refit. FIT leaves the framebuffer resolution untouched, so the crisp high-res
  render carries into fullscreen, letterboxed to preserve aspect ratio.
- **Exit:** clear the container styles + `restoreWindowed()` = NONE + `setZoom(1)`.

Two Scale-Manager gotchas fixed along the way: `refresh()` reads `parentSize` but
only re-measures it at the *end* of its own layout pass, so FIT-fit needs an
explicit `getParentBounds()` first or the canvas never grows to the screen; and
`applyRenderScale` (scene navigation) checks `isFullscreenActive()` (the media
query) rather than Phaser's `scale.isFullscreen` (element-only), so navigating
while F11-fullscreen keeps the FIT layout instead of snapping back to windowed.

## Test-harness fix

`web/tests/lib.mjs`'s `canvasMapper` (world coords -> page pixels for clicks)
assumed framebuffer == world space; now framebuffer == world*factor, so it
multiplies by the main camera's `zoom` (`wx * cam.zoom * (cssW/backingW)`) -
correct for both the new camera-zoom and the old CSS-zoom (where `cam.zoom == 1`).

## Verification (real browser)

- Framebuffer measured `native*factor` at Standard/Large/Huge on both the map and
  levels; CSS box 1:1 with it; camera zoom == factor.
- 2x step-counter crop: sharp glyph + clean outline (crisp). airplane @1.5 renders
  the full room correctly from top-left, nothing clipped/stretched.
- Live Huge switch from Options: backing 960x720 -> 1280x960, camZoom 1.5 -> 2, no
  reload, no errors.
- Fullscreen (simulated: container sized to a 1600x900 viewport + `fitToParent`,
  since headless can't fire real F11): framebuffer 1080x450 (aspect 2.4) scales to
  CSS 1600x667 (aspect **2.4 preserved**), fills the width, letterboxed top/bottom
  (screenshot); `restoreWindowed` snaps back to 1080x450 1:1. The `(display-mode:
  fullscreen)` trigger and real F11 need a manual browser check.
- Options panel shows the Game size row (Standard/**Large**/Huge), all text crisp,
  panel fits.
- `tsc -b` clean; e2e suite **7/7** (the node-hover and pedometer cases exercise
  the `canvasMapper` coordinate fix under camera zoom).

## Not done / notes

- Fullscreen text crispness is capped by the windowed framebuffer (`room*factor`)
  stretched by FIT to the screen - fine at 2x; rendering the framebuffer at the
  actual screen resolution while fullscreen is a possible later refinement.
- `crispTextResolution` is a flat 3 (covers all sizes + fullscreen); a touch of
  texture memory for text, negligible here.
