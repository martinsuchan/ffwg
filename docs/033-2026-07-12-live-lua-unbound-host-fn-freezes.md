# 033 - Live-Lua unbound host-function freezes (cabin1 + a whole class)

_2026-07-12_

User reported `cabin1` **freezing after a few seconds** of play (unsure whether
movement- or timer-triggered), and asked whether it's a generic problem worth
checking across other levels. It was generic - a small class of the same bug.

## Root cause

The live per-round Lua engine (`web/src/lua/levelScript.ts`) runs each level's
real `code.lua` `prog_update()` every round (docs/014). If that code calls a host
function (`model_*`/`game_*`/...) that isn't bound in the live engine, wasmoon
throws "attempt to call a nil value", which propagates out of `levelScript.tick()`
and - for a normal (non-show) level - is re-thrown by `LevelScene.tick()`, killing
the round-loop timer. The screen freezes.

Crucially this only bites when a **delayed / random / position-gated** branch first
fires, which is why the level plays fine for a few seconds first and why the
load-time sweeps (docs/024/028) missed it - those tick only a handful of rounds
from the start position.

`cabin1` specifically: `code.lua`'s screen-shake gag arms itself with
`if room.mov == 0 and random(100) < 1 then room.mov = 1 end` (~1%/round → ~13s
average), and from then on calls `big:getTouchDir()` / `room:getTouchDir()` **every**
round. `getTouchDir` (level_creation.lua) calls the host binding
`model_getTouchDir`, which was **never bound** - the port had dropped
`Rules::setTouched`/`m_touchDir` entirely (its old doc comment: "both no-ops...
don't exist in airplane").

## Fixes

### cabin1: port `touchDir` (`web/src/game/Rules.ts`, `GameEngine.ts`, `levelScript.ts`)

Ported legacy `Rules::setTouched`/`getTouchDir`/`m_touchDir` faithfully:
- `touchDir` field, reset to `Dir.NO` in `occupyNewPos()` (legacy resets it there
  each round, line 88 of Rules.cpp).
- `setTouched(dir)` - sets `touchDir` and recurses into every dead/immovable model
  it's pushing against in `dir` (legacy lines 614-629).
- `actionMoveDir` now calls `setTouched(dir)` in the blocked (`else`) branch.
  Legacy's `touchSpec` special case there is windoze-only (`output_*` border "go
  out on touch"), so it's still skipped - only `setTouched` is needed.
- `RenderModel.touchDir` (from `cube.rules.getTouchDir()`), and a real
  `model_getTouchDir(index)` binding reading it. The `Dir` enum values already match
  the Lua `dir_*` constants exactly (NO=0..RIGHT=4), so no mapping.

`setTouched` writes only `touchDir` with a balanced unmask/mask; it never changes a
position or `dir`, and `actionMoveDir`'s return value is unchanged - so move
resolution is byte-identical (no physics regression possible).

### The generic sweep + the rest of the class

Ran an authoritative runtime sweep (`test-binding-sweep`): collected every
host-style call (`(game|level|model|...)_*(`) appearing in any level's `code.lua`
or the shared runtime Lua (61 distinct names), then queried a **live** engine's Lua
globals for each. Nine resolved to nil. Triaged:

- **Genuine per-round freezes (same class as cabin1)**, now fixed:
  - `game_changeBg(picture)` - `corridor`/`rotate`/`steel` swap the whole room
    background as the puzzle progresses. Bound for real: it records a pending
    picture (`LevelScript.takeBgChange()`), and `LevelScene.applyBgChange()` swaps
    the background `Image`'s texture, loading it on demand (level-scoped key). This
    also fixes replay of those levels (`ReplayScene` runs the same live engine).
    Note: the on-demand load uses the **key-specific** `filecomplete-image-<key>`
    event, not the generic `COMPLETE` - the scene's loader is shared with
    `AudioManager`'s on-demand sound loads (docs/018), so a generic `once(COMPLETE)`
    got consumed by an unrelated audio load finishing first (found in testing: the
    texture loaded but the swap never applied).
  - `model_getViewShift(index)` - `pyramid` reads it every round for parallax. The
    paired *setter* `model_setViewShift` was already a no-op stub (docs/028), but the
    getter was nil. Bound to return `(0,0)`, consistent with the setter being a
    no-op (view shift is a cosmetic render offset this port doesn't apply).
  - `game_getBg()` - bound (returns the last `game_changeBg` value) for completeness,
    since it pairs with `game_changeBg`.
- **Confirmed dead in this port** (left nil, not called on any path the port drives):
  `game_checkActive` (windoze-only, unsupported); and `level_save`/`level_load`/
  `model_change_setLocation`/`model_change_setExtraParams`/`model_getExtraParams`
  (all only in `prog_save.lua`'s C++ save/**undo** path - this port's save/load glue
  uses `pickle` directly and undo isn't ported, confirmed by docs/026 save/load
  still being green while these stay nil).

## Verification (real browser, Playwright)

- `cabin1`: forced the `room.mov=1` gate, drove the big fish 24 rounds into walls -
  0 errors, loop kept advancing, and `getTouchDir` returned a real non-zero
  direction on 12/24 rounds (whole setTouched→touchDir→binding path works, not just
  returning 0).
- `game_changeBg`: `corridor`'s background texture actually swaps to `dark.webp`.
- `model_getViewShift`: `pyramid` returns `(0,0)`, no throw.
- Smoke: `corridor`/`rotate`/`steel`/`pyramid` each driven ~20 rounds - zero
  freeze-class errors. (Two pre-existing audio-asset errors on these levels -
  missing sprite.json / undecodable clip - are unrelated and don't freeze the loop;
  a separate follow-up.)
- Binding sweep re-run: only the 6 confirmed-dead names remain nil.

## Files

- `web/src/game/Rules.ts` - `touchDir`/`setTouched`/`getTouchDir`, `else`-branch.
- `web/src/game/GameEngine.ts` - `RenderModel.touchDir` + `Dir` import.
- `web/src/lua/levelScript.ts` - `model_getTouchDir`, `model_getViewShift`,
  `game_changeBg`/`game_getBg` bindings, `pendingBgChange`/`currentBg` state,
  `takeBgChange()`.
- `web/src/scenes/LevelScene.ts` - `bgImage` ref, `applyBgChange()`, per-round
  `takeBgChange()` call.

## Open / follow-ups

- Pre-existing audio-asset gaps on some levels (missing `sprite.json` / undecodable
  placeholder clips) surface as console errors but don't affect gameplay - worth a
  dedicated asset-conversion pass, separate from this fix.
- A cheap guard against this whole class regressing: the `test-binding-sweep`
  approach (query a live engine's globals for every host-style call in the runtime
  Lua) could become a checked-in tool like the docs/028 sweep.
