# 048 - Atlas trim cropping opaque images

_2026-07-14_

## Why

User reported the texture-atlas conversion (docs/042) cropping images:

- Level **backgrounds** came out narrower than the source - e.g. submarine's
  555px-wide background packed as 507px.
- **Opaque rectangular** sprites lost their border - `briefcase/cedule.png`
  (the yellow warning sign) and steel assets like `4-ocel.png` had their outer
  edge stripped.

## Cause

`scripts/asset-tools/build-atlas.mjs` called `sharp(f).trim()` with **no
arguments**. sharp's default `.trim()` trims edges matching **the top-left
pixel's colour**. That's correct for a sprite whose corner is transparent (only
transparent padding is removed), but for an **opaque** image the corner pixel is
a solid colour, so trim strips the uniform-coloured border/edge as if it were
padding:

- submarine `zrc-p.png` (555x225, opaque) -> trimmed to 507x222.
- briefcase `cedule.png` (45x75, opaque, black-bordered) -> trimmed to 43x73.

The atlas JSON does record `sourceSize`/`spriteSourceSize` so Phaser repositions
trimmed frames, but here the trimmed pixels were *real content*, not padding, so
they were simply gone.

## Fix

Trim only against **transparency**, never a solid colour:

```js
const canTrim = meta.width >= 3 && meta.height >= 3 && meta.hasAlpha;
const { data, info } = canTrim
  ? await sharp(fullPath)
      .trim({ background: { r: 0, g: 0, b: 0, alpha: 0 }, threshold: 0 })
      .toBuffer({ resolveWithObject: true })
  : await sharp(fullPath).toBuffer({ resolveWithObject: true });
```

- Forcing the trim background to fully transparent means an opaque image (no
  alpha, or alpha but no transparent border) keeps every pixel.
- `threshold: 0` trims only exactly-transparent pixels, so anti-aliased
  semi-transparent edges are preserved.
- Images with **no alpha channel** can't be trimmed against transparency at all,
  so they're packed untrimmed at full size (added `&& meta.hasAlpha` to the
  existing `<3x3` guard).

Transparent sprites still get their padding trimmed (verified 36/66 fish frames
still trimmed), so atlas packing efficiency is unchanged - only opaque images
behave differently.

## Verification

Regenerated all level + fish atlases. Confirmed in the generated JSON:

- submarine `zrc-p`: `w:555,h:225` (was 507x222), `trimmed:false`.
- briefcase `cedule`: `w:45,h:75` (was 43x73), `4-ocel`: `w:30,h:90`.

Real browser (sandbox): submarine background now fills the full 555px room with
no right-edge crop; briefcase's yellow warning sign shows its full black-striped
border intact (screenshots). Fish/item transparent sprites unaffected.

## Files
- **Modify:** `scripts/asset-tools/build-atlas.mjs` (transparent-only trim).
- **Regenerate:** all `web/public/assets/images/**/atlas.{webp,json}` (run
  `scripts/convert-assets.ps1`, or the packer per dir).
