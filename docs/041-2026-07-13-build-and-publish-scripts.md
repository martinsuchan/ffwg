# 041 - One-command build.ps1 + publish.ps1 (and production Lua packaging)

_2026-07-13_

Two user-facing PowerShell scripts so anyone who clones the repo can build/run
the game locally and produce a deployable package - no AI tooling, no tribal
knowledge of the asset pipeline. Windows 11 only for now (Linux out of scope).
Also closes the long-standing "production packaging of Lua content is still
open" item (docs/005/006).

## Production Lua packaging (the enabling code change)

The game fetches its Lua content at runtime via `fetchLegacyFile()` ->
`LEGACY_ROOT`. Everything it fetches lives under `legacy/script/**` (level
models/code/dialogs + shared helpers, worldmap/worlddesc/worldfame/labels) and
`legacy/solution/**` (reference solutions for replay). In dev, `LEGACY_ROOT` was
a static `new URL("../../../legacy/", import.meta.url)` that Vite's dev server
rewrites into an `/@fs/` path (`server.fs.allow`) - **dev-only**; a `vite build`
has no such route, so production couldn't even boot the world map.

`web/src/lua/levelLoader.ts` now branches:

```ts
export const LEGACY_ROOT = import.meta.env.DEV
  ? new URL(new URL(/* @vite-ignore */ "../../../legacy/", import.meta.url).href.replace(/\/?$/, "/"))
  : new URL(`${import.meta.env.BASE_URL}legacy/`, window.location.origin);
```

In production it resolves to `<site>/legacy/`, and `publish.ps1` copies
`legacy/script` + `legacy/solution` there. The dev literal is dead code in a
prod build (`import.meta.env.DEV` is statically `false`); `/* @vite-ignore */`
stops Vite warning about resolving it. Added `web/src/vite-env.d.ts`
(`/// <reference types="vite/client" />`) so `import.meta.env` is typed.

## `build.ps1` (repo root) - local build + run

One command for a fresh clone: `.\build.ps1`. Steps, each logged with colored
progress and clear errors:
1. **Tool check** (`scripts/lib/common.ps1` `Assert-Prerequisites`) - verifies
   `node`/`npm`/`ffmpeg`/`ffprobe`, prints each version, and if any are missing
   prints the exact `winget install` commands (`OpenJS.NodeJS.LTS`,
   `Gyan.FFmpeg`) + the "open a new terminal" reminder, then aborts.
2. `npm install` (if needed).
3. **Assets** - `convert-assets.ps1` (images/music/sound) + the intro movie
   (`intro.mpg` -> H.264 `intro.mp4`, previously a manual docs/038 step) +
   `build-image-manifest.ps1` / `build-audio-manifest.ps1`.
4. Starts the dev server (`scripts/start.ps1`) and opens the browser.

Flags: `-NoRun` (prepare only), `-SkipAssets` (fast restart), `-Force`
(re-convert), `-Install`, `-Port`.

## `publish.ps1` (repo root) - deployable package

`.\publish.ps1` produces a self-contained `publish/` folder:
1. Runs `build.ps1 -NoRun` (tools + deps + assets + manifests).
2. `npm run build` (tsc + vite) -> `web/dist`.
3. Assembles `publish/`: the built site (`dist/*` = HTML/JS + `public/`'s
   `/assets`, `/lua`, favicon), plus `legacy/script` + `legacy/solution` under
   `legacy/`, plus host config: `staticwebapp.config.json` (Azure Static Web
   Apps), `web.config` (IIS / Azure App Service MIME types - `.wasm`/`.lua`/
   `.webp`/`.mp4`; no URL-rewrite needed since the app has no client-side
   routes), and a `README.txt` with per-host deploy steps.

Flags: `-OutputDir`, `-SkipAssets`, `-Force`. Result: ~5700 files, ~176 MB
(mostly converted images/sound). It's a pure static site - no server runtime.

## Shared lib

`scripts/lib/common.ps1` (new) - colored logging helpers (`Write-Step`/`-Ok`/
`-Info`/`-WarnLine`/`-ErrLine`), `Find-Tool` (PATH + winget Links fallback, like
the existing `ffmpeg-tools.ps1`), and `Assert-Prerequisites`. Dot-sourced by
both root scripts.

## Verification

- `build.ps1 -NoRun -SkipAssets`: tool check lists real versions, structure OK.
- `publish.ps1 -SkipAssets`: full run - `npm run build` clean (no more
  import.meta.url warning), `publish/` assembled with all trees + config files.
- Served `publish/` with a plain static server and drove it in a **production**
  browser (no dev server, no `window.__game`): world map renders, boot fetches
  `worldmap.lua` from `/legacy/` (200), clicking the "start" node fetches
  `script/start/models.lua` from `/legacy/` (200), no JS errors. (One benign
  `sound/start/en/sprite.json` 404 - the docs/036 English-fallback probe for a
  level with no English VO; identical in dev, unrelated to packaging.)
- `tsc -b` clean.

## Files
- **New:** `build.ps1`, `publish.ps1`, `scripts/lib/common.ps1`,
  `web/src/vite-env.d.ts`.
- **Modify:** `web/src/lua/levelLoader.ts` (`LEGACY_ROOT` dev/prod branch),
  `README.md` (build/publish/prereqs).
