import Phaser from "phaser";

/**
 * Browser Back-button handling. Without this, pressing Back while in a level
 * (or replay/intro/credits/demo) unloads the single-page app and lands on a
 * blank tab. Instead we push a history entry whenever the world map launches a
 * sub-view, and on `popstate` (Back) route back to the world map, keeping the
 * page loaded.
 *
 * The original is a native app with no equivalent - this is a browser-port-only
 * concern. `returnToWorldMap` keys off which Phaser scenes are actually active
 * (not the popped history state), so it stays correct however the history stack
 * has drifted.
 */

/** Sub-view scene keys launched from the world map that Back returns from. */
const SUB_SCENES = ["level", "replay", "intro", "credits", "demo"];

let game: Phaser.Game | undefined;

export function initHistoryNav(g: Phaser.Game): void {
  game = g;
  window.addEventListener("popstate", () => returnToWorldMap());
}

/** WorldMapScene.create() calls this so the base history entry is the map. */
export function markWorldMap(): void {
  history.replaceState({ ffwg: "worldmap" }, "");
}

/** Call right before `scene.start`ing a sub-view from the world map: pushes one
 *  history entry so the next Back pops it (firing `popstate`) instead of
 *  leaving the page. */
export function pushSubView(): void {
  history.pushState({ ffwg: "sub" }, "");
}

function returnToWorldMap(): void {
  if (!game) return;
  const sm = game.scene;
  const inSub = SUB_SCENES.some((k) => sm.isActive(k) || sm.isPaused(k));
  if (!inSub) return; // already on the map (or nothing to leave) - let Back be
  for (const k of SUB_SCENES) {
    if (sm.isActive(k) || sm.isPaused(k)) sm.stop(k);
  }
  sm.start("worldmap");
}
