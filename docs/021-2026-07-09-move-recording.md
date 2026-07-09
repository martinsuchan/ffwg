# 021 - Move Recording

2026-07-09

## Context

Step 1 of turning the playable POC into a real game: recording each round's
move as a compact symbol string, the foundation the rest of the roadmap
(fast headless solution validation, replay mode, solved-solution
persistence, eventually mid-level save/load) builds on. Full research
write-up (how the original does save/load/undo/replay/solved-tracking, and
the proposed 5-step roadmap) is in the prior chat turn, not a docs file -
summarized here only as it bears on this step.

## What the original does

`legacy/src/level/ControlSym.h`/`ModelFactory.cpp`: each fish is built with
4 fixed characters - `ControlSym('u','d','l','r')` for `fish_small`,
`ControlSym('U','D','L','R')` for `fish_big`. Every successful move or turn
appends that unit's symbol for that direction to `Controls::m_moves`
(`std::string`), regardless of whether it came from a held key
(`driveUnit()`), a discrete keystroke (`useStroke()` -> `makeMove()`), or
mouse (`MouseControl` -> `Controls::makeMove()`) - all three paths funnel
through the same recording point. A flat string like `uuuuuulllDDDRRR...`
(exactly the format of the `legacy/solution/*.lua` files) needs no
separators and no "which fish was active" context to replay deterministically
- each symbol names both the unit and the direction. Turning also records a
symbol (`Unit::goLeft()` returns `m_symbols.getLeft()` in *both* the
turn-branch and the successful-move branch), which is why replay doesn't
need a distinct "turn" marker: replaying the same symbol against the same
starting facing reproduces the turn-then-move split on its own.

## Port

- `web/src/game/Unit.ts`: new `ControlSym` interface (mirrors `KeyControl`'s
  shape). `Unit`'s constructor takes one. `drive()`/`driveBorrowed()`/
  `driveDir()` and the private `goLeft/goRight/goUp/goDown` now return
  `string | null` (the move symbol) instead of `boolean` - a direct,
  faithful translation, not a new mechanism: the symbol *is* the "did a
  move happen" signal, exactly like legacy's `char` return.
- `web/src/game/Controls.ts`: new `private moves = ""` plus `getMoves()`/
  `getStepCount()` (legacy's `StepCounter` interface) and `recordMove()`.
  `driveUnit()` appends the symbol it gets back from `driveBorrowed`/`drive`
  on success - this is the keyboard path's recording point (covers both
  held-key polling and `docs/019`'s queued-key edge trigger, since both
  already funnel through `driveUnit()`). Switching (`useSwitch()`) still
  doesn't record anything, matching legacy.
- `web/src/game/MouseControl.ts`: since `MouseControl` already resolves a
  `Unit` + `Dir` directly (pathfinding or coordinate comparison) rather than
  going through `Controls`' key-lookup, it calls `Unit.driveDir()` and
  records the result itself via `Controls.recordMove()` - legacy's
  `Controls::makeMove()` is the single choke point both keyboard and mouse
  go through; this port splits it into two call sites for the same effect
  (`driveUnit()` for keyboard, `MouseControl` for mouse), rather than
  routing mouse moves back through `Controls` by symbol lookup, since our
  `MouseControl` already has the concrete `Unit` in hand.
- `web/src/game/GameEngine.ts`: new `SMALL_FISH_SYMBOLS`/`BIG_FISH_SYMBOLS`
  tables alongside the existing key tables, threaded into `buildUnit()`.
  `getMoves()`/`getStepCount()` exposed for callers.
- `web/src/game/Room.ts`: same two getters, delegating to `Controls` -
  mirrors the existing `switchFish()`/`selectFish()` delegation pattern.

No physics changes. Recording resets for free on restart/`R` - `LevelScene.
startEngine()` already constructs a fresh `GameEngine` -> `Room` ->
`Controls` from scratch.

## Verification

- `npx tsc -b` clean.
- Live-browser checks (temporary `window.__game` hook, focused page, same
  technique as prior docs entries):
  - Held `ArrowRight` from spawn (`fish_small` starts facing left) recorded
    `"rr"` - first `'r'` the turn, second `'r'` the actual move (blocked by
    a wall the next cell over, per `docs/019`'s trace of this same spot) -
    confirming the turn-then-move-same-symbol behavior exactly.
  - Held `KeyW` (big fish direct key, no borrowed-arrow ambiguity) recorded
    uppercase `"U"` - correct case for `fish_big`.
  - Mouse click-and-hold pathing from `(35,19)` to `(35,15)` recorded
    `"uuuu"` - 4 lowercase `u`s for 4 actual cells moved, exact count and
    case match.
  - `Space` (switch) recorded nothing; switching active fish then driving
    it via the *borrowed* arrow keys correctly recorded uppercase `D`s once
    big fish was active - confirms borrowed-arrow recording resolves
    symbols from whichever unit is actually active, not a fixed unit.
  - Restart cleared `getMoves()` back to `""`.
  - Zero console/page errors throughout.

## Open for next time

- Step 2 (next): port `Room::loadMove()` - settle falls, apply one symbol
  or throw, settle falls again - as a headless driver over `GameEngine`
  (no `LevelScene`/Phaser at all), then run it against every
  `legacy/solution/*.lua` file to actually validate them, per the user's
  own open item.
- Steps 3-5 (replay mode, solved-solution persistence via `localStorage`,
  mid-level save/load) remain as scoped in the prior chat turn's roadmap,
  unstarted.
