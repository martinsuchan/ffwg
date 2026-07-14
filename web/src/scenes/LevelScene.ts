import Phaser from "phaser";

import { GRID_SCALE, fetchLegacyFile, extractSavedMoves, type LevelData } from "../lua/levelLoader";
import {
  createLevelScript,
  levelSoundSpriteDirs,
  levelDialogVoiceDir,
  type LevelScript,
  type HostActions,
  type EngineControl,
} from "../lua/levelScript";
import { GameEngine, type RenderModel } from "../game/GameEngine";
import { V2 } from "../game/V2";
import { Weight } from "../game/Cube";
import { CYCLE_MS, IDLE_ROUND_MS } from "../game/timing";
import { ModelAnimator, collectAtlasKeys, preloadAtlases, roundPhases } from "./ModelAnimator";
import { AudioManager, type MusicCommand } from "./AudioManager";
import { isFishKind, resolveInitialFrame } from "./sceneUtils";
import { pictureToAtlas } from "./atlas";
import { SaveSlotUI } from "./SaveSlotUI";
import { HelpOverlay } from "./HelpOverlay";
import { SubtitleStack } from "./SubtitleStack";
import {
  loadSavedGames,
  addSavedGame,
  deleteSavedGame,
  saveTutorialGame,
  loadTutorialGame,
  loadSolvedMoves,
  saveSolvedMoves,
} from "../storage/levelStorage";
import { loadSettings } from "../storage/settingsStorage";
import { isSandboxMode } from "../game/appMode";

/** Keys that can drive a fish - the only ones worth buffering as a queued
 *  edge (see LevelScene.queuedKey). Space/R are already handled as
 *  immediate, un-queued actions, not through this movement path. */
const MOVE_KEYS = new Set([
  "ArrowUp",
  "ArrowDown",
  "ArrowLeft",
  "ArrowRight",
  "KeyW",
  "KeyA",
  "KeyS",
  "KeyD",
  "KeyI",
  "KeyJ",
  "KeyK",
  "KeyL",
]);

/** Rounds to linger on a solved room before returning to the world map -
 *  legacy's LevelCountDown::getCountForSolved(): the original counts down
 *  ~10 game cycles (own_updateState, ~timeinterval=100ms/cycle) once the
 *  room is solved, then quitState()s back to the map - or 30 cycles if a
 *  dialog is still running, so the player can finish reading/hearing it.
 *  Counted in fixed CYCLE_MS cycles now (the countdowns decrement by the
 *  round's `cyclesThisRound`), so the wall-clock delay is stable under the
 *  variable, phase-locked round interval (docs/046). */
const SOLVED_RETURN_ROUNDS = 10;
const SOLVED_RETURN_ROUNDS_DIALOG = 30;

/** Cycles to wait, once both fish can no longer move (dead/wedged), before the
 *  level auto-restarts - legacy LevelCountDown's getCountForWrong() (75 cycles)
 *  feeding action_restart(1) from Level::finishLevel(). Counted in fixed
 *  CYCLE_MS cycles (docs/046), like SOLVED_RETURN_ROUNDS. */
const WRONG_RESTART_ROUNDS = 75;

/** Step-counter colors - legacy StepDecor's COLOR_ORANGE (255,197,102) for a
 *  normal active fish and COLOR_BLUE (162,244,255) when the "powerful" (big)
 *  fish is active (Unit::isPowerful). */
const STEP_COLOR_NORMAL = "#ffc566";
const STEP_COLOR_POWERFUL = "#a2f4ff";

/**
 * Renders and plays a level's puzzle: background + every model, driven by
 * the real game-logic port (web/src/game/) on a fixed round tick, with
 * real fish body/head animation and item/fish position sliding on top
 * (web/src/scenes/ModelAnimator.ts - see docs/009). One fish is "active"
 * at a time (small fish first, legacy's default): arrow keys always drive
 * the active fish, WASD/IJKL drive the big/small fish directly (their real
 * legacy key bindings - see ModelFactory::createUnit) and silently make it
 * active, and Space explicitly switches active fish with a brief "greet"
 * pose (see docs/016, web/src/game/Controls.ts). Mouse: click a fish to
 * select it, click-and-hold the left button to path the active fish
 * around obstacles toward the cursor, hold the right button to push it
 * straight toward the cursor (see docs/017, web/src/game/MouseControl.ts).
 * R restarts. See docs/007 for the rules this plays by, docs/009 for the
 * animation system.
 */
