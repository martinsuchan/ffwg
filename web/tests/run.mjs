// E2e regression runner for the Fish Fillets web port.
//
// Loads every cases/NN-*.mjs test module, runs each against a fresh browser
// context pointed at the dev server, and prints a pass/fail summary. Exits
// non-zero if anything fails (so CI / a shell `&&` chain can gate on it).
//
// Usage (from web/):  npm run test:e2e        (needs the dev server running)
// Or via PowerShell:  scripts\test.ps1        (starts/stops the server for you)
//
// Env:
//   FFWG_BASE_URL   dev-server URL (default http://localhost:5173/)
//   FFWG_TEST       substring filter - only run cases whose filename matches
//
// See web/tests/README.md.

import { chromium } from "playwright";
import { readdirSync } from "fs";
import { fileURLToPath, pathToFileURL } from "url";
import { dirname, join } from "path";

const BASE_URL = process.env.FFWG_BASE_URL ?? "http://localhost:5173/";
const FILTER = process.env.FFWG_TEST ?? "";
const here = dirname(fileURLToPath(import.meta.url));
const casesDir = join(here, "cases");

const files = readdirSync(casesDir)
  .filter((f) => /^\d.*\.mjs$/.test(f))
  .filter((f) => !FILTER || f.includes(FILTER))
  .sort();

if (files.length === 0) {
  console.error(`No test cases found in ${casesDir}` + (FILTER ? ` matching "${FILTER}"` : ""));
  process.exit(2);
}

console.log(`Running ${files.length} test case(s) against ${BASE_URL}\n`);

const browser = await chromium.launch();
let failures = 0;

for (const file of files) {
  const mod = await import(pathToFileURL(join(casesDir, file)).href);
  const name = mod.name ?? file;
  const context = await browser.newContext({ viewport: { width: 1100, height: 900 } });
  const page = await context.newPage();
  const started = Date.now();
  try {
    const result = await mod.default({ page, baseURL: BASE_URL });
    const ms = Date.now() - started;
    const detail = result && result.detail ? `  - ${result.detail}` : "";
    console.log(`\x1b[32mPASS\x1b[0m  ${name}  (${ms}ms)${detail}`);
  } catch (err) {
    failures++;
    const ms = Date.now() - started;
    console.log(`\x1b[31mFAIL\x1b[0m  ${name}  (${ms}ms)`);
    console.log(`      ${err && err.message ? err.message : err}`);
  } finally {
    await context.close();
  }
}

await browser.close();

const passed = files.length - failures;
console.log(`\n${passed}/${files.length} passed${failures ? ` - ${failures} FAILED` : ""}`);
process.exit(failures ? 1 : 0);
