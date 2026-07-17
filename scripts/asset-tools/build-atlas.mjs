// Packs every .png under --source (recursively) into one Phaser texture atlas:
// a single WebP image + a TexturePacker "JSON Hash" file, the exact format
// Phaser's Textures.Parsers.JSONHash reads (verified against
// node_modules/phaser/dist/phaser.esm.js directly, not just its .d.ts types).
//
// Each frame is named by its path relative to --source, forward-slashed, minus
// the ".png" extension (e.g. "letadlo-p", or "left/body_0" for a nested fish
// sprite) - the same string the runtime derives from a Lua picture path. The
// recursion + relative naming is what lets the shared fish tree
// (images/fishes/<variant>/{left,right,heads/...}) pack into one atlas without
// its left/right "body_0.png" basenames colliding.
//
// Usage:
//   node build-atlas.mjs --source <dir> --output <path-without-extension> \
//       [--padding 2] [--max-size 2048] [--quality 90]

import fs from "node:fs/promises";
import path from "node:path";
import sharp from "sharp";
import { MaxRectsPacker } from "maxrects-packer";

function parseArgs(argv) {
  const args = { padding: 2, maxSize: 2048, quality: 90, lossless: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--source") args.source = argv[++i];
    else if (a === "--output") args.output = argv[++i];
    else if (a === "--padding") args.padding = Number(argv[++i]);
    else if (a === "--max-size") args.maxSize = Number(argv[++i]);
    else if (a === "--quality") args.quality = Number(argv[++i]);
    else if (a === "--lossless") args.lossless = true;
  }
  if (!args.source || !args.output) {
    throw new Error(
      "Usage: node build-atlas.mjs --source <dir> --output <path-without-extension> " +
        "[--padding N] [--max-size N] [--quality N] [--lossless]"
    );
  }
  return args;
}

// Recursively collect every .png under `dir`, returned as paths relative to
// `root` (forward-slashed) so the frame name carries the subdir (fish left/right).
async function collectPngs(dir, root = dir) {
  const entries = await fs.readdir(dir, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      files.push(...(await collectPngs(full, root)));
    } else if (entry.isFile() && entry.name.toLowerCase().endsWith(".png")) {
      files.push(path.relative(root, full).split(path.sep).join("/"));
    }
  }
  return files;
}

async function loadSprite(source, relPath) {
  // Frame name = relative path minus ".png" (forward slashes already normalized
  // by collectPngs). Flat level dirs -> just the basename; nested fish dirs ->
  // "left/body_0" etc.
  const name = relPath.replace(/\.png$/i, "");
  const fullPath = path.join(source, relPath);

  const meta = await sharp(fullPath).metadata();
  // Only trim *transparent* padding, never a solid-colour border. sharp's
  // default .trim() trims edges matching the top-left pixel, which for an
  // opaque image (a level background, or a bordered rectangle like 4-ocel.png /
  // cedule.png whose corner pixel is the black border colour) strips the real
  // border/edge as if it were padding - cropping the sprite (e.g. submarine's
  // 555px background packed as 507px). Forcing the trim background to fully
  // transparent means opaque images keep every pixel, while sprites with an
  // alpha border still get their transparent padding removed. Semi-transparent
  // edges are preserved (threshold 0 - only exactly-transparent pixels trim).
  // Also: sharp's .trim() throws on anything smaller than 3x3 (the extra fish's
  // tiny placeholder heads), and an image with no alpha channel can't be
  // trimmed against transparency at all - pack both untrimmed at full size,
  // matching the old per-file conversion.
  const canTrim = meta.width >= 3 && meta.height >= 3 && meta.hasAlpha;
  const { data, info } = canTrim
    ? await sharp(fullPath)
        .trim({ background: { r: 0, g: 0, b: 0, alpha: 0 }, threshold: 0 })
        .toBuffer({ resolveWithObject: true })
    : await sharp(fullPath).toBuffer({ resolveWithObject: true });

  return {
    name,
    width: info.width,
    height: info.height,
    buffer: data,
    sourceWidth: meta.width,
    sourceHeight: meta.height,
    // sharp's trimOffsetLeft/Top are the negation of TexturePacker's
    // spriteSourceSize.x/y (the offset of the trimmed content within the
    // original canvas) - confirmed empirically, not assumed from docs.
    offsetX: -(info.trimOffsetLeft ?? 0),
    offsetY: -(info.trimOffsetTop ?? 0),
  };
}

async function main() {
  const { source, output, padding, maxSize, quality, lossless } = parseArgs(process.argv.slice(2));

  const pngFiles = (await collectPngs(source)).sort();

  if (pngFiles.length === 0) {
    throw new Error(`No .png files found under ${source}`);
  }

  const sprites = [];
  for (const relPath of pngFiles) {
    sprites.push(await loadSprite(source, relPath));
  }

  // pot:false - non-power-of-2 textures are fine in WebGL/Phaser and POT
  // would waste real space on these per-level atlases.
  const packer = new MaxRectsPacker(maxSize, maxSize, padding, {
    smart: true,
    pot: false,
    square: false,
    allowRotation: false,
  });

  packer.addArray(sprites);

  if (packer.bins.length > 1) {
    throw new Error(
      `Sprites in ${source} don't fit in a single ${maxSize}x${maxSize} atlas page ` +
        `(needed ${packer.bins.length} pages). Multi-page atlases aren't implemented yet - ` +
        `raise --max-size or split the source folder.`
    );
  }

  const bin = packer.bins[0];
  const composites = bin.rects.map((rect) => ({ input: rect.buffer, left: rect.x, top: rect.y }));

  const frames = {};
  for (const rect of bin.rects) {
    const trimmed = rect.width !== rect.sourceWidth || rect.height !== rect.sourceHeight;
    frames[rect.name] = {
      frame: { x: rect.x, y: rect.y, w: rect.width, h: rect.height },
      rotated: false,
      trimmed,
      spriteSourceSize: { x: rect.offsetX, y: rect.offsetY, w: rect.width, h: rect.height },
      sourceSize: { w: rect.sourceWidth, h: rect.sourceHeight },
    };
  }

  const outDir = path.dirname(output);
  await fs.mkdir(outDir, { recursive: true });

  const imageFileName = `${path.basename(output)}.webp`;
  await sharp({
    create: {
      width: bin.width,
      height: bin.height,
      channels: 4,
      background: { r: 0, g: 0, b: 0, alpha: 0 },
    },
  })
    .composite(composites)
    .webp(lossless ? { lossless: true } : { quality, lossless: false })
    .toFile(`${output}.webp`);

  const json = {
    frames,
    meta: {
      app: "ffwg-asset-tools/build-atlas",
      version: "1.0",
      image: imageFileName,
      format: "RGBA8888",
      size: { w: bin.width, h: bin.height },
      scale: "1",
    },
  };

  await fs.writeFile(`${output}.json`, JSON.stringify(json, null, 2), "utf8");

  console.log(
    `Packed ${sprites.length} sprites into ${bin.width}x${bin.height} atlas: ${output}.webp`
  );
}

main().catch((err) => {
  console.error(err.message);
  process.exitCode = 1;
});