export class LevelScene extends Phaser.Scene {
  private levelData!: LevelData;
  private engine!: GameEngine;
  private animators = new Map<number, ModelAnimator>();
  /** The room background image - swapped at runtime by game_changeBg()
   *  (corridor/rotate/steel), see applyBgChange() and docs/033. */
  private bgImage!: Phaser.GameObjects.Image;
  private statusText!: Phaser.GameObjects.Text;
  /** Stacking, colored, self-dismissing subtitles at the bottom of the screen -
   *  a port of the original's SubTitleAgent, replacing docs/015's single white
   *  line. Each speaker's color comes from dialog_addFont. See docs/037. */
  private subtitleStack!: SubtitleStack;
  private heldKeys = new Set<string>();
  /** One-shot queued keydown edge, drained by Controls.driving() at most
   *  once per round - see MOVE_KEYS and docs/019 "input reliability". Fixes
   *  a real bug: sampling only heldKeys at each round's tick() instant (pure
   *  level-triggered polling) let any keypress shorter than ~one round
   *  interval fall entirely between two polls and vanish, since a fast tap's
   *  keydown *and* keyup could both land inside that ~130ms gap. Legacy's
   *  Controls::controlEvent()/m_strokeSymbol avoids this by capturing the
   *  raw keydown edge itself, independent of round timing - this mirrors
   *  that design instead of only polling held state. */
  private queuedKey: string | null = null;
  private gameOver = false;
  /** This level's recap poster (a demo_poster.lua path), or null - only the 9
   *  world-final levels + the ending have one. Played after solving, before
   *  returning to the map (legacy Level::finishLevel -> createPoster, docs/050). */
  private poster: string | null = null;
  /** Countdown (in rounds) from a solved room to the auto-return to the
   *  world map - see SOLVED_RETURN_ROUNDS and tick(). -1 = not counting. */
  private solvedCountdown = -1;
  /** Countdown (in rounds) from "both fish can't move" (dead/wedged) to an
   *  automatic restart - legacy LevelCountDown's getCountForWrong path. -1 =
   *  not counting. See WRONG_RESTART_ROUNDS and tick(). */
  private wrongCountdown = -1;
  /** F1 controls popup (replaces the old always-on top-of-screen help). */
  private helpOverlay!: HelpOverlay;
  /** Item animation (docs/014) - null until the async Lua bootstrap for
   *  this play session resolves; tick()/ModelAnimator handle that gap by
   *  just not applying any override yet, not by waiting for it. */
  private levelScript: LevelScript | null = null;
  /** Guards against a superseded startEngine()'s createLevelScript() call
   *  resolving *after* a newer restart already happened - see startEngine(). */
  private scriptGeneration = 0;
  /** Music/sound-effect/dialog-voice playback - see docs/018, docs/043. */
  private audioManager!: AudioManager;
  /** Last background-music "play" command this level issued, so it can be
   *  re-applied when resuming from the briefcase movie (which stops it and
   *  plays its own) - null if the level has no music. See docs/031. */
  private lastMusicCommand: MusicCommand | null = null;
  /** Set by the level_newDemo host action (briefcase pushes the briefcase
   *  down); read after levelScript.tick() to launch the movie - see docs/031. */
  private pendingDemo: string | null = null;
  /** Callbacks the live Lua engine invokes for the briefcase level's special
   *  host functions - close over `this` so a restart swapping `this.engine`
   *  stays consistent. See docs/031. */
  private readonly hostActions: HostActions = {
    newDemo: (demoFile) => {
      this.pendingDemo = demoFile;
    },
    move: (symbol) => this.engine.showMove(symbol),
    save: () => this.demoSave(),
    load: () => this.demoLoad(),
    restart: () => this.demoRestart(),
  };
  /** The live Lua engine's only hook into physics - windoze's control-swap /
   *  fast-fall (docs/035). Closes over `this` so a restart swapping
   *  `this.engine` stays consistent. No-op for every other level. */
  private readonly engineControl: EngineControl = {
    setBusy: (index, busy) => this.engine.setBusy(index, busy),
    checkActive: () => this.engine.checkActive(),
    setFastFalling: (value) => this.engine.setFastFalling(value),
  };
  /** Guards keydown-P against double-firing while the reference-solution
   *  fetch for launchReplay() is still in flight - see docs/025. */
  private launchingReplay = false;
  // --- Shared phase-locked animation clock (docs/046) ---------------------
  /** True once startEngine() has built the engine + animators; gates update(). */
  private roundsActive = false;
  /** ms accumulated toward the current round's interval (advanced in update()). */
  private roundClock = 0;
  /** Did anything move the round just decided (fish/push/fall)? Drives the
   *  phase-locked slide vs. an idle one-cycle round. */
  private moving = false;
  /** Wall-clock length of the current round = `cyclesThisRound · CYCLE_MS`. */
  private moveDurationMs = CYCLE_MS;
  /** Fixed CYCLE_MS cycles the current round occupies (= phases when moving, 1
   *  when idle) - passed to levelScript.tick() so dialog/voice timing stays
   *  wall-clock-accurate under the variable round interval. */
  private cyclesThisRound = 1;
  /** Mid-level save slots, shown as a row of clickable dots - see docs/026. */
  private saveSlotUI!: SaveSlotUI;
  /** Transient save/load confirmation text (top-right) - mirrors the
   *  original's displaySaveStatus() on-screen flash. See docs/026. */
  private feedbackText!: Phaser.GameObjects.Text;
  private feedbackTimer?: Phaser.Time.TimerEvent;
  /** Always-on step counter, top-right corner - legacy StepDecor (font_console
   *  size 20, outlined; orange normally, blue when the powerful/big fish is
   *  active). The original gates it behind a show_steps toggle; here it's
   *  always visible per the user's request. */
  private stepCounterText!: Phaser.GameObjects.Text;

  constructor() {
    super("level");
  }

  /** Dynamic per-launch data (docs/027) - the world map picks a level at
   *  runtime, so `levelData` can no longer be known when this scene is
   *  constructed (it used to be, back when `main.ts` always booted into
   *  one hardcoded level). Same pattern `ReplayScene` already used. */
  init(data: { levelData: LevelData; poster?: string | null }): void {
    this.levelData = data.levelData;
    this.poster = data.poster ?? null;
  }

  preload(): void {
    // One atlas per level dir (items + background) + one per shared fish
    // variant, instead of hundreds of individual load.image() calls (docs/042).
    preloadAtlases(this, collectAtlasKeys(this.levelData.models, this.levelData.bgPicture));
  }

