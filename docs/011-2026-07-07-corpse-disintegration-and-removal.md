# 011 - Corpse Disintegration and Removal

2026-07-07

## Bug report

User noticed that a dead fish's corpse stays in the room forever, and
anything resting on it never falls - in the original, the corpse
disappears after a few seconds and whatever it was supporting then drops.

## Original mechanism

Checked the original source directly rather than guessing:

- `Cube::change_die()` (`legacy/src/level/Cube.cpp:59-64`) only flips
  `m_alive=false` and attaches a purely visual `EffectDisintegrate`. It
  does **not** unmask the corpse - it keeps its Field cells, weight, and
  support behavior exactly like a live fish.
- `EffectDisintegrate` (`legacy/src/effect/EffectDisintegrate.h:11-12`,
  `DISINT_START=400`, `DISINT_SPEED=30`) decrements once per rendered
  frame; `docs/009` already established the original's render and logic
  loops run in lockstep at a flat ~10Hz, so this is effectively once per
  game cycle. `400/30 = 13.33` -> 14 decrements to reach zero.
- The actual field-level removal happens in `Rules::changeState()`
  (`Rules.cpp:244-247`), gated on the effect finishing:
  `if (!isLost() && isDisintegrated()) { unmask(); change_remove(); }`.
  `change_remove()` sets `isLost=true`, `weight=NONE`, and teleports the
  model to `(-1000,-1000)` - the exact same removal a `goal_out`/escaped
  model gets.

So: death -> ~14 game-cycles of dissolve while still fully solid -> unmask
+ remove. Anything resting on the corpse only loses support at that final
step, in the very next `Landslip::computeFall()`.

## What was missing in the port

`web/src/game/Rules.ts`'s `changeState()` and `Cube.ts`'s `changeDie()`
had no equivalent of the disintegration branch at all - `Landslip.ts`'s
`isFixed()` was already ported correctly (`isStoned || isWall || isAlive
|| isLost`), but since a corpse never reached `isLost`, it stayed
masked/solid forever, so nothing resting on it ever fell.

## Fix

- **`Rules.ts`**: new `DEATH_REMOVE_ROUNDS = 14` constant (ported from the
  `400/30` math above) and a `deathRoundsLeft` counter. `changeState()`
  now starts the counter the round a model actually dies
  (`readyToDie` branch), and ticks it down every subsequent round; at
  zero it calls `this.m.unmask()` + `this.model.changeRemove()` - reusing
  the `changeRemove()` that already existed for the escape/`goal_out`
  path, since it's the exact same operation. Counted in physics rounds,
  not wall-clock time or render frames - `docs/009` already decoupled
  animation/rendering from the physics loop, so there's no render-call
  equivalent to hook on the physics side, and rounds are the
  deterministic, framerate-independent unit that actually matters here.
- **`LevelScene.ts`**: found and fixed a second, related bug while
  verifying this in real gameplay - `tick()` was completely stopping the
  round loop the instant the level became unsolvable (i.e. the moment a
  fish died), *before* the 14-round countdown could ever finish. The
  round loop now always keeps running; `gameOver` only latches the status
  text/message once, it no longer gates simulation. Without this fix the
  physics-layer timer above would be correct in isolation but would never
  actually fire during real play.
- **`ModelAnimator.ts`**: `sync()` no longer instantly hides a model the
  round it goes `isLost` - it fades it out (alpha 1->0, `REMOVE_FADE_MS =
  400`) first, then hides it. This is a deliberately simple stand-in for
  the original's full pixel-dissolve shader (which is exactly the kind of
  per-level/per-death visual effect `docs/009` already put out of scope) -
  the corpse already sits in its "skeleton" pose for the full 14-round
  countdown beforehand (existing behavior, unchanged), so the fade is just
  softening the final disappearance rather than trying to reproduce
  `EffectDisintegrate` pixel-for-pixel.

No changes to `Landslip.ts`, `Field.ts`, `MarkMask.ts`, or `Goal.ts` -
`Landslip`'s fixed/support logic was already correct and needed nothing
new to react to a corpse's removal once it actually happens.

## Verification

- `npx tsc -b` clean.
- New synthetic test (Playwright + dynamic `import()`, same technique as
  `docs/007`): a heavy item free-falls onto a fish sitting on the floor,
  killing it and coming to rest directly on the corpse. Ran 40 rounds
  un-truncated (previous tests like `docs/007`'s test 5 stopped at the
  moment of death) and confirmed: the item's y stays constant for exactly
  14 rounds after death (still supported), the corpse's `isLost` flips
  true at exactly round `deathRound + 14`, and the item's y increases by
  1 the very next round (falls into the now-empty cell) - round-perfect
  match with the ported constant.
- Real-browser check: triggered a genuine death on the live `airplane`
  level through the actual game-rule path (setting `readyToDie` on a real
  `Rules` instance, same flag `checkDead()` would set, then letting the
  normal round loop consume it - not a mocked/synthetic room), and
  screenshotted across the disintegration window. Confirmed the corpse
  shows its skeleton pose immediately, then is fully gone from the screen
  within the expected few-second window, with the "A fish died" status
  message staying visible the whole time (confirming the `LevelScene` fix
  above - the loop kept running post-death instead of freezing).
- Regression: re-ran `docs/007`'s synthetic-room tests, `docs/008`'s
  10-level goal-loading test, and `docs/009`'s stress-play/restart
  scripts unmodified - identical results, zero console errors.

## Open for next time

- Everything from `docs/009`'s and `docs/010`'s "Open for next time"
  still applies (phase 2/3, blink not visually reconfirmed, texture atlas
  extension, the pre-existing `test5_fallingItemKillsFish` `isSolvable`
  oddity).
- The fade-out is a flat 400ms regardless of cause (corpse vs. escaping
  goal_out/goal_escape model) - fine for now since both are genuinely
  "vanish" moments, but worth reconsidering if a future level makes the
  distinction visually matter.
