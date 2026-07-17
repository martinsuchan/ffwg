# 065 - Fullscreen fixes: black screen, aspect ratio, crisp text, Esc-exit

_2026-07-17_

Follow-up to docs/064's fullscreen, from real-browser (Edge) testing. Four
issues, and the fullscreen mechanism changed twice getting there. **Supersedes
docs/064's "F11 fullscreen" section.**

## 1. Black screen in Edge (docs/064's first mechanism)

docs/064 captured F11 (`preventDefault` + Phaser `startFullscreen`) and applied
FIT *before* entering fullscreen. Black screen in Edge, two causes: F11 is a
browser-reserved key and `preventDefault()` doesn't reliably stop the browser's
native fullscreen, so our element-fullscreen and the browser's native F11 fought;
and FIT was applied to the small windowed container, not the screen.

**Interim fix (native F11):** don't capture F11 at all - ride the browser's own
F11 and react to the `(display-mode: fullscreen)` media query (matches native F11
*and* the Fullscreen API, unlike the API-only `fullscreenchange`). On enter, size
the game container to the viewport and switch to FIT. This rendered (user
confirmed), but exposed that **the browser's native F11 fullscreen can't be
exited from JS and isn't reliably exited by Esc** - which issue 4 needs.

## 2. Aspect ratio not preserved on room change (the stretch bug)

In fullscreen, opening a differently-shaped room (e.g. tall/narrow `library`
after the 4:3 map) stretched it. Cause: `applyRenderScale` called
`scale.resize(newFb)`, and Phaser's `resize()` does `displaySize.setSize()` which
**keeps the old aspect ratio** (line ~214019 of phaser.esm.js) - so FIT kept
letterboxing to the *previous* room's shape. Fix: in FIT/fullscreen mode use
`scale.setGameSize()` instead, which calls `displaySize.setAspectRatio(w/h)` so
FIT re-letterboxes to the new framebuffer's shape. `applyRenderScale` now sets the
scale mode explicitly (FIT vs NONE) + the matching `setGameSize`/`resize` both
ways. (Also fixed: `refresh()` re-measures `parentSize` only at the END of its
pass, so FIT-fit needs an explicit `getParentBounds()` first or the canvas never
grows to the screen.)

## 3. Text stretched (not crisp) in fullscreen

docs/064 kept the framebuffer at the *windowed* size (`native * gameSize`) in
fullscreen and let FIT CSS-stretch it to the screen - so text was upscaled from
the small framebuffer. Fix: in fullscreen, `applyRenderScale` computes the factor
from the **actual on-screen fit** - `min(innerW/nativeW, innerH/nativeH) *
devicePixelRatio` (capped so the framebuffer stays <= 4096) - so the framebuffer
matches the display size, FIT scales it ~1:1, and text/graphics render crisp at
full resolution. New `reapplyRenderScale()` re-runs the active scene's render
scale when fullscreen toggles (the factor + layout mode both change); the
fullscreen handler calls it on the media-query/`fullscreenchange` event.
`crispTextResolution` floor raised 3 -> 4 to cover the larger fullscreen camera
zoom. Verified: map framebuffer 960x720 -> 1200x900 (fit res) in a 1600x900
viewport; `library` 472x832 -> 510x900 with **aspect preserved** (0.567==0.568),
pillarboxed.

## 4. Esc should exit fullscreen (not end the level) - back to the Fullscreen API

To make Esc reliably exit fullscreen, JS must control it - which native F11
(issue 1's interim fix) can't. So back to the **Fullscreen API**, now that the
layout bugs (2, 3) are fixed: F11 (captured, `preventDefault`) toggles
`document.documentElement.requestFullscreen()` / `exitFullscreen()`. The
Fullscreen API is exited by Esc *by the browser itself*, and fires
`fullscreenchange` - so Esc needs no explicit exit call. Each scene's own Esc
handler (`LevelScene`/`WorldMapScene`/`ReplayScene`) just guards
`if (isFullscreenActive()) return;` so Esc-while-fullscreen no-ops in-game (the
browser ends fullscreen, the level stays). The global keydown handler
deliberately does **not** handle Esc: it runs before the scene handlers (added at
boot), so calling `exitFullscreen()` there would flip the state and make the
scene handler then see "not fullscreen" and leave the level. Layout still rides
the media query + `fullscreenchange` (belt and suspenders; catches a native F11
that slips through `preventDefault`).

## Files

- `web/src/scenes/sceneUtils.ts`: `renderFactor()` (windowed vs fullscreen-fit),
  `applyRenderScale` rewrite (explicit FIT/NONE + `setGameSize`/`resize`),
  `reapplyRenderScale()`, `crispTextResolution` floor 4, `MAX_FRAMEBUFFER` cap.
- `web/src/fullscreen.ts`: Fullscreen API (F11 toggle), media-query +
  `fullscreenchange` -> container sizing + `reapplyRenderScale`; no Esc handling.
- `web/src/scenes/{LevelScene,WorldMapScene,ReplayScene}.ts`: Esc guard.

## Verification

- Aspect + crisp: map + library in a forced-fullscreen headless run - framebuffer
  at fit resolution, aspect preserved both, library pillarboxed (screenshot).
- Esc guard (real key press): Esc while fullscreen keeps the level; Esc while
  windowed leaves to the map.
- e2e 7/7 (windowed path unchanged); tsc clean.
- **Needs a manual browser check (can't be done headless):** real F11 enter/exit
  in Edge (Playwright intercepts F11; the browser's F11->page-keydown +
  `preventDefault` path can't be exercised), and the browser's own Esc-exit of
  API fullscreen. The black-screen layout bugs are fixed, so the remaining
  unknown is only whether F11 + `preventDefault` cleanly enters API fullscreen in
  Edge (vs. a native-F11 conflict); if it regresses, the fallback is the native-F11
  mechanism (issue 1), trading away JS-driven Esc-exit.
