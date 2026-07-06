// Packs every .png directly inside --source into one Phaser texture atlas:
// a single WebP image + a TexturePacker "JSON Hash" file, the exact format
// Phaser's Textures.Parsers.JSONHash reads (verified against
// node_modules/phaser/dist/phaser.esm.js directly, not just its .d.ts types).
//
// Usage:
//   node build-atlas.mjs --source <dir> --output <path-without-extension> \
//       [--padding 2] [--max-size 2048] [--quality 90]

import fs from "node:fs/promises";
import path from "node:path";
import sharp from "sharp";
import { MaxRectsPacker } from "maxrects-packer";

function parseArgs(argv) {
  const args = { padding: 2, maxSize: 2048, quality: 90 };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--source") args.source = argv[++i];
    else if (a === "--output") args.output = argv[++i];
    else if (a === "--padding") args.padding = Number(argv[++i]);
    else if (a === "--max-size") args.maxSize = Number(argv[++i]);
    else if (a === "--quality") args.quality = Number(argv[++i]);
  }
  if (!args.source || !args.output) {
    throw new Error(
      "Usage: node build-atlas.mjs --source <dir> --output <path-without-extension> " +
        "[--padding N] [--max-size N] [--quality N]"
    );
  }
  return args;
}

async function loadSprite(source, fileName) {
  const name = path.basename(fileName, ".png");
  const fullPath = path.join(source, fileName);

  const meta = await sharp(fullPath).metadata();
  const { data, info } = await sharp(fullPath).trim().toBuffer({ resolveWithObject: true });

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
  const { source, output, padding, maxSize, quality } = parseArgs(process.argv.slice(2));

  const pngFiles = (await fs.readdir(source, { withFileTypes: true }))
    .filter((e) => e.isFile() && e.name.toLowerCase().endsWith(".png"))
    .map((e) => e.name)
    .sort();

  if (pngFiles.length === 0) {
    throw new Error(`No .png files found directly in ${source}`);
  }

  const sprites = [];
  for (const fileName of pngFiles) {
    sprites.push(await loadSprite(source, fileName));
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
    .webp({ quality, lossless: false })
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
