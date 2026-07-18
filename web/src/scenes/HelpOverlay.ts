import Phaser from "phaser";

import { crispText } from "./sceneUtils";
import { t } from "../i18n";

/** The controls reference, shown only on demand (F1). Each line is a fixed key
 *  label (not translated) + an i18n key for the localized description (docs/073),
 *  rendered in two aligned columns. */
const HELP_LINES: Array<[string, string]> = [
  ["Arrow keys", "help_move_active"],
  ["W A S D", "help_move_big"],
  ["I J K L", "help_move_small"],
  ["Space", "help_switch"],
  ["Left-click", "help_select"],
  ["Hold left-click", "help_swim"],
  ["Hold right-click", "help_push"],
  ["Backspace", "help_restart"],
  ["P", "help_replay"],
  ["F2 / F3", "help_saveload"],
  ["F5 / F6", "help_toggles"],
  ["F10", "help_settings"],
  ["F11", "help_fullscreen"],
  ["Esc", "help_backmap"],
  ["F1", "help_help"],
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
      .text(0, 0, t("help_title"), crispText({
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
      .text(0, 0, HELP_LINES.map(([, descKey]) => t(descKey)).join("\n"), rowStyle)
      .setOrigin(0, 0);

    const okButton = this.scene.add
      .text(0, 0, t("help_ok"), crispText({
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
    // Scale the panel down (around its centre) if the room is narrower/shorter
    // than the panel, so it isn't clipped by the room-sized camera in tall/narrow
    // levels like library (docs/069). Container is centred, so setScale is enough.
    const fit = Math.min(1, (this.width - 16) / panelW, (this.height - 16) / panelH);
    this.container.setScale(fit);
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
