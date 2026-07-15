# 053 - Replay: no fish acceleration, instant falls

_2026-07-15_

## Why

User reported that in replay mode the fish accelerates and falls animate cell by
cell, and asked to validate against the original. Both were real infidelities.

## What the original does

Replay is driven by `LevelLoading::nextLoadAction()`, which for each cycle calls
`Room::loadMove(symbol)` (`m_loadSpeed = 1` for `loadReplay`):

```cpp
void Room::loadMove(char move) {          // "Load this move, let object to fall
    static const bool NO_INTERACTIVE = false;   //  fast. Don't play sound."
    bool falling = true;
    while (falling) {
        falling = beginFall(NO_INTERACTIVE);
        makeMove(move);
        finishRound(NO_INTERACTIVE);
    }
}
```

Two consequences, both directly matching the report:

1. **Falls are instant.** The `while (falling)` loop settles *every* pending fall
   inside the same move - a replay never animates gravity cell by cell.
2. **The fish never accelerates.** `finishRound(NO_INTERACTIVE)` skips
   `m_controls->lockPhases()`, so `Controls::m_speedup` never increments and
   `ensurePhases()` is never called (`getLocked()` stays 0). Nothing during a
   replay builds a speed-up streak.

(`beginFall(NO_INTERACTIVE)` likewise skips `fallout`'s `ensurePhases(3)` and the
impact sounds.)

## The port's bug

`Room.replayRound()` (docs/025) deliberately mirrored `nextRound()` instead -
one `beginFall()` per round (so gravity animated a cell at a time) **and** a
`updateMoveStreaks()` call. That last one is the port's per-Cube equivalent of
`m_speedup` (docs/017, visual-only) - and its own doc comment says it mirrors
"finishRound() -> lockPhases() timing", which is exactly the call the original
*skips* under NO_INTERACTIVE. So the streak climbed during replay and
`movePhases()` accelerated the fish 3 -> 2 -> 1 phases.

## Fix

`web/src/game/Room.ts`:

- `replayRound()` now calls `fastForwardSettle()` (already the port of
  `loadMove`'s "fall fast" loop, docs/035) instead of a single `beginFall()`, and
  **no longer calls `updateMoveStreaks()`**. It still applies exactly one driven
  move per call, so the fish is still watched swimming cell by cell - only
  gravity is instant, matching the original. The old `fastFalling` branch is
  subsumed (this always settles fully now).
- `loadMove()` also dropped its `updateMoveStreaks()` - same NO_INTERACTIVE
  reasoning. It matters beyond the headless validator: **save-resume** (docs/026)
  replays a whole move string through `loadMove`, and a leftover streak made the
  resumed fish start out looking mid-sprint.

Visual-only in both cases - `moveStreak` never affects physics (docs/017), which
is why the headless validator is unaffected.

## Verification (real browser)

Sampling `moveDurationMs` across whole replays:

- `airplane` (523 samples) and `warcraft` (428 samples): duration is **always
  300ms** - never 200/100, i.e. no acceleration.
- **0** rounds in either where something moved without a fish driving it, i.e. no
  animated gravity fall - falls are instant.
- Interactive play still accelerates: 300 -> 200 -> 100 (no regression).
- A full 533-move `gods` watchable replay still drives to Solved and reaches its
  poster (docs/050), so the settle change didn't break replay outcomes.
- e2e suite 7/7 (incl. the 80/81 headless all-solutions replay); `tsc -b` clean.

## Files
- **Modify:** `web/src/game/Room.ts` (`replayRound` settles fully + no streak
  update; `loadMove` no streak update).