  create(): void {
    // Each level has its own room size, unlike the world map's fixed
    // 640x480 - resize the game canvas on every entry (docs/027). This
    // port's Game config sets no explicit scale `mode`, so the Scale
    // Manager defaults to NONE - Phaser's own docs are explicit that
    // `.setGameSize()` is for FIT-style modes only (it updates just the
    // internal/backing resolution) and `.resize()` is the one that
    // actually matches for NONE (it also updates the canvas's CSS display
    // box). Using setGameSize() here left the CSS size frozen at whatever
    // it was on boot (960x720, the map's size at zoom 1.5), so every level
    // with a different aspect ratio got visibly stretched into that fixed
    // box - see docs/029.
    this.scale.resize(
      this.levelData.roomWidth * GRID_SCALE,
      this.levelData.roomHeight * GRID_SCALE,
    );
    const bg = pictureToAtlas(this.levelData.bgPicture);
    this.bgImage = this.add.image(0, 0, bg.atlasKey, bg.frame).setOrigin(0, 0);

    // Hidden while empty - an empty Text with a background + padding still
    // renders its little padding box (the stray top-left smudge), so it's
    // only made visible when there's an actual solved/died message.
    this.statusText = this.add
      .text(8, 8, "", {
        fontFamily: "sans-serif",
        fontSize: "16px",
        color: "#ffffff",
        backgroundColor: "#000000a0",
        padding: { x: 6, y: 4 },
      })
      .setDepth(1000)
      .setVisible(false);

    const roomWidthPx = this.levelData.roomWidth * GRID_SCALE;
    const roomHeightPx = this.levelData.roomHeight * GRID_SCALE;
    this.subtitleStack = new SubtitleStack(this, roomWidthPx, roomHeightPx);

    this.feedbackText = this.add
      .text(roomWidthPx - 8, 8, "", {
        fontFamily: "sans-serif",
        fontSize: "16px",
        color: "#ffffff",
        backgroundColor: "#000000a0",
        padding: { x: 6, y: 4 },
      })
      .setOrigin(1, 0)
      .setDepth(1000)
      .setVisible(false);

    // Step counter (legacy StepDecor): top-right corner (right edge at the room
    // width, y=10), outlined console-style font at size 20, orange / blue by
    // active-fish power. Always visible.
    this.stepCounterText = this.add
      .text(roomWidthPx, 10, "0", {
        fontFamily: "monospace",
        fontSize: "20px",
        color: STEP_COLOR_NORMAL,
        stroke: "#000000",
        strokeThickness: 3,
      })
      .setOrigin(1, 0)
      .setDepth(1000);

    this.saveSlotUI = new SaveSlotUI(
      this,
      16,
      roomHeightPx - 16,
      (id) => this.loadGame(id),
      () => this.saveGame(),
      (id) => this.deleteGame(id),
    );
    this.saveSlotUI.refresh(loadSavedGames(this.levelData.levelName));

    this.helpOverlay = new HelpOverlay(this, roomWidthPx, roomHeightPx);

    // Capture arrows/space/F1/F2/F3 so the browser doesn't scroll the page,
    // open its own help (F1), or (Firefox's F3) pop up quick-find while playing.
    this.input.keyboard!.addCapture("UP,DOWN,LEFT,RIGHT,SPACE,F1,F2,F3");
    this.input.keyboard!.on("keydown", (e: KeyboardEvent) => {
      this.heldKeys.add(e.code);
      if (this.queuedKey === null && MOVE_KEYS.has(e.code)) {
        this.queuedKey = e.code;
      }
    });
    this.input.keyboard!.on("keyup", (e: KeyboardEvent) =>
      this.heldKeys.delete(e.code),
    );
    this.input.keyboard!.on("keydown-F1", () => this.helpOverlay.toggle());
    // The gameplay keys below are inert while the help modal is open, so
    // reading it can't accidentally restart/switch/save. Movement keys are
    // gated separately (via a no-op input in tick()).
    this.input.keyboard!.on("keydown-R", () => this.whenPlaying(() => this.restart()));
    this.input.keyboard!.on("keydown-SPACE", () => this.whenPlaying(() => this.engine.switchFish()));
    this.input.keyboard!.on("keydown-P", () => this.whenPlaying(() => void this.launchReplay()));
    this.input.keyboard!.on("keydown-F2", () => this.whenPlaying(() => this.saveGame()));
    this.input.keyboard!.on("keydown-F3", () => this.whenPlaying(() => this.loadLatestGame()));
    this.input.keyboard!.on("keydown-ESC", () => {
      // Esc closes the help popup if it's open, otherwise leaves for the map.
      if (this.helpOverlay.isShowing) this.helpOverlay.hide();
      else this.scene.start("worldmap");
    });

    // Right-click is a real game action (push-toward-cursor) now, so the
    // browser's context menu would otherwise get in the way.
    this.input.mouse?.disableContextMenu();
    this.input.on("pointerdown", (pointer: Phaser.Input.Pointer) => {
      if (this.helpOverlay.isShowing || (this.levelScript?.isShowing() ?? false)) return;
      if (pointer.leftButtonDown()) {
        this.engine.selectAt(this.toFieldPos(pointer));
      }
    });

    // Returning from the briefcase movie (DemoScene resumes this scene): the
    // movie resized the canvas to 720x555 and we disabled input to lock out
    // controls - restore both, and re-apply the level's music if it had any.
    // See docs/031, Phase 1.
    this.events.on(Phaser.Scenes.Events.RESUME, () => {
      this.input.enabled = true;
      this.scale.resize(
        this.levelData.roomWidth * GRID_SCALE,
        this.levelData.roomHeight * GRID_SCALE,
      );
      if (this.lastMusicCommand) void this.audioManager.applyMusicCommand(this.lastMusicCommand);
    });

    this.audioManager = new AudioManager(this);
    // Pre-decode this level's sound sprites now, so playback is instant instead
    // of decoding on first use (docs/043). Includes the level's own voice + the
    // shared pools, plus the `en` fallback dir (viking1's instrument clips live
    // only there - docs/036). Non-blocking; the level's own voice dir is
    // additionally gated on in startEngine() so the first line is in sync.
    void this.audioManager.preloadAll([
      ...levelSoundSpriteDirs(this.levelData.levelName),
      `${this.levelData.levelName}/en`,
    ]);

    // The Sound Manager is game-global, not scene-scoped (docs/025) - stop
    // this scene's music explicitly when leaving (e.g. P -> replay) so it
    // doesn't keep playing underneath whichever scene comes next. Registered
    // before startEngine() runs (not after) so it's still in place even if
    // that throws below.
    this.events.once(Phaser.Scenes.Events.SHUTDOWN, () => {
      this.audioManager.destroy();
      this.levelScript?.destroy();
      this.subtitleStack.destroy();
    });

    try {
      this.startEngine();
    } catch (error) {
      // A level whose content this port doesn't support at all (e.g.
      // "windoze"'s fish_extra kind, docs/022) throws synchronously while
      // building the GameEngine - not the async loadLevelModels() step
      // WorldMapScene already guards, but this scene's own create(). Left
      // uncaught, this stops create() mid-way with Phaser considering the
      // scene never properly started - stuck with neither scene active,
      // unrecoverable without a page reload. Report it and let the
      // already-registered Esc handler get the player back out, matching
      // how a failed loadLevelModels() is handled one level up.
      console.error(`Failed to start level "${this.levelData.levelName}"`, error);
      this.statusText
        .setText(`This level isn't supported yet (${String(error)}). Press Esc for the map.`)
        .setVisible(true);
      this.gameOver = true;
      return;
    }

    // Rounds are no longer a fixed timer - update() advances a shared clock and
    // runs a round when the (variable, phase-locked) interval elapses (docs/046).
    // buildAnimators() already reset the clock.
    this.roundsActive = true;
  }

