# 045 - Standard game at `/`, sandbox at `/sandbox`

_2026-07-13_

Until now the port always ran in "sandbox" mode (every level unlocked, the
bundled reference solutions replayable) - a compile-time `SANDBOX_MODE = true`
constant from docs/027. The published game now behaves like the real game by
default and keeps the sandbox as a separate endpoint, both from one build.

## Behavior

- **`/`** - the standard game: real progression gating (only solved levels
  unlock the next; secret branches stay hidden), and Replay plays **only the
  player's own** saved solutions.
- **`/sandbox`** - every node unlocked (incl. hidden branches), and the bundled
  `legacy/solution/**` reference solutions are available to replay.

## Implementation

The mode is decided at runtime from the URL path (`web/src/game/appMode.ts`,
`isSandboxMode()` = path ends with `/sandbox`), so a single static build serves
both. In-app navigation keeps the path fixed (navigation.ts's `pushState`/
`replaceState` never change the URL), so the mode is stable for the session.

- `WorldMapScene` replaces the `SANDBOX_MODE` constant with `isSandboxMode()`,
  passed to the existing `computeNodeStates(..., sandbox)` (which already
  implemented real gating vs. force-open - docs/027). The map title gets a
  `- Sandbox` suffix in sandbox mode.
- `LevelScene.launchReplay()` (the `P` key) now only falls back to
  `legacy/solution/<level>.lua` **when `isSandboxMode()`**; the standard game
  replays the player's own solution or nothing. The Pedometer's Replay button
  was already player-solution-only (it only opens on player-solved nodes).

## Hosting (`scripts/publish.ps1`)

`/sandbox` needs the host to serve `index.html` for that path (SPA fallback):

- `staticwebapp.config.json` already rewrites unmatched paths to `/index.html`
  (Azure Static Web Apps) - no change needed.
- `web.config` gained an IIS URL-Rewrite SPA-fallback rule (was MIME-only,
  since the app previously had no routes). Azure App Service has the URL Rewrite
  module; hosts without it can drop the `<rewrite>` block (`/` still works).
- Vite's dev server already serves `/sandbox` via its default SPA history
  fallback. `README.txt` documents the two endpoints.

## Verification

- E2e case `07-sandbox-mode` (new): `/` with a clean solved set shows **1** open
  node (the root) and 79 gated; `/sandbox` shows **80** open, 0 gated. The other
  UI cases now load the map via `/sandbox` (helper default) so any level is
  reachable; full suite **7/7**.
- Production package: a SPA-fallback static server serves `/` and `/sandbox`
  (both 200; `/legacy/*.lua` still served as real files), and a real browser
  boots both - `/sandbox` shows the "… - Sandbox" title, `/` the plain title, no
  errors. `tsc -b` clean.

## Launching locally

`scripts/start.ps1` opens the standard game at `/`; `start.ps1 -Sandbox` opens
`/sandbox`. Both share the same dev server - the switch just changes the path 
the browser opens (`vite --open <path>`, or the URL when attaching to an
already-running server).

## Files
- **New:** `web/src/game/appMode.ts`, `web/tests/cases/07-sandbox-mode.mjs`
- **Modify (launch):** `scripts/start.ps1` (`-Sandbox` switch).
- **Modify:** `web/src/scenes/WorldMapScene.ts` (URL-driven mode + title),
  `web/src/scenes/LevelScene.ts` (gate reference-solution replay),
  `web/src/game/worldMapState.ts` (comment), `web/tests/lib.mjs`
  (`gotoWorldmap` defaults to /sandbox), `scripts/publish.ps1` (web.config SPA
  rewrite + README modes).
