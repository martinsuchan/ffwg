# 024 - Closing the Level-Load Gap

2026-07-09

## Context

Direct follow-up to docs/022/023: of the 12 levels still failing to
validate, the user asked to dig into the 10 that fail to even *load*
(explicitly excluding `windoze`'s unsupported `fish_extra` and `redhat`'s
missing level directory). All 10 crashed inside `code.lua`'s synchronous
`prog_init()`, each on a different missing Lua global/method.

## Root cause (all 10, one pattern)

`levelLoader.ts`'s `loadLevelModels()` is a deliberately minimal loader -
its own doc comment explains it only needs `code.lua`'s top-level
`prog_init()` to run far enough to extract goal-setting calls (docs/008's
final-level `goal_alive`/`goal_out` reassignment), never runs the level's
real per-round update loop, and stubs out anything assumed to only matter
for that per-round loop. `initModels()` (the real one attaches
`.afaze`/`.updateAnim`/etc. to every model, then loads fonts/jokes/
border-shout sound content) was stubbed as a complete no-op on exactly
that assumption.

The assumption was right for `airplane`/`viking1` (this project's two
long-exercised levels) but wrong for these 10: their `code.lua` calls
things like `pf2:updateAnim()` or `addv(...)` **synchronously**, directly
in `prog_init()`'s body - not deferred into a per-round closure the way
airplane's equivalent calls are. Traced this precisely by reading each
level's `code.lua` around the reported line and checking whether the
call sat inside a `return function() ... end` (deferred, never runs here)
or directly in the outer function body (synchronous, runs here and
therefore needs to actually work).

## The 10, by root cause

- **`updateAnim` undefined** (`alibaba`, `city`, `gems`) - needs
  `initModels()`'s real per-model setup, which the stub skipped entirely.
- **`mod` undefined** (`bathroom`, `experiments`) - `math.mod`, Lua 5.0's
  integer modulo (renamed `math.fmod` in 5.1+, with different
  negative-number semantics - `%` is the correct equivalent, not
  `fmod`). Missed by `docs/005`'s `check-lua-compat.mjs` because that
  tool only verifies *parsing*, not execution - a missing global only
  surfaces when actually called.
- **`level_planShow` undefined** (`briefcase`) - real C++ host binding
  (`legacy/src/level/level-script.cpp`), schedules a callback for later.
- **`addv` undefined** (`chest`) - pure-Lua dialog-planning helper from
  `prog_compatible.lua`, which this loader never ran.
- **`game_addDecor` undefined** (`elevator1`, `elevator2`) - real C++ host
  binding (`legacy/src/level/game-script.cpp`), purely visual decoration.
- **`options_getParam` undefined** (`music`) - real C++ host binding
  (`legacy/src/gengine/options-script.cpp`), reads a config value.

Fixing the first uncovered a second layer for two levels: `chest` then
hit `game_planAction` (pulled in by `prog_compatible.lua`'s own
`planTimeAction`, itself needed by `addv`), and `music` then hit two
distinct **wasmoon marshaling bugs** on the way to fixing
`options_getParam` (see below) - fixed both.

## Fix

`web/src/lua/levelLoader.ts`:

- `INIT_MODELS_SOURCE` (new): a literal Lua string with a faithful subset
  of the real `initModels()` (see `legacy/script/share/level_start.lua`)
  - the per-model `.afaze`/`.updateAnim`/`.X`/`.Y`/`.XStart`/`.YStart`/
    `.dir`/`.anim` setup - deliberately omitting its trailing
    `borderShoutLoad()`/`stdBoreJokeLoad()`/`stdBlackJokeLoad()`/
    `stdBublesLoad()`/`loadFonts()` calls, which pull in sound/font content
    this goal-extraction-only loader still doesn't need. `doString`'d
    before `modelsSource` runs.
- Fetches and runs two more real, unmodified shared files: `prog_finder.lua`
  (`dir_no`/`dir_up`/etc., needed by `INIT_MODELS_SOURCE`) and
  `prog_compatible.lua` (`addm`/`addv`/`adddel`/`planSet`/`planBusy`/
  `xdist`/`ydist`/`dist`/`look_at`/`no_dialog`/`isReady`/`odd`/
  `modelEquals`/`isWater`). `prog_compatible.lua` also redefines
  `getRestartCount()` to call the (unbound) real `level_getRestartCounter()`
  - re-applied this loader's own fixed stub (`() => 1`) immediately after,
  so the redefinition doesn't silently break other levels that rely on it.
- New no-op host binding stubs: `game_addDecor`, `level_planShow`,
  `game_planAction` (all "schedule/decorate something this loader's
  one-shot pass never acts on" - same reasoning as the pre-existing
  `model_setBusy`/`model_setEffect` stubs) and `model_getLoc` (a real,
  small implementation - `LuaMultiReturn.of(model.x, model.y)` - needed by
  `INIT_MODELS_SOURCE`'s `model:getLoc()` call).
- `options_getParam` needed care: returning JS `null` crashes wasmoon's
  `PromiseTypeExtension.pushValue` (it dereferences `.then` on the
  returned value before reaching any plain-nil handling), and returning
  `undefined` marshals as *zero* Lua return values rather than one `nil` -
  breaking `tonumber(options_getParam(x))` call sites (`tonumber()` with
  no arguments is a Lua error, not a graceful nil). Settled on returning
  `""` (empty string): marshals cleanly as one value, and `tonumber("")`
  itself already returns `nil` gracefully in Lua, which is exactly what
  callers like `level_plan.lua`'s `optionsGetAsInt()` already handle.

`web/public/lua/lua50-compat.lua`: added `math.mod = math.mod or function(a, b) return a % b end`.

No physics or gameplay-engine changes - everything here is scoped to the
one-shot goal-extraction loader.

## Verification

- `npx tsc -b` clean.
- All 10 levels re-tested individually after each incremental fix,
  confirming the exact next error (or success) at each step rather than
  batching changes and hoping.
- Full 81-level solution-validation batch re-run: **79/81 passed** - every
  one of these 10 plus all 69 from docs/023 still pass (zero regressions
  from touching the shared loader path every level goes through). The
  only 2 remaining failures are exactly `redhat` (no matching level
  directory in this repo) and `windoze` (`fish_extra`) - both explicitly
  out of scope for this pass per the user's own instruction.

## Open for next time

- `windoze`'s `fish_extra` support and `redhat`'s missing level are still
  open, deliberately unaddressed here.
- The two wasmoon marshaling quirks found (`null` crashing
  `PromiseTypeExtension`, `undefined` returning zero values instead of one
  `nil`) are general facts about this wasmoon version, not specific to
  `options_getParam` - worth remembering for any *future* host binding
  that needs to represent "no value"/Lua `nil`: return `""` (or another
  concrete falsy-but-real value appropriate to the call site), never
  `null`, and only use bare `undefined` where the call site can tolerate
  zero Lua return values.
- This loader now runs more real, unmodified shared Lua (`prog_finder.lua`,
  `prog_compatible.lua`) than before - worth checking whether any *other*
  level (beyond today's 10) newly exercises a path through these files
  that hits a still-missing binding, if more solutions get uploaded later.