  /** Resets the shared animation clock - called whenever the engine/animators
   *  are (re)built (startEngine, restart, a demo show's physics-only reset) so
   *  fresh animators never inherit a stale mid-slide progress. See docs/046. */
  private resetAnimationClock(): void {
    this.roundClock = 0;
    this.moving = false;
    this.moveDurationMs = CYCLE_MS;
    this.cyclesThisRound = 1;
  }

  /**
   * Per-frame (Phaser) render + round driver (docs/046). Advances one shared
   * `cellProgress` (0→1) and places every model from it - the source of
   * lockstep movement, replacing the old per-model position tweens - then runs
   * the next physics round when the round's interval elapses. A moving round
   * lasts `phases · CYCLE_MS` (the slide fills it exactly, no zip-wait); an idle
   * round is one cycle so input stays responsive.
   */
  update(_time: number, delta: number): void {
    if (!this.roundsActive) return;
    this.roundClock += delta;

    const progress = this.moving ? Math.min(1, this.roundClock / this.moveDurationMs) : 1;
    for (const animator of this.animators.values()) animator.render(progress);

    const interval = this.moving ? this.moveDurationMs : IDLE_ROUND_MS;
    if (this.roundClock >= interval) {
      this.roundClock -= interval; // keep the remainder so pacing doesn't drift
      this.tick();
    }
  }

  /** Sets the current round's pacing from the moves it just decided - legacy
   *  PhaseLocker/Controls::getNeededPhases (docs/046, refined docs/049). Phase
   *  count comes from the ACTIVE fish when it drives the move (turn/swam ratios),
   *  else a model leaving the room = 3 phases and a pure gravity fall = 1 phase
   *  (100ms/cell, ~3x faster than a swim - see roundPhases). */
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

