import Phaser from "phaser";

import { GRID_SCALE, type LevelData } from "../lua/levelLoader";
import { GameEngine, type RenderModel } from "../game/GameEngine";
import { CYCLE_MS, IDLE_ROUND_MS } from "../game/timing";
import { ModelAnimator, roundPhases } from "./ModelAnimator";
import { crispText, isFishKind, resolveInitialFrame } from "./sceneUtils";
import { pictureToAtlas } from "./atlas";

const LABEL_STYLE = {
  fontFamily: "sans-serif",
  fontSize: "14px",
  color: "#ffffff",
  backgroundColor: "#000000a0",
  padding: { x: 6, y: 3 },
} as const;

/**
 * One half of the solution compare view (CompareScene, docs/086): a level room
 * that replays one solution, drawn at its own world offset so a camera can show
 * it beside the other half. Cut down from ReplayScene: the same per-round
 * physics (GameEngine.tickReplay), animators and phase-locked pacing, but no
 * live Lua, audio or export (the scene plays the music) - and without Lua a
 * seek is just a headless fast-forward.
 *
 * The scene drives it with playTo(): the pane plays rounds until it has used
 * the moves up to that index and the room has settled, then reports done() and
 * waits. seek() jumps anywhere at once (Room.loadMove, as save-resume does).
 */
export class ComparePane {
  private engine!: GameEngine;
  private readonly animators = new Map<number, ModelAnimator>();
  private readonly background: Phaser.GameObjects.Image;
  private readonly label: Phaser.GameObjects.Text;
  private readonly counter: Phaser.GameObjects.Text;
  private readonly note: Phaser.GameObjects.Text;

  private moves = "";
  private index = 0;
  private target = 0;
  private roundClock = 0;
  private moving = false;
  private moveDurationMs = CYCLE_MS;

  constructor(
    private readonly scene: Phaser.Scene,
    private readonly levelData: LevelData,
    /** World position of the room's top-left corner. */
    readonly ox: number,
    readonly oy: number,
  ) {
    const width = levelData.roomWidth * GRID_SCALE;
    const height = levelData.roomHeight * GRID_SCALE;
    const { atlasKey, frame } = pictureToAtlas(levelData.bgPicture);
    this.background = scene.add.image(ox, oy, atlasKey, frame).setOrigin(0, 0).setDepth(-1);
    this.label = scene.add.text(ox + 6, oy + 6, "", crispText(LABEL_STYLE)).setDepth(1000);
    this.counter = scene.add.text(ox + width - 6, oy + 6, "", crispText(LABEL_STYLE)).setOrigin(1, 0).setDepth(1000);
    this.note = scene.add
      .text(ox + width / 2, oy + height - 10, "", crispText({ ...LABEL_STYLE, fontSize: "15px", color: "#ffd75e" }))
      .setOrigin(0.5, 1)
      .setDepth(1000)
      .setVisible(false);
    this.engine = new GameEngine(levelData);
  }

  /** Loads a solution (only its playable moves) and shows the start state. */
  setSolution(moves: string, label: string): void {
    this.moves = moves;
    this.label.setText(label);
    this.seek(0);
  }

  get moveIndex(): number {
    return this.index;
  }

  get length(): number {
    return this.moves.length;
  }

  /** Lets the pane play on until move `target` (exclusive count). */
  playTo(target: number): void {
    this.target = Math.min(target, this.moves.length);
  }

  /** Every move up to the target is played and the room has settled. */
  done(): boolean {
    return this.index >= this.target && !this.moving && this.engine.room.isFresh();
  }

  /** "Waiting" banner (or null to hide it). */
  setNote(text: string | null): void {
    this.note.setText(text ?? "").setVisible(text !== null);
  }

  /** Jumps to the state after `moveIndex` moves, at once (no animation). */
  seek(moveIndex: number): void {
    const k = Math.max(0, Math.min(moveIndex, this.moves.length));
    this.engine = new GameEngine(this.levelData);
    for (let i = 0; i < k; i++) this.engine.loadMove(this.moves[i]);
    this.engine.settleAll();
    this.engine.takePlanInterrupt();
    this.index = k;
    this.target = k;
    this.roundClock = 0;
    this.moving = false;
    this.moveDurationMs = CYCLE_MS;
    this.rebuildAnimators();
    this.updateCounter();
  }

