// Walks every sprite.json under web/public/assets/sound/ (already-converted
// web output, not legacy/sound/ directly - sound is sprite-packed, so
// "does this exist" has to mean "is it in a built sprite," which correctly
// reports false for levels outside whatever's been converted so far,
// rather than resolving a path that would 404 at playback time) and writes
// each spritemap region as a "sound/<dir>/<region>.ogg" string - the exact
// form Lua's file_exists("sound/...") receives via level_dialog.lua's
// dataPathSound() and sound_addSound()'s own file arguments. This is what
// lets web/src/lua/levelScript.ts (and levelLoader.ts) implement a real
// file_exists for sound instead of always returning false - see docs/018.
//
// Usage: node build-audio-manifest.mjs <webAssetsSoundDir> <outputPath>

import fs from "node:fs/promises";
import path from "node:path";

async function findSpriteFiles(root) {
  const results = [];
  async function walk(dir) {
    const entries = await fs.readdir(dir, { withFileTypes: true });
    for (const entry of entries) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        await walk(full);
      } else if (entry.name === "sprite.json") {
        results.push(full);
      }
    }
  }
  await walk(root);
  return results.sort();
}

async function main() {
  const [soundDir, outputPath] = process.argv.slice(2);
  if (!soundDir || !outputPath) {
    throw new Error("Usage: node build-audio-manifest.mjs <webAssetsSoundDir> <outputPath>");
  }

  const spriteFiles = await findSpriteFiles(soundDir);
  if (spriteFiles.length === 0) {
    throw new Error(`No sprite.json files found under ${soundDir}`);
  }

  const manifest = [];
  for (const spriteFile of spriteFiles) {
    const relDir = path.relative(soundDir, path.dirname(spriteFile)).replace(/\\/g, "/");
    const { spritemap } = JSON.parse(await fs.readFile(spriteFile, "utf8"));
    for (const region of Object.keys(spritemap)) {
      manifest.push(relDir ? `sound/${relDir}/${region}.ogg` : `sound/${region}.ogg`);
    }
  }
  manifest.sort();

  await fs.mkdir(path.dirname(outputPath), { recursive: true });
  await fs.writeFile(outputPath, JSON.stringify(manifest), "utf8");

  console.log(`Wrote ${manifest.length} sound paths (from ${spriteFiles.length} sprites) to ${outputPath}`);
}

main().catch((err) => {
  console.error(err.message);
  process.exitCode = 1;
});
