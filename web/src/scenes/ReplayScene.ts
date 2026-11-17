import Phaser from "phaser";

import { GRID_SCALE, type LevelData } from "../lua/levelLoader";
import { createLevelScript, type LevelScript, type EngineControl } from "../lua/levelScript";
import { GameEngine, type RenderModel } from "../game/GameEngine";
import { CYCLE_MS, IDLE_ROUND_MS } from "../game/timing";
import { ModelAnimator, collectAtlasKeys, preloadAtlases, roundPhases } from "./ModelAnimator";
import { AudioManager } from "./AudioManager";
import {
  applyRenderScale,
  crispText,
  drawRopeDecors,
  isFishKind,
  resolveInitialFrame,
  setRenderScaleLocked,
} from "./sceneUtils";
import { WavyBackground } from "./WavyBackground";
import { IconButton, type IconKind } from "./IconButton";
import {
  EXPORT_FPS,
  LEAD_IN_MS,
  MusicCommandLog,
  TAIL_MS,
  computeVideoLayout,
  createVideoWriter,
  detectCodecs,
  downloadBlob,
  estimateReplayMs,
  renderMusicTrack,
  yieldToEventLoop,
  type MusicSegment,
  type VideoWriter,
} from "./replayExport";
import { ReplayExportDialog, type ReplayExportOptions } from "./ReplayExportDialog";
import { t } from "../i18n";

type PlayState = "paused" | "play" | "fast";

/** How much faster "fast" is than normal-speed replay - a plain guess at a
 *  good default (see docs/025's "Open for next time"), not measured
 *  against anything yet. Easy to retune. */
const FAST_MULTIPLIER = 6;

/** Settled rounds after the last move before replay stops (docs/081). */
const IDLE_ROUNDS_BEFORE_STOP = 3;

/** One video frame of the MP4 export (docs/078). */
const FRAME_MS = 1000 / EXPORT_FPS;
/** Depth of the export's black letterbox masks - below the step counter (1000). */
const MASK_DEPTH = 999;

type ReplayInitData = {
  levelData: LevelData;
  moves: string;
  returnTo?: "level" | "worldmap";
  poster?: string | null;
  depth?: number;
  /** True when replaying the ending itself, so its post-poster return doesn't
   *  make the map re-present the ending (docs/061). */
  isEnding?: boolean;
};

export interface ReplayExportResult {
  blob: Blob;
  width: number;
  height: number;
  frames: number;
  durationSec: number;
  audio: "aac" | "opus" | null;
  filename: string;
  /** The recorded music timeline (seconds on the video clock), for checks. */
  musicSegments: MusicSegment[];
}

/** State of a running video export ("record mode", docs/078). */
interface Recording {
  log: MusicCommandLog;
  /** Video time (s) of the frame being stepped - stamps the music commands. */
  timeSec: number;
  frame: number;
  /** Frame where the replay ended (solved/died/out of moves); the tail follows. */
  endFrame: number | null;
  aborted: boolean;
  /** The real Math.random (a seeded one stands in during each export game.step). */
  nativeRandom: () => number;
}

/** Thrown by runExport() on Cancel / scene shutdown. */
function exportCancelled(): Error {
  const error = new Error("Video export cancelled.");
  error.name = "AbortError";
  return error;
}

/** mulberry32 - a fixed-seed Math.random stand-in so the fish idle animations
 *  (ModelAnimator's random blink/talk picks) come out the same in every export. */
function seededRandom(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let x = a;
    x = Math.imul(x ^ (x >>> 15), x | 1);
    x ^= x + Math.imul(x ^ (x >>> 7), x | 61);
    return ((x ^ (x >>> 14)) >>> 0) / 4294967296;
  };
}

/** "1 min 5 s" / "42 s" - same as the export dialog's duration text. */
function formatDuration(ms: number): string {
  const total = Math.max(1, Math.round(ms / 1000));
  if (total < 60) return t("export_sec", total);
  return t("export_min_sec", Math.floor(total / 60), total % 60);
}

/** Runs each cleanup step on its own, so one failure can't skip the rest
 *  (above all game.loop.wake() - a sleeping loop freezes the whole game). */
