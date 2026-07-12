import Phaser from "phaser";

import { loadSettings } from "../storage/settingsStorage";

/**
 * The intro movie, reached from the world map's Intro corner button - legacy
 * WorldMap::runIntro() plays images/menu/intro.mpg (a MovieState). Browsers
 * can't play MPEG-1, so the source .mpg is transcoded to H.264 mp4
 * (assets/video/intro.mp4) and played here via a Phaser Video object. Esc, a
 * click, or the end of the clip returns to the world map. See docs/038.
 */

/** intro.mpg's native size. */
const VIDEO_WIDTH = 640;
const VIDEO_HEIGHT = 480;

export class IntroScene extends Phaser.Scene {
  private video?: Phaser.GameObjects.Video;
  private finished = false;

  constructor() {
    super("intro");
  }

  init(): void {
    this.finished = false;
    this.video = undefined;
  }

  preload(): void {
    this.load.video("intro-video", "/assets/video/intro.mp4");
    // A missing/unconverted video shouldn't strand the player on black.
    this.load.on(Phaser.Loader.Events.FILE_LOAD_ERROR, () => {});
  }

  create(): void {
    this.scale.resize(VIDEO_WIDTH, VIDEO_HEIGHT);
    this.add.rectangle(0, 0, VIDEO_WIDTH, VIDEO_HEIGHT, 0x000000).setOrigin(0, 0).setDepth(-1);

    if (this.cache.video.exists("intro-video")) {
      const video = this.add
        .video(VIDEO_WIDTH / 2, VIDEO_HEIGHT / 2, "intro-video")
        .setOrigin(0.5);
      // Fit to the canvas (native is already 640x480, but be explicit).
      video.setDisplaySize(VIDEO_WIDTH, VIDEO_HEIGHT);
      // The intro's own audio, scaled by the music-volume setting (docs/038).
      video.setVolume(loadSettings().musicVolume / 100);
      // Launched from a click on the corner button, so a user gesture exists -
      // audio autoplay is allowed. If the browser still blocks it, Phaser
      // retries muted, which is an acceptable fallback.
      video.play(false);
      video.once("complete", () => this.finish());
      this.video = video;
    } else {
      // No video available - just bounce back to the map.
      this.time.delayedCall(0, () => this.finish());
    }

    this.input.keyboard?.on("keydown-ESC", () => this.finish());
    this.input.on("pointerdown", () => this.finish());

    this.events.once(Phaser.Scenes.Events.SHUTDOWN, () => {
      this.video?.stop();
    });
  }

  private finish(): void {
    if (this.finished) return;
    this.finished = true;
    this.video?.stop();
    this.scene.start("worldmap");
  }
}
