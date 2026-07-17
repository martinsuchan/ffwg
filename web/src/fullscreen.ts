import Phaser from "phaser";

import { reapplyRenderScale } from "./scenes/sceneUtils";

/**
 * Aspect-preserving fullscreen via the Fullscreen API - see docs/064/065.
 *
 * History: the first version captured F11 + `preventDefault` + Phaser
 * `startFullscreen`, which black-screened in Edge (F11-conflict + a FIT layout
 * timing bug). The second rode the browser's native F11 (media query only) - it
 * rendered, but the browser's own F11 fullscreen can't be exited from JS (needed
 * for the Esc-to-exit request) and isn't reliably exited by Esc. So we're back to
 * the Fullscreen API, now that the layout bugs (stale parentSize, wrong aspect on
 * room change) are fixed: F11 requests/exits it, and Esc exits it (both the
 * browser's built-in behavior AND an explicit call, so it's reliable).
 *
 * Layout is driven off the actual fullscreen state, not the key press: we react
 * to the `(display-mode: fullscreen)` media query AND `fullscreenchange` (belt
 * and suspenders - the query also catches a native F11 that slips through), size
 * the game container to the viewport, and re-apply the active scene's render
 * scale (which recomputes the framebuffer at the fullscreen fit factor and
 * switches to FIT - reapplyRenderScale/applyRenderScale in sceneUtils).
 */

const FS_QUERY = "(display-mode: fullscreen)";

/** True while any fullscreen is showing - the Fullscreen API's element OR the
 *  browser's native F11 (the media query catches both; `fullscreenElement`
 *  covers the instant before the query updates). */
export function isFullscreenActive(): boolean {
  return window.matchMedia(FS_QUERY).matches || document.fullscreenElement != null;
}

function requestFullscreen(): void {
  void document.documentElement.requestFullscreen?.().catch(() => {});
}

function exitFullscreen(): void {
  if (document.fullscreenElement) void document.exitFullscreen?.().catch(() => {});
}

export function initFullscreen(game: Phaser.Game): void {
  const container = game.canvas.parentElement;

  // Fill the viewport so FIT scales the canvas up to the whole screen; a plain
  // black backdrop covers the letterbox bars.
  const FULLSCREEN_STYLE: Partial<CSSStyleDeclaration> = {
    position: "fixed",
    inset: "0",
    width: "100vw",
    height: "100vh",
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    background: "#000",
    zIndex: "9999",
  };

  const setContainerFullscreen = (on: boolean): void => {
    if (!container) return;
    for (const key of Object.keys(FULLSCREEN_STYLE) as Array<keyof CSSStyleDeclaration>) {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      (container.style as any)[key] = on ? FULLSCREEN_STYLE[key] : "";
    }
  };

  const onChange = (): void => {
    setContainerFullscreen(isFullscreenActive());
    reapplyRenderScale();
    // Some browsers (Edge) report the fullscreen viewport a frame late - re-fit.
    requestAnimationFrame(() => reapplyRenderScale());
  };

  window.matchMedia(FS_QUERY).addEventListener("change", onChange);
  document.addEventListener("fullscreenchange", onChange);

  window.addEventListener("keydown", (event: KeyboardEvent) => {
    if (event.key !== "F11") return;
    event.preventDefault();
    if (isFullscreenActive()) exitFullscreen();
    else requestFullscreen();
  });

  // Esc is intentionally NOT handled here: the browser exits Fullscreen-API
  // fullscreen on Esc by itself, and the scenes' own Esc handlers no-op while
  // fullscreen (they check isFullscreenActive) so the level isn't left - only
  // fullscreen ends. Calling exitFullscreen() here would race those handlers
  // (this listener runs first, flips the state, then the scene handler would
  // see "not fullscreen" and leave the level). See docs/065.

  // Handle a page that somehow loads already fullscreen.
  if (isFullscreenActive()) onChange();
}
