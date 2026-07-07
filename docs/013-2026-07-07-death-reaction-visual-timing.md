# 013 - Death Reaction Visual Timing

2026-07-07

## Bug report

User noticed that when a fish is pushing/holding an item and that item
starts falling, the fish turns into a corpse *before* the item visibly
reaches it.

## Investigation: is the physics wrong?

Compared `web/src/game/Rules.ts`'s `checkDeadFall`/`checkDeadMove`/
`whoIsFalling`/`isOnHolderBacks` line-by-line against
`legacy/src/level/Rules.cpp` - they match exactly, including the call
order relative to `occupyNewPos()`/`changeState()`. The original's death
check is **predictive by design**: `whoIsFalling()`/the move-crush check
look at `getResist(Dir.UP)` (what's directly above, in the cell adjacent
to the potential victim) and whether that model's `dir` is already queued
to move down/sideways *this round* - not whether it has actually entered
the victim's cell. Two cubes can never literally occupy the same Field
cell in this engine's model, so "wait for actual overlap" was never a
valid design either version could have used - a kill is always detected
the round the killer becomes adjacent-and-already-moving, one round before
its move would nominally carry it in.

So the physics is faithful and not the bug. The original's rendering,
though, applies both changes (the killer's position update *and* the
victim's death) within the same synchronous game/render cycle (docs/009:
original logic+render run in lockstep at a flat ~10Hz) - a player would
never see one happen without the other. Our port's presentation layer is
deliberately decoupled from the physics round (docs/009) and slides
positions smoothly over `SLIDE_MS` (docs/010) - but `ModelAnimator.sync()`
was swapping the dying fish's texture to the skeleton pose *instantly*,
the same round physics detected the death, while the killer item's own
position tween was still gliding into the adjacent cell for the next
`SLIDE_MS`. That gap - real, if short (~100ms) - is what read as "died
before it was hit."

## Fix

`web/src/scenes/ModelAnimator.ts`: the skeleton-pose transition is now
delayed by `SLIDE_MS` via `scene.time.delayedCall` instead of applied
immediately, guarded by a `deathReactionPending` flag so it's only
scheduled once per model (and the returned timer is tracked and removed
in `destroy()`, same as the existing body/head timers, so a mid-delay
restart can't fire a callback against an already-destroyed sprite). The
fish keeps showing its last live pose for that one slide-window, then
switches to skeleton - by which point any killer's own `SLIDE_MS` position
tween (started the same round, same duration) has also finished, so the
two now land in the same visual moment again. Head-sprite hiding stays
immediate (unaffected - it's a fade-in/out of a small overlay, not the
main "is this fish dead" visual cue).

Applied uniformly to all death causes (fall, move-crush, stress), not
just the falling case - simpler than special-casing per cause, and
harmless when nothing else happens to be sliding that round (the delay is
just imperceptible then).

No physics changes - `Rules.ts`/`Landslip.ts`/`Cube.ts` untouched.

## Verification

- `npx tsc -b` clean.
- Live-browser check (temporary `window.__game` hook, same technique as
  `docs/011`/`docs/012`, removed after): triggered a real death through
  the actual `Rules.readyToDie` flag and sampled the fish's body sprite's
  texture key at high frequency. With the page properly focused (an
  unfocused/background Playwright tab throttles Chromium's
  `requestAnimationFrame`-driven timers, which skewed an initial
  unfocused run to ~440ms - a test-harness artifact, not a real slowdown,
  confirmed by re-running focused), the skeleton texture appeared 113ms
  after `isAlive` flipped false - matching the intended `SLIDE_MS` (~104ms
  at the current `ROUND_MS`) almost exactly.
- Regression: re-ran `docs/007`'s synthetic-room tests, `docs/008`'s
  10-level goal test, `docs/011`'s corpse-removal round-timing test, and
  `docs/012`'s restart-error check - all unchanged, zero regressions.

## Open for next time

- Everything from `docs/009`-`docs/012`'s "Open for next time" still
  applies.
- The same instant-vs-delayed-slide mismatch could in principle affect
  other instant state reactions if any get added later (e.g. a future
  "escape" flourish) - worth remembering the pattern (delay non-position
  visual reactions by `SLIDE_MS` when they can coincide with a position
  slide) rather than re-discovering it from scratch.
