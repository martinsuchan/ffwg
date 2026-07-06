# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Fish Fillets - Next Generation (fillets-ng), a C++/SDL 1.2 puzzle game (moving two fish
around underwater rooms), originally by Ivo Danihelka, based on a 1998 ALTAR Interactive
game released under GPLv2 in 2004. Homepage referenced in code: http://fillets.sf.net.

**Active work in this repo is porting the game to the browser** (TypeScript, reusing the
original Lua level content) — see the next section. Repo layout:

- `legacy/` — the original C++/SDL1.2/Lua game source (autotools build). Reference material
  and the source of Lua content to port; see "Legacy game" below.
- `web/` — the new browser port: TypeScript + Phaser + Vite. Early stage.
- `docs/` — dated, numbered dev log of the port (`docs/README.md` has the convention).
  **This is the source of truth for current status/open questions** — this file covers
  stable architecture and won't be kept in sync with day-to-day progress.
- `scripts/` — PowerShell helpers (`start.ps1`, `build.ps1`, `new-doc.ps1`) for the web port.
- `README.md` — human-facing overview and run instructions.

## Web port (`web/`) — start here for new work

Goal: reuse the legacy Lua content (level layouts, dialogs/translations, scripted room
behavior under `legacy/script/`) unmodified where possible, rewrite the puzzle
physics/rules (`Field`, `Cube`/`Unit`, `Rules`, `Landslip`, `FinderAlg` — see "Legacy game"
below) in TypeScript, and re-implement the C++ "host API" the Lua scripts call into
(`*-script.cpp` files) as TypeScript bindings. Target inputs: keyboard, mouse, gamepad,
touch.

Current stack (see `docs/001-2026-07-06-legacy-review-and-phaser-spike.md` for the
reasoning; check for later numbered entries too — decisions here can change):

- Rendering/input: **Phaser 4** (`phaser@^4.2.0`), chosen for its built-in input manager
  unifying keyboard/mouse/touch/gamepad. TypeScript + Vite.
- Lua runtime: **wasmoon** (`wasmoon@^1.16.0`, Lua 5.4 via WASM). POC in
  `web/src/lua/luaPoc.ts` + `web/public/lua/sample.lua` confirms host callbacks work both
  ways (scalar return values and table round-trips) — see
  `docs/002-2026-07-06-wasmoon-lua-poc.md`. Checked against the real legacy corpus too:
  `web/tools/check-lua-compat.mjs` (`scripts/check-lua-compat.ps1`) parses all 1,469
  `legacy/script/**/*.lua` files with wasmoon's actual Lua 5.4 — 1,468 parse clean; the one
  real incompatibility category found (`table.getn`, `loadstring`) is fixed via a loaded-once
  shim, `web/public/lua/lua50-compat.lua`, not per-file edits — see
  `docs/005-2026-07-06-lua-5-4-compatibility-check.md`. `fengari` fallback is no longer
  needed.
