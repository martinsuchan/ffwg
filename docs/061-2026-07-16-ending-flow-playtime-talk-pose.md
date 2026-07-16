# 061 - Ending flow (pedometer), play-time counter, and the talking body pose

_2026-07-16_

Three ending-related issues the user found while play-testing the `ending` level.

## 1. Talk body pose (the head was fine, the body wasn't)

When a fish speaks to the player in a scripted conversation, FF NG turns its
**body** to a front-facing `talk` pose; the port kept rendering the side pose.
`animateHead()` (level_update.lua) does, for a fish whose action is `busy`
(held by `planBusy` during a conversation): `setAnim("talk", talk_phase)` while
talking (cycling `body_talk_00/01/02`), `setAnim("turn", 0)` otherwise, and -
crucially - **no head overlay** in either case (the talk body already has the
face).

The port's `computeBodyAnim` returned a held `turn` frame for `busy` and its
comment claimed "nothing in this port ever sets busy". But `planBusy` ->
`model:setBusy` -> `cube.busy` -> `getAction()=="busy"` **is** reachable (wired
for every level via `engineControl`, docs/035/054) - the branch just never got
ported (docs/029 did the head, left the body).

Fixed:
- `computeBodyAnim(model, isTalking, talkPhase)`: `busy` + talking -> `talk`
  (held, phase = talk_phase); `busy` + idle -> held `turn`.
- `computeHeadAnim`: `busy` -> `null` (no overlay).
- `ModelAnimator`: stores the real `lastAction` (computeHeadAnim's busy check
  needs the action, not the body-anim name), and drives the held `talk` body
  frame from `talk_phase` on the same head timer that cycles it.

Verified: busy+talking renders a `body_talk_*` frame with the head hidden;
busy+idle renders `turn`; non-busy talking is unchanged (rest body + head
overlay).

## 2. Play-time counter (the ending's "%1 hours" said 0)

The ending tells you how long the whole game took: `optionsGetAsInt("playtime")
/ 3600` hours. FF NG keeps `playtime` as a **persistent, cumulative** option -
`GameAgent::own_shutdown()` does `playtime += SDL_GetTicks()/1000` every session.
The port's `options_getParam("playtime")` returned `""` -> 0.

New `web/src/storage/playtime.ts`: a `localStorage`-backed cumulative seconds
counter. The total is always `bootTotal + floor(sessionMs/1000)`, so flushing is
idempotent; it flushes on a 30s interval and on `visibilitychange`/`pagehide`
(the browser's closest analogue to SDL's shutdown hook). `initPlaytimeTracking()`
starts it at app boot (`main.ts`); `options_getParam("playtime")` now returns
`String(getPlaytimeSeconds())`. Verified: a stored 10800s reads back as 3 hours.

## 3. Ending flow - the pedometer

The user corrected my first reading of FF NG's ending behavior. The real flow,
from `WorldMap::own_resumeState`/`checkEnding`/`runSelected`:

> finish (or watch the replay of) a **final** level, once the whole game is
> solved -> that level's **poster** -> the **ending's Pedometer** (if the ending
> is already solved) or the ending played straight through (first time) -> the
> **ending's poster** -> world map.

Key points from the source:
- `runSelected()` shows a `Pedometer` when the node is `STATE_SOLVED`, else plays
  the level directly. So the ending shows its pedometer once it's been beaten.
- `checkEnding()` triggers when you return from a **leaf** (final) level with
  `areAllSolved()`, guarded by `m_selected != m_ending` so it doesn't loop.
- The ending has no map position - it's never a normal clickable node.

The port previously (docs/050) **auto-ran** the ending once (no pedometer),
gated on "ending not already solved". Reworked to match:
- **Standard mode** (`setupEnding`): present the ending only right after a
  **final** level (poster path -> `fromFinal`) with all levels solved, and never
  straight back from the ending itself (`endingDone` - the port's `m_selected !=
  m_ending`). `presentEnding()` then shows its pedometer if solved, else launches
  it. `LevelScene`/`ReplayScene` set `fromFinal`/`endingDone` in the poster's
  return data; a non-final level returns neither.
- **Sandbox**: the top-centre ending node is now solved-aware - styled solved
  with its pedometer on click once beaten, open/pulsing (launches directly)
  otherwise - exactly like a real node, so the pedometer path stays testable
  without completing the game.
- `launchLevel`/`launchReplay` are ending-aware (the ending's own poster + depth
  + an `isEnding` flag), so the pedometer's Run/Replay drive the ending with its
  poster, and the separate `launchEnding` is gone.

Verified (real browser): sandbox ending node open -> (fake-solve) -> solved-
styled -> click opens its pedometer (rack + Run/Replay/Cancel, nodes hidden,
"Doma"); standard `fromFinal`+all-solved+ending-unsolved launches the ending
directly; the `endingDone` guard stops it re-presenting after the ending.

## Verification
- Talk pose, play-time, ending flow: all verified in a real browser (above).
- No cs regression: e2e **7/7** (incl. the 80-level sweep + 80/81 replays);
  `tsc -b` clean.

## Files
- **Modify:** `web/src/game/UnitAnimator.ts` (busy talk/turn body + no busy head),
  `web/src/scenes/ModelAnimator.ts` (lastAction; drive the talk body phase),
  `web/src/lua/levelScript.ts` (`options_getParam("playtime")`),
  `web/src/main.ts` (`initPlaytimeTracking`), `web/src/scenes/WorldMapScene.ts`
  (ending pedometer / present logic / sandbox node / ending-aware launch),
  `web/src/scenes/LevelScene.ts` + `web/src/scenes/ReplayScene.ts` (`isEnding` +
  `fromFinal`/`endingDone` return flags).
- **Add:** `web/src/storage/playtime.ts`.
