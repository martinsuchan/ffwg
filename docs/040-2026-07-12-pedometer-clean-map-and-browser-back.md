# 040 - Pedometer clean-map presentation + browser Back to world map

_2026-07-12_

Three small main-screen fixes, the first two matching the original
(`legacy/src/menu/`), the third a browser-port-only concern.

## Pedometer: hide the node graph, no dark overlay (`PedometerUI.ts` / `WorldMapScene.ts`)

The original Pedometer is a **separate state**, not an overlay: `Pedometer::
prepareBg()` builds a fresh background from just `map.png` + the level name +
the solver text - it never calls `NodeDrawer::drawPath`, so no dots and no
edges are drawn, and there's no dark tint. This port renders the pedometer as
an in-scene overlay on the live world map, so it has to reproduce that look:

- The backdrop rectangle is now **fully transparent** (`alpha 0`) instead of
  `0x000000 @ 55%`, still `setInteractive()` so it absorbs clicks and hosts the
  button hover/click hit-testing.
- `WorldMapScene.setNodesVisible(false)` hides the whole node graph (every
  dot's `far`+overlay image and the edges `Graphics`, now stored as a field)
  while the pedometer is up, restored on close. `showPedometer()`/
  `closePedometer()` wrap the lifecycle; the Cancel button routes through a new
  `onCancel` callback (so it can restore the dots), and **Esc** also closes it.

## Pedometer: real localized best-solution text (`PedometerUI.ts` / `worldMapLoader.ts`)

The info line below the rack was a placeholder English sentence on a dark
background. It's now the original `SolverDrawer`'s real text: the localized
`solver_better` / `solver_equals` / `solver_worse` label from
`script/labels.lua`, chosen by `LevelStatus::compareToBest()`'s logic (your
moves vs. the world-record `node_bestSolution`), with `%1`/`%2` filled by the
best move count + author (`Dialog::getFormatedSubtitle`). `worldMapLoader.ts`
now also runs `labels.lua` (binding `label_text`, capturing only those 3
labels for every language) and exposes `WorldMapData.solverLabels`;
`PedometerUI` picks the row for the current `settings.lang` (cs/nl, en
fallback). Rendered centered at the original's screen position (`h - 150`),
white with a thin outline, **no background box**.

## Browser Back returns to the world map (`navigation.ts` new)

Pressing the browser Back button while in a level unloaded the single-page app
to a blank tab. New `web/src/navigation.ts`: `pushSubView()` pushes one history
entry whenever the world map launches a sub-view scene (level / replay / intro
/ credits), and a `popstate` listener (`initHistoryNav`, wired in `main.ts`)
routes Back to the world map instead - keying off which Phaser scenes are
actually active (robust to history drift), stopping the active sub-scene(s) and
`scene.start("worldmap")`. `WorldMapScene.create()` calls `markWorldMap()` to
label the base entry. The original is a native app with no equivalent.

## Verification (dev server, temp `window.__game`, removed after)

Real-browser (seeded a solved node): pedometer backdrop alpha 0, all node dots
+ edges hidden, solver text is the real cs `solver_better` string with no
background; Cancel restores dots+edges. Back button: clicking a node enters the
level, `page.goBack()` returns to the active world map with the page still
loaded (no blank tab). Screenshot confirms the clean-map look. `tsc -b` clean;
debug hook removed.

## Files
- **New:** `web/src/navigation.ts`.
- **Modify:** `web/src/scenes/PedometerUI.ts` (transparent backdrop, onCancel,
  real solver text), `web/src/scenes/WorldMapScene.ts` (node-visibility toggle,
  show/close wrappers, Esc, solverLabels, history push), `web/src/lua/
  worldMapLoader.ts` (`labels.lua` -> `solverLabels`), `web/src/main.ts`
  (`initHistoryNav`).
