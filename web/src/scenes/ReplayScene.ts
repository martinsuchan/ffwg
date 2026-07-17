import Phaser from "phaser";

import { GRID_SCALE, type LevelData } from "../lua/levelLoader";
import { createLevelScript, type LevelScript, type EngineControl } from "../lua/levelScript";
import { GameEngine, type RenderModel } from "../game/GameEngine";
import { CYCLE_MS, IDLE_ROUND_MS } from "../game/timing";
import { ModelAnimator, collectAtlasKeys, preloadAtlases, roundPhases } from "./ModelAnimator";
import { AudioManager } from "./AudioManager";
import { applyRenderScale, crispText, drawRopeDecors, isFishKind, resolveInitialFrame } from "./sceneUtils";
import { isFullscreenActive } from "../fullscreen";
import { WavyBackground } from "./WavyBackground";

type PlayState = "paused" | "play" | "fast";

/** How much faster "fast" is than normal-speed replay - a plain guess at a
 *  good default (see docs/025's "Open for next time"), not measured
 *  against anything yet. Easy to retune. */
const FAST_MULTIPLIER = 6;

/**
 * Watchable playback of a level's recorded move string (docs/021's
 * symbol format) - unlike the headless validator (docs/022), this ticks
 * one physics round at a time on a real-time timer, exactly like live
 * play, so falling/sliding/turning look the same. Deliberately *better*
 * than the original's own replay (legacy/src/level/LevelLoading.cpp's
 * loadReplay(): fixed fast pace, no pause/step/speed control at all):
 * starts playing at normal speed immediately, shows a step counter, and
 * has Pause/Step/Play/Fast-forward controls.
 *
 * Only fish animation and background music play - no subtitles, no
 * item decorative animation, no sound effects/dialog voice. The live Lua
 * engine (docs/014) still runs every round regardless, since level-specific
 * music commands (including mid-level stops like viking1's musician gag)
 * come from it - only its *other* outputs are ignored here.
 *
 * Launched from LevelScene via P (see docs/025); Escape returns to it.
 */
export class ReplayScene extends Phaser.Scene {
  private levelData!: LevelData;
  private moves!: string;
  private moveIndex = 0;
  /** Where Escape goes back to - a real distinction the original also has
   *  (P from a live level vs. the world map's Pedometer "Replay" button
   *  are genuinely different entry points there too, see docs/027):
   *  "level" resumes the LevelScene replay was launched from; "worldmap"
   *  is used when replay was launched directly from the map (a solved
   *  level was never actually entered interactively this session). */
  private returnTo: "level" | "worldmap" = "level";
  /** The replayed level's recap poster (a demo_poster.lua path) or null. When a
   *  world-final level's replay reaches solved and returnTo === "worldmap" (the
   *  Pedometer path), the poster plays before returning to the map - faithful to
   *  the original, where a Pedometer replay drives the room to solved and the
   *  same finishLevel -> createPoster path runs (docs/050). */
  private poster: string | null = null;
  /** LevelNode::m_depth for level_getDepth() - see docs/054. */
  private depth = 1;
  /** True only when replaying the ending level (docs/061). */
  private isEnding = false;
  /** Room background + its underwater ripple (docs/056). */
  private background!: WavyBackground;

  private engine!: GameEngine;
  private animators = new Map<number, ModelAnimator>();
  private audioManager!: AudioManager;
  private levelScript: LevelScript | null = null;
  /** Lazily created on the first level that actually registers a rope decor
   *  (elevator1/elevator2 only) - see drawRopes() and docs/055. */
  private ropeGraphics?: Phaser.GameObjects.Graphics;
  /** windoze's live-Lua control-swap/fast-fall hook, so the replay's engine
   *  tracks busy/fast-falling exactly as interactive play did (docs/035).
   *  Closes over `this` so a replay restart swapping `this.engine` stays
   *  consistent. No-op for every other level. */
  private readonly engineControl: EngineControl = {
    setBusy: (index, busy) => this.engine.setBusy(index, busy),
    checkActive: () => this.engine.checkActive(),
    setFastFalling: (value) => this.engine.setFastFalling(value),
    askFieldIndex: (x, y) => this.engine.askFieldIndex(x, y),
    isSolved: () => this.engine.isSolved(),
  };
  /** Guards against a superseded startReplay()'s createLevelScript() call
   *  resolving after a newer restart already happened - same pattern as
   *  LevelScene.scriptGeneration (docs/014). Also doubles as the
   *  restartCount createLevelScript() needs for level_getRestartCounter(). */
  private scriptGeneration = 0;
  private gameOver = false;

