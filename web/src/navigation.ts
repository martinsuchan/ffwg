import Phaser from "phaser";

/**
 * Browser Back-button handling. Without this, pressing Back while in a level
 * (or replay/intro/credits/demo) unloads the single-page app and lands on a
 * blank tab. Instead we push a history entry whenever the world map launches a
 * sub-view, and on `popstate` (Back) route back to the world map, keeping the
 * page loaded.
 *
 * A view opened from a level (replay on P, compare on C) pushes one entry of its
 * own and registers a back handler. Back then does what its Esc does: it
 * returns to the level, not to the map (docs/087).
 *
 * The original is a native app with no equivalent - this is a browser-port-only
 * concern. `returnToWorldMap` keys off which Phaser scenes are actually active
 * (not the popped history state), so it stays correct however the history stack
 * has drifted.
 */

/** Sub-view scene keys launched from the world map that Back returns from. */
const SUB_SCENES = ["level", "replay", "compare", "intro", "credits", "demo"];

/** The state of an entry pushed for a view opened from a level. */
const VIEW_STATE = "view";

let game: Phaser.Game | undefined;
/** What Back does in the active view (its Esc); null = back to the map. */
let backHandler: (() => void) | null = null;
/** popstates caused by our own history.back() (leaveLevelView), not by the user. */
let ownPops = 0;

export function initHistoryNav(g: Phaser.Game): void {
  game = g;
  window.addEventListener("popstate", () => {
    if (ownPops > 0) {
      ownPops -= 1;
      return;
    }
    if (backHandler) backHandler();
    else returnToWorldMap();
  });
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

/** Call right before a level `scene.start`s a view that Esc returns from to the
 *  level (replay, compare): its own entry, so Back leaves only that view. */
export function pushLevelView(): void {
  history.pushState({ ffwg: VIEW_STATE }, "");
}

/** The view's create() registers what Back does; it is cleared again on
 *  SHUTDOWN, so a scene left any other way never handles a later Back. */
export function setBackHandler(scene: Phaser.Scene, handler: () => void): void {
  backHandler = handler;
  scene.events.once(Phaser.Scenes.Events.SHUTDOWN, () => {
    if (backHandler === handler) backHandler = null;
  });
}

/** The view was left in-app (Esc, a Back button) for the level that opened it:
 *  drop its history entry too, so the next Back leaves the level, not a view
 *  that is already gone. */
export function leaveLevelView(): void {
  if ((history.state as { ffwg?: string } | null)?.ffwg !== VIEW_STATE) return;
  ownPops += 1;
  history.back();
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
