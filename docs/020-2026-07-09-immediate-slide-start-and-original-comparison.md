# 020 - Immediate Slide Start and Original Comparison

2026-07-09

## Context

Follow-up to `docs/019`. After fixing dropped/delayed key input, the user
asked whether the remaining wait before a fish visibly moves could be
tightened further, noting the original "starts moving immediately, without
lag." Rather than guess, traced the original's real animation-timing
mechanism (`legacy/src/level/PhaseLocker.cpp`, `Controls::lockPhases()`,
`Level::own_updateState()`, `View::getScreenPos()`) instead of assuming it
matched this port's own design.

## What the original actually does

- The whole engine - input, physics, drawing - runs in **one
  single-threaded loop** paced by a fixed 100ms cycle (`TimerAgent`,
  `own_init()`'s `m_timeinterval` default). While idle it polls input every
  cycle, same shape of wait this port already has (just 100ms vs 130ms).
- `Level::own_updateState()` only starts a **new** round
  (`nextAction()` -> `Room::nextRound()`) once `PhaseLocker::getLocked() ==
  0` - i.e. once the *previous* round's decided move has finished playing
  out its full animation window. `Controls::lockPhases()` sets that window
  each round from `getNeededPhases()`: the turn anim's own frame count while
  turning, else the swim anim's frame count divided by 2/3/6 depending on
  consecutive-move streak (`SPEED_WARP1=6`, `SPEED_WARP2=10`). Queried the
  real frame counts this port already extracted from the same legacy
  assets: `swam` = 6 frames, `turn` = 3 frames (both fish, `airplane`) - so
  a fresh move needs 3 phases (300ms), dropping to 2 (200ms) then 1 (100ms)
  once warmed up.
- Critically, **the visible slide is not gated behind that lock at all**.
  `View::getScreenPos()` computes a model's screen position from
  `Cube::getLastMoveDir()` (the direction *just decided* this round) plus a
  shift that grows by `SCALE / phases` every single draw call
  (`View::drawOn()` increments `m_animShift` unconditionally, regardless of
  lock state) - **not** from the model's committed grid `location`, which
  only updates via `occupyNewPos()` once the lock finally drains. So the
  original has the *same* decide-this-round/apply-next-round split
  internally (confirmed identical to this port's own `Rules.ts`/`Room.ts`
  port), but it's invisible to the player: the slide begins the exact
  instant a move is decided and completes smoothly over the phases window,
  landing exactly on the new cell right as the model location itself
  updates.

## The actual gap

This port's `ModelAnimator.sync()` only started a position tween once it
*saw* `model.x`/`model.y` change - and that doesn't happen until the round
*after* the move was decided (`occupyNewPos()` runs at the start of the
next round). The swim texture already reacted immediately (`computeBodyAnim`
reads `action`, not position) - only the **position tween** was delayed by
a full extra round, leaving the fish standing still, replaying its swim
frames in place, for ~130ms before any real motion began. That artificial
stand-still stage - not the input-polling wait, which both engines share -
is what read as "lag" relative to the original.

## Fix

`web/src/scenes/ModelAnimator.ts`: new `MOVE_OFFSETS` (action name -> grid
offset) lets `sync()` predict a model's slide target from the round's own
freshly-decided direction (`RenderModel.action`, e.g. `"move_right"`)
instead of waiting for the official `model.x`/`model.y` to change - the
same trick `View::getScreenPos()` uses (direction + growing shift, not
committed location):

```ts
const officialX = model.x * GRID_SCALE;
const offset = MOVE_OFFSETS[model.action];
const predictedX = offset ? this.lastPxX + offset.dx * GRID_SCALE : officialX;
const targetX = officialX !== this.lastPxX ? officialX : predictedX;
```

`officialX` wins whenever it disagrees with the last known target (the
normal case once `occupyNewPos()` catches up, and a safe fallback for
anything the prediction doesn't cover) - the sprite can never get stuck on
a missed prediction, it just falls back to the pre-fix behavior. Applies
uniformly to every model, not just fish: pushed items and falling items get
the same immediate-slide treatment, matching `View::getScreenPos()`'s own
uniform (all-`Cube`) scope rather than special-casing units.

No physics changes - `Rules.ts`/`Room.ts` untouched, this is presentation
layer only.

## Verification

- `npx tsc -b` clean.
- Direct browser trace (`window.__game` hook, focused page - an unfocused
  headless tab throttles `requestAnimationFrame`-driven timers, the same
  artifact `docs/013` hit and documented, re-confirmed here on a first pass
  that showed rounds firing every ~280ms instead of 130ms until the page
  was properly focused): confirmed a tween is already running
  (`tweenProgress: 0`) in the very same tick that `action` first reports
  `"move_up"`, while `model.location` is still the old cell - proving the
  slide starts the same round the move is decided, not the round after.
  Held movement across 6 cells showed continuous, gapless sliding with no
  double-tweens or jumps.
- 8-tap batch latency measurement (keydown timestamp vs. tween-start
  timestamp vs. tween-settle timestamp, page focused): reaction latency
  (keydown -> slide starts) ranged 22-105ms as expected (0-130ms window,
  `docs/019`'s queued-key fix already guarantees the round notices it);
  **total time to visually arrive at the next cell ranged 173-274ms,
  averaging 212ms** - down from the previous design's up-to-362ms, and
  under the original's own 400ms worst case for a fresh (non-warmed-up)
  move, though that's incidental (`SLIDE_MS`=104ms is shorter than the
  original's fresh-move 300ms for unrelated reasons - see `docs/010`).
- Regression: held continuous movement (2s, 12 cells), mouse
  click-and-hold pathing, restart, and a `docs/010`-style high-frequency
  (25ms) diagonal-blending check on a pushed/falling item (122 samples,
  zero frames with both axes simultaneously mid-flight) - all re-verified
  clean, zero console/page errors throughout.
- Updated the `docs/019` comparison artifact with these real measured
  numbers, relabeling the "proposed" section as implemented.

## Open for next time

- The 0-130ms wait for the next round to notice a keypress at all is
  unchanged - `docs/019`'s "Open for next time" on a faster `ROUND_MS`
  still applies as the only remaining lever, and is a bigger, separate
  change (`SLIDE_MS` and the swim-speedup tiers are tuned against its
  current value).
- The keydown-time prediction idea from `docs/019` (start the swim texture
  synchronously on keydown, before any round fires) hasn't been
  implemented - still valid and still stacks with this fix, but this fix
  covers the larger share of the gap on its own.
