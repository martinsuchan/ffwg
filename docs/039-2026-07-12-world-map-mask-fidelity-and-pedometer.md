# 039 - World-map mask fidelity, thicker edges, dot-sized hover, real pedometer

_2026-07-12_

Follow-up polish to docs/038's world map, all cross-checked against the
original (`legacy/src/menu/`): lossless masks, faithful edge/hover rendering,
and a real mask-driven Pedometer.

## Lossless mask assets

`map_mask.webp` and `pedometer_mask.webp` are the flat-color button-region
masks read pixel-by-pixel (`getImageData`) for hit-testing and for building the
prelit hover shapes. The bulk `convert-images.ps1` run encodes lossy WebP
(quality 85), which smears the flat fills at region boundaries and breaks the
exact-color matching (ragged/holey button shapes, mis-detected corners). Both
were reconverted with `ffmpeg -c:v libwebp -lossless 1` (verified: the 640x480
map mask decodes to exactly **5** unique colors - 4 buttons + background). The
prelit/normal art (`*_lower`, `map`, `pedometer`) stays lossy; only the masks
need to be exact.

Since the converted assets are gitignored build artifacts, this is also baked
into `scripts/convert-images.ps1`: any `*_mask.*` file is now forced lossless
regardless of the `-Lossless` switch, so a future full bulk conversion (lossy
by default) can't silently revert them.

## Shared masked-texture helpers (`sceneUtils.ts`)

docs/038's corner-reveal logic (read a texture's pixels; build a canvas texture
of a *source* image masked to one flat *mask* color) is now shared, since the
pedometer needs the identical trick: `readTexturePixels`, `packRgb`,
`buildMaskedTexture`. `WorldMapScene` was refactored onto them (its private
`readImagePixels`/`buildCornerTexture`/`sampleMask` removed); `PedometerUI` uses
them too.

## World-map rendering fixes (`WorldMapScene.ts`)

- **Thicker edges** - legacy `NodeDrawer::drawEdge` draws solid yellow
  (`0xdea500`) as 5 overlaid anti-aliased lines (centre + 4 diagonal ±1
  offsets) ≈ a 3px stroke. The port drew a 1px dim gold line; now
  `lineStyle(3, 0xdea500, 1)`.
- **Dot-sized hover highlight** - legacy `NodeDrawer::drawSelect` tints the
  hovered dot with a translucent yellow disc *the size of the dot*
  (`radius = max(dotW, dotH)/2 + 1`, `0xffc618` @ 50%), drawn over it. The port
  drew a `NODE_HIT_RADIUS + 2` (=15px) disc *behind* the dot - a large yellow
  halo. Now the radius is derived from the `node-solved` (n0.png, 19x20 -> 11)
  texture and drawn on top (depth 4).
- **Modal input guard** - the scene-level corner `pointermove`/`pointerdown`
  handlers now early-return while `pedometerUI`/`optionsOverlay` is showing
  (`isModalOpen()`), so corners don't light/dispatch under an open overlay.

## Real Pedometer (`PedometerUI.ts`)

Rewritten from plain text buttons + a text move counter to match legacy
`Pedometer.cpp`'s `LayeredPicture` rack:

- **Masked buttons** - `pedometer.png` is the rack art (Run/Replay/Cancel icons
  already drawn on it) at (193,141); `pedometer_mask.png` marks each button
  region, sampled at the original's panel-relative points (Run 86,100 / Replay
  128,100 / Cancel 170,100). Hovering a button reveals `pedometer_lower.png`'s
  prelit pixels through that region (a `buildMaskedTexture` per button), and a
  click runs it. Hover/click ride on the full-screen backdrop (the topmost
  interactive object while shown), so node/corner input underneath is inert.
- **numbers.png digits** - the 5-cipher step count is drawn from `numbers.png`
  (a vertical strip of digits 9..0, each 19x24) loaded as a spritesheet, blitted
  at the original's absolute (275,177), one digit every 19px, with leading
  zeros. numbers.png's rows run 9 (top) to 0 (bottom), so digit `d` -> frame
  `9 - d`. The original's per-digit "slot machine" roll is still simplified to a
  count-up tween, now over the real digit art instead of a Text object.

The port's own extras (level-name label above the panel, English best-solution
comparison line below it) are kept.

## Verification (dev server, temp `window.__game`, removed after)

- Map mask decodes to 5 unique colors (lossless); 4 corners still detected.
- Node hover: selection ring radius 11, depth 4.
- Solved node (seeded solution) opens the Pedometer: 3 masked buttons built
  (`pedo-run`/`replay`/`cancel`), 5 digit sprites, count-up reads the right
  value (12 moves -> `00012`), hovering the Run region lights `pedo-run`.
- Screenshot confirms the rack renders with lit Run button, icon buttons, and
  thicker yellow edges. `tsc -b` clean; debug hook removed.

## Files
- **Assets:** reconvert `web/public/assets/images/menu/{map_mask,pedometer_mask}.webp`
  lossless.
- **Modify:** `scripts/convert-images.ps1` (force lossless for `*_mask.*`),
  `web/src/scenes/sceneUtils.ts` (shared mask helpers),
  `web/src/scenes/WorldMapScene.ts` (helpers + edges + hover + modal guard +
  pedometer asset preloads), `web/src/scenes/PedometerUI.ts` (rewrite).
