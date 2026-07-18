import Phaser from "phaser";

import type { BestSolution } from "../lua/worldMapLoader";
import { loadSolvedMoves } from "../storage/levelStorage";
import { t } from "../i18n";
import {
  crispText,
  readTexturePixels,
  buildMaskedTexture,
  packRgb,
  type TexturePixels,
} from "./sceneUtils";

/** legacy Pedometer.cpp: the rack (pedometer.png) sits at (193,141) on the
 *  640x480 map; its 3 buttons are identified by mask pixels at panel-relative
 *  (86,100)/(128,100)/(170,100) (maskRun/maskReplay/maskCancel). */
const PANEL_X = 193;
const PANEL_Y = 141;
/** Panel-relative sample points of each button's mask region. */
const BUTTON_POINTS = {
  run: [86, 100],
  replay: [128, 100],
  cancel: [170, 100],
} as const;

/** legacy Pedometer::drawNumbers: 5 ciphers from numbers.png (a vertical strip
 *  of the digits 9..0, each 19x24), blitted at absolute (275,177), one digit
 *  every 19px. */
const NUM_X = 275;
const NUM_Y = 177;
const DIGIT_W = 19;
const CIPHERS = 5;

type ButtonAction = keyof typeof BUTTON_POINTS;

interface PedoButton {
  action: ButtonAction;
  color: number;
  textureKey: string;
  onClick: () => void;
}

/**
 * The "already solved" screen shown when clicking a solved node - legacy's
 * Pedometer/NodeDrawer/SolverDrawer. An in-scene overlay (backdrop + panel +
 * name + digit counter + 3 masked buttons), not a separate Phaser scene -
 * WorldMapScene owns one instance and calls show()/hide() (same shape as
 * SaveSlotUI.ts / OptionsOverlay.ts).
 *
 * Faithful to the original's LayeredPicture buttons: pedometer.png is the
 * rack art (Run/Replay/Cancel already drawn on it), pedometer_mask.png marks
 * each button region, and hovering one reveals pedometer_lower.png's prelit
 * pixels through that region (docs/039). The step count is drawn from
 * numbers.png. The original's per-digit "slot machine" roll is simplified to
 * a count-up tween over the same digit art.
 */
export class PedometerUI {
  private backdrop?: Phaser.GameObjects.Rectangle;
  private panel?: Phaser.GameObjects.Image;
  private lowerOverlay?: Phaser.GameObjects.Image;
  private nameText?: Phaser.GameObjects.Text;
  private digits: Phaser.GameObjects.Image[] = [];
  private compareText?: Phaser.GameObjects.Text;
  private countTween?: Phaser.Tweens.Tween;

  /** pedometer_mask.png pixels (panel-local), read once for hit-testing. */
  private maskPixels?: TexturePixels;
  private buttons: PedoButton[] = [];
  private activeButton: PedoButton | null = null;

  constructor(
    private readonly scene: Phaser.Scene,
    private readonly mapWidth: number,
    private readonly mapHeight: number,
    /** Resolves a codename to its localized display name (docs/073). */
    private readonly nameOf: (codename: string) => string,
    private readonly bestSolutions: Map<string, BestSolution>,
    private readonly onRun: (codename: string) => void,
    private readonly onReplay: (codename: string) => void,
    /** Cancel/close - lets WorldMapScene restore the hidden node dots. */
    private readonly onCancel: () => void,
  ) {}

  get isShowing(): boolean {
    return this.backdrop !== undefined;
  }

  show(codename: string): void {
    this.hide();

    const moves = loadSolvedMoves(codename);
    if (moves === null) return; // shouldn't happen - only shown for solved nodes

    // Fully transparent (no dark tint - matches the original, which just draws
    // the rack on the plain map with its dots hidden), but still interactive so
    // it absorbs clicks and hosts the button hover/click hit-testing.
    this.backdrop = this.scene.add
      .rectangle(0, 0, this.mapWidth, this.mapHeight, 0x000000, 0)
      .setOrigin(0, 0)
      .setDepth(2000)
      .setInteractive();

    this.panel = this.scene.add
      .image(PANEL_X, PANEL_Y, "pedometer-bg")
      .setOrigin(0, 0)
      .setDepth(2001);

    // Prelit hover overlay (masked to the hovered button's shape).
    this.lowerOverlay = this.scene.add
      .image(PANEL_X, PANEL_Y, "pedometer-bg")
      .setOrigin(0, 0)
      .setDepth(2002)
      .setVisible(false);

    this.nameText = this.scene.add
      .text(this.mapWidth / 2, PANEL_Y - 24, this.nameOf(codename), crispText({
        fontFamily: "sans-serif",
        fontSize: "18px",
        color: "#ffffcc",
        stroke: "#000000",
        strokeThickness: 3,
      }))
      .setOrigin(0.5, 1)
      .setDepth(2003);

    this.buildButtons(codename);
    this.buildDigits(moves.length);
    this.buildCompareText(codename, moves.length);

    // Button hover/click ride on the backdrop (the topmost interactive object
    // under the cursor while shown), so node/corner input underneath is inert.
    this.backdrop.on("pointermove", (p: Phaser.Input.Pointer) => this.onPointerMove(p));
    this.backdrop.on("pointerdown", (p: Phaser.Input.Pointer) => this.onPointerDown(p));
  }

