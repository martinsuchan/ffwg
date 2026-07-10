import Phaser from "phaser";

import { MAX_SAVES, type SavedGame } from "../storage/levelStorage";

const DOT_RADIUS = 8;
const DOT_SPACING = 22;
const FILLED_COLOR = 0x3388ff;
const EMPTY_COLOR = 0x333333;

/**
 * A row of small clickable dots at the bottom-left of the screen, one per
 * mid-level save slot - Fish Fillets 2's mission-screen save UI
 * (`ff2/Fish Fillets 2 Manual.pdf`, "Mission Screen"), not anything the
 * original FF NG had (it only ever supported one save per level). Per the
 * user's own simplification of FF2's slightly fussier select-then-load
 * flow: left-click a filled dot to load it, right-click to delete it;
 * click the dim trailing dot (shown only while under MAX_SAVES) to save a
 * new slot. See docs/026.
 */
export class SaveSlotUI {
  private dots: Phaser.GameObjects.Arc[] = [];

  constructor(
    private readonly scene: Phaser.Scene,
    private readonly x: number,
    private readonly y: number,
    private readonly onLoad: (id: string) => void,
    private readonly onCreate: () => void,
    private readonly onDelete: (id: string) => void,
  ) {}

  /** Rebuilds the dot row from the current save list - cheap, at most
   *  MAX_SAVES + 1 small GameObjects. */
  refresh(saves: SavedGame[]): void {
    this.destroy();

    saves.forEach((save, i) => {
      const dot = this.makeDot(i, FILLED_COLOR, 0xffffff);
      dot.on("pointerdown", (pointer: Phaser.Input.Pointer) => {
        if (pointer.rightButtonDown()) this.onDelete(save.id);
        else this.onLoad(save.id);
      });
      this.dots.push(dot);
    });

    if (saves.length < MAX_SAVES) {
      const addDot = this.makeDot(saves.length, EMPTY_COLOR, 0x999999);
      addDot.on("pointerdown", () => this.onCreate());
      this.dots.push(addDot);
    }
  }

  destroy(): void {
    for (const dot of this.dots) dot.destroy();
    this.dots = [];
  }

  private makeDot(index: number, fillColor: number, strokeColor: number): Phaser.GameObjects.Arc {
    return this.scene.add
      .circle(this.x + index * DOT_SPACING, this.y, DOT_RADIUS, fillColor)
      .setStrokeStyle(2, strokeColor)
      .setDepth(1000)
      .setInteractive({ useHandCursor: true });
  }
}
