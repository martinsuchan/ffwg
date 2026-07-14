# 044 - Committed e2e regression suite

_2026-07-13_

Every session so far verified changes with throwaway Playwright scripts in its
scratchpad - re-deriving the same "boot the map / load a level / sweep all
levels" harness each time, and depending on a `window.__game` debug handle
added and removed by hand per run. This commits a durable version so future
sessions (and CI) can run the regression net directly.

## Dev-only game handle (the enabler)

`main.ts` now sets `window.__game` **permanently but gated on
`import.meta.env.DEV`**, so tests always have it in dev and it's statically
stripped from production builds (verified in docs/041 - production has no
handle). No more per-run add/remove dance.

## Structure (`web/tests/`)

- `run.mjs` - discovers `cases/NN-*.mjs`, runs each against a fresh Playwright
  browser context, prints PASS/FAIL + a summary, exits non-zero on any failure.
  `FFWG_BASE_URL` / `FFWG_TEST` (filename filter) env vars. Uses the `playwright`
  library with a tiny custom runner (not `@playwright/test`).
- `lib.mjs` - shared helpers (`gotoWorldmap`, `canvasMapper` for the CSS-zoomed
  canvas, `nodeXY`/`nodeCodenames`, `assert`).
- `cases/` - one file per area, each default-exporting
  `async ({ page, baseURL }) => { ... }` (throws / returns `{pass:false}` to
  fail, `{detail}` for the summary line):
  1. `01-worldmap` - boot, 4 mask-driven corner buttons + hover reveal, 5-color
     lossless mask, dot-sized node hover (docs/027/038/039).
  2. `02-pedometer` - solved-node pedometer: masked buttons, numbers.png digits
     reading `00012`, localized `solver_*` text with no bg, clean map (dots +
     edges hidden, alpha-0 backdrop), Cancel restores (docs/039/040).
  3. `03-level-and-back` - load a level from the map; browser Back returns to it
     (docs/027/040).
  4. `04-settings` - dialog/voice language setting drives the voice dir +
     persists (docs/038).
  5. `05-levels-load` - **all 80** map levels' `loadLevelModels` +
     `createLevelScript` + 5 live ticks, headless (catches per-round unbound
     host fns - docs/024/028/033/035); passes a no-op `EngineControl` so windoze
     doesn't throw.
  6. `06-solutions-validate` - **all 81** `legacy/solution/*.lua` replay to
     solved through the ported engine; 80/81, `redhat` excluded (no level
     content in this repo) (docs/022/023/024/035). Strongest gameplay net.
- `production-build.mjs` - standalone (not in the default runner): smoke-tests a
  built `publish/` package (no `window.__game`; Lua from `<site>/legacy/`),
  proving the docs/041 prod `LEGACY_ROOT` path. Run after `scripts\publish.ps1`
  + serving the folder.
- `README.md` - how to run + add cases.

## Running

- `scripts\test.ps1` (new) - ensures Playwright's Chromium, **starts the dev
  server if one isn't up** (launches Vite via `node node_modules\vite\bin\vite.js`
  - a hidden `Start-Process npm run dev` exits without keeping Vite alive,
  whereas the node process binds the port and `stop.ps1` kills it by port),
  runs the suite, stops the server it started. `-Filter <substr>`, `-Port`,
  `-KeepServer`.
- `npm run test:e2e` (= `node tests/run.mjs`) against an already-running dev
  server. `playwright` added as a `web/` devDependency (pinned to the cached
  1.61 browser build).

## Verification

Full run green via both paths: `6/6 passed` - `05` sweeps 80 levels clean
(~15s), `06` validates 80/81 solutions (~7s), the 4 UI/nav cases pass (~1-5s
each). `scripts\test.ps1` correctly starts+stops the server. `production-build.mjs`
passes against a served `publish/`. `tsc -b` clean.

## Files
- **New:** `web/tests/{run.mjs,lib.mjs,README.md,production-build.mjs}`,
  `web/tests/cases/0{1..6}-*.mjs`, `scripts/test.ps1`.
- **Modify:** `web/src/main.ts` (dev-only `window.__game`), `web/package.json`
  (`playwright` devDep + `test:e2e` script), `CLAUDE.md`.

## Open
- No unit-test layer (pure `Field`/`Rules`/etc. in isolation) - the headless
  sweeps cover the engine end-to-end instead.
- The suite targets the dev server; wiring it into a CI workflow (+ the prod
  smoke test) is a future step.
