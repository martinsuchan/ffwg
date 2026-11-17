# legacy/CLAUDE.md — the original game (reference only)

Loaded on demand when Claude touches files under `legacy/`. **Never modify anything here**:
`web/` and `solver/` both consume it as-is. To match original behavior, read the C++ under
`legacy/src/` and the Lua under `legacy/script/`; the reference solutions are
`legacy/solution/<level>.lua` (`saved_moves = '<symbols>'`, exactly one per level - 81, the
`ending` has none).

Sanctioned exceptions so far, all at the user's request (root `docs/083`): the `redhat` level
(`script/redhat/`, `images/redhat/`, no `sound/redhat/`) was imported from the upstream
`fillets-ng-redhat.zip` and `script/worldmap.lua` gained its node; `script/worlddesc.lua` came
from that zip and `script/worldfame.lua` from the current upstream (SourceForge
`code-fillets_data`); a stray solver test solution (`solution/cabin1-simple.lua`) was removed.
Anything else still needs an explicit user request.

## Build

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

## Source layout (`legacy/src/`)

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

### Engine architecture (agents)

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

### Scripting (Lua)

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