  /** (Re)starts the room. With no arguments, matches R/a plain restart -
   *  exactly like the original, which never touches save data on restart.
   *  Passing `resumeMoves` (a saved slot's move string) fast-forwards
   *  physics to that position before anything is rendered (docs/022's
   *  headless mechanism, reused); `resumeModelState` (that slot's captured
   *  Lua state, docs/026) is applied once the fresh Lua bootstrap below
   *  resolves. A resume that fails (saved data no longer valid, e.g. level
   *  content changed since it was saved) warns and falls back to a fresh
   *  start rather than crashing. */
  private startEngine(resumeMoves?: string, resumeModelState?: string): void {
    this.levelScript?.destroy();
    this.levelScript = null;
    this.scriptGeneration += 1;
    this.queuedKey = null;
    const generation = this.scriptGeneration;
    // Level::own_initState() always stops music on a fresh restart (this
    // port has no undo to exempt) - docs/018.
    this.audioManager.reset();
    // Clear any lingering subtitles from the previous attempt (docs/037).
    this.subtitleStack?.clear();

    this.engine = new GameEngine(this.levelData);
    if (resumeMoves) {
      try {
        for (const symbol of resumeMoves) {
          this.engine.loadMove(symbol);
        }
        this.engine.settleAll();
      } catch (error) {
        console.warn(
          `Saved position for "${this.levelData.levelName}" is no longer valid, starting fresh`,
          error,
        );
        this.engine = new GameEngine(this.levelData);
        resumeModelState = undefined;
      }
    }
    this.gameOver = false;
    this.solvedCountdown = -1;
    this.wrongCountdown = -1;
    // No permanent on-screen controls text anymore - it's in the F1 popup
    // (HelpOverlay). statusText stays hidden during play, shown only for the
    // solved/died result below.
    this.statusText.setText("").setVisible(false);

    const initialRenderModels = this.engine.getRenderModels();
    this.buildAnimators(initialRenderModels);

    // Fire-and-forget: physics/animators above already reset synchronously
    // (no visible restart delay), and levelScript swaps in whenever the
    // Lua bootstrap resolves - tick() just skips item-anim overrides until
    // then. The generation check discards a superseded restart's result
    // instead of resurrecting a stale engine (see docs/014).
    createLevelScript(
      this.levelData.levelName,
      initialRenderModels,
      generation,
      this.hostActions,
      this.engineControl,
    )
      .then(async (script) => {
        if (generation !== this.scriptGeneration) {
          script.destroy();
          return;
        }
        // Hold the script from going live until this level's dialog voice
        // sprite has finished decoding (it loaded in parallel with the Lua
        // bootstrap above), so the first line's audio plays in sync with its
        // subtitle instead of ~2s late while a big sprite is still decoding
        // (docs/031 follow-up). Time-boxed so a stuck/failed load can never
        // brick the level - worst case the first line is a touch late.
        await Promise.race([
          this.audioManager.whenLoaded(levelDialogVoiceDir(this.levelData.levelName)),
          new Promise<void>((resolve) => this.time.delayedCall(4000, resolve)),
        ]);
        if (generation !== this.scriptGeneration) {
          script.destroy();
          return;
        }
        if (resumeModelState) {
          try {
            script.restoreModelState(resumeModelState);
          } catch (error) {
            // The physics restore above already succeeded independently -
            // a corrupted/incompatible decorative-state overlay is only a
            // cosmetic loss, not worth discarding the whole resume for.
            console.warn(
              `Failed to restore saved decorative state for "${this.levelData.levelName}"`,
              error,
            );
          }
        }
        this.levelScript = script;
      })
      .catch((error: unknown) => {
        console.error(
          `Failed to load level script for "${this.levelData.levelName}"`,
          error,
        );
      });
  }

  private restart(): void {
    this.startEngine();
  }

  /** Swaps the room background to `picture` (a legacy image path from
   *  game_changeBg, e.g. "images/corridor/dark.png") - corridor/rotate/steel
   *  do this mid-puzzle. The target is always another image in the level's own
   *  dir, so it's already packed into the level atlas loaded at preload
   *  (docs/042) - a synchronous frame swap, no on-demand load needed anymore
   *  (this used to lazy-load an individual webp). A missing frame is logged and
   *  the old background simply stays. See docs/033. */
  private applyBgChange(picture: string): void {
    const { atlasKey, frame } = pictureToAtlas(picture);
    if (this.textures.exists(atlasKey) && this.textures.get(atlasKey).has(frame)) {
      if (this.bgImage.active) this.bgImage.setTexture(atlasKey, frame);
    } else {
      console.warn(
        `game_changeBg: frame "${frame}" not in atlas "${atlasKey}" for "${this.levelData.levelName}"`,
      );
    }
  }

  /** Destroys any existing model animators and (re)builds them from the given
   *  render state - shared by startEngine() and the demo's physics-only
   *  restart/load (docs/031), which reset the engine *without* rebuilding the
   *  Lua script (so the show queue survives). */
  private buildAnimators(renderModels: RenderModel[]): void {
    for (const animator of this.animators.values()) animator.destroy();
    this.animators.clear();

    for (const model of renderModels) {
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
      animator.sync(model);
      this.animators.set(model.index, animator);
    }
    // Fresh animators start from a clean shared clock (docs/046) - matters for a
    // mid-tick demo restart/load, which rebuilds animators at new positions.
    this.resetAnimationClock();
  }

  /** level_action_save() during a "show" (docs/031, Phase 2): saves into a
   *  real, persistent *tutorial* save slot (upserted - the walkthrough saves
   *  repeatedly onto the one slot), shown as a distinct amber dot and loadable
   *  by the player once the tutorial is over. It never overwrites the player's
   *  own save slots. */
  private demoSave(): void {
    if (!this.levelScript) return;
    saveTutorialGame(
      this.levelData.levelName,
      this.engine.getMoves(),
      this.levelScript.captureModelState(),
    );
    this.saveSlotUI.refresh(loadSavedGames(this.levelData.levelName));
  }

  /** level_action_load(): restore the tutorial save slot - resets *physics
   *  only* (new GameEngine + replay moves), keeping the live Lua engine and its
   *  show queue alive, then re-applies the slot's Lua-side model state. */
  private demoLoad(): void {
    const save = loadTutorialGame(this.levelData.levelName);
    if (!save) return;
    this.resetPhysicsOnly(save.moves, save.modelState);
  }

  /** level_action_restart(): restart the room during a show - physics only
   *  (fresh GameEngine), Lua engine + show queue preserved. Safe without
   *  re-running code.lua because a level's own per-round logic is suppressed
   *  while level_isShowing() (see docs/031). */
  private demoRestart(): void {
    this.resetPhysicsOnly();
  }

  /** Swaps in a fresh GameEngine (optionally fast-forwarded to `moves` and
   *  with `modelState` re-applied to the surviving Lua engine) and rebuilds
   *  the sprite animators - the physics/visual half of startEngine() without
   *  the levelScript teardown. Used only by the demo's show restart/load. */
  private resetPhysicsOnly(moves?: string, modelState?: string): void {
    this.engine = new GameEngine(this.levelData);
    if (moves) {
      try {
        for (const symbol of moves) this.engine.loadMove(symbol);
        this.engine.settleAll();
      } catch (error) {
        console.warn(`Demo load position no longer valid for "${this.levelData.levelName}"`, error);
        this.engine = new GameEngine(this.levelData);
      }
    }
    this.gameOver = false;
    this.buildAnimators(this.engine.getRenderModels());
    if (modelState && this.levelScript) {
      try {
        this.levelScript.restoreModelState(modelState);
      } catch (error) {
        console.warn(`Demo load: failed to restore decorative state`, error);
      }
    }
  }

