# 012 - Restart Sprite Leak Fix

2026-07-07

## Bug report

User noticed that restarting a level with R left stale sprites on screen
from the previous run - including a "ghost" fish sitting at its old,
pre-restart location - and that moving items would reveal more of them.

## Root cause

`ModelAnimator.destroy()` only ever removed the model's two Phaser
`TimerEvent`s (`bodyTimer`/`headTimer`) - it never destroyed the
`bodySprite`/`headSprite` `Image` GameObjects it owns. `LevelScene.
startEngine()` (called both on first load and on every `R` restart) does:

```ts
for (const animator of this.animators.values()) {
  animator.destroy();
}
this.animators.clear();
...
// then creates a brand new bodySprite/headSprite per model via this.add.image(...)
```

Since `destroy()` never removed the old GameObjects from the scene's
display list, every restart left the previous run's full set of sprites
behind - frozen at whatever texture/position they last had - and added an
entirely new set on top. Each `R` press compounded this: sprite count grew
by ~1 image per item/fish body (plus a hidden head sprite per fish) every
time, exactly matching what the user saw.

## Fix

One-line fix in `web/src/scenes/ModelAnimator.ts`'s `destroy()`:

```ts
destroy(): void {
  this.bodyTimer.remove();
  this.headTimer?.remove();
  this.bodySprite.destroy();
  this.headSprite?.destroy();
}
```

Now destroying an animator fully releases everything it owns - timers and
GameObjects - so `startEngine()`'s existing destroy-then-recreate cycle
actually leaves a clean scene.

## Verification

- `npx tsc -b` clean.
- New Playwright script counted `scene.children.list` `Image` GameObjects
  (via a temporary `window.__game` debug hook in `main.ts`, removed after)
  across 4 consecutive restarts, each preceded by moving the fish around:
  count stayed constant (15) every time, versus growing unbounded before
  the fix (confirmed by reading the pre-fix `destroy()` - it provably
  never touched the sprites, so the leak is structural, not timing-
  dependent).
- Regression: re-ran `docs/007`'s synthetic-room tests, `docs/008`'s
  10-level goal test, `docs/009`'s stress-play test, `docs/010`'s existing
  restart-cycle script (checks for console errors, not leaked objects -
  which is exactly why this bug slipped through originally), and
  `docs/011`'s corpse-removal test - all unchanged, zero regressions.

## Open for next time

- `docs/009`'s original restart test only checked for console errors and
  failed network requests, not for leaked/duplicate GameObjects - worth
  keeping in mind that "no errors" isn't sufficient to catch this class of
  bug in future changes that add scene-owned resources.
- Everything from `docs/009`/`docs/010`/`docs/011`'s "Open for next time"
  still applies.