  // Shared phase-locked animation clock (docs/046) - same model as LevelScene.
  private roundsActive = false;
  private roundClock = 0;
  private moving = false;
  private moveDurationMs = CYCLE_MS;
  private cyclesThisRound = 1;
  private playState: PlayState = "paused";
  private controlButtons = new Map<PlayState | "step", Phaser.GameObjects.Text>();

  private stepText!: Phaser.GameObjects.Text;
  private statusText!: Phaser.GameObjects.Text;

  constructor() {
    super("replay");
  }

  init(data: {
    levelData: LevelData;
    moves: string;
    returnTo?: "level" | "worldmap";
    poster?: string | null;
    depth?: number;
    /** True when replaying the ending itself, so its post-poster return doesn't
     *  make the map re-present the ending (docs/061). */
    isEnding?: boolean;
  }): void {
    this.levelData = data.levelData;
    this.moves = data.moves;
    this.returnTo = data.returnTo ?? "level";
    this.poster = data.poster ?? null;
    this.depth = data.depth ?? 1;
    this.isEnding = data.isEnding ?? false;
    // Phaser reuses the scene instance across scene.start(), but SHUTDOWN
    // destroys its GameObjects - drop the stale handle so drawRopes() builds a
    // fresh one instead of touching a destroyed Graphics (cf. docs/012).
    this.ropeGraphics = undefined;
  }

  preload(): void {
    // One atlas per level dir + shared fish variants (docs/042) - see
    // LevelScene.preload() for the identical rationale.
    preloadAtlases(this, collectAtlasKeys(this.levelData.models, this.levelData.bgPicture));
  }

  create(): void {
    // Needed here too, not just LevelScene/WorldMapScene - the Pedometer's
    // "Replay" button launches this scene directly from the (differently-
    // sized) world map, without ever passing through LevelScene first.
    // applyRenderScale sizes the framebuffer + camera zoom (docs/064).
    applyRenderScale(
      this,
      this.levelData.roomWidth * GRID_SCALE,
      this.levelData.roomHeight * GRID_SCALE,
    );
    this.background = new WavyBackground(
      this,
      this.levelData.levelName,
      this.levelData.bgPicture,
      this.levelData.roomWidth * GRID_SCALE,
      this.levelData.roomHeight * GRID_SCALE,
      this.levelData.waves,
    );

    const roomWidthPx = this.levelData.roomWidth * GRID_SCALE;

    this.stepText = this.add
      .text(roomWidthPx - 8, 8, "", crispText({
        fontFamily: "sans-serif",
        fontSize: "16px",
        color: "#ffffff",
        backgroundColor: "#000000a0",
        padding: { x: 6, y: 4 },
      }))
      .setOrigin(1, 0)
      .setDepth(1000);

    this.statusText = this.add
      .text(8, 8, `Replay - Esc = ${this.escLabel()}, R = restart replay`, crispText({
        fontFamily: "sans-serif",
        fontSize: "16px",
        color: "#ffffff",
        backgroundColor: "#000000a0",
        padding: { x: 6, y: 4 },
      }))
      .setDepth(1000);

    this.input.keyboard!.on("keydown-R", () => this.startReplay());
    this.input.keyboard!.on("keydown-ESC", () => {
      // While fullscreen, Esc only leaves fullscreen (docs/065).
      if (isFullscreenActive()) return;
      if (this.returnTo === "worldmap") this.scene.start("worldmap");
      // Hand poster/depth back so the level it returns to is fully restored
      // (otherwise solving it afterwards would skip its recap poster).
      else
        this.scene.start("level", {
          levelData: this.levelData,
          poster: this.poster,
          depth: this.depth,
        });
    });

    this.audioManager = new AudioManager(this);
    this.createControls();
    this.startReplay();

    // The Sound Manager is game-global, not scene-scoped - stop this
    // scene's music explicitly on the way out so it doesn't keep playing
    // underneath whichever scene comes next (docs/025).
    this.events.once(Phaser.Scenes.Events.SHUTDOWN, () => {
      this.audioManager.destroy();
      this.levelScript?.destroy();
      this.background.destroy(); // frees the bg's extracted canvas (docs/056)
    });
  }

