# 002 - Wasmoon Lua POC

2026-07-06

## Goal

Prove that `wasmoon` (Lua interpreter in the browser) works inside the `web/`
Phaser+Vite project: load a standalone `.lua` file at runtime and confirm
callbacks between Lua and TypeScript work in both directions, since that's
the exact mechanism the real port will need to reimplement the legacy
C++ `*-script.cpp` host API.

## What was done

- Installed `wasmoon@^1.16.0` (current npm `latest`; a `2.0.0-next` prerelease
  exists but wasn't used). Checked its shipped `.d.ts` files directly instead
  of relying on possibly-stale docs — confirmed `LuaFactory` /
  `factory.createEngine()` / `engine.global.set/get` / `engine.doString()` /
  `engine.global.close()`, and that plain JS objects/arrays round-trip as Lua
  tables (`type-extensions/table.d.ts`).
- Added `web/public/lua/sample.lua` — a standalone script (not bundled into
  the JS, fetched at runtime via `/lua/sample.lua`) exercising three
  Lua<->JS boundary shapes:
  1. one-way call out (`host_log(message)`)
  2. call with a return value (`host_add(a, b)`)
  3. passing a table in and reading a table back (`host_describe_fish(fish)`)
- Added `web/src/lua/luaPoc.ts` (`runLuaPoc(scriptUrl)`): fetches the script
  text, spins up a fresh wasmoon engine, registers the three host functions,
  runs the script, and returns the collected log lines.
- Wired it into `web/src/main.ts`'s existing scene: on `create()`, runs the
  POC and renders each log line as on-screen text (or an error line in red if
  it throws), so success is visible without opening dev tools.

## Verified

- `tsc -b` type-checks clean.
- Both `npm run dev` (esbuild pre-bundling) and `npm run build` (Rollup
  production build) work. Rollup prints one harmless warning — wasmoon's
  `require('module')` gets externalized for browser compatibility, matching
  a known note in wasmoon's own README — no runtime impact.
- Headless-Chromium (Playwright) run of both the dev server and the built
  `vite preview` output shows, with zero console errors:
  ```
  [lua] hello from sample.lua
  [lua] host_add(2, 3) = 5
  [lua] loop iteration 1/2/3
  [lua] host_describe_fish -> small at (11,5)
  [lua] sample.lua finished
  ```
  The table case (`host_describe_fish`) confirms table marshaling works in
  both directions (`{name, x, y}` in, modified copy back), which is the shape
  most of the real level data (`models.lua`) will need.
- Gotcha for next time: `page.goto(..., { waitUntil: "networkidle" })` hangs
  forever against the Vite dev server (its HMR websocket never goes idle) —
  use `waitUntil: "load"` and wait for an explicit completion signal instead.
  Also, wasmoon's cold-start wasm compile takes a couple of seconds on first
  load — don't assume it's done after a short fixed sleep.

## Open for next time

- This used a **new** sample script, so it doesn't yet tell us whether the
  **actual legacy Lua 5.0 scripts** (`legacy/script/<level>/*.lua`) run
  as-is under wasmoon's Lua 5.4. That's the next real risk to spike: fetch
  one real level's `models.lua`/`code.lua` and see what breaks.
- No decision yet on `fengari` as a fallback — only needed if a real-script
  spike turns up something wasmoon can't handle.
- Current POC creates a fresh engine and throws it away each run; the real
  integration will need a longer-lived engine/lifecycle tied to level
  load/unload.
