import Phaser from "phaser";

import { createDemoScript, DEMO_CYCLE_MS, type DemoScript } from "../lua/demoScript";
import { levelSoundSpriteDirs } from "../lua/levelScript";
import { applyRenderScale, crispText, pictureToAssetUrl } from "./sceneUtils";
import { pictureToAtlas, atlasWebpUrl, atlasJsonUrl } from "./atlas";
import { AudioManager } from "./AudioManager";

/** briefcase movie canvas = kufr256.png's real size (DemoMode resizes the video
 *  mode to the first displayed picture). Posters are a fixed 640x480 (all the
 *  world-final/ending poster.png images are that size). */
const MOVIE_WIDTH = 720;
const MOVIE_HEIGHT = 555;
const POSTER_WIDTH = 640;
const POSTER_HEIGHT = 480;

/** Highest demo_NNN frame in images/demo_briefcase (000..292) - preloaded up
 *  front (a one-time cutscene load); any gap is tolerated via loaderror. */
const MAX_DEMO_FRAME = 292;

/** How the demo behaves and looks - a briefcase movie (overlays a paused level,
 *  resumes it on finish) vs a final-level/ending poster (a fullscreen recap
 *  cutscene that returns to the world map). See docs/031, docs/050. */
type DemoMode = "movie" | "poster";

export interface DemoSceneData {
  demoFile: string;
  levelName: string;
  mode?: DemoMode;
  /** Where finish() goes: "level" resumes the paused LevelScene (movie);
   *  "worldmap" starts the world map (poster after a solve/replay). */
  returnTo?: "level" | "worldmap";
  /** Passed to the world map on finish when returnTo === "worldmap" - lets the
   *  map run its post-solve ending check (docs/050). */
  returnData?: Record<string, unknown>;
}

/**
 * Plays a fullscreen DemoMode cutscene - either the briefcase tutorial movie
 * (demo_briefcase.lua, docs/031) or a final-level/ending "poster"
 * (demo_poster.lua, docs/050). Runs demoScript (web/src/lua/demoScript.ts) on a
 * fixed cycle, drawing its demo_display() pictures + model_talk()
 * subtitles/voice + music. Skippable with Esc only (per user). A movie resumes
 * the paused level on finish; a poster returns to the world map.
 */
export class DemoScene extends Phaser.Scene {
  private demoFile!: string;
  private levelName!: string;
  private mode: DemoMode = "movie";
  private returnTo: "level" | "worldmap" = "level";
  private returnData: Record<string, unknown> = {};
  /** Poster mode: the level atlas key + "poster" frame (a poster.png lives
   *  inside its atlased level dir now, not as an individual file - docs/042). */
  private posterAtlas = { atlasKey: "", frame: "poster" };
  private demoScript: DemoScript | null = null;
  private audioManager!: AudioManager;
  private cycleTimer?: Phaser.Time.TimerEvent;
  private subtitleText!: Phaser.GameObjects.Text;
  private lastDialogId: string | null = null;
  private finished = false;

  constructor() {
    super("demo");
  }

  init(data: DemoSceneData): void {
    this.demoFile = data.demoFile;
    this.levelName = data.levelName;
    this.mode = data.mode ?? "movie";
    this.returnTo = data.returnTo ?? (this.mode === "poster" ? "worldmap" : "level");
    this.returnData = data.returnData ?? {};
    this.posterAtlas = pictureToAtlas(`images/${this.levelName}/poster.png`);
    // init() runs on every (re)start - reset per-run state so a second demo
    // in the same session starts clean.
    this.demoScript = null;
    this.lastDialogId = null;
    this.finished = false;
  }

  private get canvasWidth(): number {
    return this.mode === "poster" ? POSTER_WIDTH : MOVIE_WIDTH;
  }
  private get canvasHeight(): number {
    return this.mode === "poster" ? POSTER_HEIGHT : MOVIE_HEIGHT;
  }

