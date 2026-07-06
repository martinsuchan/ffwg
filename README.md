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

## Running the web port

Requires [Node.js](https://nodejs.org) (developed against v24).

```
scripts\start.ps1
```

Installs dependencies on first run, starts the Vite dev server, and opens a
browser tab at the printed local URL (e.g. `http://localhost:5173`).
`-NoOpen` skips the auto-open, `-Install` forces a fresh `npm install`.

Equivalent by hand:

```
cd web
npm install
npm run dev
```

## Scripts

All in [`scripts/`](scripts/), run from the repo root:

- `start.ps1` - launch the dev server (see above).
- `build.ps1` - type-check and produce a production build (`web/dist`).
  `-Preview` serves it locally afterwards.
- `new-doc.ps1 <slug>` - scaffold the next numbered `docs/` log entry, e.g.
  `scripts\new-doc.ps1 "lua runtime spike"`.

## Status

Early stage - see [`docs/`](docs/) for the current progress log and open
questions (e.g. which Lua runtime to embed). Nothing here is playable yet.

## License

GPLv2, see [`LICENSE`](LICENSE) and [`legacy/COPYING`](legacy/COPYING).
Original game credits in [`legacy/AUTHORS`](legacy/AUTHORS).
