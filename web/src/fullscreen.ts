import Phaser from "phaser";

import { reapplyRenderScale } from "./scenes/sceneUtils";

/**
 * Aspect-preserving fullscreen that rides the browser's OWN F11, and does NOT
 * capture any key - see docs/064/065/066.
 *
 * We deliberately use the browser's native F11 fullscreen rather than the
 * Fullscreen API. The API is exited by Esc *and that exit can't be prevented*
 * (browsers won't let a page trap you in fullscreen) - so Esc would drop out of
 * fullscreen every time you leave a level, which the player doesn't want. Native
 * F11 fullscreen is toggled only by F11 and is unaffected by Esc, so Esc keeps
 * doing its normal in-game job (leave the level, close a popup) while fullscreen
 * stays on. F11 is the single enter/exit control.
 *
 * Since we don't drive fullscreen ourselves, layout is purely reactive: the
 * `(display-mode: fullscreen)` media query matches whenever the browser is
 * fullscreen (native F11 included, unlike the Fullscreen-API-only
 * `fullscreenchange`). On its change we size the game container to the viewport
 * and re-apply the active scene's render scale, which recomputes the framebuffer
 * at the fullscreen fit factor and switches to FIT (applyRenderScale in
 * sceneUtils). `fullscreenchange` is also listened to, harmlessly, in case an API
 * fullscreen is ever entered from elsewhere.
 */

const FS_QUERY = "(display-mode: fullscreen)";

/** True whenever the browser is fullscreen - native F11 (the media query) or the
 *  Fullscreen API (`fullscreenElement`, covering the instant before the query
 *  updates). */
export function isFullscreenActive(): boolean {
  return window.matchMedia(FS_QUERY).matches || document.fullscreenElement != null;
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

  // Handle a page that somehow loads already fullscreen.
  if (isFullscreenActive()) onChange();
}
