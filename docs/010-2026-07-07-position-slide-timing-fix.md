# 010 - Position Slide Timing Fix

2026-07-07

## Bug reports

Playing `airplane` after `docs/009` surfaced two visible bugs:

1. Holding D to push an item right: the item visibly starts sliding
   before the fish sprite has reached it.
2. Pushing an item off a ledge so it falls: the item appears to move
   diagonally (both x and y at once) instead of finishing the horizontal
   push before falling straight down, like the original.

## Root cause

Both bugs traced to the same thing: `ModelAnimator`'s position-slide tween
(`SLIDE_MS = 300`, `docs/009`) was longer than the round interval driving
new position targets (`LevelScene`'s `TICK_MS = 130`). Every `sync()` call
that saw a new grid position called `scene.tweens.add()` unconditionally -
with a new round arriving every 130ms but each tween taking 300ms to
finish, each new tween stacked on top of the still-running previous one
instead of replacing it.

Confirmed by instrumenting a live browser session (`window.__game`
temporarily exposed in `main.ts`, removed after) and polling
`scene.engine.getRenderModels()` (logical grid position) against
`scene.children.list` (actual on-screen sprite pixel position) every
~40ms while holding a movement key:

- **Bug 1**: a continuously-driven fish's sprite fell further and further
  behind its true grid cell the longer the key was held (observed lag
  reached 2+ grid cells), because each round's tween restarted the 300ms
  clock from wherever the sprite currently was, which itself was already
  behind. An item being pushed for the first time has no such backlog -
  its tween starts fresh and on-time - so by the time contact happens the
  fish visually hasn't caught up yet, making the item look like it started
  moving on its own.
- **Bug 2**: confirmed the underlying physics is actually correct - a
  cube's `Rules.dir` is a single value, so `occupyNewPos()` only ever
  moves a model along one axis per round (verified by reading `Room.ts`/
  `Rules.ts`/`Landslip.ts`: `prepareRound()` applies exactly one queued
  `dir` per model, and `changeState()` resets it before fallout/falldown/
  driving queues the next one - horizontal and vertical moves can never
  be queued for the same round). The diagonal look was purely a rendering
  artifact: when an overlapping horizontal tween got superseded by a new
  vertical-fall target before finishing, the leftover horizontal delta and
  the new vertical delta animated together in the same tween.

## Fix

- New `web/src/game/timing.ts` exports `ROUND_MS = 130`, replacing the
  local `TICK_MS` constant that used to live in `LevelScene.ts` - now the
  single source of truth for the round interval, imported by both
  `LevelScene.ts` (its round timer) and `ModelAnimator.ts`.
- `ModelAnimator.ts`'s `SLIDE_MS` is now `Math.round(ROUND_MS * 0.8)`
  (104ms) instead of a fixed, unrelated 300ms - a slide reliably finishes
  before the next round's position update arrives.
- Defense in depth for when a round is still late anyway (dropped frame,
  background-tab throttling): `sync()` now calls
  `scene.tweens.killTweensOf(targets)` before adding a new position tween,
  and explicitly snaps the sprite back to `lastPxX`/`lastPxY` (the
  *previous* round's exact grid-aligned pixel position) first, rather than
  leaving it wherever the killed tween's interpolation stopped. Since each
  round moves at most one axis (see above), starting every slide from a
  clean grid cell guarantees each individual tween is itself single-axis,
  regardless of timing jitter.

No change to `web/src/game/` physics/rules logic at all - this was purely
a presentation-layer bug.

## Verification

- `npx tsc -b` clean.
- Re-ran the same live-instrumentation technique used to diagnose the bug,
  after the fix: a held-key push now keeps the fish sprite within about
  one grid cell of its logical position (previously 2+ and growing), and
  catches up within ~100ms of releasing the key (previously ~800ms+ of
  visible "gliding"). A pushed-then-falling item's x sprite coordinate now
  reaches its exact target pixel *before* y ever starts changing, in every
  sampled transition - no diagonal blending observed.
- Regression: re-ran `docs/007`'s synthetic-room tests and `docs/008`'s
  10-level goal-loading test unmodified - identical results (the one
  pre-existing oddity in `test5_fallingItemKillsFish`, `isSolvable: true`
  where the test's own comment expects `false`, predates this change and
  isn't in code this fix touched - not investigated further here).
- Re-ran `docs/009`'s stress-play and restart-cycle regression scripts -
  zero console errors, zero failed requests.

## Open for next time

- The `test5_fallingItemKillsFish` `isSolvable` discrepancy noted above is
  unrelated to this fix but worth a look next time synthetic-room tests
  are touched.
- Everything else from `docs/009`'s "Open for next time" still applies
  (phase 2/3, blink not visually reconfirmed, texture atlas extension).
