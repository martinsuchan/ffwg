import Phaser from "phaser";

import {
  loadSettings,
  saveSettings,
  GAME_SIZES,
  type DialogLang,
  type GameSize,
} from "../storage/settingsStorage";
import { crispText } from "./sceneUtils";

/**
 * The settings panel, reached from the world map's Options corner button -
 * legacy WorldMap::runOptions() -> MenuOptions (sound/music volume sliders,
 * language, subtitles; the original's speech/game-audio selector is omitted
 * per the user, since only cs/nl are converted). A modal owned-UI overlay in
 * the same shape as HelpOverlay/PedometerUI - not a separate scene. Each
 * control writes settingsStorage immediately (docs/038); music volume also
 * applies live via the onVolumeChange callback.
 */

const PANEL_W = 400;
const PANEL_H = 364;
const ROW_H = 44;
const SLIDER_W = 180;

const LANGS: Array<{ code: DialogLang; label: string }> = [
  { code: "cs", label: "Čeština" },
  { code: "nl", label: "Nederlands" },
];

const GAME_SIZE_LABELS: Record<GameSize, string> = {
  1: "Standard",
  1.5: "Large",
  2: "Huge",
};

export class OptionsOverlay {
  private objects: Phaser.GameObjects.GameObject[] = [];
  private escHandler?: (event: KeyboardEvent) => void;

  constructor(
    private readonly scene: Phaser.Scene,
    private readonly width: number,
    private readonly height: number,
    /** Called after the music volume changes, so the caller can update the
     *  currently-playing track live (AudioManager.refreshMusicVolume). */
    private readonly onVolumeChange: () => void,
    /** Called after the game-size (zoom) changes, so the caller can apply it
     *  live via scale.setZoom - see docs/064. */
    private readonly onGameSizeChange: () => void,
  ) {}

  get isShowing(): boolean {
    return this.objects.length > 0;
  }

  show(): void {
    if (this.isShowing) return;
    const cx = this.width / 2;
    const cy = this.height / 2;
    const left = cx - PANEL_W / 2;
    const top = cy - PANEL_H / 2;

    const backdrop = this.add(
      this.scene.add
        .rectangle(0, 0, this.width, this.height, 0x000000, 0.6)
        .setOrigin(0, 0)
        .setInteractive()
        .setDepth(2999),
    );
    void backdrop;
    const panel = this.add(
      this.scene.add
        .rectangle(cx, cy, PANEL_W, PANEL_H, 0x0a1f33, 0.98)
        .setStrokeStyle(2, 0xffc618, 0.9)
        .setDepth(3000),
    );
    void panel;

    this.add(
      this.scene.add
        .text(cx, top + 16, "Options", crispText({
          fontFamily: "sans-serif",
          fontSize: "20px",
          color: "#ffc618",
          fontStyle: "bold",
        }))
        .setOrigin(0.5, 0)
        .setDepth(3001),
    );

    let y = top + 60;
    this.buildLanguageRow(left + 24, y);
    y += ROW_H;
    this.buildGameSizeRow(left + 24, y);
    y += ROW_H;
    this.buildVolumeRow(left + 24, y, "Music", () => loadSettings().musicVolume, (v) => {
      saveSettings({ musicVolume: v });
      this.onVolumeChange();
    });
    y += ROW_H;
    this.buildVolumeRow(left + 24, y, "Sound", () => loadSettings().soundVolume, (v) =>
      saveSettings({ soundVolume: v }),
    );
    y += ROW_H;
    this.buildSubtitlesRow(left + 24, y);

    // Back button + Esc.
    const back = this.scene.add
      .text(cx, cy + PANEL_H / 2 - 34, "Back", crispText({
        fontFamily: "sans-serif",
        fontSize: "15px",
        color: "#ffffff",
        backgroundColor: "#2a5a8a",
        padding: { x: 22, y: 6 },
      }))
      .setOrigin(0.5, 0)
      .setDepth(3001)
      .setInteractive({ useHandCursor: true })
      .on("pointerdown", () => this.hide());
    this.add(back);

    this.escHandler = (e: KeyboardEvent) => {
      if (e.key === "Escape") this.hide();
    };
    window.addEventListener("keydown", this.escHandler);
  }

  hide(): void {
    if (this.escHandler) {
      window.removeEventListener("keydown", this.escHandler);
      this.escHandler = undefined;
    }
    for (const o of this.objects) o.destroy();
    this.objects = [];
  }

  private add<T extends Phaser.GameObjects.GameObject>(obj: T): T {
    this.objects.push(obj);
    return obj;
  }

  private label(x: number, y: number, text: string): void {
    this.add(
      this.scene.add
        .text(x, y, text, crispText({ fontFamily: "sans-serif", fontSize: "14px", color: "#cfe6ff" }))
        .setOrigin(0, 0.5)
        .setDepth(3001),
    );
  }

