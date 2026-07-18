import Phaser from "phaser";

import {
  loadSettings,
  saveSettings,
  GAME_SIZES,
  type DialogLang,
  type GameSize,
} from "../storage/settingsStorage";
import {
  serializeProgress,
  backupFilename,
  parseBackup,
  restoreProgress,
  MAX_BACKUP_BYTES,
} from "../storage/progressBackup";
import { crispText } from "./sceneUtils";
import { t } from "../i18n";

/**
 * The settings panel, reached from the world map's Options corner button -
 * legacy WorldMap::runOptions() -> MenuOptions (sound/music volume sliders,
 * language, subtitles; the original's speech/game-audio selector is omitted
 * per the user, since only cs/nl are converted). A modal owned-UI overlay in
 * the same shape as HelpOverlay/PedometerUI - not a separate scene. Each
 * control writes settingsStorage immediately (docs/038); music volume also
 * applies live via the onVolumeChange callback.
 */

/** Dutch isn't shipped for now (only cs content is packaged), so the cs/nl
 *  language switch is hidden - flip back to re-enable it. See docs/075. */
const SHOW_LANGUAGE = false;

const PANEL_W = 400;
/** Tall enough for all rows; one row shorter when the language row is hidden. */
const PANEL_H = SHOW_LANGUAGE ? 430 : 386;
/** Min gap kept between the panel and the room edges when scaling to fit. */
const FIT_MARGIN = 24;
const ROW_H = 44;
const SLIDER_W = 180;
/** X offset from a row's label to its control - wide enough for the localized
 *  row labels (e.g. Dutch "Ondertiteling", "Game progress") not to overlap. */
const CTRL_DX = 108;

/** Language names shown as their own endonyms - never translated. */
const LANGS: Array<{ code: DialogLang; label: string }> = [
  { code: "cs", label: "Čeština" },
  { code: "nl", label: "Nederlands" },
];

/** GameSize -> i18n key for the Standard/Large/Huge buttons. */
const GAME_SIZE_KEYS: Record<GameSize, string> = {
  1: "size_standard",
  1.5: "size_large",
  2: "size_huge",
};