  /** Runs a gameplay action only when the help modal isn't up and no
   *  auto-play "show" is running - keeps the help popup a true modal and makes
   *  the briefcase demo/show non-interruptible (docs/031: only Esc leaves). */
  private whenPlaying(action: () => void): void {
    if (this.helpOverlay.isShowing) return;
    if (this.levelScript?.isShowing()) return;
    action();
  }

  /** Leaves a just-solved level for the world map. A world-final level (or the
   *  ending) first plays its recap poster (legacy Level::finishLevel ->
   *  createPoster -> a DemoMode, docs/050); other levels go straight back.
   *  `fromLevel` lets the map run its post-solve ending check (docs/050). */
  private leaveToMapAfterSolve(): void {
    if (this.poster) {
      this.scene.start("demo", {
        demoFile: this.poster,
        levelName: this.levelData.levelName,
        mode: "poster",
        returnTo: "worldmap",
        returnData: { fromLevel: true },
      });
    } else {
      this.scene.start("worldmap", { fromLevel: true });
    }
  }

  /** F2 or the save row's dim "add" dot: captures the current position
   *  into a new save slot - legacy's Level::action_save(). */
  private saveGame(): void {
    if (!this.engine.isSolvable()) {
      this.showFeedback("Can't save - no longer solvable");
      return;
    }
    if (!this.levelScript) {
      this.showFeedback("Still loading, try again in a moment");
      return;
    }
    const modelState = this.levelScript.captureModelState();
    const saved = addSavedGame(this.levelData.levelName, this.engine.getMoves(), modelState);
    if (!saved) {
      this.showFeedback("All save slots full - right-click a dot to delete one");
      return;
    }
    this.saveSlotUI.refresh(loadSavedGames(this.levelData.levelName));
    this.showFeedback(`Saved (${this.engine.getStepCount()} moves)`);
  }

  /** Left-click on a filled save dot: jumps straight to that slot's
   *  position - legacy's Level::action_load(). */
  private loadGame(id: string): void {
    const save = loadSavedGames(this.levelData.levelName).find((s) => s.id === id);
    if (!save) return;
    this.startEngine(save.moves, save.modelState);
    this.showFeedback(`Loaded (${save.moves.length} moves)`);
  }

  /** F3: loads the most recently created slot - a "quick load" stand-in
   *  for the original's "load whatever's currently selected," since this
   *  port has no selection concept (see docs/026). */
  private loadLatestGame(): void {
    const saves = loadSavedGames(this.levelData.levelName);
    if (saves.length === 0) {
      this.showFeedback("No saved position");
      return;
    }
    this.loadGame(saves[saves.length - 1].id);
  }

  /** Right-click on a save dot. */
  private deleteGame(id: string): void {
    deleteSavedGame(this.levelData.levelName, id);
    this.saveSlotUI.refresh(loadSavedGames(this.levelData.levelName));
  }

  /** Transient top-right confirmation - mirrors the original's
   *  displaySaveStatus() on-screen flash. */
  private showFeedback(message: string): void {
    this.feedbackTimer?.remove();
    this.feedbackText.setText(message).setVisible(true);
    this.feedbackTimer = this.time.delayedCall(1500, () => this.feedbackText.setVisible(false));
  }

  /** P: launch a watchable replay - see docs/025. Plays the player's own
   *  solved solution (docs/026) if this level has been solved here before.
   *  In sandbox mode (docs/045) it also falls back to the bundled
   *  legacy/solution/<level>.lua reference solution; the standard game does
   *  not, so only the player's own solutions are ever replayable there. */
  private async launchReplay(): Promise<void> {
    if (this.launchingReplay) return;
    this.launchingReplay = true;
    try {
      const levelName = this.levelData.levelName;
      let moves = loadSolvedMoves(levelName);
      if (!moves && isSandboxMode()) {
        const solutionSource = await fetchLegacyFile(`solution/${levelName}.lua`);
        moves = extractSavedMoves(solutionSource);
      }
      if (!moves) {
        console.warn(`No solution found for "${levelName}"`);
        this.showFeedback("No solution to replay");
        return;
      }
      this.scene.start("replay", { levelData: this.levelData, moves });
    } catch (error) {
      console.error(
        `Failed to load a solution to replay for "${this.levelData.levelName}"`,
        error,
      );
    } finally {
      this.launchingReplay = false;
    }
  }

  /** Converts a pointer's world position (already zoom/scroll-adjusted by
   *  Phaser) to a field cell, the same GRID_SCALE mapping sprites use -
   *  legacy's View::getFieldPos(), simplified since this project never
   *  scrolls the camera. */
  private toFieldPos(pointer: Phaser.Input.Pointer): V2 {
    return new V2(Math.floor(pointer.worldX / GRID_SCALE), Math.floor(pointer.worldY / GRID_SCALE));
  }

