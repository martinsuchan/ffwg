# 019 - Keyboard Input Reliability Fix

2026-07-09

## Bug report

User noticed a real delay between pressing a key/clicking and the fish
reacting compared to the original game, plus keypresses and mouse clicks
that sometimes silently do nothing (estimated ~10-20% of the time) - and
that a fresh level's first empty-space mouse-click-to-swim didn't move a
fish until a fish had been clicked/selected first, even though arrow keys
worked immediately.

## Investigation

Traced the real cause with a `window.__game` debug hook (same technique as
`docs/011`-`docs/013`) driving the live dev server via Playwright, comparing
against `legacy/src/level/Controls.cpp`/`InputAgent.cpp` line-by-line rather
than guessing:

- **Root cause, confirmed with timestamped traces**: `LevelScene.tick()`
  only samples `heldKeys.has(code)` once per round (`ROUND_MS` = 130ms). A
  discrete keydown/keyup pair that both land inside the gap between two
  round polls is invisible to the game - the key was genuinely never
  "held" at any instant the game happened to check. A synthetic 35-40ms
  tap (a plausible real tap speed for precise one-square puzzle movement)
  measured a **~65-70% drop rate** in an isolated test, and 20 alternating-
  direction real taps in a confirmed-open corridor landed at 30% before the
  fix (36/36 = 100% after - see Verification).
- **The original does not have this flaw.** `InputAgent::own_update()`
  drains the full SDL event queue every cycle and immediately forwards
  every raw keydown to `Controls::controlEvent(stroke)`
  (`Room::controlEvent` -> `Level::controlEvent` -> `LevelInput::specStroke`),
  which resolves it to a move symbol *at the moment of the keydown* and
  buffers it in a single-slot `m_strokeSymbol` - independent of whatever
  the physical key state is by the time a round actually consumes it
  (`Controls::useStroke()`, tried before falling back to held-state
  `driveUnit()` polling). This buffering is exactly what our TS port
  dropped when porting `Controls.cpp` (noted at the time in `docs/016`'s
  comment: "discrete-keystroke... dropped, matching this project's
  existing no-save/no-demo scope") - it turns out that simplification also
  quietly dropped input reliability, which wasn't the intent.
- **Two things turned out *not* to be bugs**, confirmed by direct
  comparison against the same original source before ruling them out:
  - The first few hundred ms of unresponsiveness right after a level
    loads/restarts is `Room.nextRound()`'s `isFresh()` gate correctly
    freezing *all* input (keyboard and mouse alike) while unsupported
    items are still settling under gravity - `legacy/src/level/Room.cpp`'s
    `nextRound()` has the exact same global `if (isFresh())` gate around
    both `driving()` and `mouseDrive()`. Faithful, not a regression.
  - Mouse click-and-hold pathing itself was never actually broken: a
    correctly-targeted, genuinely-held click moved the fish immediately
    and reliably on the very first attempt after page load, no prior
    fish-selection required, in a properly instrumented test. The
    "must select a fish first" report couldn't be reproduced against the
    real engine state - most likely an earlier click landed on an
    unreachable/misjudged field by chance (a plain BFS `FinderAlg.findDir`
    correctly returns no move for an unreachable target) rather than a
    code defect. (Chasing this down needed one detour: an early repro
    used a too-small Playwright viewport, so the CSS-zoomed canvas was
    centered and partly off-screen, feeding the pointer math wrong
    coordinates - a test-harness artifact, not a game bug, resolved by
    reading `canvas.getBoundingClientRect()` and sizing the viewport to
    match.)

## Fix

Ported the missing half of `Controls.cpp`: a one-shot queued keydown edge,
captured independent of round timing and guaranteed to be consumed by the
very next round regardless of whether the key is still physically held by
then.

- `web/src/game/Unit.ts`: `InputProvider` gains an optional
  `takeQueuedKey(): string | null`.
- `web/src/scenes/LevelScene.ts`: new `queuedKey` field, set by the
  `keydown` listener (only for `MOVE_KEYS` - arrows/WASD/IJKL; Space/R stay
  immediate, un-queued actions, matching the original's separate `specKey`
  path) if no key is already queued - a single slot, first-come-first-
  served, exactly like `m_strokeSymbol`. `tick()`'s `input.takeQueuedKey()`
  drains it (read-and-clear) when actually consumed. Reset on restart.
- `web/src/game/Controls.ts`: `driving()` now tries the queued key first
  (via a synthetic one-shot `InputProvider` fed through the existing
  `driveUnit()` - reuses all borrowed-arrow/per-unit-key resolution
  unchanged) and, if one was queued, **always** counts the round as used
  even if the resolved move is blocked - matching `useStroke()`'s own
  "NOTE: returns true even for bad move" - so a blocked queued key doesn't
  also fall through to mouse input the same round. Falls back to the
  existing held-state `driveUnit(input)` polling when no key is queued,
  which still drives continuous movement correctly for as long as a key is
  genuinely held (the queue only ever fires once per keydown).

Mouse input is unchanged - both engines require a genuine hold for
path-following, and that was already working correctly.

## Verification

- `npx tsc -b` clean.
- Direct timestamped trace (keydown/keyup/tick events correlated) proved
  the exact failure mode before the fix: a 40-50ms tap's keydown *and*
  keyup both landing inside one ~130ms inter-round gap, with zero ticks
  observing the key as held.
- Fast discrete-tap reliability, staying inside a level geometry range
  confirmed open by a long continuous hold first (to rule out level walls
  as a confound - this level's fish starts boxed in on three sides within
  a few cells): 36/36 fast (35ms) alternating up/down taps produced a move
  after the fix, versus 30-35% before.
- Regression: Space switch, WASD/IJKL direct-unit keys (including a fast
  tap through the new queue), 1s continuous held-key driving, mouse
  click-and-hold pathing, and restart all re-verified against the live
  engine - unchanged.

## Open for next time

- If a similar "must be captured independent of round timing" reliability
  gap ever turns up for mouse (not reproduced this time), the same
  single-slot queued-edge pattern is the template to reach for.