export class OptionsOverlay {
  /** Full-screen dimmer behind the panel (absorbs clicks) - NOT scaled. */
  private backdrop?: Phaser.GameObjects.Rectangle;
  /** Holds the panel + all controls so they can be scaled down to fit a room
   *  narrower/shorter than the fixed panel (e.g. library) - see docs/069. */
  private container?: Phaser.GameObjects.Container;
  private escHandler?: (event: KeyboardEvent) => void;
  /** Status/result line for the Backup/Restore actions (docs/072). */
  private statusText?: Phaser.GameObjects.Text;
  /** Reused hidden file input for Restore, so cancelled dialogs don't leak
   *  elements. Created lazily, removed in hide(). */
  private fileInput?: HTMLInputElement;

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
    return this.container !== undefined;
  }

  show(): void {
    if (this.isShowing) return;
    const cx = this.width / 2;
    const cy = this.height / 2;
    const left = cx - PANEL_W / 2;
    const top = cy - PANEL_H / 2;

    // Full-room dimmer (kept out of the scaled container so it always covers the
    // whole room and absorbs clicks).
    this.backdrop = this.scene.add
      .rectangle(0, 0, this.width, this.height, 0x000000, 0.6)
      .setOrigin(0, 0)
      .setInteractive()
      .setDepth(2999);
    // Everything else goes in a container that's scaled down to fit rooms
    // narrower/shorter than the panel (docs/069).
    this.container = this.scene.add.container(0, 0).setDepth(3000);

    const panel = this.add(
      this.scene.add
        .rectangle(cx, cy, PANEL_W, PANEL_H, 0x0a1f33, 0.98)
        .setStrokeStyle(2, 0xffc618, 0.9)
        .setDepth(3000),
    );
    void panel;

    this.add(
      this.scene.add
        .text(cx, top + 16, t("opt_title"), crispText({
          fontFamily: "sans-serif",
          fontSize: "20px",
          color: "#ffc618",
          fontStyle: "bold",
        }))
        .setOrigin(0.5, 0)
        .setDepth(3001),
    );

    let y = top + 60;
    if (SHOW_LANGUAGE) {
      this.buildLanguageRow(left + 24, y);
      y += ROW_H;
    }
    this.buildGameSizeRow(left + 24, y);
    y += ROW_H;
    this.buildVolumeRow(left + 24, y, t("opt_music"), () => loadSettings().musicVolume, (v) => {
      saveSettings({ musicVolume: v });
      this.onVolumeChange();
    });
    y += ROW_H;
    this.buildVolumeRow(left + 24, y, t("opt_sound"), () => loadSettings().soundVolume, (v) =>
      saveSettings({ soundVolume: v }),
    );
    y += ROW_H;
    this.buildSubtitlesRow(left + 24, y);
    y += ROW_H;
    this.buildProgressRow(left + 24, y);

    // Backup/Restore status + result line (docs/072).
    this.statusText = this.scene.add
      .text(cx, y + 30, "", crispText({
        fontFamily: "sans-serif",
        fontSize: "12px",
        color: "#cfe6ff",
        align: "center",
        wordWrap: { width: PANEL_W - 48 },
      }))
      .setOrigin(0.5, 0)
      .setDepth(3001);
    this.add(this.statusText);

    // Back button + Esc.
    const back = this.scene.add
      .text(cx, cy + PANEL_H / 2 - 34, t("menu_back"), crispText({
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

    // Scale the panel down (around its centre) if the room is narrower/shorter
    // than the fixed panel + margin - otherwise the panel is clipped by the
    // room-sized camera in tall/narrow levels like library (docs/069). Wide
    // rooms and the 640x480 world map keep scale 1.
    const s = Math.min(1, (this.width - FIT_MARGIN) / PANEL_W, (this.height - FIT_MARGIN) / PANEL_H);
    this.container.setScale(s).setPosition(cx * (1 - s), cy * (1 - s));

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
    this.backdrop?.destroy();
    this.backdrop = undefined;
    this.container?.destroy(); // destroys its children too
    this.container = undefined;
    this.statusText = undefined; // was a child of the container, already destroyed
    this.fileInput?.remove();
    this.fileInput = undefined;
  }

  /** Add an object to the (scaled) panel container. */
  private add<T extends Phaser.GameObjects.GameObject>(obj: T): T {
    this.container?.add(obj);
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
    this.label(x, y, t("opt_language"));
    let bx = x + CTRL_DX;
    const current = loadSettings().lang;
    const buttons: Array<{ code: DialogLang; text: Phaser.GameObjects.Text }> = [];
    const refresh = () => {
      const active = loadSettings().lang;
      for (const b of buttons) {
        b.text.setBackgroundColor(b.code === active ? "#2a7a3a" : "#333c48");
      }
    };
    for (const { code, label } of LANGS) {
      const btn = this.scene.add
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
      this.add(btn);
      buttons.push({ code, text: btn });
      bx += btn.width + 10;
    }
  }

  /** Standard/Large/Huge on-screen size (docs/064) - same button-row shape as
   *  the language row. Applies live via onGameSizeChange (scale.setZoom). */
  private buildGameSizeRow(x: number, y: number): void {
    this.label(x, y, t("opt_gamesize"));
    let bx = x + CTRL_DX;
    const buttons: Array<{ size: GameSize; text: Phaser.GameObjects.Text }> = [];
    const refresh = () => {
      const active = loadSettings().gameSize;
      for (const b of buttons) {
        b.text.setBackgroundColor(b.size === active ? "#2a7a3a" : "#333c48");
      }
    };
    for (const size of GAME_SIZES) {
      const active = loadSettings().gameSize;
      const btn = this.scene.add
        .text(bx, y, t(GAME_SIZE_KEYS[size]), crispText({
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
      this.add(btn);
      buttons.push({ size, text: btn });
      bx += btn.width + 8;
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
    const trackX = x + CTRL_DX;
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

    // Fraction from a pointer's WORLD x, using the track's world-space bounds
    // (getBounds() accounts for the container's fit-scale, docs/069) so the
    // slider stays correct when the panel is scaled down in a narrow room.
    const fromPointerX = (worldX: number): number => {
      const b = track.getBounds();
      return ((worldX - b.x) / b.width) * 100;
    };
    knob.on("drag", (p: Phaser.Input.Pointer) => apply(fromPointerX(p.worldX), true));
    track.on("pointerdown", (p: Phaser.Input.Pointer) => apply(fromPointerX(p.worldX), true));
  }

  private buildSubtitlesRow(x: number, y: number): void {
    this.label(x, y, t("opt_subtitles"));
    const toggle = this.scene.add
      .text(x + CTRL_DX, y, "", crispText({
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
      toggle.setText(on ? t("toggle_on") : t("toggle_off")).setBackgroundColor(on ? "#2a7a3a" : "#7a3a3a");
    };
    refresh();
    toggle.on("pointerdown", () => {
      saveSettings({ subtitles: !loadSettings().subtitles });
      refresh();
    });
  }

  /** Backup / Restore game progress (docs/072) - two styled buttons that
   *  download / import the JSON progress file. */
  private buildProgressRow(x: number, y: number): void {
    this.label(x, y, t("opt_progress"));
    let bx = x + CTRL_DX;
    const button = (text: string, handler: () => void): void => {
      const btn = this.scene.add
        .text(bx, y, text, crispText({
          fontFamily: "sans-serif",
          fontSize: "13px",
          color: "#ffffff",
          backgroundColor: "#2a5a8a",
          padding: { x: 10, y: 4 },
        }))
        .setOrigin(0, 0.5)
        .setDepth(3001)
        .setInteractive({ useHandCursor: true })
        .on("pointerdown", handler);
      this.add(btn);
      bx += btn.width + 8;
    };
    button(t("btn_backup"), () => this.doBackup());
    button(t("btn_restore"), () => this.doRestore());
  }

  private setStatus(text: string, color: string): void {
    this.statusText?.setText(text).setColor(color);
  }

  /** Serialize all progress and trigger a browser download of the JSON file. */
  private doBackup(): void {
    try {
      const blob = new Blob([serializeProgress()], { type: "application/json" });
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = backupFilename();
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      // No success toast - the browser's own download indicator confirms it (docs/073).
    } catch (error) {
      console.error("Progress backup failed", error);
      this.setStatus(t("backup_failed"), "#ff9090");
    }
  }

  /** Open a file picker and restore progress from the chosen JSON file. */
  private doRestore(): void {
    if (!this.fileInput) {
      const input = document.createElement("input");
      input.type = "file";
      input.accept = "application/json,.json";
      input.style.display = "none";
      input.addEventListener("change", () => {
        const file = input.files?.[0] ?? null;
        input.value = ""; // allow re-picking the same file later
        void this.handleRestoreFile(file);
      });
      document.body.appendChild(input);
      this.fileInput = input;
    }
    this.fileInput.click();
  }

  private async handleRestoreFile(file: File | null): Promise<void> {
    if (!file) return;
    if (file.size > MAX_BACKUP_BYTES) {
      this.setStatus(t("backup_err_big"), "#ff9090");
      return;
    }
    let text: string;
    try {
      text = await file.text();
    } catch {
      this.setStatus(t("restore_read_err"), "#ff9090");
      return;
    }
    const parsed = parseBackup(text);
    if (!parsed.ok) {
      // parsed.error is an i18n key (docs/073).
      this.setStatus(t(parsed.error), "#ff9090");
      return;
    }
    try {
      const report = await restoreProgress(parsed.value, {
        onProgress: (done, total) => this.setStatus(t("backup_validating", done, total), "#e6e08a"),
      });
      const rejected = report.solvedRejected.length;
      const summary =
        t("restore_done", report.solvedAccepted.length, report.savesAccepted) +
        (rejected > 0 ? t("restore_rejected", rejected) : "") +
        ". " +
        t("reloading");
      this.setStatus(summary, "#a8e6a0");
      // Reload so the world map re-derives node states and playtime/settings
      // re-read from the freshly written storage (docs/072).
      window.setTimeout(() => window.location.reload(), 1400);
    } catch (error) {
      console.error("Progress restore failed", error);
      this.setStatus(t("restore_failed"), "#ff9090");
    }
  }
}
