# Fish Fillets NG — Web Port

A browser port of [Fish Fillets — Next Generation](http://fillets.sf.net), the
classic underwater puzzle game where you guide two fish around cluttered rooms
without crushing them. This port **reuses the original game's Lua content**
(level layouts, dialogs, translations, scripted behaviour) unchanged, and
rebuilds the engine (physics, rules, rendering, input, audio) in TypeScript for
the browser.

**Status: playable.** All 80 levels with content are solvable, with the world
map, progression, mid-level save/load, solution replay, dialogs, voice, music,
subtitles, menus, fullscreen, and the final-level posters + ending all working.
The one exception is `redhat`, whose level content isn't present in this repo.
See [`docs/BACKLOG.md`](docs/BACKLOG.md) for a full feature comparison against the
original and the list of what's still open.

## What's in the box

- **80 playable levels** driven by the original Lua content, at their real grid
  layouts and rules.
- **World map** with progressive per-house unlocking, or a `/sandbox` endpoint
  that unlocks everything and enables the bundled reference solutions.
- **Save / load** (multiple slots per level) and **watchable replays** with
  pause / step / speed controls.
- **Czech dialogs, voice-over, music, and subtitles** (colour-coded per speaker),
  plus built-in impact/death sounds. (Dutch is fully built but hidden for now —
  see the backlog.)
- **Menus**: Options (volume, subtitles, on-screen size, progress backup/restore),
  an F1 controls popup, the intro movie, and the credits scroll.
- **Progress backup/restore** to a human-readable JSON file, so your solved
  levels and saves survive clearing browser data.

It's a pure client-side static site — no server, no database. The Lua interpreter
runs in the browser as WebAssembly (via [wasmoon](https://github.com/ceifa/wasmoon)),
and rendering/input go through [Phaser 4](https://phaser.io).

## Controls

| Key(s) | Action |
|---|---|
| Arrow keys | move the active fish |
| W A S D / I J K L | move the big / small fish directly |
| Space | switch the active fish |
| Left-click / hold | select a fish / swim toward the cursor |
| Hold right-click | push toward the cursor |
| Backspace | restart the level |
| P | watch the solution replay |
| F2 / F3 | save / load a position |
| F5 / F6 | toggle the step counter / subtitles |
| F10 | settings · F1 help · F11 fullscreen |
| Esc | back to the world map (closes a popup first) |

On the world map: click a level to play it, **Tab** to cycle open levels,
**Enter** to run the selected one.

## Running it locally (Windows 11)

Two prerequisites, both installable with
[winget](https://learn.microsoft.com/windows/package-manager/):

```
winget install --id OpenJS.NodeJS.LTS -e     # Node.js + npm
winget install --id Gyan.FFmpeg -e           # ffmpeg + ffprobe (asset conversion)
```

Then, from the repository root, one command builds everything and launches the
game in your browser:

```
scripts\setup.ps1
```

It checks the tools (and prints install hints for any that are missing), installs
npm dependencies, converts the legacy game assets (images, sound, music, the
intro movie) into web-ready formats, builds the lookup manifests, and starts the
dev server. The first run converts thousands of assets and takes a few minutes;
later runs skip anything already up to date. Once assets are built,
`scripts\setup.ps1 -SkipAssets` restarts quickly.

> If PowerShell blocks the script ("running scripts is disabled"), run it as
> `powershell -ExecutionPolicy Bypass -File scripts\setup.ps1`.

The game opens at `/` (the standard, progression-gated game). Append `/sandbox`
to the URL for the all-unlocked sandbox with reference solutions.

Only Windows 11 is supported for now; Linux/macOS are out of scope.

## Publishing and deploying

```
scripts\publish.ps1
```

Produces a self-contained `publish/` folder — a pure static site — with host
config for Azure Static Web Apps (`staticwebapp.config.json`) and IIS / Azure App
Service (`web.config`), plus a `README.txt` with per-host deploy steps. Copy its
contents to any static host, served at the site root over HTTPS.

To deploy to **Azure Static Web Apps** (recommended), install the CLI and push
the folder:

```
npm install -g @azure/static-web-apps-cli
swa deploy .\publish --env production        # needs the app's deployment token
```

> Note: the full package is ~184 MB, which the SWA CLI uploads as a single zip
> that must complete within a fixed 100 s network timeout — so a slow uplink can
> fail. Workarounds and details are in [`docs/BACKLOG.md`](docs/BACKLOG.md) §H.

## Tests

```
scripts\test.ps1
```

Runs the Playwright end-to-end suite (`web/tests/`) against a fresh dev server:
world-map boot, pedometer, level load + browser Back, settings/localization, an
all-80-levels Lua-load sweep, an all-solutions headless replay, sandbox vs
standard gating, and progress backup/restore.

## Repository layout

- [`web/`](web/) — the browser port: TypeScript + Phaser + Vite. This is the
  game.
- [`legacy/`](legacy/) — the original C++/SDL1.2/Lua source, kept as reference and
  as the source of the Lua content that's reused at runtime. See
  [`legacy/README`](legacy/README) to build/run the original.
- [`docs/`](docs/) — a dated, numbered dev log, one entry per milestone;
  [`docs/BACKLOG.md`](docs/BACKLOG.md) is the feature-status / FF NG comparison /
  open-items summary, and [`docs/README.md`](docs/README.md) explains the log
  convention.
- [`scripts/`](scripts/) — PowerShell helpers: `setup.ps1` (build + run),
  `publish.ps1` (deployable package), `start.ps1` (restart dev server),
  `test.ps1` (e2e suite), `new-doc.ps1` (scaffold a log entry), plus the
  lower-level asset-conversion scripts `setup.ps1` orchestrates.
- [`CLAUDE.md`](CLAUDE.md) — architecture notes and the running change log, for
  anyone (human or AI) working on the port.

Equivalent commands by hand, from `web/`: `npm install`, `npm run dev`,
`npm run build`, `npm run preview`, `npm run test:e2e`.

## License

GPLv2 — see [`LICENSE`](LICENSE) and [`legacy/COPYING`](legacy/COPYING). Game data
(images, sound, fonts, levels) and translations are credited per contributor in
[`legacy/AUTHORS`](legacy/AUTHORS).
