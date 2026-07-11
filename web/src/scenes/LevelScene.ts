import Phaser from "phaser";

import { GRID_SCALE, fetchLegacyFile, extractSavedMoves, type LevelData } from "../lua/levelLoader";
import { createLevelScript, type LevelScript, type ResolvedSound } from "../lua/levelScript";
import { GameEngine } from "../game/GameEngine";
import { V2 } from "../game/V2";
import { Weight } from "../game/Cube";
import { ROUND_MS } from "../game/timing";
import { ModelAnimator, preloadModelFrames } from "./ModelAnimator";
import { AudioManager } from "./AudioManager";
import { pictureToAssetUrl, isFishKind, resolveInitialTextureKey } from "./sceneUtils";
import { SaveSlotUI } from "./SaveSlotUI";
import { HelpOverlay } from "./HelpOverlay";
import {
  loadSavedGames,
  addSavedGame,
  deleteSavedGame,
  loadSolvedMoves,
  saveSolvedMoves,
} from "../storage/levelStorage";

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
 *  Counted here in physics rounds (tick(), ROUND_MS), this port's per-cycle
 *  proxy. */
const SOLVED_RETURN_ROUNDS = 10;
const SOLVED_RETURN_ROUNDS_DIALOG = 30;

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
  private statusText!: Phaser.GameObjects.Text;
  /** Dialog text (docs/015) - fixed screen position, matching the
   *  original's own fixed on-screen subtitle region rather than per-fish
   *  floating speech bubbles (SubTitleAgent's TITLE_BASE, confirmed from
   *  source). English only, no audio. */
  private subtitleText!: Phaser.GameObjects.Text;
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
  /** Countdown (in rounds) from a solved room to the auto-return to the
   *  world map - see SOLVED_RETURN_ROUNDS and tick(). -1 = not counting. */
  private solvedCountdown = -1;
  /** F1 controls popup (replaces the old always-on top-of-screen help). */
  private helpOverlay!: HelpOverlay;
  /** Item animation (docs/014) - null until the async Lua bootstrap for
   *  this play session resolves; tick()/ModelAnimator handle that gap by
   *  just not applying any override yet, not by waiting for it. */
  private levelScript: LevelScript | null = null;
  /** Guards against a superseded startEngine()'s createLevelScript() call
   *  resolving *after* a newer restart already happened - see startEngine(). */
  private scriptGeneration = 0;
  /** Music/sound-effect/dialog-voice playback - see docs/018. */
  private audioManager!: AudioManager;
  /** Identity of the dialog last seen active, so a new dialog's voice clip
   *  plays exactly once (docs/018) rather than every round it's showing. */
  private lastDialogId: string | null = null;
  /** Guards keydown-P against double-firing while the reference-solution
   *  fetch for launchReplay() is still in flight - see docs/025. */
  private launchingReplay = false;
  /** Mid-level save slots, shown as a row of clickable dots - see docs/026. */
  private saveSlotUI!: SaveSlotUI;
  /** Transient save/load confirmation text (top-right) - mirrors the
   *  original's displaySaveStatus() on-screen flash. See docs/026. */
  private feedbackText!: Phaser.GameObjects.Text;
  private feedbackTimer?: Phaser.Time.TimerEvent;

  constructor() {
    super("level");
  }

  /** Dynamic per-launch data (docs/027) - the world map picks a level at
   *  runtime, so `levelData` can no longer be known when this scene is
   *  constructed (it used to be, back when `main.ts` always booted into
   *  one hardcoded level). Same pattern `ReplayScene` already used. */
  init(data: { levelData: LevelData }): void {
    this.levelData = data.levelData;
  }

  preload(): void {
    this.load.image(this.bgKey(), pictureToAssetUrl(this.levelData.bgPicture));
    this.levelData.models.forEach((model, index) => {
      preloadModelFrames(this, this.levelData.levelName, index, model.anims, pictureToAssetUrl);
    });
  }

  /** Level-scoped, not a bare "bg" - two different levels' textures must
   *  never collide under one key, or switching levels (now dynamic via
   *  the world map, docs/027) silently keeps the previous level's images
   *  since Phaser's loader no-ops a load.image() against an already-
   *  registered key. See docs/028. */
  private bgKey(): string {
    return `${this.levelData.levelName}-bg`;
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
    this.add.image(0, 0, this.bgKey()).setOrigin(0, 0);

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
    this.subtitleText = this.add
      .text(roomWidthPx / 2, roomHeightPx - 8, "", {
        fontFamily: "sans-serif",
        fontSize: "13px",
        color: "#ffffff",
        backgroundColor: "#000000c0",
        padding: { x: 8, y: 4 },
        align: "center",
        wordWrap: { width: roomWidthPx - 40 },
      })
      .setOrigin(0.5, 1)
      .setDepth(1000)
      .setVisible(false);

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
      if (this.helpOverlay.isShowing) return;
      if (pointer.leftButtonDown()) {
        this.engine.selectAt(this.toFieldPos(pointer));
      }
    });

    this.audioManager = new AudioManager(this);

    // The Sound Manager is game-global, not scene-scoped (docs/025) - stop
    // this scene's music explicitly when leaving (e.g. P -> replay) so it
    // doesn't keep playing underneath whichever scene comes next. Registered
    // before startEngine() runs (not after) so it's still in place even if
    // that throws below.
    this.events.once(Phaser.Scenes.Events.SHUTDOWN, () => {
      this.audioManager.destroy();
      this.levelScript?.destroy();
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

    this.time.addEvent({
      delay: ROUND_MS,
      loop: true,
      callback: () => this.tick(),
    });
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
    for (const animator of this.animators.values()) {
      animator.destroy();
    }
    this.animators.clear();

    this.levelScript?.destroy();
    this.levelScript = null;
    this.scriptGeneration += 1;
    this.queuedKey = null;
    const generation = this.scriptGeneration;
    // Level::own_initState() always stops music on a fresh restart (this
    // port has no undo to exempt) - docs/018.
    this.audioManager.reset();
    this.lastDialogId = null;

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
    // No permanent on-screen controls text anymore - it's in the F1 popup
    // (HelpOverlay). statusText stays hidden during play, shown only for the
    // solved/died result below.
    this.statusText.setText("").setVisible(false);

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

    // Fire-and-forget: physics/animators above already reset synchronously
    // (no visible restart delay), and levelScript swaps in whenever the
    // Lua bootstrap resolves - tick() just skips item-anim overrides until
    // then. The generation check discards a superseded restart's result
    // instead of resurrecting a stale engine (see docs/014).
    createLevelScript(this.levelData.levelName, initialRenderModels, generation)
      .then((script) => {
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

  /** Runs a gameplay action only when the help modal isn't up - keeps the
   *  popup a true modal (reading the controls can't restart/switch/save). */
  private whenPlaying(action: () => void): void {
    if (this.helpOverlay.isShowing) return;
    action();
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

  /** P: launch a watchable replay - see docs/025. Prefers the player's own
   *  solved solution (docs/026) if this level has been solved here before;
   *  otherwise falls back to legacy/solution/<level>.lua, the same
   *  reference solution the headless validator (docs/022-024) checks. */
  private async launchReplay(): Promise<void> {
    if (this.launchingReplay) return;
    this.launchingReplay = true;
    try {
      const levelName = this.levelData.levelName;
      let moves = loadSolvedMoves(levelName);
      if (!moves) {
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
    // The round loop keeps running even after the level is unwinnable, so
    // e.g. a dead fish's corpse still disintegrates and drops whatever was
    // resting on it (see docs/011) - only the status text/flag latch once.
    // While the help modal is open the fish shouldn't move - feed the engine
    // a no-op input (and drain any queued key) so held arrows/mouse can't
    // drive it, without freezing the round loop (item anims/audio keep going).
    const helpOpen = this.helpOverlay.isShowing;
    const pointer = this.input.activePointer;
    const input = {
      isPressed: (code: string) => !helpOpen && this.heldKeys.has(code),
      isLeftPressed: () => !helpOpen && pointer.leftButtonDown(),
      isRightPressed: () => !helpOpen && pointer.rightButtonDown(),
      getMouseField: () => this.toFieldPos(pointer),
      takeQueuedKey: () => {
        const key = this.queuedKey;
        this.queuedKey = null;
        return helpOpen ? null : key;
      },
    };
    this.engine.tick(input);

    const renderModels = this.engine.getRenderModels();
    this.levelScript?.tick(renderModels);

    const subtitle = this.levelScript?.getActiveSubtitle() ?? null;
    if (subtitle) {
      this.subtitleText.setText(subtitle.text).setVisible(true);
    } else {
      this.subtitleText.setVisible(false);
    }
    this.tickAudio(subtitle);

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

    if (this.engine.isSolved()) {
      if (!this.gameOver) {
        // First round solved: latch the result and start the return
        // countdown (legacy LevelCountDown - longer if a dialog is still
        // running so the player can finish it).
        this.gameOver = true;
        const isNewBest = saveSolvedMoves(this.levelData.levelName, this.engine.getMoves());
        this.statusText
          .setText(
            isNewBest
              ? `Solved in ${this.engine.getStepCount()} moves - new best!`
              : "Solved! Both fish made it out.",
          )
          .setVisible(true);
        this.solvedCountdown = this.levelScript?.getActiveSubtitle()
          ? SOLVED_RETURN_ROUNDS_DIALOG
          : SOLVED_RETURN_ROUNDS;
      } else if (this.solvedCountdown > 0) {
        this.solvedCountdown -= 1;
      } else if (this.solvedCountdown === 0) {
        this.solvedCountdown = -1; // one-shot: don't re-trigger after start()
        this.scene.start("worldmap");
        return;
      }
    } else if (!this.engine.isSolvable() && !this.gameOver) {
      this.gameOver = true;
      this.statusText.setText("A fish died - press R to restart, or Esc for the map.").setVisible(true);
    }
  }

  /** Music, one-shot sound effects, built-in impact/death sounds, and
   *  dialog/NPC voice playback for this round - see docs/018. */
  private tickAudio(subtitle: { sound: ResolvedSound | null; volume: number } | null): void {
    void this.audioManager.applyMusicCommand(this.levelScript?.getMusicCommand() ?? null);

    for (const effect of this.levelScript?.getPendingSoundEffects() ?? []) {
      void this.audioManager.playSoundEffect(effect.sound, effect.volume);
    }

    // Built-in sounds (Room::playImpact/playDead) - no Lua call site at
    // all, resolved through the same sound_addSound()-populated registry
    // (level_creation.lua registers these 4 names for every level).
    const impact = this.engine.lastImpact;
    if (impact === Weight.LIGHT) this.playNamedSound("impact_light", 50);
    else if (impact === Weight.HEAVY) this.playNamedSound("impact_heavy", 50);

    for (const dead of this.engine.lastDead) {
      this.levelScript?.killSound(dead.index);
      if (dead.power === Weight.LIGHT) this.playNamedSound("dead_small", 100);
      else if (dead.power === Weight.HEAVY) this.playNamedSound("dead_big", 100);
    }

    // Dialog/NPC voice: play once when a *new* dialog starts, not every
    // round it's still showing.
    const dialogId = this.levelScript?.getActiveDialogId() ?? null;
    if (dialogId !== this.lastDialogId) {
      this.lastDialogId = dialogId;
      if (dialogId && subtitle?.sound) {
        void this.audioManager.playSoundEffect(subtitle.sound, subtitle.volume);
      }
    }
  }

  private playNamedSound(name: string, volume: number): void {
    const sound = this.levelScript?.resolveSound(name) ?? null;
    if (sound) void this.audioManager.playSoundEffect(sound, volume);
  }
}
