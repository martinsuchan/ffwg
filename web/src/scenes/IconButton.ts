import Phaser from "phaser";

/** The replay transport icons (docs/079). */
export type IconKind = "pause" | "step" | "play" | "fast" | "export";

const BUTTON_W = 38;
const BUTTON_H = 30;
const RADIUS = 7;
const IDLE_FILL = 0x000000;
const IDLE_ALPHA = 0.63;
const SELECTED_FILL = 0x2a7a3a;
const HOVER_STROKE = 0xffc618;
const ICON_COLOR = 0xffffff;

/**
 * A small rounded media-control button with a vector icon, drawn with Graphics
 * so it stays sharp at every render scale (applyRenderScale's camera zoom,
 * docs/064) - replaces the font glyphs (⏸ ⏭ ▶ ⏩ ⬇), whose look and weight
 * varied per OS font. `selected` = the current play state (green); hover shows
 * a gold outline. Positioned by its centre.
 */
export class IconButton extends Phaser.GameObjects.Container {
  private readonly bg: Phaser.GameObjects.Graphics;
  private selected = false;
  private hovered = false;

  constructor(scene: Phaser.Scene, x: number, y: number, kind: IconKind, onClick: () => void) {
    super(scene, x, y);
    this.bg = scene.add.graphics();
    const icon = scene.add.graphics();
    drawIcon(icon, kind);
    this.add([this.bg, icon]);
    this.setSize(BUTTON_W, BUTTON_H);
    this.setInteractive({ useHandCursor: true });
    this.on("pointerdown", onClick);
    this.on("pointerover", () => {
      this.hovered = true;
      this.redraw();
    });
    this.on("pointerout", () => {
      this.hovered = false;
      this.redraw();
    });
    this.redraw();
    scene.add.existing(this);
  }

  static readonly WIDTH = BUTTON_W;
  static readonly HEIGHT = BUTTON_H;

  setSelected(selected: boolean): this {
    if (selected !== this.selected) {
      this.selected = selected;
      this.redraw();
    }
    return this;
  }

  private redraw(): void {
    const g = this.bg;
    g.clear();
    g.fillStyle(this.selected ? SELECTED_FILL : IDLE_FILL, this.selected ? 1 : IDLE_ALPHA);
    g.fillRoundedRect(-BUTTON_W / 2, -BUTTON_H / 2, BUTTON_W, BUTTON_H, RADIUS);
    if (this.hovered) {
      g.lineStyle(1.5, HOVER_STROKE, 1);
      g.strokeRoundedRect(-BUTTON_W / 2, -BUTTON_H / 2, BUTTON_W, BUTTON_H, RADIUS);
    }
  }
}

/** Draws `kind` centred on (0, 0), about 16 px tall. */
function drawIcon(g: Phaser.GameObjects.Graphics, kind: IconKind): void {
  g.fillStyle(ICON_COLOR, 1);
  switch (kind) {
    case "play":
      g.fillTriangle(-5, -8, -5, 8, 8, 0);
      break;
    case "pause":
      g.fillRoundedRect(-6.5, -7.5, 4.5, 15, 1);
      g.fillRoundedRect(2, -7.5, 4.5, 15, 1);
      break;
    case "step":
      g.fillTriangle(-7, -7.5, -7, 7.5, 3.5, 0);
      g.fillRoundedRect(4, -7.5, 3.5, 15, 1);
      break;
    case "fast":
      g.fillTriangle(-9, -7, -9, 7, 0, 0);
      g.fillTriangle(0, -7, 0, 7, 9, 0);
      break;
    case "export":
      // Arrow down into a tray.
      g.fillRect(-1.5, -9, 3, 8);
      g.fillTriangle(-6, -2, 6, -2, 0, 4.5);
      g.lineStyle(2.5, ICON_COLOR, 1);
      g.beginPath();
      g.moveTo(-7.5, 3);
      g.lineTo(-7.5, 8);
      g.lineTo(7.5, 8);
      g.lineTo(7.5, 3);
      g.strokePath();
      break;
  }
}
