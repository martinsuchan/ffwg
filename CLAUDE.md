# CLAUDE.md

Guidance for Claude Code in this repository. This file loads into **every** session, so it
holds only stable, cross-cutting facts. Tree-specific guidance (source map, invariants,
how to verify) lives in `web/CLAUDE.md`, `solver/CLAUDE.md` and `legacy/CLAUDE.md`. Claude
Code loads them on demand when a file in that tree is read or edited, but **before working in
one of those trees, Read its CLAUDE.md yourself if it isn't already in context**. This also
applies to subagents, and to work done only through Bash/grep.

## What this is

Fish Fillets - Next Generation (fillets-ng): a C++/SDL 1.2/Lua puzzle game (two fish
moving objects around underwater rooms) by Ivo Danihelka, based on ALTAR Interactive's 1998
game (GPLv2 since 2004). This repo holds the original plus two independent new efforts that
share only its rules and level content:

| Tree | What | Log / status |
|---|---|---|
| `legacy/` | Original C++/SDL1.2/Lua 5.0 source + all Lua level content. **Reference only.** | — |
| `web/` | **"Fish Fillets - Web Generation"**: browser port, TypeScript + Phaser 4 + Vite + wasmoon (Lua 5.4/WASM). Playable end to end. | `docs/` |
| `solver/` | C#/.NET 11 console solver (`ffsolve`), A* over a separate physics port, plus a WPF level editor. | `solver/docs/` |
| `docs/` | Numbered dev log of the web port + `BACKLOG.md`. **Source of truth for status.** | `docs/README.md` (index) |
| `scripts/` | PowerShell helpers (Windows 11) for both trees. | — |

## Where to find things

- **Current status / open items:** `docs/BACKLOG.md`, then the latest `docs/NNN-*.md` entries
  (one-line index in `docs/README.md`). Solver: `solver/README.md` + `solver/docs/README.md`.
- **Why something is the way it is:** search `docs/` for the feature; the pre-2026-11-15
  milestone summaries are in `docs/HISTORY.md` (large — grep it, don't read it whole).
- **Original behavior to match:** `legacy/src/**` (C++) and `legacy/script/**` (Lua).

## Commands (from repo root)

```
scripts\setup.ps1              # web: one-command build+run (checks tools, converts assets, dev server)
scripts\setup.ps1 -SkipAssets  # web: fast restart once assets are built
scripts\start.ps1              # web: (re)start dev server at / (assumes assets built)
scripts\build.ps1              # web: tsc -b + vite build only (-Preview to serve)
scripts\test.ps1               # web: e2e regression suite (starts/stops the dev server)
scripts\publish.ps1            # web: deployable static site in publish/
scripts\build-solver.ps1       # solver: build + tests (-Publish = Native AOT exe)
scripts\export-levels.ps1      # regenerate solver/levels/*.json from legacy Lua (via web loader)
scripts\new-doc.ps1 "<slug>"   # scaffold the next docs/NNN entry + add it to the docs/README.md index
```

## Conventions

- **Never modify `legacy/`.** Both new trees read it as-is; work around Lua 5.0-isms or data
  quirks at load time in the consuming code (see `web/CLAUDE.md`). The one exception is
  `legacy/solution/`. It holds the **best known** solution per level, which `/sandbox`
  replays. Where the solver found a shorter one (`solver/solutions/`), the game's original
  moved to `legacy/solution/old/` and is kept there. Do this only when the user asks.
- **Dev log:** when a notable feature/decision lands (not small edits), add the next
  `docs/NNN-YYYY-MM-DD-slug.md` via `scripts/new-doc.ps1` (web) or the next
  `solver/docs/NNN-...` (solver): what changed, why, what's open — without being asked.
- **Do not append milestone summaries to any CLAUDE.md.** They go only in the dev log and its
  index. Edit a CLAUDE.md only when a *stable* fact changes (layout, commands, an invariant).
- Scratch probes/scripts go in the session scratchpad; promote durable checks into the test
  suites (`web/tests/cases/`, `solver/tests/`) instead of re-deriving them.
- Shell scripts are PowerShell; the dev machine is Windows 11.

## Licensing

GPLv2 (`legacy/COPYING`, root `LICENSE`). Game data and translations are credited
per-contributor in `legacy/AUTHORS`.
