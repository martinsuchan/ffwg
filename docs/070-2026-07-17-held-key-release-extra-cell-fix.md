# 070 - Held-key release: one phantom extra cell fixed

_2026-07-17_

Play-test report: holding a movement key (arrows, or WASD/IJKL) and releasing
it *mid-cell-animation* often made the fish swim **one more cell** before
stopping. The desired behavior is what the original does - release, finish the
cell already in flight, stop.

## Root cause: OS key-repeat re-arming the queued-key buffer

`LevelScene`'s keydown handler buffers a one-shot **queued key** (docs/019) so a
tap shorter than one round interval still registers instead of vanishing between
two round polls. `Controls.driving()` drains it once per round and treats a
queued key as **used even for a blocked move** (legacy `useStroke()`; docs/019).

The handler armed `queuedKey` on **every** keydown:

```ts
this.heldKeys.add(e.code);
if (this.queuedKey === null && MOVE_KEYS.has(e.code)) this.queuedKey = e.code;
```

But the OS fires `keydown` **repeatedly** while a key is held (auto-repeat, every
~30ms). The `queuedKey === null` guard only prevents *overwriting* a pending
key - it happily **re-fills** the buffer after each round drains it. So while you
hold a direction, auto-repeat keydowns keep re-arming `queuedKey`. If one lands
in the small window between the last round's drain and your `keyup`, the next
round's `takeQueuedKey()` returns that **stale** key and `driving()` drives one
**phantom cell** - even though the key is already released and `heldKeys` no
longer contains it. "Often", not "always", because it depends on a repeat event
landing in that drain->keyup window.

The held-key *polling* path (`driveUnit` reading `heldKeys`) was never the
problem: on release `heldKeys` clears immediately, so it stops correctly. Only
the queued-edge buffer leaked a phantom.

## Fix

Only buffer a **genuine fresh keydown edge** - not OS auto-repeat, and not a key
already down (`web/src/scenes/LevelScene.ts`):

```ts
const isFreshEdge = !e.repeat && !this.heldKeys.has(e.code);
this.heldKeys.add(e.code);
if (isFreshEdge && this.queuedKey === null && MOVE_KEYS.has(e.code)) {
  this.queuedKey = e.code;
}
```

`!e.repeat` filters OS auto-repeat semantically; `!heldKeys.has(e.code)` is a
belt-and-suspenders guard for any platform/key where `repeat` isn't set (a
keydown for an already-held key can only be a repeat). This **preserves docs/019's
fast-tap reliability**: a genuine tap still arms the buffer exactly once and fires
even if it's released before the round consumes it - `queuedKey` is deliberately
**not** cleared on `keyup`, so a sub-round tap isn't lost. It just no longer gets
re-armed by auto-repeat, so a release leaves no stale edge to fire a phantom cell.

Only `LevelScene` has this buffer (interactive play); `ReplayScene`/others don't
drive fish from live held keys, so nothing else changes.

## Verification (real browser, Playwright)

A probe drives `airplane` through the live `LevelScene`:

- **Mechanism (loop paused, deterministic):** a fresh keydown edge arms
  `queuedKey`; an auto-repeat keydown (key still held, `repeat=true`) does **not**
  re-arm after a drain; a fresh press after release arms again. Proven to
  discriminate - reverting `isFreshEdge` to `true` makes the auto-repeat case
  re-arm with `"ArrowRight"` (the phantom source) and the check fails.
- **End-to-end:** hold `ArrowRight` through several repeats, release, then confirm
  the recorded step count stops climbing (no extra move after `keyup`).
- e2e suite green; tsc clean.