  /**
   * Per-frame driver, the same shared phase-locked clock as ReplayScene
   * (docs/046): `speed` scales real time, 0 = paused.
   */
  update(delta: number, speed: number): void {
    if (speed > 0 && !this.done()) this.roundClock += delta * speed;
    const progress = this.moving ? Math.min(1, this.roundClock / this.moveDurationMs) : 1;
    for (const animator of this.animators.values()) animator.render(progress, this.ox, this.oy);
    if (speed <= 0 || this.done()) {
      this.roundClock = 0;
      return;
    }
    const interval = this.moving ? this.moveDurationMs : IDLE_ROUND_MS;
    if (this.roundClock >= interval - 1e-6) {
      this.roundClock = Math.max(0, this.roundClock - interval);
      this.tick();
    }
  }

  destroy(): void {
    for (const animator of this.animators.values()) animator.destroy();
    this.animators.clear();
    this.background.destroy();
    this.label.destroy();
    this.counter.destroy();
    this.note.destroy();
  }

  private tick(): void {
    const symbol = this.index < this.target ? this.moves[this.index] : null;
    if (this.engine.tickReplay(symbol)) this.index++;
    // No live Lua here, so the plan-interrupt flag has nobody to tell (docs/082).
    this.engine.takePlanInterrupt();
    const renderModels = this.engine.getRenderModels();
    this.syncAnimators(renderModels);
    this.updatePacing(renderModels);
    this.updateCounter();
  }

  private rebuildAnimators(): void {
    for (const animator of this.animators.values()) animator.destroy();
    this.animators.clear();
    const renderModels = this.engine.getRenderModels();
    for (const model of renderModels) {
      const levelModel = this.levelData.models[model.index];
      const initial = resolveInitialFrame(levelModel);
      if (!initial) continue;
      const isFish = isFishKind(model.kind);
      const x = this.ox + model.x * GRID_SCALE;
      const y = this.oy + model.y * GRID_SCALE;
      const body = this.scene.add.image(x, y, initial.atlasKey, initial.frame).setOrigin(0, 0);
      const head = isFish
        ? this.scene.add.image(x, y, initial.atlasKey, initial.frame).setOrigin(0, 0).setVisible(false)
        : undefined;
      const animator = new ModelAnimator(this.scene, levelModel.anims, body, isFish, head, model.x, model.y, model.isLeft);
      this.animators.set(model.index, animator);
    }
    this.syncAnimators(renderModels);
    for (const animator of this.animators.values()) animator.render(1, this.ox, this.oy);
  }

  private syncAnimators(renderModels: RenderModel[]): void {
    for (const model of renderModels) {
      // As in ReplayScene: items keep only their init-time effect (a model
      // hidden at init stays hidden, docs/058); no Lua-driven animation. Except
      // "mirror" (submarine): it captures the main camera's framebuffer, and
      // here three cameras share the canvas - the mirror draws as its plain picture.
      const initial = this.levelData.models[model.index].initialEffect;
      const effect = isFishKind(model.kind) || initial === "mirror" ? null : initial;
      this.animators.get(model.index)?.sync(model, null, false, null, effect);
    }
  }

  /** Same as ReplayScene.updateRoundPacing (docs/046, docs/049). */
  private updatePacing(renderModels: RenderModel[]): void {
    this.moving = this.engine.anyModelMoving();
    if (!this.moving) {
      this.moveDurationMs = CYCLE_MS;
      return;
    }
    const phases = roundPhases(this.engine.getActiveInfo(), renderModels, this.levelData.models, this.engine.wentOutThisRound());
    this.moveDurationMs = phases * CYCLE_MS;
  }

  private updateCounter(): void {
    this.counter.setText(`${this.index} / ${this.moves.length}`);
  }
}
