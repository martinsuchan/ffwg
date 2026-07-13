# Fish Fillets NG - Web Port

Browser port of [Fish Fillets - Next Generation](http://fillets.sf.net), the
classic fish-pushing puzzle game. The goal is to reuse the original game's Lua
content (levels, dialogs, translations) while rebuilding the engine in
TypeScript for the browser, playable with keyboard, mouse, gamepad, and touch.

## Layout

- [`legacy/`](legacy/) - the original C++/SDL1.2/Lua game source (autotools
  build). Kept as reference and as the source of Lua content to port. See
  [`legacy/README`](legacy/README) for how to build/run it.
- [`web/`](web/) - the new browser port: TypeScript + [Phaser](https://phaser.io)
  + Vite. Currently an early spike, not yet a playable game.
- [`docs/`](docs/) - running dev log of the port, one dated file per
  milestone. Start at [`docs/README.md`](docs/README.md) for the convention,
  then browse the numbered entries for what's been done so far.
- [`scripts/`](scripts/) - PowerShell helper scripts for launching/building the
  web port and scaffolding new `docs/` entries.
- [`CLAUDE.md`](CLAUDE.md) - architecture notes on the legacy engine, for
  anyone (human or AI) working on the port.

## Building and running the web port (Windows 11)

Two prerequisites, both installable with [winget](https://learn.microsoft.com/windows/package-manager/):

```
winget install --id OpenJS.NodeJS.LTS -e     # Node.js + npm
winget install --id Gyan.FFmpeg -e           # ffmpeg + ffprobe (asset conversion)
```

Then, from the repository root, one command builds everything and launches the game:

```
scripts\setup.ps1
```

It checks the tools are installed (and tells you how to install any that are
missing), installs npm dependencies, converts all the legacy game assets
(images, sound, music, the intro movie) into web-ready formats, builds the
lookup manifests, and opens the game in your browser. The first run converts
~9000 assets and takes a few minutes; later runs skip anything up to date.
Once assets are built, `scripts\setup.ps1 -SkipAssets` restarts the game quickly.

> If PowerShell blocks the script ("running scripts is disabled"), run it as
> `powershell -ExecutionPolicy Bypass -File scripts\setup.ps1`.

Only Windows 11 is supported for now; Linux is out of scope.

## Publishing (a deployable package)

```
scripts\publish.ps1
```

Produces a self-contained `publish/` folder - a pure static site (no server
runtime or database; the Lua interpreter runs in the browser as WebAssembly).
Copy its contents to any static web host, served at the site root over HTTPS.
It includes host config for Azure Static Web Apps (`staticwebapp.config.json`)
and IIS / Azure App Service (`web.config`), plus a `README.txt` with per-host
deploy steps. See [`docs/041`](docs/) for details.

## Scripts

Run from the repo root:

- `scripts\setup.ps1` - one-command local build + run (see above).
- `scripts\publish.ps1` - produce the deployable `publish/` folder (see above).
- `scripts\start.ps1` - just (re)start the dev server, assuming assets are built.
- `scripts\new-doc.ps1 <slug>` - scaffold the next numbered `docs/` log entry.

Lower-level asset-conversion helpers also live in [`scripts/`](scripts/)
(`convert-assets.ps1`, `build-image-manifest.ps1`, ...); `setup.ps1` orchestrates
them, so you rarely need to call them directly.

## Status

Early stage - see [`docs/`](docs/) for the current progress log and open
questions (e.g. which Lua runtime to embed). Nothing here is playable yet.

## License

GPLv2, see [`LICENSE`](LICENSE) and [`legacy/COPYING`](legacy/COPYING).
Original game credits in [`legacy/AUTHORS`](legacy/AUTHORS).
