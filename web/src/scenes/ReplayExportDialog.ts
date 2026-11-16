import Phaser from "phaser";

import type { CodecSupport, VideoLayout } from "./replayExport";
import { crispText } from "./sceneUtils";
import { t } from "../i18n";

/**
 * Confirmation dialog for the replay → MP4 export (ReplayScene's ⬇ button).
 * Explains what will happen (the replay re-runs off-screen frame by frame, then
 * the MP4 downloads), shows the estimated video length and output format, and
 * offers two toggles: step counter and level music (off = no audio track).
 *
 * Same look and mechanics as OptionsOverlay: full-room dimmer that absorbs
 * clicks, a panel container scaled down to fit narrow/short rooms (docs/069),
 * `crispText` everywhere, world/room coordinates (the camera zoom of
 * applyRenderScale is transparent to it). Unlike OptionsOverlay the panel
 * height flows from its (wrapped, localized) content.
 *
 * Keyboard: while showing, a capture-phase window listener swallows keydowns
 * (preventDefault -> Phaser's KeyboardManager ignores them), so the scene's own
 * Esc/R/Space handlers can't fire underneath. Esc = cancel, Enter = export.
 * F-keys and modified keys pass through untouched.
 */

export interface ReplayExportOptions {
  showSteps: boolean;
  music: boolean;
}

export interface ReplayExportInfo {
  /** Shown verbatim as the video length - pass the full video duration
   *  (estimateReplayMs + LEAD_IN_MS + TAIL_MS). */
  estimateMs: number;
  layout: VideoLayout;
  codecs: CodecSupport;
  moveCount: number;
}

const PANEL_W = 440;
const PAD = 24;
const INNER_W = PANEL_W - 2 * PAD;
/** Min gap kept between the panel and the room edges when scaling to fit. */
const FIT_MARGIN = 24;
const ROW_H = 38;
/** X offset from a toggle row's label to its On/Off button - wide enough for
 *  the localized labels ("Muziek van het veld"). */
const CTRL_DX = 170;
const DEPTH = 3000;

const BODY_STYLE = {
  fontFamily: "sans-serif",
  fontSize: "13px",
  color: "#cfe6ff",
  lineSpacing: 2,
  wordWrap: { width: INNER_W },
} as const;

/** "1 min 5 s" / "42 s" for a millisecond duration. */
function formatDuration(ms: number): string {
  const total = Math.max(1, Math.round(ms / 1000));
  if (total < 60) return t("export_sec", total);
  return t("export_min_sec", Math.floor(total / 60), total % 60);
}

export class ReplayExportDialog {
  /** Full-room dimmer behind the panel (absorbs clicks) - NOT scaled. */
  private backdrop?: Phaser.GameObjects.Rectangle;
  /** Panel + all content, scaled down to fit small rooms (docs/069). */
  private container?: Phaser.GameObjects.Container;
  private keyHandler?: (event: KeyboardEvent) => void;
  private readonly shutdownHandler = (): void => this.hide();

  private showSteps = true;
  private music = true;
  private formatText?: Phaser.GameObjects.Text;
  private opusNote?: Phaser.GameObjects.Text;

  constructor(
    private readonly scene: Phaser.Scene,
    private readonly roomWidthPx: number,
    private readonly roomHeightPx: number,
    private readonly info: ReplayExportInfo,
    private readonly onConfirm: (opts: ReplayExportOptions) => void,
    private readonly onCancel: () => void,
  ) {}

  get isShowing(): boolean {
    return this.container !== undefined;
  }

