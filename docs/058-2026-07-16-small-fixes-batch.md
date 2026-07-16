# 058 - Small fixes: parrot flash, rope in replay, ending node, step counter

_2026-07-16_

Five user-reported issues in one batch. The parrot one turned out to be the
interesting one - it surfaced a real latent crash (negative anim phases) that
had nothing to do with parrots. The party2 one (#5) is a second instance of the
parrot's deferred-init-state class, via a different call (`setEffect`).

## 1. cabin2's parrot showed in full colour for ~1s, then "died"

`cabin2` has two parrots. `papouch` is an obvious skeleton; `papzivy` ("živý
papoušek", the *live* one) is the gag - the fish see a colourful bird, and by
the time they look properly it's just another skeleton:

> **big:** Look at that parrot!!!
> **small:** So what? It's a normal parrot skeleton.

`pap-zivy_00..08` are the live bird, `pap-zivy_09` is the skeleton.
`prog_init_papzivy()` sets `papzivy.afaze = 9` at init, but defers the
`updateAnim()` that would *apply* it into its per-round closure.

The original gets away with that: `addItemAnim()` ends with
`model:setAnim("default", 0)`, so it too opens on frame 0 - for exactly one
100ms cycle, until the first `script_update()`. Imperceptible.

This port's live engine (`levelScript.ts`) only goes live after an async Lua
bootstrap **and** the dialog-audio gate (docs/031). Measured on cabin2:

| step | ms after scene start |
|---|---|
| Lua bootstrap (`createLevelScript`) | ~380 |
| dialog voice sprite decoded (the gate) | ~805 |
| **script live** (flash ends) | **~990** |

Frame-sampling confirmed the flash ends *exactly* at script-live:
`pap-zivy_00` through 1824ms, `pap-zivy_09` from 1928ms on.

**The user's hunch was right that this appeared after the recent host-fn work** -
not because a binding broke anything, but because the port only *started*
applying `afaze` correctly then. Before, the parrot presumably just stayed
colourful, so there was no visible transition to notice. (An attempted A/B by
reverting `levelScript.ts` alone was invalid - `LevelScene` at HEAD calls
`getScreenShift()`, added in the same commit, so the mixed tree crashed its
render loop. Noted here so nobody re-runs that experiment.)

### Fix

`levelLoader.ts` already records `setAnim` calls into
`initialAnim`/`initialPhase` (the animator's opening frame), and docs/024
already has it run `code.lua`'s **synchronous** `prog_init()`. So the state is
all there - it just never gets applied. New `APPLY_INITIAL_ANIMS_SOURCE` runs
one `updateAnim()` pass per model right after `code.lua`:

```lua
function ffwg_applyInitialAnims()
    local models = getModelsTable()
    local units = getUnitTable()
    for key, model in pairs(models) do
        if model.updateAnim and not units[model.index] and ffwg_hasAnims(model.index) then
            pcall(function() model:updateAnim() end)
        end
    end
end
```

This is the *same* `updateAnim()` the first `script_update()` would call, at the
same point in the sequence - not a guess. It calls nothing but the already-bound
`model_setAnim`; the level's per-round closures are still never run here (they
need the full live binding set this goal-extraction loader deliberately lacks -
which is why running `script_update()` itself was rejected).

Two guards, both found by the sweep rather than by inspection:

- **Fish are skipped** (via level_creation.lua's own `getUnitTable()`). Fish have
  no `"default"` anim - `addFishAnim` ends with `runAnim("rest")` - but
  `initModels()` hands its `setAnim("default", afaze)` `updateAnim` to *every*
  model. Without the guard all 164 fish opened on an unresolvable anim. The
  original never hits this: `script_update()` routes fish through
  `animateFish`/`animateHead`, and only item closures call `updateAnim()`. Also
  matches this port's split (fish anim is TS-owned, docs/009/013/014).
- **Models with no pictures are skipped** (new `ffwg_hasAnims` glue). Only
  windoze's invisible `spuntik` has none; it was being handed a `"default"` that
  doesn't exist. Harmless (`buildAnimators` skips a null initial frame anyway)
  but it shouldn't invent state.

**Blast radius**: 150 models across ~12 levels now open on the phase their
`prog_init` actually chose - gems'/alibaba's crystals (varied `krystal_NN`),
city's/music's crabs, experiments' bubbles, viking musicians, chest's ring,
crabshow, fdto's seahorse. Every one resolves to a real frame.

## 2. Negative anim phases crashed (found while verifying #1)

The blast-radius sweep flagged `gods #0 phase=-1` resolving to an **empty**
frame name. A negative `afaze` is a real scripting idiom for "not started yet":

```lua
objekty.afaze = -1          -- gods: the sunken wreck, parked
konik.afaze = -random(100)  -- fdto: the seahorse, counting up to its anim
```

Legacy `ResourcePack::getRes(name, rank)` advances with
`for (i = 0; i < rank && ...)`, which **never runs for a negative rank** - so it
returns frame 0. This port's `resolveFrame` did `phase % length`, and JS's `%`
keeps the sign (`-37 % 3 === -1`), indexing off the front of the array and
handing `undefined` to `pictureToAtlas()`.

This was a **pre-existing latent bug reachable at runtime** (`updateAnim()` sends
a negative phase straight to `setAnim`), not something #1 introduced - #1 just
made it reachable at *load* time too, where it would have thrown in
`buildAnimators`. Fixed to match `getRes`: `const index = phase < 0 ? 0 : phase % len`.

## 3. Elevator rope missing in replay

`game_addDecor("rope")` (docs/055) was drawn by `LevelScene` only. In the
original, decors hang off the Room's View (`Room::addDecor` ->
`m_view->addDecor`) and replay drives *that same Room* via `Room::loadMove()` -
so the cables are drawn in replay exactly as in play. Ported: the drawing logic
moved to a shared `drawRopeDecors()` in `sceneUtils.ts`, now called by both
scenes. `ReplayScene.init()` also clears its `ropeGraphics` handle, since Phaser
reuses the scene instance but destroys its GameObjects (cf. docs/012).

## 4. Ending node + step counter

- The sandbox's ending affordance was a text button with a background box. It's
  now a real map node (`node-far` + pulsing `node-open`) at top centre, built
  from a synthetic `WorldMapNode` so it hovers/labels like any other node - and,
  being registered in `nodeSprites`, it's hidden by `setNodesVisible()` with the
  rest of the graph while the pedometer is up (the reported bug). The ending has
  no position in `worldmap.lua` at all (docs/050), so `ENDING_NODE_Y` is the
  port's own choice.
- Step counter: right edge was flush at the room width while the top margin was
  10px. Both are `STEP_MARGIN` now.

## 5. party2's window limbs flashed over the cabin at load

Same deferred-init-state class as #1, different call. `party2`'s cabin
(`kabina`) has limbs that poke out of its portholes - `kuk`, `ruka`, `frkavec`,
`hnat`. Each `prog_init_*` calls `<limb>:setEffect("invisible")` at init and only
reveals it later (a `drazdit` countdown). But `model_setEffect` was a **no-op in
`levelLoader.ts`** and `LevelModel` had no initial-effect field, so the loader
rendered them normally and the `hnat` image flashed over the cabin for the ~1s
before the live engine applied `invisible`. (Same root shape as the parrot: an
init state set in `prog_init` but not reflected until `levelScript` goes live.)

### Fix

Two parts, because the flash had two sources:

1. **Loader records it.** `model_setEffect` now writes `HostModel.currentEffect`,
   exported as `LevelModel.initialEffect` (the effect prog_init left the model
   in - usually null; `"invisible"`/`"reverse"` render differently). `sync()`
   already applies an effect, so `buildAnimators` passes `initialEffect` to the
   build-time `sync`.
2. **The round loop respects it until live.** The build-time sync alone wasn't
   enough - `tick()` runs every round *before* `levelScript` exists and passed
   `effect = null` (`getEffect` is only reachable once live), re-showing the
   sprite on the first tick. Fixed by falling the effect back to
   `initialEffect` while the live engine is absent:
   `this.levelScript?.getEffect(i) ?? this.levelData.models[i].initialEffect`.
   Once live, the live engine's own `prog_init` has re-set the same effect, so
   `getEffect` wins and the fallback stops mattering. Confirmed the test
   discriminates: with only the build-time sync, `hnat` was still visible in
   22/27 pre-live frames.

`ReplayScene` keeps the **init** effect (not the live one) throughout - replay
doesn't drive decorative effects (docs/025), and an init-hidden model staying
hidden is the safe default (gods' wreck, party2's limbs). This also tightens
docs/051: gods' parked wreck is now hidden pre-live too, not just once the live
`setEffect` binding runs.

## Verification (real browser)

- **Parrot**: loader reports papzivy `initialPhase: 9`, papouch still `0`;
  cabin2 renders `pap-zivy_09` from the *first* frame - the colourful frames
  never appear.
- **Negative phase**: `resolveFrame` -1 and -37 both -> frame 0; phase 4 still
  wraps to 1. Confirmed the check discriminates (the old expression really does
  yield `undefined`). `gods` + `fdto` both drive 3s with zero errors.
- **All-81 sweep**: every level loads; all 164 fish still open on `"rest"`; no
  model opens on an anim it lacks.
- **Rope**: elevator1 replay registers its 2 ropes, Graphics at depth 5 with
  real draw commands; screenshot shows both cables.
- **Ending node**: at (320, 18), texture `node-far`, hidden with the pedometer
  and restored on cancel, old text button gone (7/7 checks).
- **Step counter**: top margin 10px == right margin 10px.
- **party2 limbs**: loader records `invisible` for kuk/ruka/frkavec/hnat, `null`
  for the never-hidden cabin; `hnat` hidden in all 29 pre-script-live frames
  (0 visible); screenshot shows a clean cabin.
- e2e suite **7/7** (incl. the 80-level sweep and 80/81 solution replays);
  `tsc -b` clean.

## Files
- **Modify:** `web/src/lua/levelLoader.ts` (`APPLY_INITIAL_ANIMS_SOURCE` +
  `ffwg_hasAnims` glue, run after `code.lua`; `model_setEffect` records
  `currentEffect` -> `LevelModel.initialEffect`), `web/src/scenes/ModelAnimator.ts`
  (`resolveFrame` negative-phase guard), `web/src/scenes/sceneUtils.ts`
  (`drawRopeDecors`), `web/src/scenes/LevelScene.ts` (use it; `STEP_MARGIN`;
  apply `initialEffect` at build + as the round-loop fallback until live),
  `web/src/scenes/ReplayScene.ts` (draw ropes; clear the stale handle; keep
  `initialEffect`), `web/src/scenes/WorldMapScene.ts` (`drawEndingNode`,
  `ENDING_NODE_Y`).

## Open
The ~380ms Lua-bootstrap window before `levelScript` goes live still exists -
#1 fixes the *opening frame*, so nothing visibly wrong renders during it, but a
level's decorative item animation genuinely starts ~380ms late (and the dialog
gate adds ~400ms more before the first line can fire). Closing that would mean
bootstrapping the live engine during the world map's "Loading" phase, which
needs `createLevelScript`'s `hostActions`/`engineControl` to become attachable
after creation rather than constructor args. Not done; no known visible symptom
now.