- First real level render: `web/src/lua/levelLoader.ts` runs a level's actual, unmodified
  `legacy/script/<level>/models.lua` (+ `legacy/script/share/level_creation.lua`) through
  wasmoon, and `web/src/scenes/LevelScene.ts` draws the result in Phaser — background, wall
  overlay, items and both fish, all at their real grid positions (`x*15, y*15`, see
  `legacy/src/level/View.h`'s `SCALE`). Dev-only: legacy `.lua`/image files are fetched
  straight off disk via Vite's `/@fs/` route (`server.fs.allow` in `web/vite.config.ts`),
  not copied — production packaging of Lua content is still open. Scoped to "models only"
  (no dialogs/code.lua/animation/sound) — see
  `docs/006-2026-07-07-level-models-rendering-poc.md`.

Commands (from repo root):

```
scripts\start.ps1              # installs deps if needed, runs the Vite dev server, opens a browser
scripts\build.ps1              # tsc -b + vite build (add -Preview to serve the build)
scripts\new-doc.ps1 "<slug>"   # scaffold the next numbered docs/ entry
```

Equivalent by hand, from `web/`: `npm install`, `npm run dev`, `npm run build`,
`npm run preview`. No test suite yet.

**Workflow convention:** whenever a notable feature/decision lands (not small edits), add
the next `docs/NNN-YYYY-MM-DD-slug.md` entry via `scripts/new-doc.ps1` summarizing what
changed, why, and what's open — don't wait to be asked.

## Legacy game (`legacy/`) — reference for porting

### Build

Autotools-based C++ project (no CMake, no package.json). From `legacy/`:

```
./configure
make
make install
```

Requires libSDL 1.2, SDL_mixer, SDL_image, SDL_ttf, and Lua 5.0 (`liblua50`/`liblualib50`,
or pass `--with-lua=PREFIX`). Optional: FriBidi (bidi text), Boost.Filesystem (non-POSIX
systems only, `--with-boost=PREFIX`), SMPEG, X11.

There is no `make check` / test suite in this project.

Regenerating `configure` from `configure.in` requires autoconf/automake (`aclocal.m4`,
`ltmain.sh`, etc. are checked in) — normally unnecessary unless `configure.in` changes.

Run the built game with the data package (not included in this repo — see
`legacy/README`) placed at a system dir:

```
./src/game/fillets systemdir=$datadir
```

### Source layout (`legacy/src/`)

Static libraries built bottom-up in this dependency order (see `src/Makefile.am`), each
its own subdir with its own `Makefile.am`:

```
SDL_gfx -> gengine -> effect -> widget -> plan -> option -> state -> level -> menu -> game
```

- `SDL_gfx` — vendored SDL_gfx primitives (C).
- `gengine` — the core "GenGine" engine: agents, messaging, scripting glue, resource
  packs, exceptions, input handling. Everything else depends on it.
- `effect` — drawing/pixel-level effects (Picture, Font, LayeredPicture, disintegrate/
  mirror/reverse/wavy effects on sprites).
- `widget` — simple UI widgets (boxes, buttons, sliders, labels) built on `effect`.
- `plan` — planner/state-machine glue: `GameState`, `StateManager`, key bindings, console.
- `option` — options/help menus built on `widget`+`plan`.
- `state` — top-level game states (demo mode, movie/poster states).
- `level` — the puzzle simulation itself: `Level`, `Field`, `Cube`/`Unit` (the fish and
  movable objects), `Room`, physics/rules (`Rules`, `Landslip`, `FinderAlg`), and the
  Lua binding layer for levels (`level-script.cpp`, `game-script.cpp`).
- `menu` — world map / level-select screens (`WorldMap`, `LevelNode`, `Pedometer`).
- `game` — `main.cpp`, `Application`, `GameAgent`; produces the `fillets` binary.

#### Engine architecture (agents)

The engine (GenGine) is built around **agents** — see the doxygen block at the top of
`src/game/main.cpp` for the canonical description. Key points:

- `AgentPack` owns all agents, calling `init()`/`update()`/`shutdown()` on each, ordered
  by name (see `Name.h`/`Name.cpp`, e.g. `"10script"`, `"20option"`, `"30video"`,
  `"90game"`). Lower-named agents init before higher-named ones.
- Every agent derives from `BaseAgent` (`init()`/`update()`/`shutdown()` template methods
  calling protected `own_init()`/`own_update()`/`own_shutdown()`).
- Rule: an agent may only reference lower-named agents (and itself) from `own_init()`,
  and only higher-named agents (and itself) from `own_shutdown()`.
- The `AGENT(TYPE, NAME)` macro (in `BaseAgent.h`) generates a static
  `TYPE *TYPE::agent()` accessor that looks the singleton instance up from
  `AgentPack`, e.g. `OptionAgent::agent()->getAsInt("screen_width")`.
- Agents: `MessagerAgent` (always present; pub/sub messaging via `BaseListener`/
  `BaseMsg`), `ScriptAgent` (Lua), `OptionAgent` (global options), `VideoAgent`,
  `SoundAgent`, `TimerAgent` (fixed-FPS pacing), `InputAgent`, `SubTitleAgent`,
  `GameAgent` (drives the actual game via `StateManager`).
- `GameAgent` owns a `StateManager`, a stack of `GameState`s
  (`pushState`/`popState`/`changeState`) — this is how the app moves between world map,
  level play, demo mode, menus, etc.

#### Scripting (Lua)

- `Scripter`/`ScriptState`/`ScriptAgent` wrap the embedded Lua 5.0 interpreter.
  C++-to-Lua bindings live in files named `*-script.cpp` (e.g. `level-script.cpp`,
  `game-script.cpp`, `dialog-script.cpp`, `worldmap-script.cpp`, `options-script.cpp`,
  `def-script.cpp`), each exposing a set of `script_*` functions callable from Lua.
- Global/shared Lua sits directly under `legacy/script/`: `init.lua` (startup/locale
  setup), `labels.lua` (UI strings), `worlddesc.lua` (per-level, per-language title/
  description registered via `worldmap_addDesc`), `worldmap.lua`, `worldfame.lua`,
  `level_funcs.lua`, `select_lang.lua`, `select_speech.lua`, and shared helpers/dialog
  fragments under `script/share/`.
- Every level is its own directory `legacy/script/<levelname>/` containing `init.lua`
  (level entry point), `models.lua` (room object/unit layout), `code.lua` (puzzle
  scripted behavior/hints), and localized `dialogs_<lang>.lua` files.
- Level-name directories are mirrored across three trees that must stay in sync:
  `legacy/script/<name>/`, `legacy/images/<name>/`, `legacy/sound/<name>/`. When adding
  or renaming a level, update all three plus its `worldmap_addDesc(...)` entries in
  `worlddesc.lua`.

## Licensing

GPLv2 (`legacy/COPYING`, root `LICENSE`). Game data (images/sound/fonts/levels) and translations
are credited per-contributor in `legacy/AUTHORS`.