  show(): void {
    if (this.isShowing) return;
    const { codecs } = this.info;
    const supported = codecs.video !== null;
    this.showSteps = true;
    this.music = codecs.audio !== null;

    const cx = this.roomWidthPx / 2;
    const cy = this.roomHeightPx / 2;

    this.backdrop = this.scene.add
      .rectangle(0, 0, this.roomWidthPx, this.roomHeightPx, 0x000000, 0.6)
      .setOrigin(0, 0)
      .setInteractive()
      .setDepth(DEPTH - 1);
    this.container = this.scene.add.container(0, 0).setDepth(DEPTH);

    // Container-local coords: x = 0 is the panel centre, y = 0 its top edge.
    // The panel goes in first (child order = draw order inside a container)
    // and is sized once the content height is known.
    const panel = this.add(
      this.scene.add
        .rectangle(0, 0, PANEL_W, 100, 0x0a1f33, 0.98)
        .setOrigin(0.5, 0)
        .setStrokeStyle(2, 0xffc618, 0.9),
    );
    const left = -PANEL_W / 2 + PAD;

    let y = 16;
    const title = this.add(
      this.scene.add
        .text(0, y, t("export_title"), crispText({
          fontFamily: "sans-serif",
          fontSize: "20px",
          color: "#ffc618",
          fontStyle: "bold",
        }))
        .setOrigin(0.5, 0),
    );
    y += title.height + 12;

    const textBlock = (text: string, gap: number, style: Phaser.Types.GameObjects.Text.TextStyle = BODY_STYLE) => {
      const obj = this.add(this.scene.add.text(left, y, text, crispText(style)).setOrigin(0, 0));
      y += obj.height + gap;
      return obj;
    };

    if (supported) {
      textBlock(t("export_body"), 12);
      textBlock(t("export_length", formatDuration(this.info.estimateMs)), 4, {
        ...BODY_STYLE,
        color: "#ffffff",
        fontStyle: "bold",
      });
      textBlock(t("export_time"), 4);
      this.formatText = textBlock(this.formatLine(), 8, { ...BODY_STYLE, color: "#ffffff" });

      this.buildToggleRow(left, y + ROW_H / 2, t("export_opt_steps"), true, () => this.showSteps, (v) => {
        this.showSteps = v;
      });
      y += ROW_H;
      this.buildToggleRow(left, y + ROW_H / 2, t("export_opt_music"), codecs.audio !== null, () => this.music, (v) => {
        this.music = v;
        this.refreshAudioInfo();
      });
      y += ROW_H + 6;

      // Space is reserved whenever opus is possible, so toggling music only
      // hides/shows the note and never reflows or rescales the panel.
      if (codecs.audio === "opus") {
        this.opusNote = textBlock(t("export_opus_note"), 12, {
          ...BODY_STYLE,
          color: "#ffb347",
          fontStyle: "bold",
        });
      } else {
        y += 6;
      }
      this.refreshAudioInfo();

      y = this.buildButtons(y, [
        { label: t("export_start"), bg: "#2a7a3a", onClick: () => this.confirm() },
        { label: t("export_cancel"), bg: "#2a5a8a", onClick: () => this.cancel() },
      ]);
    } else {
      textBlock(t("export_unsupported"), 16, { ...BODY_STYLE, color: "#ff9090", fontStyle: "bold" });
      y = this.buildButtons(y, [{ label: t("export_close"), bg: "#2a5a8a", onClick: () => this.cancel() }]);
    }

    const panelH = y + 18;
    panel.setSize(PANEL_W, panelH);

    // Scale the panel down (around the room centre) if the room is narrower/
    // shorter than the panel + margin (docs/069).
    const s = Math.min(1, (this.roomWidthPx - FIT_MARGIN) / PANEL_W, (this.roomHeightPx - FIT_MARGIN) / panelH);
    this.container.setScale(s).setPosition(cx, cy - (panelH * s) / 2);

    this.keyHandler = (e: KeyboardEvent) => {
      if (e.ctrlKey || e.altKey || e.metaKey || /^F\d+$/.test(e.key) || e.key === "Tab") return;
      // Swallow it so the scene's own keyboard handlers ignore it (Phaser
      // skips defaultPrevented events).
      e.preventDefault();
      if (e.repeat) return;
      if (e.key === "Escape") this.cancel();
      else if (e.key === "Enter" && supported) this.confirm();
    };
    window.addEventListener("keydown", this.keyHandler, true);
    this.scene.events.once(Phaser.Scenes.Events.SHUTDOWN, this.shutdownHandler);
  }

