import Phaser from "phaser";

import { createDemoScript, DEMO_CYCLE_MS, type DemoScript } from "../lua/demoScript";
import { levelSoundSpriteDirs } from "../lua/levelScript";
import { pictureToAssetUrl } from "./sceneUtils";
import { AudioManager } from "./AudioManager";

/** kufr256.png's real size - the movie's fixed canvas (DemoMode resizes the
 *  video mode to the first displayed picture, which is this background). */
const MOVIE_WIDTH = 720;
const MOVIE_HEIGHT = 555;

/** Highest demo_NNN frame in images/demo_briefcase (000..292) - preloaded up
 *  front (a one-time cutscene load); any gap is tolerated via loaderror. */
const MAX_DEMO_FRAME = 292;

/**
 * Plays a level's fullscreen "movie" (only briefcase's demo_briefcase.lua
 * today) - the port's equivalent of the C++ DemoMode, launched as an overlay
 * over a paused LevelScene (see LevelScene's level_newDemo handling). Runs
 * demoScript (web/src/lua/demoScript.ts) on a fixed cycle, drawing its
 * demo_display() pictures + model_talk() subtitles/voice + music. Skippable
 * with Esc only (per user); resumes the level on finish. See docs/031.
 */
export class DemoScene extends Phaser.Scene {
  private demoFile!: string;
  private levelName!: string;
  private demoScript: DemoScript | null = null;
  private audioManager!: AudioManager;
  private cycleTimer?: Phaser.Time.TimerEvent;
  private subtitleText!: Phaser.GameObjects.Text;
  private lastDialogId: string | null = null;
  private finished = false;

  constructor() {
    super("demo");
  }

  init(data: { demoFile: string; levelName: string }): void {
    this.demoFile = data.demoFile;
    this.levelName = data.levelName;
    // init() runs on every (re)start - reset per-run state so a second movie
    // in the same session starts clean.
    this.demoScript = null;
    this.lastDialogId = null;
    this.finished = false;
  }

  preload(): void {
    this.load.image(this.frameKey("kufr256"), pictureToAssetUrl("images/demo_briefcase/kufr256.png"));
    for (let i = 0; i <= MAX_DEMO_FRAME; i++) {
      const name = `demo_${String(i).padStart(3, "0")}`;
      this.load.image(this.frameKey(name), pictureToAssetUrl(`images/demo_briefcase/${name}.png`));
    }
    // Any missing frame just doesn't draw - never blocks the movie.
    this.load.on(Phaser.Loader.Events.FILE_LOAD_ERROR, () => {});
  }

  /** demo_display() passes a legacy image path ("images/demo_briefcase/
   *  demo_000.png"); the texture key is its basename, matching preload. */
  private frameKey(basename: string): string {
    return `demo-briefcase-${basename}`;
  }

  private keyForPath(path: string): string {
    const base = path.replace(/^.*\//, "").replace(/\.[^.]+$/, "");
    return this.frameKey(base);
  }

  create(): void {
    this.scale.resize(MOVIE_WIDTH, MOVIE_HEIGHT);
    this.add.rectangle(0, 0, MOVIE_WIDTH, MOVIE_HEIGHT, 0x000000).setOrigin(0, 0).setDepth(-1);

    this.subtitleText = this.add
      .text(MOVIE_WIDTH / 2, MOVIE_HEIGHT - 10, "", {
        fontFamily: "sans-serif",
        fontSize: "15px",
        color: "#ffffff",
        backgroundColor: "#000000c0",
        padding: { x: 8, y: 5 },
        align: "center",
        wordWrap: { width: MOVIE_WIDTH - 48 },
      })
      .setOrigin(0.5, 1)
      .setDepth(1000)
      .setVisible(false);

    this.audioManager = new AudioManager(this);
    // Warm the movie's voice/music sprites so the first line plays on time.
    void this.audioManager.preloadAll(levelSoundSpriteDirs(this.levelName));

    // Esc-only skip (per user - not Space/click).
    this.input.keyboard!.addCapture("ESC");
    this.input.keyboard!.on("keydown-ESC", () => this.finish());

    this.events.once(Phaser.Scenes.Events.SHUTDOWN, () => {
      this.cycleTimer?.remove();
      this.audioManager.destroy();
      this.demoScript?.destroy();
      this.demoScript = null;
    });

    createDemoScript(this.demoFile, this.levelName)
      .then((script) => {
        if (this.finished || !this.scene.isActive()) {
          script.destroy();
          return;
        }
        this.demoScript = script;
        this.cycleTimer = this.time.addEvent({
          delay: DEMO_CYCLE_MS,
          loop: true,
          callback: () => this.cycle(),
        });
      })
      .catch((error: unknown) => {
        console.error(`Failed to load demo movie "${this.demoFile}"`, error);
        // Don't strand the player on a black screen - return to the level.
        this.finish();
      });
  }

  private cycle(): void {
    const script = this.demoScript;
    if (!script || this.finished) return;

    const done = script.tick();

    // Add each new demo_display() draw as an Image stacked on top of the
    // previous ones (default depth 0, insertion order = draw order), so an
    // opaque frame covers earlier ones and a transparent frame layers over
    // them - matching DemoMode's never-cleared surface buffer. The subtitle
    // (depth 1000) stays on top. Images are freed on scene shutdown.
    for (const draw of script.takePendingDraws()) {
      const texKey = this.keyForPath(draw.path);
      if (!this.textures.exists(texKey)) continue; // frame failed to load - skip
      this.add.image(draw.x, draw.y, texKey).setOrigin(0, 0);
    }

    // Subtitle + one-shot voice.
    const subtitle = script.getActiveSubtitle();
    if (subtitle) this.subtitleText.setText(subtitle.text).setVisible(true);
    else this.subtitleText.setVisible(false);

    void this.audioManager.applyMusicCommand(script.getMusicCommand());

    // The movie is inherently sequential (one line at a time, gated by
    // waitForTalker), so it uses a single voice group (0) and cuts the previous
    // line when a new one starts - the degenerate case of docs/043's concurrent
    // voices.
    const dialogId = script.getActiveDialogId();
    if (dialogId !== this.lastDialogId) {
      this.lastDialogId = dialogId;
      if (dialogId && subtitle?.sound) {
        this.audioManager.stopDialogVoice(0);
        this.audioManager.playDialogVoice(subtitle.sound, subtitle.volume, 0);
      }
    }

    if (done) this.finish();
  }

  /** End the movie (natural end, load failure, or Esc) and resume the level. */
  private finish(): void {
    if (this.finished) return;
    this.finished = true;
    this.cycleTimer?.remove();
    // Stop the movie's music AND any mid-line voice, so nothing bleeds into
    // the level when it resumes (docs/031).
    this.audioManager.stopAll();
    // Resume the paused level (it restores its own canvas size + input on
    // RESUME), then tear this scene down.
    this.scene.resume("level");
    this.scene.stop();
  }
}
