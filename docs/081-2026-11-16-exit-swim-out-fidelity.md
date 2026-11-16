# 081 - Leaving the room: swim out, don't fade at the edge

2026-11-16

## Why

The user reported a regression against FF NG: in the original, a fish that reaches the room
border swims on in that direction until it is out of view. In the port, at least in replay,
it faded out on the spot as soon as it touched the edge. Asked to verify both fish and the
`goal_out` items of the final levels, which must also travel out in their direction.

## Root causes (four, compounding)

1. **Replay fast-forwarded the exit.** `Room.replayRound()` ran `fastForwardSettle()` before
   every move, including after the last one, so all going-out rounds (`fallout`) happened
   inside one tick. Legacy `LevelLoading::nextLoadAction()` does that only while moves remain
   (`loadMove`). With the queue empty, it runs one plain `beginFall` + `finishRound` per cycle.
2. **The animator froze a lost model at its last in-room cell.** `ModelAnimator.sync()` for
   `isLost` zeroed the slide and faded the sprite at its previous base cell, i.e. half inside
   the room, and live play had the same snap-back. A model goes `isLost` only when
   `Rules.actionOut()` finds it fully outside, so its last slide has already carried it out of
   view. Legacy `View::drawOn` simply doesn't draw lost models.
3. **Only the first going-out round was paced as a move.** `roundPhases` gave 3 cycles only
   for the `"goout"` state, which is `outDepth === 1`. Legacy `Room::fallout()` calls
   `m_locker->ensurePhases(3)` for every round with `outDepth > 0`, so the rest of an exit ran
   at fall speed (1 cycle per cell), in live play too.
4. **Replay stopped while the level still held a fish.** `cannons`' script parks the small
   fish at the exit (`small:setBusy(true)` once the big fish is out and `small.X > 25`), plays
   a line, then `planBusy(small, false)`. `actionOut` skips busy models. Replay stopped on the
   first settled round after the last move, so the fish stayed at the edge forever (the "small
   fish stays at the exit" open item from docs/078). Legacy replay mode (`m_replayMode`) keeps
   running rounds until solved.

## What was done

- `Room.replayRound(null)` (moves exhausted): one `beginFall()` per call, matching legacy.
  With moves left, the new `settleUntilGoingOut()` settles falls instantly as before but
  returns after a round in which `fallout` moved something, and resumes on the next call.
  This is the same sequence of `beginFall()` rounds, only spread over time, so outcomes are
  unchanged. It is a deliberate deviation from `loadMove()`, where a mid-replay exit (e.g. the
  first fish while the second still has moves) was instant.
- `ModelAnimator.sync()`: `isLost && isOut` means hide immediately, with no fade. The 400 ms
  fade stays only for disintegrated corpses.
- `Room.lastWentOut` (set in `beginFall` when `fallout()` moved something), exposed as
  `GameEngine.wentOutThisRound()`. `roundPhases(..., wentOut)` returns 3 for it, replacing the
  `"goout"` check. Used by LevelScene, ReplayScene and `estimateReplayMs`.
- ReplayScene `outOfMoves()`: stop only when moves are exhausted, the room is fresh, **no model
  is busy** (`GameEngine.anyModelBusy()`), and that has held for 3 consecutive rounds, so a
  model released on a round gets its next `beginFall`.

## Verification

- A scratch Playwright run in Edge stepped each replay at 10 ms (fast 6×) and recorded every
  going-out model's sprite each step: `cannons` (both fish) and every level with `goal_out`
  items (`atlantis barrel floppy gods grail linux map propulsion turtle`). For all 31 models,
  the exit path is monotonic in one direction (no back-steps), there is no alpha fade, nothing
  is drawn after `isLost`, and the last drawn frame is outside the room (≤ 3 px inside, one
  sampling step). Each exit cell takes 3 cycles.
- All 80 reference solutions still solve through `tickReplay`; e2e 03, 06 (only the known
  unrelated `cabin1-simple` failure, see docs/078) and 09 pass.
- `cannons` MP4: the small fish waits at the exit for its line, swims out, and the tail is
  clean.

## Open for next time

- Replay shows no subtitles or voices (docs/025), so the fish's wait at the exit in `cannons`
  has no visible reason in the video.
- `estimateReplayMs` has no live Lua, so it misses such script-held waits (`cannons`: 16.3 s
  estimated, 20.7 s actual).