  /** (Re)starts the replay from move 0 - shared by create() and R. */
  private startReplay(): void {
    for (const animator of this.animators.values()) {
      animator.destroy();
    }
    this.animators.clear();

    this.levelScript?.destroy();
    this.levelScript = null;
    this.scriptGeneration += 1;
    const generation = this.scriptGeneration;
    this.audioManager.reset();

    this.engine = new GameEngine(this.levelData);
    this.moveIndex = 0;
    this.gameOver = false;
    this.statusText.setText(`Replay - Esc = ${this.escLabel()}, R = restart replay`);

    const initialRenderModels = this.engine.getRenderModels();

    for (const model of initialRenderModels) {
      const levelModel = this.levelData.models[model.index];
      const initial = resolveInitialFrame(levelModel);
      if (!initial) continue;

      const isFish = isFishKind(model.kind);
      const bodySprite = this.add
        .image(model.x * GRID_SCALE, model.y * GRID_SCALE, initial.atlasKey, initial.frame)
        .setOrigin(0, 0);
      const headSprite = isFish
        ? this.add
            .image(model.x * GRID_SCALE, model.y * GRID_SCALE, initial.atlasKey, initial.frame)
            .setOrigin(0, 0)
            .setVisible(false)
        : undefined;

      const animator = new ModelAnimator(
        this,
        levelModel.anims,
        bodySprite,
        isFish,
        headSprite,
        model.x,
        model.y,
        model.isLeft,
      );
      // Start hidden if prog_init left it invisible (party2's limbs), matching
      // LevelScene - see docs/058.
      animator.sync(model, null, false, null, levelModel.initialEffect);
      this.animators.set(model.index, animator);
    }

    // Fire-and-forget, same reasoning as LevelScene.startEngine(): music
    // commands need the live Lua engine, but nothing here blocks on it.
    createLevelScript(
      this.levelData.levelName,
      initialRenderModels,
      generation,
      undefined,
      this.engineControl,
      this.depth,
    )
      .then((script) => {
        if (generation !== this.scriptGeneration) {
          script.destroy();
          return;
        }
        this.levelScript = script;
      })
      .catch((error: unknown) => {
        console.error(
          `Failed to load level script for "${this.levelData.levelName}"`,
          error,
        );
      });

    // Fresh shared clock for this run/restart (docs/046).
    this.roundClock = 0;
    this.moving = false;
    this.moveDurationMs = CYCLE_MS;
    this.cyclesThisRound = 1;
    this.roundsActive = true;

    this.stepText.setText(`0 / ${this.moves.length}`);
    // User's requirement: replay starts playing at normal speed immediately,
    // not paused waiting for input - unlike the original (no controls at
    // all) or a "paused until you press play" default.
    this.setPlayState("play");
  }

  private createControls(): void {
    const roomWidthPx = this.levelData.roomWidth * GRID_SCALE;
    const roomHeightPx = this.levelData.roomHeight * GRID_SCALE;
    const y = roomHeightPx - 8;

    const specs: Array<{ key: PlayState | "step"; symbol: string; onClick: () => void }> = [
      { key: "paused", symbol: "⏸", onClick: () => this.setPlayState("paused") }, // pause
      { key: "step", symbol: "⏭", onClick: () => this.step() }, // step forward
      { key: "play", symbol: "▶", onClick: () => this.setPlayState("play") }, // play
      { key: "fast", symbol: "⏩", onClick: () => this.setPlayState("fast") }, // fast-forward
    ];

    const spacing = 44;
    const startX = roomWidthPx / 2 - (spacing * (specs.length - 1)) / 2;
    specs.forEach((spec, i) => {
      const button = this.add
        .text(startX + i * spacing, y, spec.symbol, crispText({
          fontFamily: "sans-serif",
          fontSize: "20px",
          color: "#ffffff",
          backgroundColor: "#000000a0",
          padding: { x: 8, y: 6 },
        }))
        .setOrigin(0.5, 1)
        .setDepth(1000)
        .setInteractive({ useHandCursor: true })
        .on("pointerdown", spec.onClick);
      this.controlButtons.set(spec.key, button);
    });
  }

  private updateButtonHighlight(): void {
    for (const [key, button] of this.controlButtons) {
      button.setBackgroundColor(key === this.playState ? "#2a7a3aff" : "#000000a0");
    }
  }

  private setPlayState(state: PlayState): void {
    this.playState = state;
    this.updateButtonHighlight();
  }

  /** Advances exactly one round and pauses (snap, no slide) - matches standard
   *  media-player "step". */
  private step(): void {
    this.setPlayState("paused");
    this.tick();
    this.roundClock = 0; // freeze the render at the new committed positions
  }

  /**
   * Per-frame render + round driver - the same shared phase-locked clock as
   * LevelScene (docs/046). The media buttons become a speed factor on the
   * clock: play = 1×, fast = FAST_MULTIPLIER×, pause = frozen.
   */
  update(time: number, delta: number): void {
    this.background.update(time); // background ripple, docs/056
    if (!this.roundsActive) return;
    const factor = this.playState === "fast" ? FAST_MULTIPLIER : this.playState === "play" ? 1 : 0;
    if (factor > 0) this.roundClock += delta * factor;

    const progress = this.moving ? Math.min(1, this.roundClock / this.moveDurationMs) : 1;
    for (const animator of this.animators.values()) animator.render(progress);
    // Ropes anchor to the models' just-rendered screen positions. The original
    // holds decors on the Room's View (Room::addDecor -> m_view->addDecor), and
    // replay drives that very same Room via Room::loadMove() - so the elevator
    // cables are drawn in replay exactly as in play. See docs/055.
    this.drawRopes();

    if (factor > 0) {
      const interval = this.moving ? this.moveDurationMs : IDLE_ROUND_MS;
      if (this.roundClock >= interval) {
        this.roundClock -= interval;
        this.tick();
      }
    }
  }

