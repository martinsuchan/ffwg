import Phaser from "phaser";

import { crispText } from "./sceneUtils";

/** The controls reference, shown only on demand (F1) instead of a permanent
 *  top-of-screen wall of text - each line is `key\tdescription`, rendered in
 *  two aligned monospace columns. */
const HELP_LINES: Array<[string, string]> = [
  ["Arrow keys", "move the active fish"],
  ["W A S D", "move the big fish"],
  ["I J K L", "move the small fish"],
  ["Space", "switch the active fish"],
  ["Left-click", "select a fish"],
  ["Hold left-click", "swim toward the cursor"],
  ["Hold right-click", "push toward the cursor"],
  ["R", "restart the level"],
  ["P", "watch the solution replay"],
  ["F2 / F3", "save / load a position"],
  ["Esc", "back to the world map"],
  ["F1", "show / hide this help"],
];

/**
 * A modal controls-help popup shown on F1 and dismissed with Esc or the OK
 * button - an in-scene overlay (backdrop + panel + text + button), the same
 * owned-UI shape as PedometerUI/SaveSlotUI, not a separate Phaser scene.
 * The port used to paint the whole controls list permanently across the top
 * of every level; this replaces that with an on-demand popup so the room
 * stays unobstructed during play.
 */
export class HelpOverlay {
  private container?: Phaser.GameObjects.Container;

  constructor(
    private readonly scene: Phaser.Scene,
    private readonly width: number,
    private readonly height: number,
  ) {}

  get isShowing(): boolean {
    return this.container !== undefined;
  }

  toggle(): void {
    if (this.isShowing) this.hide();
    else this.show();
  }

  show(): void {
    if (this.isShowing) return;

    const backdrop = this.scene.add
      .rectangle(0, 0, this.width, this.height, 0x000000, 0.6)
      .setOrigin(0, 0)
      .setInteractive(); // absorbs clicks so gameplay underneath isn't hit

    // Build the text first, then size/position the panel around its measured
    // extents - avoids magic numbers that overlap at some room sizes.
    const PAD = 20;
    const COL_GAP = 24;
    const title = this.scene.add
      .text(0, 0, "Controls", crispText({
        fontFamily: "sans-serif",
        fontSize: "20px",
        color: "#ffc618",
        fontStyle: "bold",
      }))
      .setOrigin(0.5, 0);

    const rowStyle = crispText({
      fontFamily: "monospace",
      fontSize: "13px",
      color: "#ffffff",
      lineSpacing: 4,
    });
    const keyCol = this.scene.add
      .text(0, 0, HELP_LINES.map(([key]) => key).join("\n"), { ...rowStyle, color: "#9fd0ff" })
      .setOrigin(0, 0);
    const descCol = this.scene.add
      .text(0, 0, HELP_LINES.map(([, desc]) => desc).join("\n"), rowStyle)
      .setOrigin(0, 0);

    const okButton = this.scene.add
      .text(0, 0, "OK", crispText({
        fontFamily: "sans-serif",
        fontSize: "15px",
        color: "#ffffff",
        backgroundColor: "#2a5a8aff",
        padding: { x: 18, y: 5 },
      }))
      .setOrigin(0.5, 0)
      .setInteractive({ useHandCursor: true })
      .on("pointerdown", () => this.hide());

    const textH = Math.max(keyCol.height, descCol.height);
    const contentW = keyCol.width + COL_GAP + descCol.width;
    const panelW = Math.max(contentW, title.width, okButton.width) + PAD * 2;
    const panelH = PAD + title.height + 14 + textH + 18 + okButton.height + PAD;

    const panel = this.scene.add
      .rectangle(0, 0, panelW, panelH, 0x0a1f33, 0.97)
      .setOrigin(0.5)
      .setStrokeStyle(2, 0xffc618, 0.9);

    // Lay everything out top-to-bottom from the panel's top edge.
    const top = -panelH / 2;
    const left = -contentW / 2;
    title.setPosition(0, top + PAD);
    const textTop = top + PAD + title.height + 14;
    keyCol.setPosition(left, textTop);
    descCol.setPosition(left + keyCol.width + COL_GAP, textTop);
    okButton.setPosition(0, panelH / 2 - PAD - okButton.height);

    this.container = this.scene.add
      .container(this.width / 2, this.height / 2, [panel, title, keyCol, descCol, okButton])
      .setDepth(3000);
    // Backdrop is full-screen at (0,0), so it lives outside the centered
    // container - keep it just under the container by depth.
    backdrop.setDepth(2999);
    this.container.setData("backdrop", backdrop);
  }

  hide(): void {
    (this.container?.getData("backdrop") as Phaser.GameObjects.Rectangle | undefined)?.destroy();
    this.container?.destroy();
    this.container = undefined;
  }
}
