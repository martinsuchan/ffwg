# web/CLAUDE.md — the browser port

Loaded on demand when Claude touches files under `web/`. Stable architecture and invariants
only; per-feature reasoning is in `docs/NNN-*.md` (index: `docs/README.md`, open items:
`docs/BACKLOG.md`, older summaries: `docs/HISTORY.md` — grep it, it's large).

## Goal and approach

Run the original game in the browser, **faithful to FF NG**: reuse the legacy Lua content
(`legacy/script/**`: level layouts, dialogs, scripted room behavior) unmodified, port the
puzzle physics line-for-line to TypeScript, and re-implement the C++ "host API" the Lua calls
(`legacy/src/**/*-script.cpp`) as wasmoon bindings. When behavior is in doubt, read the
original C++/Lua and match it; deliberate deviations are documented in their `docs/NNN`.
Inputs: keyboard + mouse done; gamepad/touch not yet.

Stack: **Phaser 4** (`phaser@^4.2.0`), **wasmoon** (`wasmoon@^1.16.0`, Lua 5.4/WASM),
TypeScript + Vite. Grid cell = 15 px (`legacy/src/level/View.h` `SCALE`).

## Source map (`web/src/`)

- `main.ts` — boots Phaser; init of labels/history/playtime/fullscreen; dev-only
  `window.__game` handle used by the e2e tests.
- `game/` — **physics port** of `legacy/src/level/`: `Field`, `MarkMask`, `Rules`,
  `Landslip`, `Cube`, `Unit`, `Goal`, `ModelFactory`, `Room`, `FinderAlg`, `MouseControl`,
  `Controls` (active fish, queued key, move recording). `GameEngine.ts` wraps a Room
  (`RenderModel` snapshot, `getMoves`, `isFresh`, `askFieldIndex`...). `SolutionValidator.ts`
  = headless replay. `UnitAnimator.ts` = *which* fish anim (port of `level_update.lua`).
  `timing.ts` = `CYCLE_MS`. `worldMapState.ts` = node states/progression. `appMode.ts` =
  `/` standard vs `/sandbox`.
- `lua/` — `levelLoader.ts`: one-shot engine per level load (models.lua + code.lua →
  `LevelData`), `LEGACY_ROOT`/`fetchLegacyFile`, manifests. `levelScript.ts`: the
  **persistent live engine** for a play session — all per-round host bindings, dialogs
  (`DialogStack` port), save-state pickling, `EngineControl` bridge. `demoScript.ts`:
  movie/poster runner. `worldMapLoader.ts`: worldmap/worlddesc/worldfame/labels.
  `dialogSound.ts`: shared dialog/sound-path helpers.
- `scenes/` — `WorldMapScene` (hub; launches levels and prewarms their live engine),
  `LevelScene` (play), `ReplayScene`, `DemoScene` (movie/poster), `IntroScene`,
  `CreditsScene`; overlays `HelpOverlay`, `OptionsOverlay`, `PedometerUI`, `SaveSlotUI`,
  `SubtitleStack`; `ModelAnimator` (one per model: frames + position from the shared clock),
  `AudioManager` + `audioEngine` (Web Audio buffers), `WavyBackground` (bg ripple shader),
  `atlas.ts` (Lua picture path → atlas frame), `sceneUtils.ts` (`applyRenderScale`,
  `crispText`, `drawRopeDecors`, mask helpers).
- `storage/` — localStorage: `levelStorage` (best solutions + save slots), `settingsStorage`,
  `playtime`, `progressBackup` (JSON backup/restore, re-validates solutions).
- `i18n.ts` (`t()`; legacy `labels.lua` + port-only labels), `navigation.ts` (browser
  Back → map), `fullscreen.ts` (native F11 layout).
- `public/lua/` — `lua50-compat.lua` shim + generated manifests; `public/assets/` = converted
  assets (**gitignored**, produced by `scripts/convert-assets.ps1`; change the scripts, not
  the output). `tools/` = Node build helpers. `tests/` = e2e suite.

## Architecture decisions (details in the cited docs)

- **Ownership split:** physics + fish animation are TypeScript; item/decor animation, dialogs,
  music and level gags run in the **live Lua** engine (014). Live Lua stays physics-free
  except via the opt-in `EngineControl` bridge (windoze `busy`/`checkActive`/fast-falling,
  `model_equals` field queries) (035, 054).
- **Timing:** one shared animation clock, phase-locked like legacy `PhaseLocker`;
  `CYCLE_MS` = 100 is the single base-speed knob; phase count comes from the active fish only
  (so pusher + pushed stay in lockstep); pure gravity falls take 1 cycle (046, 049). Lua and
  dialog timing are counted in cycles. Replay/save-resume settle falls instantly with no
  acceleration (legacy `Room::loadMove`) (053).
- **Rendering:** per-level + shared-fish texture atlases (042); camera-zoom render scale for
  the Game-size setting and fullscreen (064, 065); native F11 only (066).
- **Audio:** decode each sound sprite once into an `AudioBuffer`, play regions concurrently
  (polyphonic `DialogStack`) (043). Dialog language default **cs** (text + voice); per-line
  en voice fallback when a line has no localized clip (060); language switch hidden (075).
- **Content serving:** legacy files are fetched at `/legacy/` — dev middleware in
  `vite.config.ts`, copied into `publish/` for prod (041, 042).
- **Modes:** `/` = real progression + own solutions only; `/sandbox` = all unlocked +
  bundled reference solutions (045). Saves are multi-slot and include pickled Lua model state
  (026).

## Invariants and gotchas (each cost a real bug)

- **Lua compat at load, never in `legacy/`:** `public/lua/lua50-compat.lua` (`table.getn`,
  `loadstring`, `math.mod`...) and string patches in `fetchLegacyFile` (e.g. `\/`) (005, 024, 060).
- **Never call wasmoon `doString()` from inside a host callback** — corrupts WASM state.
  Resolve `file_include` by pre-scan; defer runtime includes until after the callback (008,
  031). Repeated synchronous calls of fetched Lua function refs are fine (014, 026).
- **wasmoon marshaling:** returning `null` from a host fn crashes; `undefined` becomes *zero*
  Lua return values — return `""`/`0`/`false` instead (024).
- **An unbound host fn in the live engine** throws only when its (delayed/random) branch
  first fires, and kills the round loop. When touching bindings, sweep the live engine's
  globals for nils (033). The headless all-levels Lua sweep never runs the render path, so it
  can't catch render-side crashes (063).
- **Animation phases from Lua** can be negative or fractional: `resolveFrame` truncates and
  clamps `< 0` to frame 0, like legacy `luaL_checkint` + `getRes()` (058, 063).
- **Deferred init state** (`afaze`, `setEffect("invisible")`) must be applied before the
  first draw — both loader and live engine run one `updateAnim()` pass at bootstrap (058, 059).
- **Phaser texture keys are prefixed with the level name** (scenes are relaunched across
  levels; the loader keeps the first image for a key) (028). The scene loader is shared with
  `AudioManager` — wait on key-specific `filecomplete-*` events (033).
- **Scenes are reused instances:** reset per-entry fields in `init()`, destroy owned
  GameObjects/listeners/timers on SHUTDOWN (012, 027, 071).
- **Canvas sizing only via `applyRenderScale`** (never `scale.resize`/`setGameSize` directly);
  text via `crispText` (029, 064, 065).
- **Real-time → cycle conversions must use their own tick interval** (movie vs level) (031).
- Phaser 4 `RenderTexture.draw` renders black — stack `Image`s instead (031).
  `textures.remove()` during SHUTDOWN wedges the scene transition (056).
- **F11 is browser-reserved** — don't capture it; Esc does its in-game job and keeps
  fullscreen (066). Held keys: only a fresh keydown (`!e.repeat`) arms the queued key (070).
  Pointer input is gated on pointer-over-canvas (stale `worldX` off-canvas) (071).
- **Images:** masks read pixel-exactly must be lossless WebP (039); atlas trim only against
  transparency (048); flat sprite art (fish, demo frames) is lossless (062).

## Verifying changes

- `npx tsc -b` (from `web/`) + `scripts\test.ps1` (e2e, Playwright; `web/tests/README.md`).
  The broadest cases: all-levels Lua-load sweep and all-solutions headless replay (80/81 —
  `redhat` has no content). Add a case under `web/tests/cases/NN-*.mjs` for durable checks.
- Behavior changes need a **real-browser check** (Playwright script against the dev server,
  driving `window.__game`), not just a read-through. Plain headless Chromium already has
  WebGL — don't add ANGLE/SwiftShader flags (they hang) (056).
- e2e `04-settings` can false-fail under dev-server HMR; `scripts\test.ps1` starts a fresh
  server (075).
- Deployment: Azure Static Web Apps; a single-zip upload has a ~100 s timeout, which limits
  how much audio can ship (see `docs/BACKLOG.md` §H).
