# 006 - Level Models Rendering POC

2026-07-07

## Goal

Render an actual level room from its real, unmodified `models.lua` - proving
the wasmoon Lua runtime (`docs/005`), the WebP asset pipeline (`docs/003`)
and Phaser rendering can combine to reproduce the legacy engine's static
layout, before any puzzle physics/rules exist. Scoped to "models only" per
request: no dialogs, no `code.lua` puzzle logic, no animation/sound/input.

## What was done

- Read `legacy/script/airplane/models.lua` and the host API it depends on
  (`legacy/script/share/level_creation.lua`, plus the C++ bindings in
  `legacy/src/level/level-script.cpp` and `game-script.cpp`) to understand
  the actual function surface a level's models file calls: `createRoom`,
  `setRoomWaves`, `addModel`, `addItemAnim`, `addFishAnim` (and the
  `addHeadAnim`/`addBodyAnim` helpers it uses internally).
- Derived the grid-to-pixel mapping by reading `legacy/src/level/View.cpp`
  (`View::getScreenPos`) and its `SCALE` constant
  (`legacy/src/level/View.h`, `SCALE = 15`): a model is drawn at
  `(x, y) * SCALE` pixels, top-left aligned. Confirmed empirically, not just
  from reading code: read the raw PNG `IHDR` dimensions of every airplane
  sprite and the background, and every single one is exactly
  `shapeWidth * 15` by `shapeHeight * 15` pixels (e.g. `letadlo-p.png` is
  675x405 = 45x27 cells, `sedadlo1.png` is 45x60 = 3x4 cells). This means the
  shape ASCII-art argument to `addModel` never needs to be parsed for
  rendering - the sprite's own pixel size already encodes it, and each
  sprite can just be placed at its model's `(x*15, y*15)` with
  `setOrigin(0, 0)`. Also confirmed draw order is plain insertion order
  (`Room::addModel` pushes to a vector, `ModelList::drawOn`/`Room::drawOn`
  iterate it after the background) - no z-sorting to replicate.
- Built `web/src/lua/levelLoader.ts`: fetches
  `legacy/script/share/level_creation.lua` and
  `legacy/script/<level>/models.lua` straight off disk (unmodified) via
  Vite's dev-only `/@fs/` static route (enabled through
  `server.fs.allow: [".."]` in the new `web/vite.config.ts`), runs them
  through wasmoon with a small host-function shim
  (`level_createRoom`/`game_addModel`/`model_addAnim`/`model_setAnim`/
  `model_runAnim`/`model_isLeft`/`model_setGoal`/...), and returns the room
  size/background plus each model's kind, grid position, and current
  frame's picture path. Sound (`sound_addSound`), waves
  (`game_setRoomWaves`), and `file_exists` (used only to discover animation
  phases *beyond* phase 0, which this static POC never shows) are
  intentionally no-ops - documented inline with why that's safe here.
- Built `web/src/scenes/LevelScene.ts`: loads the background and each
  model's resolved frame as individual Phaser textures and places them at
  `(x, y) * GRID_SCALE`.
- Converted the two needed fish sprite folders
  (`legacy/images/fishes/{small,big}`) to WebP via the existing
  `scripts/convert-images.ps1` pipeline - no new conversion tooling needed.
- `web/src/main.ts` now boots straight into `LevelScene` for the `airplane`
  level (replacing the placeholder hello-world rectangle demo; the
  wasmoon/table-roundtrip POC in `web/src/lua/luaPoc.ts` from `docs/002`
  is untouched and still valid, just no longer wired into `main.ts`).
- Verified in a real browser (Playwright + headless Chromium against the
  Vite dev server, same approach as `docs/001`): the airplane room renders
  correctly - background art, the green wall-collision overlay, both fish in
  their `rest` pose, all wreckage/seat items in their correct grid positions
  and pixel sizes, with zero console errors and zero failed network
  requests.

## Bugs found and fixed along the way

- `scripts/convert-images.ps1` mishandled relative `-Source`/`-Destination`
  paths passed from outside `web/` (e.g. `legacy/images/fishes/small`):
  it substringed `FullName` by `$Source.Length` without first resolving
  both to absolute, normalized paths, producing garbled nested output
  directories (`fishes/big/s/fishes/...`). Fixed by resolving both
  parameters via `Resolve-Path`/`GetFullPath` up front. Verified by diffing
  output against a known-good previous conversion (the airplane images) -
  byte-for-byte identical file list.
- wasmoon marshals an omitted/nil Lua argument as JS `null`, not
  `undefined` - a `phase = 0` default parameter on the `model_runAnim` host
  binding silently never applied (Lua's `model:runAnim("rest")` omits the
  phase argument), so `anim.left[null]` resolved to `undefined` and both
  fish rendered as nothing. Fixed with explicit `phase ?? 0`. Caught by
  dumping the parsed level data mid-fix and noticing both fish had
  `picture: null` while every other model resolved correctly.

## Open for next time

- Only the "models" subset of a level is loaded - `dialogs.lua`, `code.lua`
  (puzzle rules) and the rest of `level_funcs.lua`'s shared includes
  (`level_plan`, `level_update`, `level_dialog`, ...) are not run. Real
  gameplay needs the `Field`/`Cube`/`Rules`/`Landslip`/`FinderAlg` TS port
  (per `CLAUDE.md`) before those matter.
- `new URL(`.../legacy/script/${level}/models.lua`, import.meta.url)` uses a
  template literal, so `vite build` treats the level segment like a glob and
  bundles *every* level's `models.lua` into `dist/` (harmless for this POC,
  wasteful for real production output) - worth a real content-packaging
  strategy rather than the dev-only `/@fs/` shortcut either way.
- Level images are loaded as individual textures, not through the
  already-built per-level atlas (`scripts/build-atlas.ps1`, `docs/004`) -
  fine at one-level scale, but real content should use the atlas to cut
  down on draw calls/requests. Fish sprites (shared across all levels)
  still need their own atlasing strategy - not yet designed.
- `file_exists` is stubbed to always return `false`, so multi-phase
  animations are invisible to this loader by construction - fine for a
  static POC, but real animation support needs it (and `model_addAnim`'s
  full per-side phase list) wired up for real.
