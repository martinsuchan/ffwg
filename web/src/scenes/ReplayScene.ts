import Phaser from "phaser";

import { GRID_SCALE, type LevelData } from "../lua/levelLoader";
import { createLevelScript, type LevelScript, type EngineControl } from "../lua/levelScript";
import { GameEngine } from "../game/GameEngine";
import { ROUND_MS } from "../game/timing";
import { ModelAnimator, preloadModelFrames } from "./ModelAnimator";
import { AudioManager } from "./AudioManager";
import { pictureToAssetUrl, isFishKind, resolveInitialTextureKey } from "./sceneUtils";

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

  private engine!: GameEngine;
  private animators = new Map<number, ModelAnimator>();
  private audioManager!: AudioManager;
  private levelScript: LevelScript | null = null;
  /** windoze's live-Lua control-swap/fast-fall hook, so the replay's engine
   *  tracks busy/fast-falling exactly as interactive play did (docs/035).
   *  Closes over `this` so a replay restart swapping `this.engine` stays
   *  consistent. No-op for every other level. */
  private readonly engineControl: EngineControl = {
    setBusy: (index, busy) => this.engine.setBusy(index, busy),
    checkActive: () => this.engine.checkActive(),
    setFastFalling: (value) => this.engine.setFastFalling(value),
  };
  /** Guards against a superseded startReplay()'s createLevelScript() call
   *  resolving after a newer restart already happened - same pattern as
   *  LevelScene.scriptGeneration (docs/014). Also doubles as the
   *  restartCount createLevelScript() needs for level_getRestartCounter(). */
  private scriptGeneration = 0;
  private gameOver = false;

  private roundTimer: Phaser.Time.TimerEvent | null = null;
  private playState: PlayState = "paused";
  private controlButtons = new Map<PlayState | "step", Phaser.GameObjects.Text>();

  private stepText!: Phaser.GameObjects.Text;
  private statusText!: Phaser.GameObjects.Text;

  constructor() {
    super("replay");
  }

  init(data: { levelData: LevelData; moves: string; returnTo?: "level" | "worldmap" }): void {
    this.levelData = data.levelData;
    this.moves = data.moves;
    this.returnTo = data.returnTo ?? "level";
  }

  preload(): void {
    this.load.image(this.bgKey(), pictureToAssetUrl(this.levelData.bgPicture));
    this.levelData.models.forEach((model, index) => {
      preloadModelFrames(this, this.levelData.levelName, index, model.anims, pictureToAssetUrl);
    });
  }

  /** Level-scoped, not a bare "bg" - see LevelScene's identical helper
   *  (docs/028) for why. */
  private bgKey(): string {
    return `${this.levelData.levelName}-bg`;
  }

  create(): void {
    // Needed here too, not just LevelScene/WorldMapScene - the Pedometer's
    // "Replay" button launches this scene directly from the (differently-
    // sized) world map, without ever passing through LevelScene first. See
    // docs/029 for why .resize() (not .setGameSize()) is the right call.
    this.scale.resize(
      this.levelData.roomWidth * GRID_SCALE,
      this.levelData.roomHeight * GRID_SCALE,
    );
    this.add.image(0, 0, this.bgKey()).setOrigin(0, 0);

    const roomWidthPx = this.levelData.roomWidth * GRID_SCALE;

    this.stepText = this.add
      .text(roomWidthPx - 8, 8, "", {
        fontFamily: "sans-serif",
        fontSize: "16px",
        color: "#ffffff",
        backgroundColor: "#000000a0",
        padding: { x: 6, y: 4 },
      })
      .setOrigin(1, 0)
      .setDepth(1000);

    this.statusText = this.add
      .text(8, 8, `Replay - Esc = ${this.escLabel()}, R = restart replay`, {
        fontFamily: "sans-serif",
        fontSize: "16px",
        color: "#ffffff",
        backgroundColor: "#000000a0",
        padding: { x: 6, y: 4 },
      })
      .setDepth(1000);

    this.input.keyboard!.on("keydown-R", () => this.startReplay());
    this.input.keyboard!.on("keydown-ESC", () => {
      if (this.returnTo === "worldmap") this.scene.start("worldmap");
      else this.scene.start("level", { levelData: this.levelData });
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
      const initialKey = resolveInitialTextureKey(this.levelData.levelName, model.index, levelModel);
      if (!initialKey) continue;

      const isFish = isFishKind(model.kind);
      const bodySprite = this.add
        .image(model.x * GRID_SCALE, model.y * GRID_SCALE, initialKey)
        .setOrigin(0, 0);
      const headSprite = isFish
        ? this.add
            .image(model.x * GRID_SCALE, model.y * GRID_SCALE, initialKey)
            .setOrigin(0, 0)
            .setVisible(false)
        : undefined;

      const animator = new ModelAnimator(
        this,
        this.levelData.levelName,
        model.index,
        levelModel.anims,
        bodySprite,
        isFish,
        headSprite,
        model.x,
        model.y,
        model.isLeft,
      );
      animator.sync(model);
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
        .text(startX + i * spacing, y, spec.symbol, {
          fontFamily: "sans-serif",
          fontSize: "20px",
          color: "#ffffff",
          backgroundColor: "#000000a0",
          padding: { x: 8, y: 6 },
        })
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
    this.roundTimer?.remove();
    this.roundTimer = null;
    if (state !== "paused") {
      const delay = state === "fast" ? ROUND_MS / FAST_MULTIPLIER : ROUND_MS;
      this.roundTimer = this.time.addEvent({ delay, loop: true, callback: () => this.tick() });
    }
    this.updateButtonHighlight();
  }

  /** Advances exactly one round, pausing first if not already - matches
   *  standard media-player "step" behavior. */
  private step(): void {
    this.setPlayState("paused");
    this.tick();
  }

  private tick(): void {
    if (this.gameOver) return;

    const symbol = this.moveIndex < this.moves.length ? this.moves[this.moveIndex] : null;
    if (this.engine.tickReplay(symbol)) {
      this.moveIndex++;
    }

    const renderModels = this.engine.getRenderModels();
    // Still runs every round - level-specific music commands (including
    // mid-level stops like viking1's musician gag) come from here, even
    // though every *other* output (subtitles, item anim, SFX/dialog voice)
    // is deliberately ignored below - see this class's own doc comment.
    this.levelScript?.tick(renderModels);

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
      // they never read scriptAnim at all (docs/009/013).
      this.animators.get(model.index)?.sync(model, null);
    }

    this.stepText.setText(`${this.moveIndex} / ${this.moves.length}`);

    if (this.engine.isSolved()) {
      this.gameOver = true;
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

  private escLabel(): string {
    return this.returnTo === "worldmap" ? "back to map" : "back to level";
  }
}
