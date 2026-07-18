# 074 - World-map progressive branch reveal

_2026-07-18_

User report: on a fresh (no-save) standard game, the world map shows the **whole
map** of dim locked dots + edges from the start; they want branches revealed only
as they're unlocked.

## This was actually faithful to the original

Legacy defaults every node to `STATE_FAR` (`LevelNode.cpp`) and
`NodeDrawer::drawNode` draws a "far" dot + edges for **every** non-secret node
(`case STATE_FAR: ...` still draws the base dot; only `STATE_HIDDEN` nodes aren't
visited by `LevelNode::drawPath`). So FF NG genuinely shows the entire main path
up front, hiding only the 10 secret branches (the `true` flags in `worldmap.lua`).
Verified the port matched this exactly (fresh: 1 open `start`, 69 far, 10 hidden;
deployed == local). So this is a **deliberate deviation** from the original, at the
user's request.

## Desired behavior (per the user)

Reveal by **section/house**, not per node:
- Fresh game: only the starting house ("Rybí domeček") is visible - its entry
  (`start`) playable, the rest of that house locked.
- Solving a level that leads into another branch reveals that whole branch: all
  its levels shown, the first playable, the rest locked. Any time the level
  leading into a branch is solved, that branch unlocks.

## Implementation

A section is `worldmap_addDesc`'s house name (`desc`) - the port already captures
it per language (`WorldMapData.sections`, keyed `<codename>:<lang>`, docs/073).
`computeNodeStates` (`web/src/game/worldMapState.ts`) gains a final standard-mode
pass:

1. Compute the normal gated states (solved / open when parent solved / far /
   hidden-secret) as before.
2. A **section is revealed** iff any of its nodes is `open` or `solved` - i.e. its
   entry opened because the level leading in was solved. (The start house is
   revealed because `start` is open.)
3. Every node in a **non-revealed** section is forced to `hidden`, so it isn't
   drawn (`drawNodes`/`drawEdges`/Tab-nav all already skip `hidden`). Secret-flag
   nodes stay individually hidden within a revealed section, unchanged.

Grouped by the **language-independent en** section name (fallback cs, then the
codename) so the grouping is stable regardless of the UI language. Sandbox mode is
untouched (everything open → every section revealed → whole map shown). No new
data or scene changes - purely a `computeNodeStates` refinement, recomputed fresh
on every map entry (docs/027), so it tracks solves with no extra plumbing.

Note: the `ending` node shares the "Fish House" section but isn't a map dot (it's
`WorldMapData.ending`, auto-run when all solved), so the start house draws its 8
real nodes.

## Verification (real browser)

- **Fresh** standard game: only "Fish House" visible - 8 nodes drawn (1 playable
  `start`, 7 locked), 72 hidden (screenshot); exactly 1 open.
- **After solving the whole start house**: the branches it leads into reveal
  (Coral reef, City in the Deep, Dump, Ship Wrecks, New Generation) - 6 sections
  visible, each new branch's entry playable, the rest locked; unreached branches
  still hidden (screenshot).
- e2e case `07-sandbox-mode` still green (standard open=1, far+hidden≫50; sandbox
  all open); full suite + `tsc -b` clean.

## Open for next time

- The change is on `main`/working tree only; the live Azure site still shows the
  old (full-map) behavior until the next deploy (needs the docs/073 nl-audio trim
  to fit the SWA upload timeout).
