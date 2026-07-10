import Phaser from "phaser";

import type { BestSolution } from "../lua/worldMapLoader";
import { loadSolvedMoves } from "../storage/levelStorage";

/** legacy's Pedometer.cpp: panel art position (193,141) on the 640x480
 *  map, and its 3 buttons' positions relative to that panel origin
 *  (maskRun/maskReplay/maskCancel, ~86/128/170,100) - reused verbatim so
 *  the layout matches the original even though the per-pixel mask hit-
 *  testing itself is replaced with plain interactive text buttons. */
const PANEL_X = 193;
const PANEL_Y = 141;
/** legacy/images/menu/pedometer.png's real dimensions. */
const PANEL_HEIGHT = 186;
const BUTTON_Y = PANEL_Y + 100;
const RUN_X = PANEL_X + 86;
const REPLAY_X = PANEL_X + 128;
const CANCEL_X = PANEL_X + 170;

/**
 * The "already solved" screen shown when clicking a solved node - legacy's
 * Pedometer/SolverDrawer. An in-scene overlay (backdrop + panel + text +
 * 3 buttons), not a separate Phaser scene - WorldMapScene owns one
 * instance and calls show()/hide() (same shape as SaveSlotUI.ts).
 * Deliberately simplified from the original's per-digit "slot machine"
 * roll-in sprite animation to a plain count-up tween on a text object -
 * same spirit as this project's other presentation simplifications (e.g.
 * docs/011's plain fade instead of a pixel-dissolve shader).
 */
export class PedometerUI {
  private backdrop?: Phaser.GameObjects.Rectangle;
  private panel?: Phaser.GameObjects.Image;
  private nameText?: Phaser.GameObjects.Text;
  private movesText?: Phaser.GameObjects.Text;
  private compareText?: Phaser.GameObjects.Text;
  private buttons: Phaser.GameObjects.Text[] = [];
  private countTween?: Phaser.Tweens.Tween;

  constructor(
    private readonly scene: Phaser.Scene,
    private readonly mapWidth: number,
    private readonly mapHeight: number,
    private readonly names: Map<string, string>,
    private readonly bestSolutions: Map<string, BestSolution>,
    private readonly onRun: (codename: string) => void,
    private readonly onReplay: (codename: string) => void,
  ) {}

  get isShowing(): boolean {
    return this.backdrop !== undefined;
  }

  show(codename: string): void {
    this.hide();

    const moves = loadSolvedMoves(codename);
    if (moves === null) return; // shouldn't happen - only shown for solved nodes

    this.backdrop = this.scene.add
      .rectangle(0, 0, this.mapWidth, this.mapHeight, 0x000000, 0.55)
      .setOrigin(0, 0)
      .setDepth(2000)
      .setInteractive(); // absorbs clicks so nodes underneath can't be hit

    this.panel = this.scene.add
      .image(PANEL_X, PANEL_Y, "pedometer-bg")
      .setOrigin(0, 0)
      .setDepth(2001);

    this.nameText = this.scene.add
      .text(this.mapWidth / 2, PANEL_Y - 24, this.names.get(codename) ?? codename, {
        fontFamily: "sans-serif",
        fontSize: "18px",
        color: "#ffffcc",
        stroke: "#000000",
        strokeThickness: 3,
      })
      .setOrigin(0.5, 1)
      .setDepth(2001);

    this.movesText = this.scene.add
      .text(PANEL_X + 134, PANEL_Y + 40, "0", {
        fontFamily: "sans-serif",
        fontSize: "26px",
        color: "#ffffff",
      })
      .setOrigin(0.5)
      .setDepth(2001);
    const counter = { value: 0 };
    this.countTween = this.scene.tweens.add({
      targets: counter,
      value: moves.length,
      duration: 500,
      ease: "Cubic.Out",
      onUpdate: () => this.movesText?.setText(String(Math.round(counter.value))),
    });

    const best = this.bestSolutions.get(codename);
    if (best) {
      const compare =
        moves.length < best.moves
          ? `You beat ${best.author}'s record of ${best.moves} moves!`
          : moves.length === best.moves
            ? `You matched ${best.author}'s record of ${best.moves} moves.`
            : `Best known: ${best.moves} moves by ${best.author} (yours: ${moves.length}).`;
      this.compareText = this.scene.add
        .text(this.mapWidth / 2, PANEL_Y + PANEL_HEIGHT + 12, compare, {
          fontFamily: "sans-serif",
          fontSize: "13px",
          color: "#ffffff",
          backgroundColor: "#000000a0",
          padding: { x: 6, y: 3 },
        })
        .setOrigin(0.5, 0)
        .setDepth(2001);
    }

    const specs: Array<{ x: number; label: string; onClick: () => void }> = [
      { x: RUN_X, label: "Run", onClick: () => this.onRun(codename) },
      { x: REPLAY_X, label: "Replay", onClick: () => this.onReplay(codename) },
      { x: CANCEL_X, label: "Cancel", onClick: () => this.hide() },
    ];
    this.buttons = specs.map((spec) =>
      this.scene.add
        .text(spec.x, BUTTON_Y, spec.label, {
          fontFamily: "sans-serif",
          fontSize: "13px",
          color: "#ffffff",
          backgroundColor: "#2a5a8aff",
          padding: { x: 8, y: 4 },
        })
        .setOrigin(0.5)
        .setDepth(2001)
        .setInteractive({ useHandCursor: true })
        .on("pointerdown", spec.onClick),
    );
  }

  hide(): void {
    this.countTween?.remove();
    this.countTween = undefined;
    this.backdrop?.destroy();
    this.panel?.destroy();
    this.nameText?.destroy();
    this.movesText?.destroy();
    this.compareText?.destroy();
    for (const button of this.buttons) button.destroy();
    this.buttons = [];
    this.backdrop = undefined;
  }
}
