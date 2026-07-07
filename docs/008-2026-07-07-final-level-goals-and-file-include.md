# 008 - Final Level Goals And File Include

2026-07-07

## Goal

User question: can "end levels" like `grail` be played, given they have a
different win condition - push a specific *item* out of the room, not the
fish? Answer: not with `docs/007`'s loader as it stood - both fish would
keep the default `goal_escape` and try to swim out themselves, and no item
would ever be marked as needing to leave, because that reassignment only
happens in `code.lua`, which the loader deliberately didn't run.

## What was found

- 9 levels - `atlantis`, `barrel`, `floppy`, `gods`, `grail`, `linux`,
  `map`, `propulsion`, `turtle` - are each their world's final level and
  share one idiom in `code.lua`'s `prog_init()` (grep for `--NOTE: a final
  level`): `small:setGoal("goal_alive"); big:setGoal("goal_alive")`, then
  one or more items get `setGoal("goal_out")` (`grail` is the only one
  that does this by a loop - `getModelsTable()` filtered to `getW()==2 and
  getH()==2` - the rest name a single item directly).
- That reassignment runs once, synchronously, at the top of `prog_init()` -
  before any of the per-round update closures the rest of the function
  defines and returns. Since this loader never runs a level's own
  `script_update()`/`prog_update()` loop, those closures are never called
  and their dependencies (dialogs, hint timers, `no_dialog()`, jokes, ...)
  were never a concern - only getting the *synchronous* prefix to complete
  mattered.
- The physics port (`docs/007`) already generalizes to this without any
  changes: `Rules::actionOut()`/`fallout()` operate on *any* model with
  `shouldGoOut()`, not just fish - a `goal_out` item just needs a player to
  push it to a clear border, exactly like a fish walking out on
  `goal_escape`. Only the *loader* (which goal ends up on which model) was
  missing.

## What changed (`web/src/lua/levelLoader.ts`)

- Now also fetches+runs `script/share/level_plan.lua` (pure Lua utilities
  the goal-setting prefix depends on: `random`, `createArray`, `switch`,
  `isIn`, no host bindings needed - loading the real file beat
  hand-rolling stubs) and `script/share/prog_goanim.lua` (also pure Lua -
  `setanim`/`resetanim`/`goanim`; some final levels call `setanim()`
  synchronously at init to queue a decorative animation sequence we never
  advance, but the call itself needs to succeed).
- Now also fetches+runs each level's `code.lua` after `models.lua`, with a
  few new no-op/fixed-value host stubs for its common init prefix:
  `initModels` (skips the real one's font/joke/sound-loading cascade -
  nothing in a goal-setting prefix depends on it), `sound_playMusic`,
  `getRestartCount` (returns 1), `dialog_addFont` (for `linux`, which also
  needed a pre-seeded empty `text = {}` global - normally populated by
  `dialogs.lua`, which still isn't run). Real, non-stubbed additions:
  `model_getW`/`model_getH` (`grail` needs these to pick out its 2x2
  pieces).
- `code.lua` itself calls `file_include(...)` at its own top level (every
  final level includes `script/share/prog_border.lua`; `gods` also
  includes its own `script/gods/prog_ships.lua`, needed for `getNShips()`).
  Handled by regex-scanning the fetched `code.lua` text for
  `file_include(...)` calls *before* running it, fetching each target, and
  running them ahead of `code.lua` with `file_include` itself bound as a
  no-op. Also refactored the fixed-path fetches (`level_creation.lua`,
  `models.lua`, ...) to resolve against one `LEGACY_ROOT` URL instead of
  each having its own literal `new URL(..., import.meta.url)` call.

## Bugs found and fixed

- **Reentrant `lua.doString()` corrupts wasmoon's WASM engine state.** The
  first attempt implemented `file_include` as an async host function that
  called `await lua.doString(source)` from inside itself - i.e. running
  Lua from a host callback invoked by an *already in-progress* Lua run.
  This surfaced as an uncaught `"function signature mismatch"` error after
  the call returned - and it happened even for a plain synchronous nested
  `doString()`, so it's about reentrancy itself, not async timing. The
  returned data still happened to be correct in testing, which made this
  easy to miss - not something to rely on. Fixed by pre-resolving
  `file_include` targets via regex before any Lua runs, so nothing ever
  calls back into the engine while it's mid-execution.
- **`new URL(relativePath, LEGACY_ROOT)` silently landed one directory up.**
  Vite's dev-server rewrite of `new URL("../../../legacy/", import.meta.url)`
  didn't preserve the trailing slash, and WHATWG URL resolution treats a
  slash-less base as "replace the last path segment" rather than "append to
  it" - so every fetch through `LEGACY_ROOT` was silently requesting
  `.../ffwg/script/...` instead of `.../ffwg/legacy/script/...`, hit Vite's
  SPA fallback (a 200 OK with `index.html`'s body), and failed as a Lua
  parse error (`unexpected symbol near '<'`) rather than a clean 404.
  Fixed by normalizing the trailing slash explicitly after construction.
  Caught by logging the resolved URL rather than guessing from the error
  text.
- **Out-of-range anim phase silently produced no picture instead of
  wrapping.** `grail`'s "light" grail-piece item gets `setAnim("default",
  1)` from `code.lua`, but `file_exists` always reporting `false` (a
  documented `docs/006` simplification - extra phases are never
  discovered) means it only ever has 1 frame loaded, so phase 1 resolved
  to `undefined` -> no texture -> invisible sprite. The real engine's
  `Anim::setAnim` wraps an out-of-range phase via modulo instead of
  dropping it (`phase %= count`, with a log warning). Fixed by applying
  the same modulo when resolving each model's final picture. Caught by
  actually rendering `grail` and cross-checking the "goal_out" items'
  resolved picture field, not just the goal values.

## Verification

- All 9 final levels plus `airplane` (regression check) load cleanly
  through the extended loader with zero uncaught errors: fish get
  `goal_alive`, the correct item(s) get `goal_out` per level (`grail`: 25
  items, matching its "every 2x2 piece" rule; the other 8: exactly 1 named
  item each).
- Ran `grail`'s actual `GameEngine` (not just the loader) through 120
  rounds of mixed WASD/IJKL input - no crash, both fish alive and moved
  from their starting cells, `isSolvable()` stayed true.
- Rendered `grail` for real (converted its images via the existing
  `scripts/convert-images.ps1`, pointed `main.ts` at it temporarily,
  screenshotted, reverted) - background, all pushable pieces (including
  the previously-invisible "light" piece), steel beams, and both fish all
  render correctly with zero console errors.

## Open for next time

- This only covers the *goal-setting* prefix of `code.lua`, not full
  gameplay - dialogs, hint timers, and the "prod the player toward the
  win condition" ambient logic in the returned update closures are still
  never run (by design - no dialogs/sound in this POC).
- Only tested with keyboard-driven movement, not an actual full solve of
  any final level (51x36 rooms need real puzzle-solving, not just
  spot-checks) - the engine and goals are verified correct, not that a
  specific level is beatable through this POC's controls.

## Bonus: fixed `docs/006`'s "every level gets bundled" build waste

Confirmed, not just theorized: switching the fixed-path fetches to the
`LEGACY_ROOT`-relative pattern (instead of each having its own literal
`new URL(templateLiteral, import.meta.url)`) also fixed the issue flagged
in `docs/006` where `vite build` treated the `${levelName}`-templated
`models.lua`/`code.lua` paths as a glob and bundled *every* level's file.
Ran `npx vite build`: it now emits a warning that `new URL("../../../legacy/",
import.meta.url)` "doesn't exist at build time, it will remain unchanged to
be resolved at runtime" - i.e. Vite correctly gives up trying to bundle it
instead of over-matching - and the build output no longer contains a
single `.lua` file (down from dozens). Production Lua *packaging* is still
open (`docs/005`), but the accidental bundling waste is gone.