  hide(): void {
    this.countTween?.remove();
    this.countTween = undefined;
    this.backdrop?.destroy();
    this.panel?.destroy();
    this.lowerOverlay?.destroy();
    this.nameText?.destroy();
    this.compareText?.destroy();
    for (const d of this.digits) d.destroy();
    this.digits = [];
    this.backdrop = undefined;
    this.activeButton = null;
    this.scene.input.setDefaultCursor("");
  }

  /** Read pedometer_mask.png + build the 3 prelit button textures (once). */
  private buildButtons(codename: string): void {
    if (!this.maskPixels) this.maskPixels = readTexturePixels(this.scene, "pedometer-mask");
    const lower = readTexturePixels(this.scene, "pedometer-lower");
    const onClick: Record<ButtonAction, () => void> = {
      run: () => this.onRun(codename),
      replay: () => this.onReplay(codename),
      cancel: () => this.onCancel(),
    };
    this.buttons = [];
    if (!this.maskPixels || !lower) return;
    for (const action of Object.keys(BUTTON_POINTS) as ButtonAction[]) {
      const [px, py] = BUTTON_POINTS[action];
      const color = packRgb(this.maskPixels, px, py);
      if (color < 0) continue;
      const key = `pedo-${action}`;
      buildMaskedTexture(this.scene, key, lower, this.maskPixels, color);
      this.buttons.push({ action, color, textureKey: key, onClick: onClick[action] });
    }
  }

  /** 5 digit sprites drawn from numbers.png, updated by a count-up tween. */
  private buildDigits(target: number): void {
    for (let i = 0; i < CIPHERS; i++) {
      this.digits.push(
        this.scene.add
          .image(NUM_X + DIGIT_W * i, NUM_Y, "pedometer-numbers", 9)
          .setOrigin(0, 0)
          .setDepth(2003),
      );
    }
    const setValue = (value: number) => {
      const s = String(Math.max(0, Math.round(value)))
        .padStart(CIPHERS, "0")
        .slice(-CIPHERS);
      for (let i = 0; i < CIPHERS; i++) {
        // numbers.png rows run 9 (top) .. 0 (bottom): digit d -> frame 9 - d.
        this.digits[i].setFrame(9 - Number(s[i]));
      }
    };
    setValue(0);
    const counter = { value: 0 };
    this.countTween = this.scene.tweens.add({
      targets: counter,
      value: target,
      duration: 500,
      ease: "Cubic.Out",
      onUpdate: () => setValue(counter.value),
    });
  }

  /** The best-solution info line(s), legacy Pedometer's SolverDrawer: the real
   *  localized `solver_better`/`solver_equals`/`solver_worse` label (no dark
   *  background, centered on the plain map below the rack). */
  private buildCompareText(codename: string, moveCount: number): void {
    const best = this.bestSolutions.get(codename);
    if (!best) return;

    // legacy LevelStatus::compareToBest(): 1 = you're better (or no record),
    // 0 = equal, -1 = worse -> the SolverDrawer label switch.
    const labelName =
      best.moves > 0 && best.moves < moveCount
        ? "solver_worse"
        : best.moves > 0 && best.moves === moveCount
          ? "solver_equals"
          : "solver_better";

    // The localized solver label from labels.lua via the shared i18n layer;
    // %1 -> best moves, %2 -> best author (Dialog::getFormatedSubtitle). See docs/073.
    const text = t(labelName, best.moves, best.author);
    if (!text || text === labelName) return;

    // SolverDrawer sits centered at screen y = h - 150 (below the rack).
    this.compareText = this.scene.add
      .text(this.mapWidth / 2, this.mapHeight - 150, text, crispText({
        fontFamily: "sans-serif",
        fontSize: "14px",
        color: "#ffffff",
        stroke: "#000000",
        strokeThickness: 3,
        align: "center",
      }))
      .setOrigin(0.5, 0)
      .setDepth(2003);
  }

  private buttonAt(pointer: Phaser.Input.Pointer): PedoButton | null {
    if (!this.maskPixels) return null;
    const lx = Math.floor(pointer.worldX - PANEL_X);
    const ly = Math.floor(pointer.worldY - PANEL_Y);
    const color = packRgb(this.maskPixels, lx, ly);
    return this.buttons.find((b) => b.color === color) ?? null;
  }

  private onPointerMove(pointer: Phaser.Input.Pointer): void {
    const button = this.buttonAt(pointer);
    if (button === this.activeButton) return;
    this.activeButton = button;
    if (button && this.lowerOverlay) {
      this.lowerOverlay.setTexture(button.textureKey).setVisible(true);
      this.scene.input.setDefaultCursor("pointer");
    } else {
      this.lowerOverlay?.setVisible(false);
      this.scene.input.setDefaultCursor("");
    }
  }

  private onPointerDown(pointer: Phaser.Input.Pointer): void {
    if (!pointer.leftButtonDown()) return;
    const button = this.buttonAt(pointer);
    if (button) button.onClick();
  }
}