  private tick(): void {
    // Fixed CYCLE_MS cycles the interval that just elapsed occupied (set at the
    // end of the previous round) - fed to Lua so dialog/voice timing tracks
    // wall-clock, and used to step the win/lose countdowns, under the variable
    // round interval (docs/046).
    const cyclesElapsed = this.cyclesThisRound;

    // The round loop keeps running even after the level is unwinnable, so
    // e.g. a dead fish's corpse still disintegrates and drops whatever was
    // resting on it (see docs/011) - only the status text/flag latch once.
    // While the help modal is open the fish shouldn't move - feed the engine
    // a no-op input (and drain any queued key) so held arrows/mouse can't
    // drive it, without freezing the round loop (item anims/audio keep going).
    // During the briefcase auto-play "show" (demo_help) the demo drives the
    // fish and the player has no control; the help modal also freezes input.
    const showing = this.levelScript?.isShowing() ?? false;
    const helpOpen = this.helpOverlay.isShowing;
    const blockInput = showing || helpOpen;
    const pointer = this.input.activePointer;
    const input = {
      isPressed: (code: string) => !blockInput && this.heldKeys.has(code),
      isLeftPressed: () => !blockInput && pointer.leftButtonDown(),
      isRightPressed: () => !blockInput && pointer.rightButtonDown(),
      getMouseField: () => this.toFieldPos(pointer),
      takeQueuedKey: () => {
        const key = this.queuedKey;
        this.queuedKey = null;
        return blockInput ? null : key;
      },
    };
    if (showing) {
      // Settle pending falls, then let the show command drive the move (run
      // inside levelScript.tick() -> runShowStep -> level_action_move ->
      // engine.showMove). A show command that hits an impossible move throws;
      // catch it and gracefully end the show rather than crashing the loop.
      try {
        this.engine.beginShowRound();
      } catch (error) {
        this.abortShow(error);
      }
    } else {
      this.engine.tick(input);
    }

    // Snapshot BEFORE the script step: an auto-play show reads live positions
    // here (moveXY -> model:getLoc()) to decide the next move, and a show
    // command may then move/restart/load the engine inside this call.
    const preStepModels = this.engine.getRenderModels();
    try {
      this.levelScript?.tick(preStepModels, cyclesElapsed);
    } catch (error) {
      // A show command (or item-anim script) threw - if a show was running,
      // end it and hand control back; otherwise re-surface the error.
      if (showing) this.abortShow(error);
      else throw error;
    }

    // level_newDemo (briefcase pushed down) requested a fullscreen movie
    // during script_update - launch it now (pauses this scene). See docs/031.
    if (this.pendingDemo) {
      const demoFile = this.pendingDemo;
      this.pendingDemo = null;
      this.launchDemo(demoFile);
      return;
    }

    // Re-read AFTER the script step for rendering: a show's move/restart/load
    // mutates or swaps this.engine (and a restart/load rebuilds the animators)
    // mid-tick, so the sprite sync must reflect the post-step engine - syncing
    // the fresh animators with the stale pre-step snapshot is what made a demo
    // restart briefly slide items around and drop the fish (docs/032). For the
    // normal (non-show) path the move already happened above, so this is
    // identical to preStepModels.
    const renderModels = this.engine.getRenderModels();

    // Visual subtitles: drain the colored lines model_talk() spawned this round
    // into the scrolling, stacking, self-dismissing SubtitleStack (docs/037).
    // Independent of activeDialog's talking-state below (which still drives the
    // voice audio and model_isTalking). Gated on the Options "subtitles" setting
    // (docs/038) - voice still plays when off; still drained so nothing queues up.
    const showSubtitles = loadSettings().subtitles;
    for (const sub of this.levelScript?.takePendingSubtitles() ?? []) {
      if (showSubtitles) this.subtitleStack.add(sub.text, sub.color);
    }
    this.tickAudio();

    // game_changeBg() (corridor/rotate/steel) swaps the room background as the
    // puzzle progresses - see docs/033.
    const bgChange = this.levelScript?.takeBgChange();
    if (bgChange) this.applyBgChange(bgChange);

    for (const model of renderModels) {
      // Fish stay entirely TS-owned (docs/009/013) - only items consult the
      // level's Lua-driven anim override (docs/014). Talking-mouth head
      // overlay is the one exception (docs/029) - fish DO consult live
      // dialog state for that, same as the original's animateHead().
      const isFish = isFishKind(model.kind);
      const scriptAnim = isFish ? null : (this.levelScript?.getScriptAnim(model.index) ?? null);
      const isTalking = isFish && (this.levelScript?.isModelTalking(model.index) ?? false);
      this.animators.get(model.index)?.sync(model, scriptAnim, isTalking);
    }

    // Phase-lock the round the moves above just decided: how long the shared
    // slide lasts, from the ACTIVE fish's speed only (docs/046) - so a fish and
    // everything it pushes share one duration and stay in lockstep.
    this.updateRoundPacing(renderModels);

    // Step counter (legacy StepDecor) - the recorded move count, colored by
    // whether the active fish is the powerful (big) one.
    const activeInfo = this.engine.getActiveInfo();
    this.stepCounterText
      .setText(String(this.engine.getStepCount()))
      .setColor(activeInfo?.powerful ? STEP_COLOR_POWERFUL : STEP_COLOR_NORMAL);

    // No win/lose evaluation during the auto-play show - the demo deliberately
    // dies and restarts to demonstrate mechanics, and "solving" mid-show must
    // not trigger the return-to-map countdown.
    if (showing) return;

    if (this.engine.isSolved()) {
      // Gate on solvedCountdown (not !gameOver) so the win still latches even
      // if the "stuck" branch briefly latched gameOver on the not-yet-settled
      // round the winning escape completes on - and cancel that stray latch.
      if (this.solvedCountdown < 0) {
        // First round solved: latch the result and start the return
        // countdown (legacy LevelCountDown - longer if a dialog is still
        // running so the player can finish it).
        this.gameOver = true;
        this.wrongCountdown = -1;
        const isNewBest = saveSolvedMoves(this.levelData.levelName, this.engine.getMoves());
        this.statusText
          .setText(
            isNewBest
              ? `Solved in ${this.engine.getStepCount()} moves - new best!`
              : "Solved! Both fish made it out.",
          )
          .setVisible(true);
        // Give the player longer to read if a subtitle is still on screen.
        this.solvedCountdown = this.subtitleStack.hasVisible()
          ? SOLVED_RETURN_ROUNDS_DIALOG
          : SOLVED_RETURN_ROUNDS;
      } else if (this.solvedCountdown > 0) {
        this.solvedCountdown -= cyclesElapsed;
      } else {
        this.solvedCountdown = -1; // one-shot: don't re-trigger after start()
        this.leaveToMapAfterSolve();
        return;
      }
    } else if (this.engine.cannotMove() && this.engine.isFresh()) {
      // Both fish can no longer move (dead or wedged) AND the room has settled:
      // legacy LevelCountDown counts getCountForWrong() cycles then
      // action_restart(1)s the room. Mirror that - latch the message, count
      // down, then restart exactly like R. The isFresh() gate matters: on the
      // round a winning escape completes, both fish are already isLost (so
      // cannotMove() is true) but the room isn't fresh yet, so isSolved() is
      // still false - without the gate this fired "stuck" on a level the player
      // just solved (docs/034). Tracked on its own counter so it still fires
      // when gameOver was already latched by the single-death branch below.
      if (this.wrongCountdown < 0) {
        this.gameOver = true;
        this.wrongCountdown = WRONG_RESTART_ROUNDS;
        this.statusText.setText("Both fish are stuck - restarting...").setVisible(true);
      } else if (this.wrongCountdown > 0) {
        this.wrongCountdown -= cyclesElapsed;
      } else {
        this.wrongCountdown = -1; // one-shot: restart() resets it anyway
        this.restart();
        return;
      }
    } else if (!this.engine.isSolvable() && !this.gameOver) {
      this.gameOver = true;
      this.statusText.setText("A fish died - press R to restart, or Esc for the map.").setVisible(true);
    }
  }