  /** Elevator cables - see drawRopeDecors() (shared with LevelScene). */
  private drawRopes(): void {
    const decors = this.levelScript?.getRopeDecors();
    if (!decors?.length) return;
    if (!this.ropeGraphics) {
      // Above the models (View::drawOn draws decors last), below the UI.
      this.ropeGraphics = this.add.graphics().setDepth(5);
    }
    drawRopeDecors(this.ropeGraphics, decors, this.animators);
  }

  private tick(): void {
    if (this.gameOver) return;
    const cyclesElapsed = this.cyclesThisRound;

    const symbol = this.moveIndex < this.moves.length ? this.moves[this.moveIndex] : null;
    if (this.engine.tickReplay(symbol)) {
      this.moveIndex++;
    }

    const renderModels = this.engine.getRenderModels();
    // Still runs every round - level-specific music commands (including
    // mid-level stops like viking1's musician gag) come from here, even
    // though every *other* output (subtitles, item anim, SFX/dialog voice)
    // is deliberately ignored below - see this class's own doc comment.
    this.levelScript?.tick(renderModels, cyclesElapsed);

    void this.audioManager.applyMusicCommand(this.levelScript?.getMusicCommand() ?? null);
    // Drain and discard - no sound effects/dialog voice during replay, but
    // still needs draining so the underlying array doesn't grow unbounded
    // over a long (2000+ move) replay.
    this.levelScript?.getPendingSoundEffects();

    for (const model of renderModels) {
      // scriptAnim always null: items never show their Lua-driven
      // decorative animation during replay, only their real physics
      // position (still applied via sync() - unavoidable and expected,
      // that's the puzzle actually being solved). Fish are unaffected -
      // they never read scriptAnim at all (docs/009/013). The init-time
      // effect is kept, though (not the live one) - so a model hidden at
      // init stays hidden rather than flashing in (party2's limbs, docs/058).
      const effect = isFishKind(model.kind) ? null : this.levelData.models[model.index].initialEffect;
      this.animators.get(model.index)?.sync(model, null, false, null, effect);
    }

    this.updateRoundPacing(renderModels);

    this.stepText.setText(`${this.moveIndex} / ${this.moves.length}`);

    if (this.engine.isSolved()) {
      this.gameOver = true;
      // Faithful to the original: a Pedometer (worldmap) replay of a world-final
      // level ends in its recap poster before returning to the map (docs/050).
      // An in-level (P) replay stays a review tool - pause and wait for Esc/R.
      if (this.returnTo === "worldmap" && this.poster) {
        // Watching a final level's replay ends in its poster, then the map
        // presents the ending - same as finishing it live (fromFinal). If this
        // WAS the ending's replay, endingDone stops it re-presenting (docs/061).
        this.scene.start("demo", {
          demoFile: this.poster,
          levelName: this.levelData.levelName,
          mode: "poster",
          returnTo: "worldmap",
          returnData: { fromLevel: true, fromFinal: true, endingDone: this.isEnding },
        });
        return;
      }
      this.setPlayState("paused");
      this.statusText.setText(`Solved! (R = restart replay, Esc = ${this.escLabel()})`);
    } else if (!this.engine.isSolvable()) {
      this.gameOver = true;
      this.setPlayState("paused");
      this.statusText.setText(`Replay ended - a fish died. (R = restart, Esc = ${this.escLabel()})`);
    } else if (this.moveIndex >= this.moves.length && this.engine.room.isFresh()) {
      // Ran out of recorded moves without solving - stop instead of
      // spinning the timer forever with nothing left to consume.
      this.setPlayState("paused");
    }
  }

  /** Current round's phase-locked pacing - same as LevelScene.updateRoundPacing
   *  (docs/046, refined docs/049: pure falls run at 1 cycle/cell). */
  private updateRoundPacing(renderModels: RenderModel[]): void {
    this.moving = this.engine.anyModelMoving();
    if (!this.moving) {
      this.cyclesThisRound = 1;
      this.moveDurationMs = CYCLE_MS;
      return;
    }
    const phases = roundPhases(this.engine.getActiveInfo(), renderModels, this.levelData.models);
    this.cyclesThisRound = phases;
    this.moveDurationMs = phases * CYCLE_MS;
  }

  private escLabel(): string {
    return this.returnTo === "worldmap" ? "back to map" : "back to level";
  }
}
