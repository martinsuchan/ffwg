# 001 - Legacy review and Phaser hello-world spike

2026-07-06

## Goal

Port Fish Fillets NG (legacy C++/SDL1.2/Lua puzzle game, see `legacy/`) to a
browser, reusing as much of the existing Lua content as possible, with
keyboard, mouse, gamepad, and touch input.

## What was done

- Reviewed the legacy codebase and wrote root [`CLAUDE.md`](../CLAUDE.md):
  build system (autotools, SDL1.2, Lua 5.0), the agent-based engine
  architecture (`AgentPack`/`BaseAgent`, `Name.h` init/shutdown ordering),
  the `StateManager`/`GameState` stack, and how Lua content is organized
  (`legacy/script/<level>/{init,models,code,dialogs_*}.lua`, mirrored by
  `legacy/images/<level>/` and `legacy/sound/<level>/`).
- Identified the real split for the port: puzzle **physics** (`Field`,
  `Cube`/`Unit`, `Rules`, `Landslip`, `FinderAlg`) is C++ and must be
  rewritten in TypeScript; the **content layer** (level layout, dialogs in
  13 languages, scripted room behavior) is Lua and is the reuse target. Lua
  scripts call into a C++ "host API" (`*-script.cpp` files, e.g.
  `level-script.cpp`, `game-script.cpp`) that we'll need to re-implement in
  TypeScript, one file at a time.
- Compared options and decided on a starting stack:
  - **Rendering + input: Phaser** — chosen specifically because its Input
    Manager already unifies keyboard/mouse/touch/gamepad, which is a hard
    requirement here. Bare PixiJS (renderer only, no input abstraction) and
    Excalibur.js (TS-native but far smaller ecosystem) were the alternatives
    considered.
  - **Lua runtime: not yet decided** — candidates are `wasmoon` (Lua 5.4 via
    WASM, actively maintained, good TS types) and `fengari` (Lua 5.3, pure
    JS, easier cross-boundary debugging, less actively maintained). Main
    open risk: the legacy scripts target Lua 5.0 (2003); both candidates are
    5.3/5.4, so some syntax fixes are expected (`module()`, `string.gfind`
    vs `gmatch`, integer/float split in 5.3+). Not yet validated against a
    real level script.
- Checked npm and found Phaser 4 is real and current (`4.2.0` on `latest`,
  not just an alpha) — built the spike against it directly rather than
  Phaser 3.
- Scaffolded `web/`: Vite + TypeScript + `phaser@^4.2.0`. One scene
  (`src/main.ts`) rendering title text and a rectangle that moves on arrow
  keys.
- Verified end-to-end with a headless-Chromium (Playwright) driver: `tsc -b`
  type-checks clean against Phaser 4's shipped types (confirms the
  Phaser 3-style `Scene`/`GameObjects`/`createCursorKeys()` API still works
  in Phaser 4), the dev server renders the scene, arrow keys move the
  rectangle, and there are no console errors.

## Open for next time

- Lua runtime spike: load one real level's `models.lua`/`code.lua` through
  `wasmoon` (or `fengari`) with a stub host API and see what actually breaks
  against Lua 5.0-era syntax, before committing to "reuse scripts unmodified"
  as a hard requirement.
- No Claude Code skill specific to this stack exists yet; revisit once the
  Lua-runtime choice is settled and a few levels are ported, then consider
  writing a small project-local skill for "port one more level."
