# 004 - Texture Atlas Packing

2026-07-06

## Goal

Build the atlas-packing step discussed in `docs/003` (individual per-image
WebP conversion works, but per-level texture atlases are better for Phaser's
WebGL batching): pack a level's PNGs into one WebP image + one Phaser-format
JSON, automated and reproducible like the rest of the asset pipeline.

## What was done

- Checked candidate off-the-shelf tools first: `free-tex-packer-cli` turned
  out to be the same story as `audiosprite` from `docs/003` - last published
  2022, aging dependencies. Composed actively-maintained pieces instead:
  `maxrects-packer` (packing algorithm, published 2025-08-30) + `sharp`
  (image compositing, published days before this session) + a hand-written
  Phaser-format JSON emitter.
- New `scripts/asset-tools/` - a separate small Node project (own
  `package.json`, not a `web/` dependency) since `sharp` ships heavy
  platform-specific native binaries that have no business in the shipped
  game's `node_modules`.
- `scripts/asset-tools/build-atlas.mjs` - for one directory of `.png` files:
  trims transparent borders per-sprite (`sharp().trim()`), packs the trimmed
  rects via `MaxRectsPacker` (non-power-of-2, since Phaser/WebGL don't need
  POT textures and POT would waste real space on these level-sized atlases),
  composites everything onto one transparent canvas, encodes to WebP, and
  writes a TexturePacker "JSON Hash" file - not assumed from memory, but
  confirmed field-for-field against `node_modules/phaser/dist/phaser.esm.js`'s
  actual `Textures.Parsers.JSONHash` implementation (`frame`, `trimmed`,
  `spriteSourceSize`, `sourceSize`, `rotated`), since the `.d.ts` types don't
  contain the runtime parsing logic.
- `scripts/build-atlas.ps1` - thin PowerShell wrapper matching the existing
  scripts/ convention (`npm install` in `asset-tools/` on first run, then
  invokes the Node script). Usage: `scripts\build-atlas.ps1 -Level airplane`.
- Added `public/assets` to `web/.gitignore` already covered `node_modules`
  globally via the pre-existing root `.gitignore`, so `scripts/asset-tools/
  node_modules` needed no new ignore rule.

## Verified

- Caught a real, non-obvious bug before it shipped: probed `sharp`'s
  `.trim()` with a synthetic image (transparent border with *noisy* RGB
  values behind alpha=0, not a clean solid color) to check both that trim
  correctly ignores garbage RGB in fully-transparent pixels (it does), and
  the sign of the returned `trimOffsetLeft`/`trimOffsetTop`. They came back
  **negative** of what TexturePacker's `spriteSourceSize.x/y` expects (a
  40px-inset square reported `trimOffsetLeft: -40`, not `40`) - had this
  gone unverified, every trimmed sprite in every atlas would have been
  mispositioned in-game. Fixed with an explicit negation
  (`offsetX = -(info.trimOffsetLeft ?? 0)`).
- Checked `maxrects-packer`'s actual source (not just its README) to confirm
  `addArray()` stores custom properties (name, buffers, offsets) flat on the
  resulting rect objects rather than nested under `.data` - the README's
  prose and the `.d.ts` overload signatures disagreed on this, so it needed
  reading the real implementation to resolve.
- Ran end-to-end on the real `airplane` level (14 PNGs): packed into a
  675x753 atlas. Cross-checked correctness two ways, not just eyeballing the
  full atlas image: (1) extracted `sedadlo1` (untrimmed) and `letadlo-p`
  (trimmed, offset 108,6) back out of the atlas via `sharp().extract()`
  using the JSON's own coordinates, and compared pixel-for-pixel against the
  original source PNGs - both matched exactly; (2) confirmed `trimmed`
  correctly flips to `true`/`false` per sprite depending on whether trimming
  actually changed the dimensions.

## Open for next time

- Not wired into `scripts/convert-assets.ps1` yet - the orchestrator still
  does one-webp-per-image via `convert-images.ps1` for level images. Once
  atlases are adopted as the real pipeline, decide whether to replace that
  step for per-level folders or keep both (atlases for levels, individual
  WebP for one-off UI/menu images that don't belong to a level).
- Multi-page atlases (a level whose images don't fit in one 2048x2048 page)
  throw an explicit error rather than being handled - none of the levels
  checked so far have needed it, but 80 levels means it will eventually
  come up.
- No edge-extrusion (replicating a sprite's own edge pixels into its padding)
  - only inter-sprite padding via `maxrects-packer`'s `padding` option. Worth
  revisiting if scaled/rotated sprites show filtering seams in practice.
