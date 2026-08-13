// Exports every world-map level's physics-relevant geometry to JSON for the
// C# solver (solver/, see solver/docs/001).
//
// Why a browser script: web/src/lua/levelLoader.ts is the only thing that
// knows how to turn legacy/script/<level>/{models,code}.lua into a model list
// (wasmoon + the Lua 5.0 compat shim + the file_include pre-scan + the
// final-level goal reassignment - docs/005/008/024), and it's written against
// browser globals (fetch, window.location.origin, import.meta.env). Rather
// than fork it for Node, this drives the real dev server through Playwright -
// exactly the pattern web/tests/cases/05 already uses to sweep all 80 levels.
//
// Output: one solver/levels/<codename>.json per level, holding ONLY what the
// physics needs - room size plus each model's kind/position/shape/goal/facing.
// Animation frames, pictures and waves are deliberately dropped (the solver has
// no rendering, dialogs or scripting - see solver/docs/001).
//
// Usage (from web/):  node tools/export-levels.mjs      (needs the dev server)
// Or via PowerShell:  scripts\export-levels.ps1         (starts/stops it for you)
//
// Env:
//   FFWG_BASE_URL   dev-server URL (default http://localhost:5173/)
//   FFWG_OUT_DIR    output directory (default <repo>/solver/levels)

import { chromium } from "playwright";
import { mkdirSync, writeFileSync } from "fs";
import { fileURLToPath } from "url";
import { dirname, join, resolve } from "path";

import { gotoWorldmap, nodeCodenames } from "../tests/lib.mjs";

const BASE_URL = process.env.FFWG_BASE_URL ?? "http://localhost:5173/";
const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, "..", "..");
const OUT_DIR = process.env.FFWG_OUT_DIR ?? join(repoRoot, "solver", "levels");

const browser = await chromium.launch();
const context = await browser.newContext({ viewport: { width: 1100, height: 900 } });
const page = await context.newPage();

let failures = 0;
try {
  await gotoWorldmap(page, BASE_URL);
  const codenames = await nodeCodenames(page);
  // The ending level ("both fish at home", docs/050) is a real playable room
  // reachable only via branch_setEnding, so it isn't in the node list.
  const ending = await page.evaluate(
    () => window.__game.scene.keys.worldmap.mapData.ending?.codename ?? null,
  );
  const all = ending ? [...codenames, ending] : [...codenames];

  console.log(`Exporting ${all.length} level(s) from ${BASE_URL} -> ${OUT_DIR}\n`);
  mkdirSync(OUT_DIR, { recursive: true });

  const results = await page.evaluate(async (cns) => {
    const loader = await import("/src/lua/levelLoader.ts");
    const out = [];
    for (const cn of cns) {
      try {
        const level = await loader.loadLevelModels(cn);
        out.push({
          cn,
          ok: true,
          data: {
            name: cn,
            width: level.roomWidth,
            height: level.roomHeight,
            models: level.models.map((m) => ({
              kind: m.kind,
              x: m.x,
              y: m.y,
              shape: m.shape,
              goal: m.goal,
              isLeft: m.isLeft,
            })),
          },
        });
      } catch (e) {
        out.push({ cn, ok: false, err: String((e && e.message) || e) });
      }
    }
    return out;
  }, all);

  const index = [];
  for (const r of results) {
    if (!r.ok) {
      failures++;
      console.log(`\x1b[31mFAIL\x1b[0m  ${r.cn}: ${r.err}`);
      continue;
    }
    // Stable, diff-friendly output: the file is checked in, so churn matters.
    writeFileSync(join(OUT_DIR, `${r.cn}.json`), JSON.stringify(r.data, null, 1) + "\n");
    const movable = r.data.models.filter((m) => m.kind !== "item_fixed").length;
    index.push(r.cn);
    console.log(
      `\x1b[32mOK\x1b[0m    ${r.cn.padEnd(14)} ${String(r.data.width).padStart(3)}x${String(r.data.height).padStart(2)}` +
        `  ${String(r.data.models.length).padStart(4)} models (${movable} movable)`,
    );
  }
  writeFileSync(join(OUT_DIR, "index.json"), JSON.stringify(index, null, 1) + "\n");

  console.log(`\n${index.length}/${results.length} exported${failures ? ` - ${failures} FAILED` : ""}`);
} finally {
  await context.close();
  await browser.close();
}

process.exit(failures ? 1 : 0);
