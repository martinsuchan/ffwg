import { t } from "../i18n";
import { parseMovesInput } from "../game/SolutionDiff";

/** Where one side of the compare view takes its solution from (docs/086). */
export type SourceKind = "best" | "old" | "mine" | "paste";

export interface SourceChoice {
  kind: SourceKind;
  /** The pasted text (only for "paste"). */
  text: string;
}

/** One line of the result, already worded by the scene. */
export interface DiffRow {
  text: string;
  /** Colour of the row's marker, as on the timeline. */
  color: string;
}

export interface CompareUIResult {
  sideA: string;
  sideB: string;
  summary: string;
  rows: DiffRow[];
}

const PANEL_STYLE: Partial<CSSStyleDeclaration> = {
  position: "fixed",
  left: "50%",
  top: "50%",
  transform: "translate(-50%, -50%)",
  zIndex: "10000", // above the fullscreen container (9999, fullscreen.ts)
  boxSizing: "border-box",
  width: "min(760px, calc(100vw - 32px))",
  maxHeight: "calc(100vh - 32px)",
  overflowY: "auto",
  padding: "16px 20px 18px",
  background: "#0a1f33",
  border: "2px solid #ffc618",
  color: "#ffffff",
  fontFamily: "sans-serif",
  fontSize: "14px",
  lineHeight: "1.4",
  textAlign: "left",
};

const BUTTON_STYLE: Partial<CSSStyleDeclaration> = {
  padding: "6px 20px",
  marginRight: "10px",
  border: "none",
  background: "#2a5a8a",
  color: "#ffffff",
  fontFamily: "sans-serif",
  fontSize: "15px",
  cursor: "pointer",
};

function element<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  style: Partial<CSSStyleDeclaration>,
  text = "",
): HTMLElementTagNameMap[K] {
  const el = document.createElement(tag);
  Object.assign(el.style, style);
  el.textContent = text;
  return el;
}

/** One side's picker: a source select, and a paste box shown for "paste". */
class SidePicker {
  readonly root = element("div", { flex: "1 1 300px", minWidth: "0" });
  private readonly select = element("select", {
    width: "100%",
    padding: "4px",
    fontSize: "14px",
    background: "#123a5c",
    color: "#ffffff",
    border: "1px solid #4a7aa8",
  });
  private readonly paste = element("textarea", {
    display: "none",
    boxSizing: "border-box",
    width: "100%",
    height: "90px",
    marginTop: "6px",
    padding: "4px",
    fontFamily: "monospace",
    fontSize: "12px",
    background: "#06121e",
    color: "#e0f0ff",
    border: "1px solid #4a7aa8",
    resize: "vertical",
  });
  readonly status = element("div", { marginTop: "6px", color: "#cfe6ff", minHeight: "1.4em", wordBreak: "break-word" });

  constructor(title: string, available: SourceKind[], choice: SourceChoice) {
    const labels: Record<SourceKind, string> = {
      best: t("cmp_src_best"),
      old: t("cmp_src_old"),
      mine: t("cmp_src_mine"),
      paste: t("cmp_src_paste"),
    };
    for (const kind of available) {
      const option = element("option", {}, labels[kind]);
      option.value = kind;
      this.select.appendChild(option);
    }
    this.select.value = choice.kind;
    this.paste.value = choice.text;
    this.paste.placeholder = t("cmp_paste_hint");
    this.paste.spellcheck = false;
    this.select.addEventListener("change", () => this.showPaste());
    // Drop a .lua (or .txt) file into the box: its text is the input.
    this.paste.addEventListener("dragover", (event) => event.preventDefault());
    this.paste.addEventListener("drop", (event) => {
      const file = event.dataTransfer?.files[0];
      if (!file) return;
      event.preventDefault();
      void file.text().then((text) => {
        this.paste.value = text;
      });
    });
    this.root.append(element("div", { color: "#ffc618", fontWeight: "bold", marginBottom: "4px" }, title), this.select, this.paste, this.status);
    this.showPaste();
  }

  choice(): SourceChoice {
    return { kind: this.select.value as SourceKind, text: this.paste.value };
  }

  private showPaste(): void {
    this.paste.style.display = this.select.value === "paste" ? "block" : "none";
  }
}