  preload(): void {
    if (this.mode === "poster") {
      // A poster is a single fullscreen frame inside its level atlas (docs/042).
      const key = this.posterAtlas.atlasKey;
      if (!this.textures.exists(key)) {
        this.load.atlas(key, atlasWebpUrl(key), atlasJsonUrl(key));
      }
    } else {
      this.load.image(this.frameKey("kufr256"), pictureToAssetUrl("images/demo_briefcase/kufr256.png"));
      for (let i = 0; i <= MAX_DEMO_FRAME; i++) {
        const name = `demo_${String(i).padStart(3, "0")}`;
        this.load.image(this.frameKey(name), pictureToAssetUrl(`images/demo_briefcase/${name}.png`));
      }
    }
    // Any missing frame just doesn't draw - never blocks the demo.
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
    const w = this.canvasWidth;
    const h = this.canvasHeight;
    applyRenderScale(this, w, h);
    this.add.rectangle(0, 0, w, h, 0x000000).setOrigin(0, 0).setDepth(-1);

    this.subtitleText = this.add
      .text(w / 2, h - 10, "", crispText({
        fontFamily: "sans-serif",
        fontSize: "15px",
        color: "#ffffff",
        backgroundColor: "#000000c0",
        padding: { x: 8, y: 5 },
        align: "center",
        wordWrap: { width: w - 48 },
      }))
      .setOrigin(0.5, 1)
      .setDepth(1000)
      .setVisible(false);

    this.audioManager = new AudioManager(this);
    // Warm the movie's voice/music sprites so the first line plays on time.
    void this.audioManager.preloadAll(levelSoundSpriteDirs(this.levelName));

    // Skip with Esc (KEY_QUIT) or Space (legacy DemoInput registers SDLK_SPACE
    // as quit) - not a click.
    this.input.keyboard!.addCapture("ESC,SPACE");
    this.input.keyboard!.on("keydown-ESC", () => this.finish());
    this.input.keyboard!.on("keydown-SPACE", () => this.finish());

    this.events.once(Phaser.Scenes.Events.SHUTDOWN, () => {
      this.cycleTimer?.remove();
      this.audioManager.destroy();
      this.demoScript?.destroy();
      this.demoScript = null;
    });

    const demoOpts =
      this.mode === "poster" ? { dialogPrefix: "demo_", includeProgDemo: true } : {};
    createDemoScript(this.demoFile, this.levelName, demoOpts)
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
        console.error(`Failed to load demo "${this.demoFile}"`, error);
        // Don't strand the player on a black screen - leave via the normal path.
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
      if (this.mode === "poster") {
        // Every poster draw is the single poster frame from the level atlas.
        const { atlasKey, frame } = this.posterAtlas;
        if (!this.textures.exists(atlasKey)) continue;
        this.add.image(draw.x, draw.y, atlasKey, frame).setOrigin(0, 0);
      } else {
        const texKey = this.keyForPath(draw.path);
        if (!this.textures.exists(texKey)) continue; // frame failed to load - skip
        this.add.image(draw.x, draw.y, texKey).setOrigin(0, 0);
      }
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

  /** End the demo (natural end, load failure, or Esc). A movie resumes the
   *  paused level; a poster starts the world map. */
  private finish(): void {
    if (this.finished) return;
    this.finished = true;
    this.cycleTimer?.remove();
    // Stop the demo's music AND any mid-line voice, so nothing bleeds into
    // the next scene (docs/031).
    this.audioManager.stopAll();
    if (this.returnTo === "worldmap") {
      // A poster ran after solving/replaying a final level - hand off to the
      // map (which runs the ending check, docs/050) and tear this scene down.
      this.scene.stop();
      this.scene.start("worldmap", this.returnData);
    } else {
      // Resume the paused level (it restores its own canvas size + input on
      // RESUME), then tear this scene down.
      this.scene.resume("level");
      this.scene.stop();
    }
  }
}
