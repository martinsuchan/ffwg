import Phaser from "phaser";

import { applyRenderScale, pictureToAssetUrl } from "./sceneUtils";

/**
 * The rolling credits, reached from the world map's Credits corner button -
 * legacy WorldMap::runCredits() -> PosterScroller(images/menu/credits.png).
 * Scrolls the game's own credits image bottom-to-top over a dark backdrop;
 * Esc or a click returns to the world map, as does reaching the end. See
 * docs/038.
 */

/** Viewport (same as the map's) the credits scroll through. */
const VIEW_WIDTH = 640;
const VIEW_HEIGHT = 480;
/** Scroll pace - ms per pixel of travel (a plain readable-speed guess). */
const MS_PER_PX = 14;

export class CreditsScene extends Phaser.Scene {
  private tween?: Phaser.Tweens.Tween;
  private finished = false;

  constructor() {
    super("credits");
  }

  preload(): void {
    this.load.image("credits-img", pictureToAssetUrl("images/menu/credits.png"));
  }

  create(): void {
    applyRenderScale(this, VIEW_WIDTH, VIEW_HEIGHT);
    this.add.rectangle(0, 0, VIEW_WIDTH, VIEW_HEIGHT, 0x000000).setOrigin(0, 0).setDepth(-1);

    const img = this.add.image(VIEW_WIDTH / 2, VIEW_HEIGHT, "credits-img").setOrigin(0.5, 0);
    const travel = img.height + VIEW_HEIGHT; // from just below the view to fully off the top

    this.tween = this.tweens.add({
      targets: img,
      y: -img.height,
      duration: travel * MS_PER_PX,
      ease: "Linear",
      onComplete: () => this.finish(),
    });

    this.input.keyboard?.on("keydown-ESC", () => this.finish());
    this.input.on("pointerdown", () => this.finish());

    this.events.once(Phaser.Scenes.Events.SHUTDOWN, () => this.tween?.remove());
  }

  private finish(): void {
    if (this.finished) return;
    this.finished = true;
    this.tween?.remove();
    this.scene.start("worldmap");
  }
}