  /** Destroys everything show() created; safe to call repeatedly. */
  hide(): void {
    if (this.keyHandler) {
      window.removeEventListener("keydown", this.keyHandler, true);
      this.keyHandler = undefined;
    }
    this.scene.events.off(Phaser.Scenes.Events.SHUTDOWN, this.shutdownHandler);
    this.backdrop?.destroy();
    this.backdrop = undefined;
    this.container?.destroy(); // destroys its children too
    this.container = undefined;
    this.formatText = undefined;
    this.opusNote = undefined;
  }

  /** Hides first, so the callback already sees `isShowing === false` and no
   *  dialog object can end up in an exported frame. */
  private confirm(): void {
    if (!this.isShowing) return;
    const opts: ReplayExportOptions = {
      showSteps: this.showSteps,
      music: this.music && this.info.codecs.audio !== null,
    };
    this.hide();
    this.onConfirm(opts);
  }

  private cancel(): void {
    if (!this.isShowing) return;
    this.hide();
    this.onCancel();
  }

  private add<T extends Phaser.GameObjects.GameObject>(obj: T): T {
    this.container?.add(obj);
    return obj;
  }

  private formatLine(): string {
    const { layout, codecs } = this.info;
    const codecText =
      this.music && codecs.audio !== null
        ? codecs.audio === "aac"
          ? "H.264 + AAC"
          : "H.264 + Opus"
        : t("export_no_audio");
    return t("export_format", layout.width, layout.height, codecText);
  }

  private refreshAudioInfo(): void {
    this.formatText?.setText(this.formatLine());
    this.opusNote?.setVisible(this.music);
  }

  /** Label + On/Off toggle in OptionsOverlay's style; `enabled === false`
   *  renders it greyed out, forced Off and non-interactive. */
  private buildToggleRow(
    x: number,
    y: number,
    label: string,
    enabled: boolean,
    get: () => boolean,
    set: (v: boolean) => void,
  ): void {
    this.add(
      this.scene.add
        .text(x, y, label, crispText({
          fontFamily: "sans-serif",
          fontSize: "14px",
          color: enabled ? "#cfe6ff" : "#7a8a99",
        }))
        .setOrigin(0, 0.5),
    );
    const toggle = this.add(
      this.scene.add
        .text(x + CTRL_DX, y, "", crispText({
          fontFamily: "sans-serif",
          fontSize: "13px",
          color: "#ffffff",
          padding: { x: 14, y: 4 },
        }))
        .setOrigin(0, 0.5),
    );
    if (!enabled) {
      toggle.setText(t("toggle_off")).setBackgroundColor("#333c48").setColor("#8a96a3");
      return;
    }
    const refresh = () => {
      const on = get();
      toggle.setText(on ? t("toggle_on") : t("toggle_off")).setBackgroundColor(on ? "#2a7a3a" : "#7a3a3a");
    };
    refresh();
    toggle.setInteractive({ useHandCursor: true }).on("pointerdown", () => {
      set(!get());
      refresh();
    });
  }

  /** A centred row of buttons starting at `y`; returns the y below it. */
  private buildButtons(y: number, specs: Array<{ label: string; bg: string; onClick: () => void }>): number {
    const gap = 16;
    const buttons = specs.map((spec) =>
      this.add(
        this.scene.add
          .text(0, y, spec.label, crispText({
            fontFamily: "sans-serif",
            fontSize: "15px",
            color: "#ffffff",
            backgroundColor: spec.bg,
            padding: { x: 22, y: 6 },
          }))
          .setOrigin(0, 0)
          .setInteractive({ useHandCursor: true })
          .on("pointerdown", spec.onClick),
      ),
    );
    const total = buttons.reduce((sum, b) => sum + b.width, 0) + gap * (buttons.length - 1);
    let bx = -total / 2;
    let h = 0;
    for (const b of buttons) {
      b.setX(bx);
      bx += b.width + gap;
      h = Math.max(h, b.height);
    }
    return y + h;
  }
}
