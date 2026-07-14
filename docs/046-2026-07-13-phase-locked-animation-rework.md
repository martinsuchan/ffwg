# 046 - Phase-locked animation rework (shared clock, matching the original)

_2026-07-13_

## Why

Reported symptoms - pushed items desync (warcraft: big fish pushing 3 items),
jumpy "fast swimming", desync in replays on pushes/falls - all traced to one
root cause: the port (docs/007/009/010/017/020) had replaced the original's
single shared animation clock + phase-lock with **independent per-model Phaser
tweens on a fixed 130ms round timer**. Concretely:

- `ModelAnimator.speedStepsFor(model.moveStreak)` computed "swims faster" **per
  model** from each cube's own streak. A pushed item's streak climbs while the
  pusher fish's resets to 0, so after ~6 rounds the items' slide halved while
  the fish's didn't - they separated.
- The fixed 130ms round was decoupled from the ~104ms slide, so the sprite
  arrived early and waited (worse with speed-up: zip ~50ms, wait ~80ms), and the
  frame index stepped by 2-3, skipping frames.

## The original's model

`legacy/src/level/View.cpp` + `plan/PhaseLocker.cpp` +
`Controls::lockPhases/getNeededPhases` + `gengine/TimerAgent.cpp`: one shared
`m_animShift` (0→SCALE) drives **every** model's draw
(`screenPos = location·SCALE + moveDir·m_animShift`), so co-moving models are in
exact lockstep. `PhaseLocker` locks the logic for `N` cycles (a move fully
animates before the next round); `N` comes from the **active fish only**
(`swam/2,/3,/6` = 3/2/1 phases; turning = 3; pushing forces speed-up to 0).
`TimerAgent` cycle = 100ms → base 300ms/cell, accelerating to 100ms.

## What changed

A faithful port, keeping physics + Lua fused in one round (preserves the
docs/031/032 show/dialog/audio ordering).

- **`timing.ts`**: `ROUND_MS` (130) → **`CYCLE_MS` (100)** + `IDLE_ROUND_MS`.
  The one base-speed knob: a move lasts `phases · CYCLE_MS` (300/200/100ms). The
  3/2/1 ratios are structural (swim-frame count), so this one constant scales
  base speed and every tier - a future "Advanced settings" slider.
- **Global speed in the engine**: `GameEngine.getActiveInfo()` (active fish's
  model index + action + speedup = its own `moveStreak`) and `anyModelMoving()`;
  `Room.getActiveUnit()`. Only the active fish's speedup drives the phase count
  (`ModelAnimator.movePhases`, the `getNeededPhases` port), so a fish and
  everything it pushes share one duration - no per-model desync. `moveStreak` is
  reused (not removed) but only the active fish's is read now.
- **Shared clock in the scenes** (`LevelScene`/`ReplayScene`): the fixed round
  `time.addEvent` is gone. A Phaser `update(time, delta)` advances one shared
  `cellProgress` (0→1) and calls `animator.render(progress)` on every animator -
  the source of lockstep - then runs the next physics round when the interval
  (`phases · CYCLE_MS` moving, one cycle idle) elapses. The decide/apply split
  (docs/007/020) falls out for free: `render` slides the model's committed cell
  toward `cell + moveDir(action)` over the round, arriving exactly where next
  round's `occupyNewPos` commits it - so a multi-cell fast-settle (windoze) just
  lands `base` at the settled cell and snaps, no smearing (the "snap guard"
  needs no special code). `resetAnimationClock()` runs in `buildAnimators` so a
  mid-tick demo restart/load starts clean.
- **`ModelAnimator`**: rewritten - `render(cellProgress)` places the sprite from
  the shared progress; the per-model position tween, `SLIDE_MS`, `speedStepsFor`,
  `MOVE_OFFSETS` prediction and `killTweensOf` defense are gone. Frame timers
  stay at `CYCLE_MS` but always step by **1** (no more frame-skipping). Corpse
  skeleton delay, removal fade, head/blink/talk unchanged.
- **Cycle-accurate Lua/dialog timing**: `levelScript.tick(models,
  cyclesThisRound)` - `state.cycles += cyclesThisRound` (phases when moving, 1
  idle) and `minTime = ceil(clipMs / CYCLE_MS)`, so `model_talk` voice length,
  `model_isTalking`, the dialog FIFO, `game_getCycles`, and `pokus` stay locked
  to wall-clock under the variable round interval. Win/lose countdowns
  (`SOLVED_RETURN_ROUNDS`/`WRONG_RESTART_ROUNDS`) also decrement by
  `cyclesThisRound`. `SubtitleStack` already ran on its own 100ms timer -
  untouched.
- **ReplayScene** media buttons become a speed factor on the shared clock
  (play 1×, fast N×, pause frozen); Step snaps one round.

Physics rules, `UnitAnimator` (which-anim), and the audio/save/replay-validation
layers are untouched.

## Verification

- **warcraft (the repro):** measured sprite positions while the big fish pushes
  3 items - max sub-cell fraction spread across the 3 horizontal co-movers =
  **0.0000** (perfect lockstep); lead mover had **0** flat-run samples (no
  zip-wait), 104px of continuous travel.
- **Acceleration:** `movePhases` maps 3/3/2/2/1 (speedup 0/6/7/10/11), turn = 3 -
  exactly `getNeededPhases`; `speedup` confirmed flowing from the active fish.
- **windoze:** 4 fish, active-switch (Space) + drive both directions, no errors.
- **briefcase:** round loop advances, no errors (the show/movie level unaffected).
- **viking1 dialog:** `model_isTalking` true after `model_talk`; a 1.82s clip →
  `endCycle` = 19 cycles (19·100ms ≈ 1900ms) - **cycle-accurate**, so voice stays
  in sync with its subtitle at any movement speed.
- **ReplayScene:** airplane replay solves end-to-end via the fast-forward
  control, no errors.
- e2e suite **7/7** (physics/loading unchanged - 05 all-levels sweep, 06
  all-solutions replay incl. windoze). `tsc -b` + `vite build` clean.

## Notes / tradeoffs
- Item-anim (`getScriptAnim`) still advances once per round, now at the variable
  interval (the original advances per fixed cycle) - cosmetic, no coupling
  regression. A fixed-cycle Lua pass is a possible later refinement.
- `CYCLE_MS = 100` = the original's authentic 300ms base; a one-line change if a
  future Advanced-settings slider wants it snappier.

## Files
- **Modify:** `web/src/game/timing.ts`, `web/src/game/GameEngine.ts`,
  `web/src/game/Room.ts`, `web/src/scenes/ModelAnimator.ts`,
  `web/src/scenes/LevelScene.ts`, `web/src/scenes/ReplayScene.ts`,
  `web/src/lua/levelScript.ts`.
