# 086 - Solution compare in the sandbox

2026-11-21

## What was done

- **Two solutions side by side.** In `/sandbox`, **C** in a level opens `CompareScene`. It shows two rooms that each replay a solution, plus a control strip, and Esc returns to the level. The point is to see where a shorter solution differs from a longer one, for example what the window optimiser (solver/docs/029) changed. Before this, the only way was to export two videos.
- **Any two solutions.** Each side picks its solution in a DOM panel (S, or the ☰ button). The choices are:
  - the best known (`solution/<level>.lua`),
  - the game's original (`solution/old/<level>.lua`, when it exists),
  - the player's own saved solution,
  - a pasted or dropped move string or `.lua` file.

  The default pair is original ↔ best, else best ↔ own, else best ↔ paste (the panel then opens).
- **Lined up by states, not by moves.** `game/SolutionDiff.ts`, a pure module with no Phaser:
  - Each solution is replayed headless (`Room.loadMove`, as the validator does, 022) and fingerprinted after every move.
  - A Myers diff in linear space (the middle snake, as in diff-match-patch) finds the states both pass through, in order.
  - Between two shared states, either both sides play the same moves (**same**) or they differ (**diff**).
  - A diff that ends in a state the sides don't share again is **open**, for example when one side is unsolved or cut short.
  - Two diffs that meet in a single shared state are merged into one.
  - The panel shows the similarity (shared moves over the longer solution), the number of diffs and the length difference. Every diff is listed and can be clicked.
- **The fingerprint follows the solver's notion of a state.** It holds every model's cell and its alive/out/lost flags.
  - *Twin items* (same weight, goal and shape) are compared as a set. The solver merges such twins, so map's shortened solution leaves two of them swapped. Fingerprinted one by one, the two solutions never met again after move ~495, and map showed 22 % similarity with one open diff. With twins as a set it shows 86 % and 16 closed diffs.
  - *Facing* is left out. `Cube.isLeft` never enters the rules: turning is cosmetic.
- **Playback.**
  - The scene walks the segments. Both rooms play to the end of the current segment, and the next segment starts once both have settled.
  - In a "same" stretch the rooms are in the same state, so they move in lockstep. In a diff, each plays its own moves, and the side that finishes first shows "čeká" (waiting).
  - ← / → step one move back / forward and pause; a held key keeps stepping. In a diff each side steps while it has moves left there, and the shorter one then waits. A step forward is animated; a step back is instant.
  - PgUp / PgDn (or Shift+← / →) jump to a few moves (5) before the previous / next diff. Jumps and steps back are a headless fast-forward, like save-resume (053).
  - L loops the current diff, with 3 moves after it.
  - +/− sets the speed from 0.25× to 16×, Space pauses, R starts over, and a click on the timeline seeks.
  - The timeline marks each diff: green when B is shorter there, red when longer, amber when equal, purple when open.
- **Layout.**
  - The rooms go side by side or stacked, whichever shows them larger in the window.
  - Three cameras each show one far-apart world area: room A, room B and the strip. A fish swimming out therefore stays clipped to its own room.
  - `applyRenderScale` gained a `fitWindow` option, so this wider-than-a-room scene shrinks to the browser window instead of using the Game-size factor as is.
- **`ComparePane`** is a cut-down `ReplayScene`: the same per-round physics, animators and phase-locked pacing (046, 049), but no live Lua, audio or export. `ReplayScene` is unchanged.

## Music

At the user's request (added the same day), the level's music loops for as long as the view is open, whatever the playback does (play, pause, steps, jumps).

- No live Lua runs here to start it, so the level loader now records the first track `code.lua`'s init passes to `sound_playMusic` as `LevelData.music`. Before, the loader dropped that call.
- The scene plays it through the existing `AudioManager`, and stops it on the way out (docs/025).
- The track name is normalised by a shared `musicTrackName()` in `levelLoader.ts`. The live engine and the demo runner use the same helper, which replaces their inline copies.
- 75 of the 81 levels get a track. briefcase, music, steel, creatures, cancan and emulator start none in `code.lua`, and they have no level music in the original either.
- Mid-level music changes (viking1's musician gag, a level's `sound_stopMusic`) don't apply, because they come from the live Lua.

## Deliberate deviations (no live Lua)

The compare view runs without the live Lua engine. Because of that:

- the elevators (elevator1/2) draw no ropes;
- cannons sends its small fish out a few rounds early (`planBusy` is Lua);
- windoze doesn't swap its `busy` couple;
- corridor/rotate/steel don't swap backgrounds;
- items show no decorative animation, as in `ReplayScene`.

The physics and the final state are the same: e2e 06 validates every solution with this same headless replay. The **mirror** effect (submarine) is drawn as its plain picture, because it captures the main camera's framebuffer and here three cameras share the canvas.

## Tests

`web/tests/cases/13-solution-compare.mjs` (~10 s) checks:

- the diff on synthetic arrays, and the input parser;
- every `legacy/solution/old/*.lua` against its replacement:
  - the segments tile both solutions,
  - "same" moves have equal states,
  - every diff starts (and, if closed, ends) in a shared state,
  - the diffs sum to the length difference;
- redhat is one diff of −4;
- map has no open diff, which pins the twin rule;
- a truncated input is unsolved with a 100 % prefix, and an invalid move stops the trace;
- the scene: C from redhat, a start 5 moves before the diff, the level's music playing, → / ← step one move and pause, B waits, both continue in step after it, and Esc returns to the level.

This also closes the open item of 085: the web now uses `old/`.

## Open for next time

- **Sharing a comparison by URL** (`/sandbox?compare=<level>&a=old&b=<moves>`). It was discussed but left out on purpose: the user chose only the C key.
- **No entry from the world map.** The Pedometer could get a compare button.
- **Diffs separated by a few shared moves are shown separately.** map has a pair split by one move. Merging across short "same" runs would shorten the list but lose sync points.