/**
 * The compare view's DOM panel (docs/086): a source for each side (best known,
 * the game's original from legacy/solution/old, the player's own, or a pasted
 * move string / .lua), the summary, and the list of differences - a click on
 * one jumps there. DOM rather than canvas for the paste box and file drop.
 *
 * While open, a capture-phase window listener stops keydowns from reaching
 * Phaser (so typing in the box can't drive the scene underneath) without
 * preventing their default, so the text still goes into the box. Esc closes.
 */
export class CompareUI {
  private root: HTMLDivElement | null = null;
  private pickerA: SidePicker | null = null;
  private pickerB: SidePicker | null = null;
  private summary: HTMLDivElement | null = null;
  private list: HTMLDivElement | null = null;
  private readonly keyHandler = (event: KeyboardEvent): void => {
    if (!this.root) return;
    if (event.key === "Escape") {
      event.preventDefault();
      this.hide();
    }
    if (!/^F\d+$/.test(event.key)) event.stopPropagation();
  };

  constructor(
    private readonly levelTitle: string,
    private readonly onApply: (a: SourceChoice, b: SourceChoice) => void,
    private readonly onPickDiff: (index: number) => void,
  ) {}

  get isOpen(): boolean {
    return this.root !== null;
  }

  show(available: SourceKind[], a: SourceChoice, b: SourceChoice, result: CompareUIResult | null): void {
    this.hide();
    const root = element("div", PANEL_STYLE);
    root.append(element("div", { color: "#ffc618", fontSize: "19px", fontWeight: "bold", marginBottom: "10px" }, t("cmp_title", this.levelTitle)));

    const sides = element("div", { display: "flex", flexWrap: "wrap", gap: "16px" });
    this.pickerA = new SidePicker(t("cmp_side_a"), available, a);
    this.pickerB = new SidePicker(t("cmp_side_b"), available, b);
    sides.append(this.pickerA.root, this.pickerB.root);
    root.append(sides);

    const buttons = element("div", { margin: "12px 0 8px" });
    const apply = element("button", BUTTON_STYLE, t("cmp_apply"));
    apply.type = "button";
    apply.addEventListener("click", () => {
      if (this.pickerA && this.pickerB) this.onApply(this.pickerA.choice(), this.pickerB.choice());
    });
    const close = element("button", { ...BUTTON_STYLE, background: "#3a4a5a" }, t("cmp_close"));
    close.type = "button";
    close.addEventListener("click", () => this.hide());
    buttons.append(apply, close);
    root.append(buttons);

    this.summary = element("div", { margin: "4px 0 8px", color: "#ffffff", fontWeight: "bold" });
    this.list = element("div", { maxHeight: "34vh", overflowY: "auto", fontFamily: "monospace", fontSize: "13px" });
    root.append(this.summary, this.list);
    root.append(element("div", { marginTop: "10px", color: "#9fb8d0", fontSize: "12px" }, t("cmp_keys")));

    document.body.appendChild(root);
    this.root = root;
    window.addEventListener("keydown", this.keyHandler, true);
    if (result) this.setResult(result);
  }

  /** Fills in the summary and the list of differences (the panel may be closed). */
  setResult(result: CompareUIResult): void {
    if (!this.root || !this.summary || !this.list || !this.pickerA || !this.pickerB) return;
    this.pickerA.status.textContent = result.sideA;
    this.pickerB.status.textContent = result.sideB;
    this.summary.textContent = result.summary;
    this.list.replaceChildren();
    result.rows.forEach((row, index) => {
      const line = element("div", { padding: "3px 6px", cursor: "pointer", whiteSpace: "pre" });
      const mark = element("span", { display: "inline-block", width: "10px", height: "10px", marginRight: "8px", background: row.color });
      line.append(mark, document.createTextNode(row.text));
      line.addEventListener("mouseenter", () => (line.style.background = "#1d4a72"));
      line.addEventListener("mouseleave", () => (line.style.background = ""));
      line.addEventListener("click", () => {
        this.hide();
        this.onPickDiff(index);
      });
      this.list?.appendChild(line);
    });
  }

  hide(): void {
    window.removeEventListener("keydown", this.keyHandler, true);
    this.root?.remove();
    this.root = null;
    this.pickerA = null;
    this.pickerB = null;
    this.summary = null;
    this.list = null;
  }
}

/** The moves a choice stands for: the pasted text parsed, or the fetched source. */
export function movesOf(choice: SourceChoice, sources: Partial<Record<SourceKind, string>>): string | null {
  return choice.kind === "paste" ? parseMovesInput(choice.text) : sources[choice.kind] ?? null;
}
