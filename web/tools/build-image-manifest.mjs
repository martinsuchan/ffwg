// Recursively lists every image file under legacy/images/ and writes their
// paths in the exact "images/<...>" form Lua's file_exists("images/...")
// receives (Path::dataReadPath is relative to systemdir, i.e. legacy/).
// This is what lets web/src/lua/levelLoader.ts implement a real
// file_exists instead of always returning false - see docs/009.
//
// Usage: node build-image-manifest.mjs <legacyImagesDir> <outputPath>

import fs from "node:fs/promises";
import path from "node:path";

const IMAGE_EXTENSIONS = new Set([".png", ".jpg", ".jpeg", ".gif", ".bmp"]);

async function findImageFiles(root) {
  const results = [];
  async function walk(dir) {
    const entries = await fs.readdir(dir, { withFileTypes: true });
    for (const entry of entries) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        await walk(full);
      } else if (IMAGE_EXTENSIONS.has(path.extname(entry.name).toLowerCase())) {
        results.push(full);
      }
    }
  }
  await walk(root);
  return results.sort();
}

async function main() {
  const [imagesDir, outputPath] = process.argv.slice(2);
  if (!imagesDir || !outputPath) {
    throw new Error("Usage: node build-image-manifest.mjs <legacyImagesDir> <outputPath>");
  }

  const files = await findImageFiles(imagesDir);
  if (files.length === 0) {
    throw new Error(`No image files found under ${imagesDir}`);
  }

  const manifest = files.map(
    (file) => "images/" + path.relative(imagesDir, file).replace(/\\/g, "/"),
  );

  await fs.mkdir(path.dirname(outputPath), { recursive: true });
  await fs.writeFile(outputPath, JSON.stringify(manifest), "utf8");

  console.log(`Wrote ${manifest.length} image paths to ${outputPath}`);
}

main().catch((err) => {
  console.error(err.message);
  process.exitCode = 1;
});
