# 034 - "Both fish are stuck" auto-restart misfiring on a win

_2026-07-12_

User solved `cabin1` and got "Both fish are stuck - restarting..." instead of the
solved message - the docs/032 death auto-restart firing on a **win**.

## Root cause

docs/032 added a `cannotMove()`-driven auto-restart (legacy `LevelCountDown`'s
`getCountForWrong` path). The win/lose block checks `isSolved()` first, else
`cannotMove()` - which looks correct, but the two aren't evaluated on the same
round during a winning escape:

- `cannotMove()` (`Room`/`Controls`) is true the instant **both** fish are `isLost`
  - and `Cube.changeGoOut()` sets `isLost` the moment a fish crosses the exit
  border.
- `isSolved()` additionally requires `isFresh()` (`Room.lastAction === NO`, i.e.
  the room has settled). On the exact round the second fish walks out, `fallout()`
  returns true so `lastAction = MOVE` → `isFresh()` is **false** → `isSolved()` is
  still false that round.

So there's a one-round window where `cannotMove()` is true but `isSolved()` isn't.
docs/032's branch fired "stuck", latched `gameOver`, and started the 75-round
restart countdown. The next round `isSolved()` turned true - but the solved branch
was gated on `!this.gameOver`, which was now false, so it did nothing. The level
hung on the "stuck" message (or eventually restarted a level the player had solved).

(The reference-solution replay didn't reproduce it because that particular move
sequence happens to make both fish settle out on the same fresh round; a
player's own solution that walks the second fish out on a non-fresh round hits the
window.)

## Fix (`web/src/scenes/LevelScene.ts` + `web/src/game/GameEngine.ts`)

Align the "stuck" detection with `isSolved()`'s own freshness requirement:

- New `GameEngine.isFresh()` (→ `room.isFresh()`).
- The "stuck" branch is now `else if (this.engine.cannotMove() && this.engine.isFresh())`.
  During the escape (not fresh) it doesn't fire; once the room settles, `isSolved()`
  (checked first) wins. A genuine loss (both fish dead) still settles to fresh with
  `cannotMove()` true and `isSolved()` false, so it still restarts - unchanged.
- Belt-and-suspenders: the solved branch now gates on `solvedCountdown < 0` (not
  `!gameOver`) and clears `wrongCountdown`, so even a stray "stuck" latch is
  corrected the moment the win registers.

Nothing in the physics changed - only the presentation-layer win/lose decision.

## Verification (real browser, Playwright)

- **Transient reproduction** (the user's exact case): forced `cannotMove=true,
  isFresh=false, isSolved=false` for several rounds → status stays empty (no
  "stuck"); then `isFresh=true, isSolved=true` → shows "Solved". With the old
  ungated condition phase 1 would have shown "stuck".
- **Real solution**: replayed `cabin1`'s reference solution round-by-round (the
  watchable `tickReplay` path, advancing the move index only when a symbol is
  consumed - mirroring `ReplayScene`) → reaches Solved (round 185), and the
  `cannotMove && isFresh && !isSolved` trigger never occurs before the solve.
- **Regression**: the docs/032 genuine auto-restart still fires (airplane, forced
  `cannotMove` on a settled/fresh room → 75-round countdown → restart).

## Files

- `web/src/game/GameEngine.ts` - `isFresh()`.
- `web/src/scenes/LevelScene.ts` - `isFresh()` gate on the stuck branch; solved
  branch gated on `solvedCountdown < 0` + clears `wrongCountdown`.
