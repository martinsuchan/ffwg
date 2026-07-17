# 068 - World-map Tab/Enter keyboard navigation

_2026-07-17_

The last keyboard-parity gap from the docs/067 audit: legacy FF NG lets you
navigate the world map by keyboard (`WorldInput.cpp`) - the port was mouse-only.

## Behavior (faithful to legacy)

- **`Tab`** = select the next *open* level (`WorldMap::selectNextLevel` ->
  `LevelNode::findNextOpen`). "Open" = unlocked **and** not yet solved
  (`STATE_OPEN`); solved and locked/hidden nodes are skipped. Cycles in node
  order, **wrapping** to the first; picks the first open node if nothing is
  currently selected or the selection isn't itself open. **No-op when nothing is
  open** (e.g. everything solved) - `findNextOpen` returns NULL there, so Tab
  clears any stale selection and does nothing.
- **`Enter`** = run the selected level (`WorldMap::runSelected`) - the Pedometer
  for a solved node, otherwise play it; exactly what clicking the node does.
- The selection is the **same `m_selected`** the mouse drives on hover - Tab and
  hover share it, and the selected node shows the yellow selection ring + level
  name (the existing `selectNode`/`deselectNode` visuals).

## Implementation

`WorldMapScene` (`web/src/scenes/WorldMapScene.ts`):

- New `selectedNode?: WorldMapNode` field; `selectNode()` sets it, `deselectNode()`
  clears it (so mouse hover and Tab share one selection, like the original).
- `nextOpenNode(current)` - the `findNextOpen` port: filter `mapData.nodes` to
  those whose `nodeStates` is `"open"`, return the one after `current` (wrapping),
  the first if `current` isn't open, `undefined` if none are open.
- `selectNextLevel()` (Tab): select `nextOpenNode(selectedNode)`, else deselect.
- `runSelected()` (Enter): `onNodeClicked(selectedNode, state)` - reuses the exact
  click path (pedometer-if-solved / launch), so no new launch logic.
- Keys wired with `addCapture("TAB,ENTER")` (so the browser doesn't move focus off
  the canvas on Tab) + `keydown-TAB`/`keydown-ENTER`, both gated on
  `!isModalOpen()` (inert while the pedometer/options overlay is up).

The sandbox's synthetic ending node (top-centre) isn't in `mapData.nodes`/
`nodeStates`, so Tab never lands on it - matching legacy, where the ending is
`m_ending`, reached via `checkEnding`, not the `findNextOpen` traversal.

## Port-specific note

The two endpoints differ a lot here: `/sandbox` force-opens every unsolved node,
so Tab cycles all ~80 in order; `/` (standard) gates properly, so "open" is just
the real frontier (often 1-2 nodes) - which is where Tab-then-Enter is genuinely
handy. Legacy only ever had the gated behavior.

## Verification (real browser)

- Sandbox: 80 open; Tab cycles (`start` -> `briefcase` -> ...); Enter launches the
  selected level.
- Standard: 1 open (`start`); Tab selects it (ring + name label shown,
  screenshot); Enter launches it.
- All solved: 0 open; Tab is a no-op (selection stays null).
- e2e 7/7; tsc clean.
