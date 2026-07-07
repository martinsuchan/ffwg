# 014 - Item Animation Via Live Lua

2026-07-08

## Goal

Phase 2 of "implement full level content" (discussed after `docs/013`):
per-level item animation - grail's aura pulse, airplane's eye blink, and
similar effects each level's `code.lua` defines in its own hand-written
`prog_update()` closure. Nothing in the port ran this before - Lua only
ever executed once, at load time, to extract static level data.

## Investigated: port to TypeScript, or run the original Lua live?

Physics (`docs/007`) and fish animation (`docs/009`) were ported straight
to TypeScript because each is *one shared algorithm* used by every level -
worth porting once and testing hard. Item animation is the opposite
shape: a broad survey found 66 of 81 levels' `code.lua` files have their
own one-off, hand-written animation state machine. Porting "everything"
would mean translating and separately re-verifying dozens of different
small scripts, and it cuts against this project's own founding goal
(`CLAUDE.md`): reuse legacy Lua content unmodified, only rewrite the
engine.

Running the original Lua live was initially assumed infeasible (calling
into Lua several times a second), but that didn't hold up under scrutiny.
Two research spikes resolved the real open questions before any code was
written:

1. **Is a live per-round Lua call actually fast/safe?** Empirically yes -
   wasmoon's calls to a plain top-level Lua function are synchronous (not
   Promise-based, confirmed from source and by testing), and measured at
   ~0.1ms/round even under heavy stress (1000 simulated rounds, up to 20
   host-function round-trips each) - three orders of magnitude under the
   130ms round budget. `LevelScene.tick()`/`GameEngine.tick()` needed no
   `async` restructuring. The known wasmoon risk (`docs/008`'s WASM-state
   corruption) is specific to calling `lua.doString()` *reentrantly from
   inside a host callback* - a different pattern from calling a plain
   function repeatedly, confirmed safe across ~15,000 repeated host
   round-trips in a stress test.
2. **What exactly does airplane's `script_update()` need to run without
   crashing?** Hand-traced against `legacy/script/share/level_funcs.lua`'s
   real bootstrap order, then empirically verified with a spike that
   loaded the level's full real script chain and called `script_update()`
   up to 500 times (including a forced fish death) with zero uncaught Lua
   errors, iterating the host-function list against real runtime failures
   rather than static reading alone.

## What was built

