# 057 - Wavy background: the edge strip is padded, not wrapped

_2026-07-15_

Bug fix to docs/056. User reported: on `start`, a **white sliver** appears at the
right edge as the wave moves inward; on `ufo`, the backdrop shows through at
**both** edges.

## Cause

docs/056 modelled the original's per-scanline shift as a **wrap-around**
(`fract(uv.x + shiftX/w)`). It isn't one.

`WavyPicture::drawOn()` does two blits per row, and `SDL_BlitSurface` **clips the
source rect** against the surface:

```cpp
line_rect.x = shiftX;  line_rect.w = m_surface->w;   // w set once, before the loop
dest_rect.x = m_loc.getX();
SDL_BlitSurface(m_surface, &line_rect, screen, &dest_rect);

pad.x = (shiftX < 0) ? 0 : m_surface->w - shiftX;
pad.w = abs(shiftX);
dest_rect.x = m_loc.getX() + pad.x;                  // == pad.x: the SAME x
SDL_BlitSurface(m_surface, &pad, screen, &dest_rect);
```

- `shiftX > 0`: SDL clips the src to `[shiftX, w)`, so the main blit gives
  `dest[0 .. w-shiftX-1] = src[shiftX .. w-1]` and the **right** strip is
  uncovered. The pad then writes `dest[w-shiftX .. w-1] = src[w-shiftX .. w-1]`.
- `shiftX < 0`: SDL clips the negative `srcx` (bumping `dstx` by `|shiftX|`), so
  `dest[|shiftX| .. w-1] = src[0 .. w-|shiftX|-1]` and the **left** strip is
  uncovered. The pad writes `dest[0 .. |shiftX|-1] = src[0 .. |shiftX|-1]`.

Both pads read and write **the same x range**: the uncovered edge columns are
simply *not shifted*. Nothing is ever pulled in from the opposite edge — which is
exactly what a wrap does, and why it was visible: `start`'s background has a
**pure white left edge** (`255,255,255`) and an orange right edge (`232,154,0`),
so wrapping dragged white into the right side.

## Fix

`WavyBackground.ts`'s shader now works in pixel space and reproduces the pad:

```glsl
float x = floor(outTexCoord.x * uSize.x);
float srcX = x + shiftX;
if (srcX < 0.0 || srcX > uSize.x - 1.0) {
    srcX = x;   // in the padded strip -> same-position pixel, unshifted
}
gl_FragColor = texture2D(uMainSampler, vec2((srcX + 0.5) / uSize.x, outTexCoord.y));
```

Also corrected the rounding while here: the original is `(Sint16)(0.5 + amp*sin(...))`
- a C cast, i.e. **truncation toward zero**, not `floor` (`-1.8 -> -1`, where floor
gives `-2`). WebGL1 GLSL has no `trunc()`, so:
`float shiftX = t < 0.0 ? -floor(-t) : floor(t);`

## Verification (real browser)

- `start`: sampled the room's **rightmost pixel column** over 40 frames via
  `gl.readPixels`, counting near-white pixels. **0 across every frame.**
- Confirmed the test actually discriminates: temporarily restoring the wrap makes
  the same column show **24-30 white pixels every frame**. (A fix whose test can't
  fail on the old code proves nothing.)
- Screenshots: `start` and `ufo` both render edge-to-edge, no slivers or gaps.
- e2e 7/7; `tsc -b` clean.

## Also checked
Swept **all 81 levels** for room-size vs background-image mismatches (the other
candidate cause of an edge gap): only `linux` (room 435x480, bg 510x510) and
`nowall` (450x510, bg 450x555) differ, both *larger* - the original draws the bg
at (0,0) at its own size and the screen clips the excess, which is what this
port's crop-to-room-size extraction already does. Not a factor here; `ufo`'s
background matches its room exactly.

## Files
- **Modify:** `web/src/scenes/WavyBackground.ts` (shader edge handling + trunc
  rounding; class comment corrected - it is not a wrap).