  private buildLanguageRow(x: number, y: number): void {
    this.label(x, y, "Language");
    let bx = x + 90;
    const current = loadSettings().lang;
    const buttons: Array<{ code: DialogLang; text: Phaser.GameObjects.Text }> = [];
    const refresh = () => {
      const active = loadSettings().lang;
      for (const b of buttons) {
        b.text.setBackgroundColor(b.code === active ? "#2a7a3a" : "#333c48");
      }
    };
    for (const { code, label } of LANGS) {
      const t = this.scene.add
        .text(bx, y, label, crispText({
          fontFamily: "sans-serif",
          fontSize: "13px",
          color: "#ffffff",
          backgroundColor: code === current ? "#2a7a3a" : "#333c48",
          padding: { x: 10, y: 4 },
        }))
        .setOrigin(0, 0.5)
        .setDepth(3001)
        .setInteractive({ useHandCursor: true })
        .on("pointerdown", () => {
          saveSettings({ lang: code });
          refresh();
        });
      this.add(t);
      buttons.push({ code, text: t });
      bx += t.width + 10;
    }
  }

  /** Standard/Large/Huge on-screen size (docs/064) - same button-row shape as
   *  the language row. Applies live via onGameSizeChange (scale.setZoom). */
  private buildGameSizeRow(x: number, y: number): void {
    this.label(x, y, "Game size");
    let bx = x + 90;
    const buttons: Array<{ size: GameSize; text: Phaser.GameObjects.Text }> = [];
    const refresh = () => {
      const active = loadSettings().gameSize;
      for (const b of buttons) {
        b.text.setBackgroundColor(b.size === active ? "#2a7a3a" : "#333c48");
      }
    };
    for (const size of GAME_SIZES) {
      const active = loadSettings().gameSize;
      const t = this.scene.add
        .text(bx, y, GAME_SIZE_LABELS[size], crispText({
          fontFamily: "sans-serif",
          fontSize: "13px",
          color: "#ffffff",
          backgroundColor: size === active ? "#2a7a3a" : "#333c48",
          padding: { x: 10, y: 4 },
        }))
        .setOrigin(0, 0.5)
        .setDepth(3001)
        .setInteractive({ useHandCursor: true })
        .on("pointerdown", () => {
          saveSettings({ gameSize: size });
          refresh();
          this.onGameSizeChange();
        });
      this.add(t);
      buttons.push({ size, text: t });
      bx += t.width + 8;
    }
  }

  private buildVolumeRow(
    x: number,
    y: number,
    label: string,
    get: () => number,
    set: (v: number) => void,
  ): void {
    this.label(x, y, label);
    const trackX = x + 90;
    const trackW = SLIDER_W;
    const track = this.scene.add
      .rectangle(trackX, y, trackW, 6, 0x11202e)
      .setOrigin(0, 0.5)
      .setStrokeStyle(1, 0x3a5a78)
      .setDepth(3001)
      .setInteractive({ useHandCursor: true });
    this.add(track);
    const fill = this.add(
      this.scene.add.rectangle(trackX, y, 0, 6, 0x4fc3ff).setOrigin(0, 0.5).setDepth(3002),
    );
    const knob = this.scene.add
      .circle(trackX, y, 8, 0xffffff)
      .setStrokeStyle(1, 0x2a5a8a)
      .setDepth(3003)
      .setInteractive({ useHandCursor: true, draggable: true });
    this.add(knob);
    const valueText = this.add(
      this.scene.add
        .text(trackX + trackW + 12, y, "", crispText({
          fontFamily: "sans-serif",
          fontSize: "13px",
          color: "#ffffff",
        }))
        .setOrigin(0, 0.5)
        .setDepth(3001),
    );

    const apply = (value: number, persist: boolean): void => {
      const v = Math.max(0, Math.min(100, Math.round(value)));
      knob.setX(trackX + (v / 100) * trackW);
      fill.setSize((v / 100) * trackW, 6);
      valueText.setText(`${v}%`);
      if (persist) set(v);
    };
    apply(get(), false);

    const fromPointerX = (px: number): number => ((px - trackX) / trackW) * 100;
    knob.on("drag", (_p: Phaser.Input.Pointer, dragX: number) => apply(fromPointerX(dragX), true));
    track.on("pointerdown", (p: Phaser.Input.Pointer) => apply(fromPointerX(p.worldX), true));
  }

  private buildSubtitlesRow(x: number, y: number): void {
    this.label(x, y, "Subtitles");
    const toggle = this.scene.add
      .text(x + 90, y, "", crispText({
        fontFamily: "sans-serif",
        fontSize: "13px",
        color: "#ffffff",
        padding: { x: 14, y: 4 },
      }))
      .setOrigin(0, 0.5)
      .setDepth(3001)
      .setInteractive({ useHandCursor: true });
    this.add(toggle);
    const refresh = () => {
      const on = loadSettings().subtitles;
      toggle.setText(on ? "On" : "Off").setBackgroundColor(on ? "#2a7a3a" : "#7a3a3a");
    };
    refresh();
    toggle.on("pointerdown", () => {
      saveSettings({ subtitles: !loadSettings().subtitles });
      refresh();
    });
  }
}
