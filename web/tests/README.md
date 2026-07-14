# E2e regression suite

Browser-driven regression tests for the web port, using [Playwright](https://playwright.dev/)
(the library, with a tiny custom runner - not `@playwright/test`). They drive
the **dev server** the same way a session would verify a change by hand, so they
double as living examples of how to inspect the running game.

## Running

From the repo root (Windows 11) - starts the dev server if needed, runs, stops it:

```
scripts\test.ps1                 # all cases
scripts\test.ps1 -Filter pedometer   # only cases whose filename matches
scripts\test.ps1 -KeepServer     # leave the dev server running afterwards
```

Or, with a dev server already running (`scripts\start.ps1`), from `web/`:

```
npm run test:e2e                 # = node tests/run.mjs
FFWG_TEST=06 npm run test:e2e    # filter by filename substring (bash)
```

Prerequisites: assets must be built once (`scripts\setup.ps1`), and Playwright's
Chromium (`npx playwright install chromium`, which `scripts\test.ps1` runs for
you). Env vars: `FFWG_BASE_URL` (default `http://localhost:5173/`), `FFWG_TEST`
(filename filter).

## How it works

- `run.mjs` - discovers `cases/NN-*.mjs`, runs each against a fresh browser
  context, prints PASS/FAIL, exits non-zero on any failure.
- `lib.mjs` - shared helpers (`gotoWorldmap`, `canvasMapper`, `nodeXY`, ...).
- Each case default-exports `async ({ page, baseURL }) => { ... }`; it throws
  (or returns `{ pass: false }`) to fail, and may return `{ detail }` for the
  summary line.

Tests read live game state through **`window.__game`** - a dev-only handle set
in `main.ts` (gated on `import.meta.env.DEV`, so it never ships in production).
`window.__game.scene.keys.<key>` reaches each scene's internals.

## Cases

| File | Covers |
|------|--------|
| `01-worldmap` | boot, 4 mask-driven corner buttons + hover reveal, dot-sized node hover (docs/027/038/039) |
| `02-pedometer` | solved-node pedometer: masked buttons, numbers.png digits, localized solver text, clean map, Cancel (docs/039/040) |
| `03-level-and-back` | loading a level from the map, and browser Back returning to it (docs/027/040) |
| `04-settings` | dialog/voice language setting wiring + persistence (docs/038) |
| `05-levels-load` | **all** map levels load their Lua + survive live ticks, headless (docs/024/028/033/035) |
| `06-solutions-validate` | **all** `legacy/solution/*.lua` replay to solved through the ported engine - 80/81, `redhat` has no level content (docs/022/023/024/035) |

`05` and `06` are the broadest gameplay nets and the slowest (~15s / ~7s).

## Production build (separate)

`production-build.mjs` is **not** part of the default suite - it smoke-tests a
built `publish/` package (no `window.__game`; Lua loads from `<site>/legacy/`),
proving the docs/041 production `LEGACY_ROOT` path. Run it after `.\publish.ps1`:

```
npx http-server .\publish -p 8123 -c-1 --silent    # serve the package
node web\tests\production-build.mjs                 # FFWG_PROD_URL to override
```

## Adding a case

Drop a `cases/NN-name.mjs` exporting `name` + a default async function. Prefer
asserting on `window.__game` scene state over pixels; keep it deterministic
(seed `localStorage` via `page.addInitScript` before `gotoWorldmap`). The many
one-off probes under a session's scratchpad are the raw material - promote the
durable ones here.
