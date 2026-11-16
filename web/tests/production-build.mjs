// Standalone smoke test for a PRODUCTION build (the publish/ package) - NOT run
// by the default suite (which targets the dev server), because production has
// no window.__game handle and loads Lua from <site>/legacy/ instead of /@fs/.
//
// It proves the docs/041 LEGACY_ROOT prod path works end to end: the world map
// boots by fetching worldmap.lua from /legacy/, and clicking a node loads that
// level's models.lua from /legacy/.
//
// How to run:
//   1) .\publish.ps1
//   2) serve the folder, e.g.:  npx http-server .\publish -p 8123 -c-1 --silent
//   3) node web/tests/production-build.mjs        (FFWG_PROD_URL to override)

import { chromium } from "playwright";

const URL = process.env.FFWG_PROD_URL ?? "http://localhost:8123/";

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1100, height: 900 } });

const errors = [];
page.on("pageerror", (e) => errors.push("pageerror: " + e.message));
const legacy = new Set();
const failed = [];
page.on("response", (r) => {
  const u = r.url();
  if (u.includes("/legacy/")) legacy.add(`${r.status()} ${u.split("/legacy/")[1]}`);
  // Ignore optional per-level voice sprites that legitimately don't exist for
  // every level/language (the docs/036 English-fallback probe).
  if (r.status() >= 400 && !/\/assets\/sound\/.*\/sprite\.json$/.test(u)) failed.push(`${r.status()} ${u}`);
});

let ok = true;
const fail = (msg) => { ok = false; console.log("FAIL  " + msg); };

await page.goto(URL, { waitUntil: "load", timeout: 30000 });
await page.waitForTimeout(6000);

const canvas = await page.evaluate(() => !!document.querySelector("canvas"));
if (!canvas) fail("no canvas rendered");
if (![...legacy].some((s) => s.startsWith("200") && s.includes("worldmap.lua"))) {
  fail("world map did not fetch worldmap.lua from /legacy/");
}

// Click the "start" node (map-space 320,121) to load a level.
const box = await page.evaluate(() => {
  const c = document.querySelector("canvas"); const r = c.getBoundingClientRect();
  return { x: r.x, y: r.y, w: r.width, h: r.height, cw: c.width, ch: c.height };
});
// The framebuffer is the 640x480 map times the render-scale camera zoom
// (docs/064), so map coordinates scale by cw/640 first.
const zoom = box.cw / 640;
const p = { x: box.x + 320 * zoom * (box.w / box.cw), y: box.y + 121 * zoom * (box.h / box.ch) };
await page.mouse.move(p.x, p.y);
await page.waitForTimeout(150);
await page.mouse.click(p.x, p.y);
// The production level load (Lua fetches + live-engine prewarm) can take several
// seconds, so poll rather than waiting a fixed time.
const loadedModels = () => [...legacy].some((s) => s.includes("/models.lua"));
for (let waited = 0; !loadedModels() && waited < 15000; waited += 250) await page.waitForTimeout(250);

if (!loadedModels()) fail("clicking a node did not load models.lua from /legacy/");
if (failed.length) fail("HTTP failures: " + failed.slice(0, 3).join(", "));
if (errors.length) fail("page errors: " + errors.slice(0, 3).join(", "));

console.log(ok ? "PASS: production build boots + loads a level from /legacy/" : "");
await browser.close();
process.exit(ok ? 0 : 1);