- **`web/src/lua/levelScript.ts`** (new): `createLevelScript(levelName,
  initialRenderModels)` fetches and runs the verified bootstrap chain
  (`level_creation.lua`, `level_plan.lua`, `level_update.lua`,
  `level_fonts.lua`, `level_dialog.lua`, `prog_goanim.lua`,
  `prog_finder.lua`, `prog_compatible.lua`, `borejokes.lua`,
  `blackjokes.lua`, `bubles.lua`, `bordershout.lua`, `level_start.lua`,
  then the level's own `models.lua`/`code.lua`) into a *persistent*
  wasmoon engine - unlike `levelLoader.ts`'s one-shot extraction, this one
  stays alive for the play session. Returns a `LevelScript` with
  `tick(renderModels)` (refreshes the live snapshot host bindings read
  from, then calls the cached `script_update` closure),
  `getScriptAnim(index)` (the latest Lua-driven `{name, phase}` for a
  model this round), and `destroy()`.
- New host functions: real getters reading the live `RenderModel[]`
  snapshot (`model_getLoc`, `model_getAction`, `model_isAlive`,
  `model_isOut`, `model_getState`, `model_isLeft`), a real per-round
  counter (`game_getCycles`), and `model_setAnim`/`runAnim`/
  `useSpecialAnim` writing into an internal anim-override map. Everything
  dialog/sound-related is a deliberate no-op/false stub - most notably
  `game_isPlanning() -> true`, which makes `no_dialog()` (`= not
  dialog_isDialog() and not game_isPlanning()`) always evaluate `false`,
  centrally and cleanly disabling every level's ambient-dialog/banter
  branches without needing a real `game_planAction` scheduler.
  `game_planAction` itself is a no-op stub too - empirically required,
  not just anticipated: the spike found `stdBlackJoke`'s death-reaction
  path (not gated by `no_dialog()`) crashes once a fish dies without it,
  a real mechanic per `docs/011`/`docs/013`.
- **Fish vs. item anim ownership**: the real `script_update()` internally
  calls `animateUnits()`, which drives *fish* anim via the same
  `setAnim`/`runAnim`/`useSpecialAnim` calls item animation uses - already
  proven safe to execute as-is by the spike. Rather than trying to
  skip/patch pieces out of `script_update()` (fragile, and `updateModels()`
  - which several levels' `prog_update` genuinely need for fresh `.X`/
  `.Y`/`.dir` - is a `local` function only reachable via the
  `script_update()` wrapper, so calling pieces separately isn't an option
  anyway), `LevelScene` simply only consults `getScriptAnim(index)` for
  non-fish models. Fish keep 100% of their existing TS-computed animation
  path (`docs/009`/`013`) untouched; whatever `animateUnits()` writes for
  fish indices is never read.
- **`LevelScene.ts`**: `startEngine()` kicks off `createLevelScript(...)`
  fire-and-forget (not awaited) - physics and animators reset
  synchronously exactly as before (no restart delay), and `levelScript`
  swaps in whenever the promise resolves. A `scriptGeneration` counter
  guards against a superseded restart's async result resurrecting a stale
  engine (verified: 6 rapid `R` presses in a row, zero errors, correct
  final generation). `tick()` calls `levelScript?.tick(renderModels)`
  right after the physics tick, then passes
  `levelScript?.getScriptAnim(index)` to each non-fish model's
  `animator.sync(...)`.
- **`ModelAnimator.ts`**: non-fish `sync()` calls now apply a script anim
  override (if present and different from the current texture) through
  the same `resolveTextureKey`/`applyBodyTexture` pathway fish body anim
  already uses - no parallel rendering logic.
- One real bug caught during implementation, not by the spikes: `code.lua`'s
  own `prog_init()` calls `initModels()` synchronously as part of loading
  `codeSource`, and `initModels()` calls `model:getLoc()` for *every*
  model at that point - before `LevelScript` even exists, let alone
  before any real `tick()`. `createLevelScript` now takes an
  `initialRenderModels` snapshot (the engine's starting `getRenderModels()`,
  already computed by `startEngine()` before the async call) to seed the
  live-model state ahead of time, rather than starting from an empty
  array that would crash `model_getLoc` immediately.

## Verification

- `npx tsc -b` and `npx vite build` both clean.
- Direct end-to-end test (Playwright + dynamic `import()`, driving
  `GameEngine`/`LevelScript` in a tight loop - no real-time waiting
  needed given the sub-millisecond round cost): loaded `airplane` for
  real, ran 300 rounds, asserted `ocicko` (the eye-blink item, model
  index 11 - the 12th `addModel()` call in `airplane/models.lua`)'s
  script anim actually changes over time - 5 distinct `(name, phase)`
  values observed (`default:0` through `default:4`), not a static value.
- Real-browser visual check: polled the *actual rendered sprite's*
  texture key (not just the internal override) over ~15s of real
  gameplay - confirmed all 5 phases render on screen in the expected
  cyclic pattern (`4,2,3,1` repeating, matching the eye-blink state
  machine's own phase-transition tables), plus a direct screenshot
  sanity check (item renders correctly, no visual breakage).
- Confirmed fish animation is unaffected: held a movement key and
  verified the big fish's texture keys still show the correct
  turn-then-swim-cycle sequence, unchanged from `docs/009`/`010`.
- Confirmed restart still feels instant and correctly swaps in a fresh
  `LevelScript` (sprite count stays at the `docs/012`-verified 15 across
  restarts; `scene.levelScript` non-null and `scriptGeneration` correct
  after both a single restart and 6 rapid-fire restarts, zero errors).
- Full regression: re-ran `docs/007`'s synthetic-room tests, `docs/008`'s
  10-level goal test, `docs/009`'s stress-play script, `docs/011`'s
  corpse-removal round-timing test, `docs/012`'s restart-leak test, and
  `docs/013`'s death-reaction-delay test - all unchanged, identical
  results.

## Open for next time

- Only `airplane` is targeted/verified here (the current hardcoded dev
  level). Other levels' `code.lua` may exercise host functions this
  spike didn't reach (e.g. `elevator1`/`elevator2` call `game_addDecor`
  synchronously at their own top level, not in the host list at all) -
  extend incrementally as each new level is tried, matching the
  established "fail loud on real gaps" philosophy (`docs/008`).
- Dialog/talk system (`model_talk`, subtitles, sound) - still explicitly
  deferred, phase 3. `game_planAction`'s no-op stub would need a real
  per-tick callback scheduler at that point.
- `model_setGoal`/`model_change_turnSide` are registered as no-ops in the
  live script engine (only `levelLoader.ts`'s static-extraction versions
  do anything) - no level's per-round update closure was found calling
  these, so this is a known, currently-inert gap rather than a verified
  need.
