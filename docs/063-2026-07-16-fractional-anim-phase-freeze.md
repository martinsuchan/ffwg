# 063 - Fractional animation phase freeze (electromagnet, rotate)

_2026-07-16_

`electromagnet` froze the instant it opened. Same **class** as docs/058's
negative-`afaze` crash, a different arithmetic case: a **fractional** animation
phase.

## Root cause

`electromagnet/code.lua:87` runs every round:

```lua
plutonium.afaze = math.mod(count, 12)/3   -- 0, 0.33, 0.67, 1, 1.33, ...
plutonium:updateAnim()
```

so `plutonium.afaze` is fractional. That phase flows through `model_setAnim` →
`ModelAnimator.applyScriptAnim` → `resolveFrame`, where docs/058's guard was:

```ts
const index = phase < 0 ? 0 : phase % sideFrames.length;
return pictureToAtlas(sideFrames[index]);
```

`0.33 % length === 0.33`, so `sideFrames[0.33]` is `undefined` and
`pictureToAtlas(undefined)` throws `Cannot read properties of undefined
(reading 'replace')`. The throw propagates out of `LevelScene.tick()` →
`LevelScene.update()`, halting Phaser's game loop entirely - a hard freeze, not
just a stuck animation (repro: `state.cycles` frozen at 1, `steps` at 0, one
`pageerror`).

The original never hits this: `model_setAnim`'s phase passes through
`luaL_checkint` (a C cast toward zero), and `ResourcePack::getRes()`'s advance
loop `for (i = 0; i < rank; i++)` only ever compares an **integer** step count -
so `0.33 → 0`, `1.67 → 1`.

## Fix

One line in `resolveFrame` (`web/src/scenes/ModelAnimator.ts`): truncate the
phase toward zero before indexing, mirroring `luaL_checkint`. `Math.trunc` (not
`floor`) also **subsumes** docs/058's negative guard (`-1.5 → -1 → frame 0`), so
the two cases are now one:

```ts
const rank = Math.trunc(phase);
const index = rank < 0 ? 0 : rank % sideFrames.length;
return pictureToAtlas(sideFrames[index]);
```

## Blast radius

Swept all `code.lua` for `afaze = <expr with '/'>`. Most divide inside
`math.floor(...)` (integer-safe). Two produce genuinely fractional phases and
were the same latent freeze:

- **electromagnet** - `math.mod(count, 12)/3` (the report).
- **rotate** - `valecek[i].orifaze/2+12` (and `/3`, `/4`, `/6` variants); e.g.
  `orifaze=1 → 12.5`.

Both fixed by the shared choke point. The others (cancan/party1/party2/reactor/
stairs/viking2/windoze) already floor and were never affected.

## Why the headless sweep missed it

The all-levels Lua sweep (e2e case 05, docs/033) runs each level's `code.lua`
and ticks it live - but it never builds a `ModelAnimator` or calls
`resolveFrame`. `model_setAnim` just stores the fractional phase in the
`scriptAnims` map; the crash is purely in the **render** path that reads it
back. So the sweep stayed green while the real level froze. Lesson: a
render-layer resolution bug needs a real (or headless-WebGL) scene to surface -
the Lua sweep structurally can't. Verified here with a Playwright probe that
launches the actual `LevelScene` and samples `state.cycles` advancing.

## Verification

- electromagnet: was frozen (`cycles` stuck at 1 + `pageerror`); after the fix
  `cycles` advances 4→61 over 6s, zero errors.
- rotate: same probe, clean (`cycles` climbs, no errors).
- `tsc -b` clean; e2e suite 7/7 (incl. the 80-level sweep + 80/81 replay).
