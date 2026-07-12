# 032 - Death auto-restart + demo restart/load render fix

_2026-07-12_

Two death/restart bugs the user found while play-testing the `briefcase` tutorial
(docs/031), one of which turned out to be a missing behavior across **all** levels.

## 1. Auto-restart when both fish can no longer move (all levels)

**Before:** when a fish died the port just latched "A fish died - press R to
restart" and waited for the player forever. The original auto-restarts.

**Original behavior** (`legacy/src/level/`): `Level::own_updateState()` runs
`m_countdown->countDown(this)` every cycle. `LevelCountDown::setCountDown()`
(`LevelCountDown.cpp`) picks the countdown source:

- `room->isSolved()` → `getCountForSolved()` (10 cycles, 30 if a dialog runs) →
  `finishLevel()` returns to the map. This port already had this (docs/030,
  `SOLVED_RETURN_ROUNDS`).
- **`room->cannotMove()`** → `getCountForWrong()` = **75 cycles** →
  `Level::finishLevel()` calls **`action_restart(1)`**. This is the piece we were
  missing.

`Room::cannotMove()` = `Controls::cannotMove()` = *no unit `willMove()`* — i.e.
both fish are dead or wedged. Note it is **not** `isSolvable()` (which the port had
been using for the death message): a single dead fish leaves the level unsolvable
but the survivor still moves, so `cannotMove()` stays false and the original does
**not** auto-restart until nothing can move at all.

**Port:** the port already exposed `GameEngine.cannotMove()`/`Room.cannotMove()`
(mirrors `willMove() = isAlive && !isLost`). Added `WRONG_RESTART_ROUNDS = 75` and a
`wrongCountdown` field to `LevelScene`, and a `cannotMove()` branch to `tick()`'s
win/lose block (checked after `isSolved()`, so a genuine win — both fish `isLost`,
which also makes `cannotMove()` true — is handled by the solved branch first). When
it fires it latches a "Both fish are stuck - restarting..." message, counts down 75
rounds (the same per-`ROUND_MS`-round proxy `SOLVED_RETURN_ROUNDS` uses), then calls
the existing `restart()` — exactly like pressing R. The counter is independent of
`gameOver` so it still starts if `gameOver` was already latched by the older
single-death (`!isSolvable()`) informational branch, which is kept.

Verified in a real browser (airplane): forcing `cannotMove()` latches the message +
`wrongCountdown=75`, and ~10s later the level auto-restarts (fresh engine,
`stepCount=0`, `wrongCountdown=-1`, `gameOver=false`).

## 2. Demo restart/load rendered stale positions (briefcase Phase 2)

**Symptom** (user): during the Phase-2 tutorial, when the demo intentionally kills
the fish and its `demo_help.lua` runs `level_action_restart()` then
`level_action_load()`, items briefly slid around the screen and the room showed at
the start position **with no fish**, before the load finally settled it.

**Cause:** ordering in `LevelScene.tick()`. It captured
`renderModels = this.engine.getRenderModels()` **before** `levelScript.tick()`, then
reused that same snapshot for the sprite sync loop afterwards. But during an
auto-play show, the show command runs *inside* `levelScript.tick()` →
`runShowStep()` → `level_action_restart/load/move` → `hostActions.*`, and
`demoRestart()`/`demoLoad()` swap in a **new** `GameEngine` and **rebuild the
animators** (`resetPhysicsOnly()` → `buildAnimators()`) at the fresh start/saved
positions. The post-`tick()` sync loop then drove those freshly-built sprites with
the *stale pre-step* snapshot (the death-position models), sliding them back toward
where the fish had died and showing dead-fish poses — hence "items moving around"
and "no fish."

**Fix:** split the snapshot. `preStepModels` is captured before `levelScript.tick()`
(the auto-play show reads live positions here via `moveXY` → `model:getLoc()` to
decide its next move); a fresh `renderModels = this.engine.getRenderModels()` is
re-read **after** the script step and used for the render/sync loop, so sprites
reflect the post-step engine (and the animators a restart/load just rebuilt). For
the normal, non-show path the move already happened before the first read, so the
re-read is identical — no behavior change there.

Verified in a real browser (briefcase): after moving the big fish off its start
cell and forcing a show `demoRestart()` through the real host path, every model
(both fish included) renders at its fresh start cell (`sprite == model*GRID_SCALE`)
and stays visible — previously the sprites tracked the stale moved/death positions.

## Files

- `web/src/scenes/LevelScene.ts` — `WRONG_RESTART_ROUNDS`, `wrongCountdown` (reset in
  `startEngine`), `cannotMove()` auto-restart branch in `tick()`; `preStepModels`
  vs. re-read `renderModels` split around `levelScript.tick()`.

## Open / follow-ups

- The 75-round auto-restart delay is faithful to `getCountForWrong()`'s 75 cycles;
  at `ROUND_MS` (130ms) that's ~9.75s vs. the original's ~7.5s at ~100ms/cycle —
  the same per-cycle-proxy imprecision docs/030 accepted for the solved countdown.