  /** Ends a running "show" after a command threw (e.g. an impossible move
   *  from a physics divergence) - drops the queued commands so control returns
   *  to the player rather than crashing the round loop. See docs/031. */
  private abortShow(error: unknown): void {
    console.error(`Auto-play show aborted for "${this.levelData.levelName}"`, error);
    this.levelScript?.abortShow();
  }

  /** Pauses the level and launches the fullscreen movie overlay (DemoScene),
   *  locking out all level controls while it plays - see docs/031, Phase 1.
   *  The DemoScene resumes this scene (RESUME handler in create()) on Esc or
   *  when the movie ends. */
  private launchDemo(demoFile: string): void {
    // Stop ALL level audio (music + any dialog voice mid-line) - the movie
    // plays its own, and Phaser's Sound Manager is game-global so it would
    // otherwise keep playing under the movie (docs/025/031).
    this.audioManager.stopAll();
    // A paused scene can still receive input in Phaser, so disable it
    // explicitly - no save/load/help/replay/restart during the movie.
    this.input.enabled = false;
    this.scene.launch("demo", { demoFile, levelName: this.levelData.levelName });
    this.scene.pause();
  }

  /** Music, one-shot sound effects, built-in impact/death sounds, and
   *  dialog/NPC voice playback for this round - see docs/018, docs/043. */
  private tickAudio(): void {
    const music = this.levelScript?.getMusicCommand() ?? null;
    if (music) this.lastMusicCommand = music.type === "play" ? music : null;
    void this.audioManager.applyMusicCommand(music);

    for (const effect of this.levelScript?.getPendingSoundEffects() ?? []) {
      this.audioManager.playSoundEffect(effect.sound, effect.volume);
    }

    // Built-in sounds (Room::playImpact/playDead) - no Lua call site at
    // all, resolved through the same sound_addSound()-populated registry
    // (level_creation.lua registers these 4 names for every level).
    const impact = this.engine.lastImpact;
    if (impact === Weight.LIGHT) this.playNamedSound("impact_light", 50);
    else if (impact === Weight.HEAVY) this.playNamedSound("impact_heavy", 50);

    for (const dead of this.engine.lastDead) {
      // Cut the dying fish's own voices (legacy DialogStack::killSound) - both
      // the Lua-side talkers and the playing audio for that actor.
      this.levelScript?.killSound(dead.index);
      this.audioManager.stopDialogVoice(dead.index);
      if (dead.power === Weight.LIGHT) this.playNamedSound("dead_small", 100);
      else if (dead.power === Weight.HEAVY) this.playNamedSound("dead_big", 100);
    }

    // Dialog/NPC voices: play each talker started this round, concurrently and
    // grouped by actor (viking1's band plays together now) - docs/043. Then stop
    // any actor whose voices were killed this round (Lua model_killSound).
    for (const v of this.levelScript?.takePendingVoices() ?? []) {
      this.audioManager.playDialogVoice(v.sound, v.volume, v.actorIndex, v.loop);
    }
    for (const actor of this.levelScript?.takeKilledActors() ?? []) {
      this.audioManager.stopDialogVoice(actor);
    }
  }

  private playNamedSound(name: string, volume: number): void {
    const sound = this.levelScript?.resolveSound(name) ?? null;
    if (sound) this.audioManager.playSoundEffect(sound, volume);
  }
}