function safely(step: () => void): void {
  try {
    step();
  } catch (error) {
    console.error("Video export cleanup step failed", error);
  }
}

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
  /** A death / fully-out model (engine.takePlanInterrupt()) whose plan-FIFO
   *  half is still owed to the live script - applied before its next tick(),
   *  as in LevelScene (docs/082). Matters for music/gags only; the engine
   *  already did checkActive + unBusyUnits. */
  private pendingPlanInterrupt = false;
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
  /** Consecutive settled, unbusy rounds after the last move - see outOfMoves(). */
  private idleRoundsAfterMoves = 0;

  // Shared phase-locked animation clock (docs/046) - same model as LevelScene.
  private roundsActive = false;
  private roundClock = 0;
  private moving = false;
  private moveDurationMs = CYCLE_MS;
  private cyclesThisRound = 1;
  private playState: PlayState = "paused";
  private controlButtons = new Map<PlayState | "step" | "export", IconButton>();

  private stepText!: Phaser.GameObjects.Text;
  private statusText!: Phaser.GameObjects.Text;

  /** This entry's init data - the video export restarts the scene with it. */
  private initData!: ReplayInitData;
  /** Bumped per init(), so an export dialog still awaiting codec detection
   *  can tell the scene was left/re-entered meanwhile. */
  private entryId = 0;
  /** ⬇ clicked: codec probe or the export dialog is in progress. */
  private exportUiBusy = false;
  /** Non-null while a video export renders (record mode, docs/078). */
  private recording: Recording | null = null;

  constructor() {
    super("replay");
  }

  init(data: ReplayInitData): void {
    this.initData = data;
    this.entryId += 1;
    this.exportUiBusy = false;
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
    // Replay shows the background still (amp 0 = WavyBackground's plain-image
    // path, no shader): the ripple made every exported frame differ and cost
    // ~40 % of the MP4 size (docs/079).
    this.background = new WavyBackground(
      this,
      this.levelData.levelName,
      this.levelData.bgPicture,
      this.levelData.roomWidth * GRID_SCALE,
      this.levelData.roomHeight * GRID_SCALE,
      { ...this.levelData.waves, amp: 0 },
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
      .text(8, 8, this.hintText(), crispText({
        fontFamily: "sans-serif",
        fontSize: "16px",
        color: "#ffffff",
        backgroundColor: "#000000a0",
        padding: { x: 6, y: 4 },
      }))
      .setDepth(1000);

    // The export steps the game itself (game.step), which still dispatches
    // queued key events - so R/Esc must be ignored while recording.
    this.input.keyboard!.on("keydown-R", () => {
      if (!this.recording) void this.startReplay();
    });
    this.input.keyboard!.on("keydown-ESC", () => {
      if (this.recording) return;
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
    void this.startReplay();

    // The Sound Manager is game-global, not scene-scoped - stop this
    // scene's music explicitly on the way out so it doesn't keep playing
    // underneath whichever scene comes next (docs/025).
    this.events.once(Phaser.Scenes.Events.SHUTDOWN, () => {
      this.audioManager.destroy();
      this.levelScript?.destroy();
      this.background.destroy(); // frees the bg's extracted canvas (docs/056)
    });
  }

  /** (Re)starts the replay from move 0 - shared by create(), R and the video
   *  export. The promise resolves once the live Lua engine is in place (or its
   *  load failed / was superseded - then levelScript stays null = no music);
   *  only the export waits for it (docs/078). */
  private startReplay(): Promise<void> {
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
    this.pendingPlanInterrupt = false;
    this.moveIndex = 0;
    this.gameOver = false;
    this.idleRoundsAfterMoves = 0;
    this.statusText.setText(this.hintText());

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
    const scriptReady = createLevelScript(
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
    return scriptReady;
  }

  private createControls(): void {
    const roomWidthPx = this.levelData.roomWidth * GRID_SCALE;
    const roomHeightPx = this.levelData.roomHeight * GRID_SCALE;
    const y = roomHeightPx - 8 - IconButton.HEIGHT / 2; // bottom edge 8 px above the room's

    const specs: Array<{ key: PlayState | "step" | "export"; icon: IconKind; onClick: () => void }> = [
      { key: "paused", icon: "pause", onClick: () => this.setPlayState("paused") },
      { key: "step", icon: "step", onClick: () => this.step() },
      { key: "play", icon: "play", onClick: () => this.setPlayState("play") },
      { key: "fast", icon: "fast", onClick: () => this.setPlayState("fast") },
      { key: "export", icon: "export", onClick: () => void this.openExportDialog() }, // MP4 export (docs/078)
    ];

    const spacing = 44;
    const startX = roomWidthPx / 2 - (spacing * (specs.length - 1)) / 2;
    specs.forEach((spec, i) => {
      const button = new IconButton(this, startX + i * spacing, y, spec.icon, spec.onClick).setDepth(1000);
      this.controlButtons.set(spec.key, button);
    });
  }

  private updateButtonHighlight(): void {
    for (const [key, button] of this.controlButtons) {
      button.setSelected(key === this.playState);
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
  update(_time: number, delta: number): void {
    // No background update: the replay background is still (docs/079).
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
      // Epsilon: the export's 3 × 33.33 ms frames must fire exactly one 100 ms
      // cycle (float sums land just below 100) - docs/078.
      if (this.roundClock >= interval - 1e-6) {
        this.roundClock = Math.max(0, this.roundClock - interval);
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
    // legacy Room::prepareRound/fallout -> interruptPlan, as in LevelScene
    // (docs/082). Kept pending while the script is still booting.
    if (this.engine.takePlanInterrupt()) this.pendingPlanInterrupt = true;
    if (this.pendingPlanInterrupt && this.levelScript) {
      this.pendingPlanInterrupt = false;
      this.levelScript.interruptPlan();
    }

    const renderModels = this.engine.getRenderModels();
    // Still runs every round - level-specific music commands (including
    // mid-level stops like viking1's musician gag) come from here, even
    // though every *other* output (subtitles, item anim, SFX/dialog voice)
    // is deliberately ignored below - see this class's own doc comment.
    const recording = this.recording;
    if (recording) {
      // The live Lua's own JS-side random picks (sound variants) get the real
      // Math.random, so they can't shift the export's seeded fish-anim sequence.
      const seeded = Math.random;
      Math.random = recording.nativeRandom;
      try {
        this.levelScript?.tick(renderModels, cyclesElapsed);
      } finally {
        Math.random = seeded;
      }
    } else {
      this.levelScript?.tick(renderModels, cyclesElapsed);
    }

    const musicCommand = this.levelScript?.getMusicCommand() ?? null;
    // Exporting: no live music - log the command on the video clock instead;
    // the soundtrack is rendered offline afterwards (docs/078).
    if (recording) recording.log.record(musicCommand, recording.timeSec);
    else void this.audioManager.applyMusicCommand(musicCommand);
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

    if (recording) {
      // Exporting: no poster, no pause, no status line - mark the end, the
      // exporter records the tail and stops. Playback keeps running, so the
      // last move still slides to completion on screen.
      const over = this.engine.isSolved() || !this.engine.isSolvable();
      if (over) this.gameOver = true;
      if (over || this.outOfMoves()) {
        recording.endFrame ??= recording.frame;
      }
      return;
    }

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
      this.statusText.setText(t("replay_solved", this.escLabel(), t("replay_restart")));
    } else if (!this.engine.isSolvable()) {
      this.gameOver = true;
      this.setPlayState("paused");
      this.statusText.setText(t("replay_died", this.escLabel(), t("replay_restart")));
    } else if (this.outOfMoves()) {
      // Ran out of recorded moves without solving - stop instead of
      // spinning the timer forever with nothing left to consume.
      this.setPlayState("paused");
    }
  }

  /** All moves played and the room settled - unless the level's Lua still
   *  holds a model busy: legacy replay mode (LevelLoading m_replayMode) keeps
   *  running plain rounds after the last move, so e.g. cannons' small fish,
   *  parked at the exit for a line of dialog, swims out once released
   *  (docs/081). Stopping on the first settled round froze it at the edge. */
  private outOfMoves(): boolean {
    const idle = this.moveIndex >= this.moves.length && this.engine.room.isFresh() && !this.engine.anyModelBusy();
    // A model released on this very round (cannons' planBusy) only starts
    // going out on the next beginFall() - so stop after a few settled rounds,
    // not on the first.
    this.idleRoundsAfterMoves = idle ? this.idleRoundsAfterMoves + 1 : 0;
    return this.idleRoundsAfterMoves >= IDLE_ROUNDS_BEFORE_STOP;
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
    const phases = roundPhases(this.engine.getActiveInfo(), renderModels, this.levelData.models, this.engine.wentOutThisRound());
    this.cyclesThisRound = phases;
    this.moveDurationMs = phases * CYCLE_MS;
  }

  private escLabel(): string {
    return t(this.returnTo === "worldmap" ? "nav_worldmap" : "nav_level");
  }

  /** The persistent replay hint line: "Esc = <target> · R = restart replay". */
  private hintText(): string {
    return t("replay_hint", this.escLabel(), t("replay_restart"));
  }

  /** ⬇: pause, probe the H.264/audio encoders, then ask (docs/078). */
  private async openExportDialog(): Promise<void> {
    if (this.exportUiBusy || this.recording) return;
    this.exportUiBusy = true;
    const entry = this.entryId;
    this.setPlayState("paused");
    const roomW = this.levelData.roomWidth * GRID_SCALE;
    const roomH = this.levelData.roomHeight * GRID_SCALE;
    const layout = computeVideoLayout(roomW, roomH);
    const codecs = await detectCodecs(layout);
    if (entry !== this.entryId || !this.sys.isActive()) return; // left meanwhile
    const estimateMs = estimateReplayMs(this.levelData, this.moves) + LEAD_IN_MS + TAIL_MS;
    new ReplayExportDialog(
      this,
      roomW,
      roomH,
      { estimateMs, layout, codecs, moveCount: this.moves.length },
      (opts) => {
        this.exportUiBusy = false;
        void this.exportAndDownload(opts);
      },
      () => {
        this.exportUiBusy = false;
      },
    ).show();
  }

  /** The UI path: export, then download (runExport alone never downloads -
   *  the e2e suite calls it directly). Cancel is silent; a failure shows why. */
  private async exportAndDownload(opts: ReplayExportOptions): Promise<void> {
    try {
      const result = await this.runExport(opts);
      downloadBlob(result.blob, result.filename);
      showExportNotice(t("export_done", result.filename), false);
    } catch (error) {
      if (error instanceof Error && error.name === "AbortError") return;
      console.error("Video export failed", error);
      showExportNotice(t("export_failed", error instanceof Error ? error.message : String(error)), true);
    }
  }

  /**
   * Renders the replay to an MP4 (docs/078): a fresh run, stepped offline at a
   * fixed 1/30 s (`game.loop.sleep()` + `game.step()`), each frame grabbed from
   * the canvas right after it renders. Room at EXPORT_ZOOM, centred in a 16:9
   * frame with black bars; a 1 s still lead-in, the run, a 2 s tail. Music is
   * logged on the video clock and rendered offline afterwards. The canvas is
   * hidden behind a DOM progress panel meanwhile. Always ends by restarting
   * the scene into its normal state (if still active). Rejects with
   * name "AbortError" on Cancel/Esc or when the scene is left mid-export.
   * Public: also the e2e hook (`maxFrames` caps the length there).
   */
  async runExport(opts: ReplayExportOptions & { maxFrames?: number }): Promise<ReplayExportResult> {
    if (this.recording) throw new Error("A video export is already running.");
    if (!this.sys.isActive()) throw new Error("The replay scene is not running.");
    const game = this.game;
    const canvas = game.canvas;
    const roomW = this.levelData.roomWidth * GRID_SCALE;
    const roomH = this.levelData.roomHeight * GRID_SCALE;
    const layout = computeVideoLayout(roomW, roomH);
    const filename = `${this.levelData.levelName}-replay.mp4`;

    const rec: Recording = {
      log: new MusicCommandLog(),
      timeSec: 0,
      frame: 0,
      endFrame: null,
      aborted: false,
      nativeRandom: Math.random,
    };
    this.recording = rec;
    const abort = (): void => {
      rec.aborted = true;
    };
    const checkAbort = (): void => {
      if (rec.aborted) throw exportCancelled();
    };
    // Browser Back (navigation.ts) stops this scene synchronously, i.e. during
    // one of the awaits below - every await is followed by checkAbort().
    this.events.once(Phaser.Scenes.Events.SHUTDOWN, abort);
    const onKey = (e: KeyboardEvent): void => {
      if (e.key !== "Escape") return;
      e.preventDefault();
      if (!e.repeat) abort();
    };
    window.addEventListener("keydown", onKey, true);

    const panel = new ExportProgressPanel(abort);
    // Hidden and out of the page flow: it keeps rendering at video size, but
    // the raw frames never show and the page layout doesn't jump.
    const canvasStyle = {
      visibility: canvas.style.visibility,
      position: canvas.style.position,
      left: canvas.style.left,
      top: canvas.style.top,
    };
    Object.assign(canvas.style, { visibility: "hidden", position: "fixed", left: "0px", top: "0px" });
    setRenderScaleLocked(true); // a fullscreen toggle must not resize the canvas now

    const cam = this.cameras.main;
    const camState = {
      x: cam.x,
      y: cam.y,
      width: cam.width,
      height: cam.height,
      zoom: cam.zoom,
      scrollX: cam.scrollX,
      scrollY: cam.scrollY,
      originX: cam.originX,
      originY: cam.originY,
      background: cam.backgroundColor.rgba,
    };
    // Tweens (the fish exit fade) run on Date.now(), not on the step delta -
    // pin them to the video clock while recording.
    const tweenClock = this.tweens as unknown as { getDelta?: () => number };
    const masks: Phaser.GameObjects.Rectangle[] = [];
    let writer: VideoWriter | null = null;
    let finished = false;

    try {
      const codecs = await detectCodecs(layout);
      checkAbort();
      if (!codecs.video) throw new Error(t("export_unsupported"));
      const withAudio = opts.music && codecs.audio !== null;
      const estimateMs = estimateReplayMs(this.levelData, this.moves) + LEAD_IN_MS + TAIL_MS;
      const estimatedFrames = Math.ceil((estimateMs / 1000) * EXPORT_FPS);
      // The estimate ignores windoze's live-Lua hooks - cap generously only.
      const frameCap = opts.maxFrames ?? Math.max(2 * estimatedFrames, estimatedFrames + 60 * EXPORT_FPS);
      const progressTotal = Math.min(estimatedFrames, frameCap);

      // From here on only the export steps the game: nothing (fish anim
      // timers included) advances on wall time while the Lua engine loads.
      game.loop.sleep();
      // A fresh run (its audio reset also stops the live music), held still
      // for the lead-in; controls and hint line stay out of the video.
      const ready = this.startReplay();
      this.setPlayState("paused");
      for (const button of this.controlButtons.values()) button.setVisible(false);
      this.statusText.setVisible(false);
      this.stepText.setVisible(opts.showSteps);
      await ready;
      checkAbort();
      // The level's opening music command is already pending - start it with the video.
      rec.log.record(this.levelScript?.getMusicCommand() ?? null, 0);

      tweenClock.getDelta = () => FRAME_MS;

      // Letterbox: video-size framebuffer, room at EXPORT_ZOOM centred via
      // camera scroll, black camera background + four black masks around the
      // room (a camera viewport alone leaves stale pixels in the bars).
      const scale = this.scale;
      scale.scaleMode = Phaser.Scale.NONE;
      scale.displaySize.setAspectMode(Phaser.Scale.NONE);
      scale.resize(layout.width, layout.height);
      scale.setZoom(1);
      cam
        .setViewport(0, 0, layout.width, layout.height)
        .setOrigin(0, 0)
        .setZoom(layout.zoom)
        .setScroll(-layout.offsetX / layout.zoom, -layout.offsetY / layout.zoom)
        .setBackgroundColor("#000000");
      const m = Math.max(layout.width, layout.height); // world px - well past the frame edge
      const mask = (x: number, y: number, w: number, h: number): void => {
        masks.push(this.add.rectangle(x, y, w, h, 0x000000, 1).setOrigin(0, 0).setDepth(MASK_DEPTH));
      };
      mask(-m, -m, m, roomH + 2 * m);
      mask(roomW, -m, m, roomH + 2 * m);
      mask(0, -m, roomW, m);
      mask(0, roomH, roomW, m);

      writer = await createVideoWriter(canvas, layout, codecs, withAudio);
      checkAbort();

      const leadFrames = Math.round(LEAD_IN_MS / FRAME_MS);
      const tailFrames = Math.round(TAIL_MS / FRAME_MS);
      // Step times are whole ms (t0 + round(i·33.33)), so time differences are
      // exact - ModelAnimator's `time.now > expiry` checks then can't flip on
      // float rounding that depends on t0 (the deltas stay exactly 1/30 s).
      const t0 = Math.ceil(performance.now());
      const started = performance.now();
      // Seeded only inside game.step(), so async work between frames (encoder
      // callbacks etc.) can't consume the fish-anim random sequence.
      const random = seededRandom(0x5eed);
      let frames = 0;
      while (frames < frameCap) {
        const i = frames;
        if (i === leadFrames) this.setPlayState("play");
        rec.frame = i;
        rec.timeSec = i / EXPORT_FPS;
        Math.random = random;
        try {
          game.step(t0 + Math.round(((i + 1) * 1000) / EXPORT_FPS), FRAME_MS);
        } finally {
          Math.random = rec.nativeRandom;
        }
        checkAbort();
        if (canvas.width !== layout.width || canvas.height !== layout.height) {
          throw new Error("The game canvas was resized during the export.");
        }
        await writer.addFrame(i / EXPORT_FPS, 1 / EXPORT_FPS); // grabs the canvas now
        frames++;
        if (rec.endFrame !== null && i >= rec.endFrame + tailFrames) break;
        if (frames % 10 === 0) panel.setProgress(frames, progressTotal, performance.now() - started);
        await yieldToEventLoop(); // keeps the panel + Cancel responsive
        checkAbort();
      }

      panel.setFinalizing();
      const durationSec = frames / EXPORT_FPS;
      const musicSegments = rec.log.finish(durationSec);
      if (withAudio) {
        const audio = await renderMusicTrack(musicSegments, durationSec, (track) =>
          this.audioManager.getMusicBuffer(track),
        );
        checkAbort();
        await writer.addAudio(audio);
      }
      let blob: Blob;
      try {
        blob = await writer.finish();
      } catch (error) {
        // An audio encoder that fails only now (despite the trial encode,
        // docs/080) surfaces here - point the player at "export without music".
        if (!withAudio) throw error;
        const message = error instanceof Error ? error.message : String(error);
        throw new Error(t("export_audio_failed", message));
      }
      finished = true;
      return {
        blob,
        width: layout.width,
        height: layout.height,
        frames,
        durationSec,
        audio: withAudio ? codecs.audio : null,
        filename,
        musicSegments,
      };
    } catch (error) {
      if (writer && !finished) {
        await writer.cancel().catch((e: unknown) => console.warn("Video export: cancel failed", e));
      }
      throw error;
    } finally {
      const active = this.sys.isActive();
      this.recording = null;
      safely(() => {
        Math.random = rec.nativeRandom;
      });
      safely(() => {
        delete tweenClock.getDelta;
      });
      this.events.off(Phaser.Scenes.Events.SHUTDOWN, abort);
      window.removeEventListener("keydown", onKey, true);
      for (const obj of masks) safely(() => obj.destroy());
      // After a shutdown the camera is gone and the next scene owns the canvas.
      if (active) {
        safely(() => {
          cam
            .setViewport(camState.x, camState.y, camState.width, camState.height)
            .setOrigin(camState.originX, camState.originY)
            .setZoom(camState.zoom)
            .setScroll(camState.scrollX, camState.scrollY)
            .setBackgroundColor(camState.background);
        });
      }
      setRenderScaleLocked(false);
      safely(() => game.loop.wake());
      // Back to the normal interactive replay; its create() re-applies the
      // render scale. Wait for it (still hidden) so the canvas never shows the
      // video-size framebuffer, and so a caller sees the restored scene.
      if (active) await this.restartAndWait();
      safely(() => Object.assign(canvas.style, canvasStyle));
      panel.remove();
    }
  }

  /** scene.restart() with this entry's init data; resolves after its create()
   *  (or a timeout, should the scene be left before it gets there). */
  private restartAndWait(): Promise<void> {
    return new Promise<void>((resolve) => {
      const done = (): void => {
        window.clearTimeout(timer);
        this.events.off(Phaser.Scenes.Events.CREATE, done);
        resolve();
      };
      const timer = window.setTimeout(done, 10_000);
      this.events.once(Phaser.Scenes.Events.CREATE, done);
      try {
        this.scene.restart(this.initData);
      } catch (error) {
        console.error("Video export: replay restart failed", error);
        done();
      }
    });
  }
}

const PANEL_STYLE: Partial<CSSStyleDeclaration> = {
  position: "fixed",
  left: "50%",
  top: "50%",
  transform: "translate(-50%, -50%)",
  zIndex: "10000", // above the fullscreen container (9999, fullscreen.ts)
  boxSizing: "border-box",
  width: "min(380px, calc(100vw - 32px))",
  padding: "18px 24px 20px",
  background: "#0a1f33",
  border: "2px solid #ffc618",
  color: "#ffffff",
  fontFamily: "sans-serif",
  fontSize: "14px",
  lineHeight: "1.4",
  textAlign: "center",
};

function element<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  style: Partial<CSSStyleDeclaration>,
  text = "",
): HTMLElementTagNameMap[K] {
  const el = document.createElement(tag);
  Object.assign(el.style, style);
  el.textContent = text;
  return el;
}

function panelButton(label: string, onClick: () => void): HTMLButtonElement {
  const button = element(
    "button",
    {
      marginTop: "14px",
      padding: "6px 22px",
      border: "none",
      background: "#2a5a8a",
      color: "#ffffff",
      fontFamily: "sans-serif",
      fontSize: "15px",
      cursor: "pointer",
    },
    label,
  );
  button.type = "button";
  button.addEventListener("click", onClick);
  return button;
}

function panelTitle(): HTMLDivElement {
  return element(
    "div",
    { color: "#ffc618", fontSize: "20px", fontWeight: "bold", marginBottom: "10px" },
    t("export_title"),
  );
}

/**
 * The export's progress UI, in the DOM rather than the canvas (anything drawn
 * in the canvas would end up in the video): percentage + bar, live ETA from the
 * measured frame rate, "keep this tab open", Cancel. docs/078.
 */
class ExportProgressPanel {
  private readonly root = element("div", PANEL_STYLE);
  private readonly status = element("div", { fontSize: "16px" }, t("export_progress", 0));
  private readonly fill = element("div", { width: "0%", height: "100%", background: "#ffc618" });
  private readonly eta = element("div", { color: "#cfe6ff", minHeight: "1.4em" });

  constructor(onCancel: () => void) {
    const track = element("div", {
      height: "10px",
      margin: "8px 0",
      background: "#1c3a57",
      border: "1px solid #2a5a8a",
    });
    track.appendChild(this.fill);
    this.root.append(
      panelTitle(),
      this.status,
      track,
      this.eta,
      element("div", { color: "#cfe6ff", fontSize: "13px", marginTop: "6px" }, t("export_keep_tab")),
      panelButton(t("export_cancel"), onCancel),
    );
    document.body.appendChild(this.root);
  }

  /** `total` is the estimate - held at 99 % until the run actually ends. */
  setProgress(done: number, total: number, elapsedMs: number): void {
    const percent = Math.min(99, Math.floor((done / Math.max(1, total)) * 100));
    this.status.textContent = t("export_progress", percent);
    this.fill.style.width = `${percent}%`;
    const remainingMs = (Math.max(0, total - done) * elapsedMs) / Math.max(1, done);
    this.eta.textContent = t("export_eta", formatDuration(remainingMs));
  }

  setFinalizing(): void {
    this.status.textContent = t("export_finalizing");
    this.fill.style.width = "100%";
    this.eta.textContent = "";
  }

  remove(): void {
    this.root.remove();
  }
}

/** After the UI-path export: "done" fades by itself, a failure waits for Close. */
function showExportNotice(message: string, isError: boolean): void {
  const root = element("div", PANEL_STYLE);
  const close = (): void => root.remove();
  root.append(panelTitle(), element("div", { color: isError ? "#ff9090" : "#ffffff" }, message));
  if (isError) root.appendChild(panelButton(t("export_close"), close));
  else window.setTimeout(close, 2500);
  document.body.appendChild(root);
}
