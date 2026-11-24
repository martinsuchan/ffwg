import Phaser from "phaser";

import { GRID_SCALE, extractSavedMoves, fetchLegacyFile, type LevelData } from "../lua/levelLoader";
import { loadSolvedMoves } from "../storage/levelStorage";
import {
  StateInterner,
  compareTraces,
  traceSolution,
  type CompareSummary,
  type DiffSegment,
  type Segment,
  type SolutionTrace,
} from "../game/SolutionDiff";
import { collectAtlasKeys, preloadAtlases } from "./ModelAnimator";
import { applyRenderScale, crispText } from "./sceneUtils";
import { ComparePane } from "./ComparePane";
import { AudioManager } from "./AudioManager";
import { CompareUI, movesOf, type DiffRow, type SourceChoice, type SourceKind } from "./CompareUI";
import { t } from "../i18n";
import { leaveLevelView, setBackHandler } from "../navigation";

type CompareInitData = {
  levelData: LevelData;
  poster?: string | null;
  depth?: number;
};

/** Gap between the two rooms, and the control strip under them (native px). */
const GAP = 10;
const STRIP_H = 74;
const MIN_W = 640;
/** World offsets: each region sits far from the others, so a camera on one
 *  never shows a sprite of another (a fish swimming out slides past its room). */
const PANE_B_WORLD = { x: 10000, y: 0 };
const STRIP_WORLD = { x: 0, y: 20000 };

/** Moves shown before a difference when jumping to it, and after it in a loop. */
const LEAD_IN = 5;
const LEAD_OUT = 3;
const SPEEDS = [0.25, 0.5, 1, 2, 4, 8, 16];
const DEFAULT_SPEED = 2;

const COLOR_SHORTER = 0x4caf50;
const COLOR_LONGER = 0xe05050;
const COLOR_EQUAL = 0xffc618;
const COLOR_OPEN = 0x9a7bd0;

interface Side {
  choice: SourceChoice;
  label: string;
  trace: SolutionTrace;
}

interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

function diffColor(diff: DiffSegment): number {
  if (!diff.closed) return COLOR_OPEN;
  const delta = diff.b1 - diff.b0 - (diff.a1 - diff.a0);
  return delta < 0 ? COLOR_SHORTER : delta > 0 ? COLOR_LONGER : COLOR_EQUAL;
}

function cssColor(color: number): string {
  return `#${color.toString(16).padStart(6, "0")}`;
}

/** "−4" / "+2" / "±0". */
function signed(value: number): string {
  return value < 0 ? `−${-value}` : value > 0 ? `+${value}` : "±0";
}

/**
 * Two solutions of one level side by side (sandbox, key C in a level,
 * docs/086). The solutions are lined up by the game states they pass through
 * (SolutionDiff): where they share states both rooms play in lockstep; in a
 * difference each plays its own moves and the one that finishes first waits.
 * ← / → jump to the previous / next difference (a few moves before it), L loops
 * the current one, and the timeline under the rooms marks every difference.
 *
 * No live Lua (so no elevator ropes, and the Lua-held busy flags of
 * cannons/windoze don't apply); the physics is the same headless replay that
 * e2e 06 validates every solution with. The level's music loops throughout,
 * the track its code.lua starts (LevelData.music), whatever the playback does.
 */
export class CompareScene extends Phaser.Scene {
  private levelData!: LevelData;
  private poster: string | null = null;
  private depth = 1;

  private paneA!: ComparePane;
  private paneB!: ComparePane;
  private ui!: CompareUI;
  private audioManager!: AudioManager;
  private cameraB!: Phaser.Cameras.Scene2D.Camera;
  private cameraStrip!: Phaser.Cameras.Scene2D.Camera;
  private layout!: { w: number; h: number; a: Rect; b: Rect; strip: Rect };

  private sources: Partial<Record<SourceKind, string>> = {};
  private available: SourceKind[] = [];
  private sideA: Side | null = null;
  private sideB: Side | null = null;
  private compare: CompareSummary | null = null;
  /** Segment being played; segments.length = finished. */
  private segIndex = 0;
  /** The difference last jumped to or entered (index into compare.diffs). */
  private focus: number | null = null;
  private loop = false;
  private paused = false;
  /** A single step (arrow key) is animating; it stays paused when it lands. */
  private stepping = false;
  private speedIndex = DEFAULT_SPEED;
  /** Where the current segment ends for each side (shorter in a loop's tail). */
  private segEndA = 0;
  private segEndB = 0;

  private timeline!: Phaser.GameObjects.Graphics;
  private playhead!: Phaser.GameObjects.Rectangle;
  private statusLeft!: Phaser.GameObjects.Text;
  private statusRight!: Phaser.GameObjects.Text;
  private readonly onResize = (): void => this.placeCameras();

  constructor() {
    super("compare");
  }

  init(data: CompareInitData): void {
    this.levelData = data.levelData;
    this.poster = data.poster ?? null;
    this.depth = data.depth ?? 1;
    this.sources = {};
    this.available = [];
    this.sideA = null;
    this.sideB = null;
    this.compare = null;
    this.segIndex = 0;
    this.focus = null;
    this.loop = false;
    this.paused = false;
    this.stepping = false;
    this.speedIndex = DEFAULT_SPEED;
  }

  preload(): void {
    preloadAtlases(this, collectAtlasKeys(this.levelData.models, this.levelData.bgPicture));
  }

  create(): void {
    this.layout = this.chooseLayout();
    applyRenderScale(this, this.layout.w, this.layout.h, { fitWindow: true });

    this.paneA = new ComparePane(this, this.levelData, 0, 0);
    this.paneB = new ComparePane(this, this.levelData, PANE_B_WORLD.x, PANE_B_WORLD.y);
    this.cameraB = this.cameras.add(0, 0, 1, 1);
    this.cameraStrip = this.cameras.add(0, 0, 1, 1);
    this.createStrip();
    this.placeCameras();
    this.scale.on(Phaser.Scale.Events.RESIZE, this.onResize);

    this.ui = new CompareUI(
      this.levelData.levelName,
      (a, b) => this.apply(a, b),
      (index) => this.jumpToDiff(index),
    );

    // The level's own music, looping regardless of play/pause - the track its
    // code.lua starts, since no live Lua runs here to start it.
    this.audioManager = new AudioManager(this);
    if (this.levelData.music) void this.audioManager.applyMusicCommand({ type: "play", track: this.levelData.music });

    this.input.keyboard!.addCapture("SPACE,LEFT,RIGHT,PAGE_UP,PAGE_DOWN");
    this.input.keyboard!.on("keydown", (event: KeyboardEvent) => this.onKey(event));
    // Browser Back = Esc (docs/087). Its history entry is already popped.
    setBackHandler(this, () => this.backToLevel(false));

    // The scene's own shutdown destroys the game objects, timers and extra
    // cameras; only what lives outside it needs undoing here.
    this.events.once(Phaser.Scenes.Events.SHUTDOWN, () => {
      this.scale.off(Phaser.Scale.Events.RESIZE, this.onResize);
      this.ui.hide();
      // The Sound Manager is game-global: stop the music on the way out (docs/025).
      this.audioManager.destroy();
    });

    void this.loadSources();
  }

  update(_time: number, delta: number): void {
    const run = this.compare !== null && (!this.paused || this.stepping);
    const speed = run ? SPEEDS[this.speedIndex] : 0;
    this.paneA.update(delta, speed);
    this.paneB.update(delta, speed);
    if (this.compare && this.segIndex < this.compare.segments.length) {
      if (this.stepping && this.paneA.done() && this.paneB.done()) {
        this.stepping = false;
        this.updateStatus();
      }
      const atEndA = this.paneA.done() && this.paneA.moveIndex >= this.segEndA;
      const atEndB = this.paneB.done() && this.paneB.moveIndex >= this.segEndB;
      if (atEndA && atEndB) {
        this.finishSegment();
      } else {
        this.paneA.setNote(atEndA && this.inDiff() ? t("cmp_waiting") : null);
        this.paneB.setNote(atEndB && this.inDiff() ? t("cmp_waiting") : null);
      }
    }
    this.drawPlayhead();
  }

  // ------------------------------------------------------------ sources --

  private async loadSources(): Promise<void> {
    const level = this.levelData.levelName;
    const fetchMoves = async (path: string): Promise<string | null> => {
      try {
        return extractSavedMoves(await fetchLegacyFile(path));
      } catch {
        return null; // no such file (most levels have no old/ original)
      }
    };
    const [best, old] = await Promise.all([fetchMoves(`solution/${level}.lua`), fetchMoves(`solution/old/${level}.lua`)]);
    if (!this.sys.isActive()) return;
    const mine = loadSolvedMoves(level);
    if (best) this.sources.best = best;
    if (old) this.sources.old = old;
    if (mine) this.sources.mine = mine;
    this.available = (["old", "best", "mine"] as SourceKind[]).filter((kind) => this.sources[kind]);
    this.available.push("paste");

    // Default pair: the game's original against the best known; else the best
    // against the player's own; else whatever there is against a paste.
    const pick = (kind: SourceKind): SourceChoice => ({ kind, text: "" });
    let a: SourceChoice;
    let b: SourceChoice;
    if (old && best) [a, b] = [pick("old"), pick("best")];
    else if (best && mine && mine !== best) [a, b] = [pick("best"), pick("mine")];
    else [a, b] = [pick(best ? "best" : mine ? "mine" : "paste"), pick("paste")];
    this.apply(a, b);
  }

  /** Traces and lines up the chosen pair; opens the panel when one side is missing. */
  private apply(a: SourceChoice, b: SourceChoice): void {
    const movesA = movesOf(a, this.sources);
    const movesB = movesOf(b, this.sources);
    if (!movesA || !movesB) {
      this.sideA = movesA ? this.trace(a, movesA, new StateInterner()) : null;
      this.sideB = null;
      this.compare = null;
      // Show the room on both sides meanwhile: A's solution (if any) at its start.
      this.paneA.setSolution(this.sideA?.trace.moves ?? "", this.sideA ? `A · ${this.sideA.label}` : "A");
      this.paneB.setSolution("", "B");
      this.drawTimeline();
      this.statusLeft.setText(t("cmp_need_second"));
      this.statusRight.setText("");
      this.ui.show(this.available, a, b, null);
      this.ui.setResult({
        sideA: movesA ? this.sideText(this.sideA!) : t("cmp_no_moves"),
        sideB: movesB ? "" : t("cmp_no_moves"),
        summary: t("cmp_need_second"),
        rows: [],
      });
      return;
    }
    const interner = new StateInterner();
    this.sideA = this.trace(a, movesA, interner);
    this.sideB = this.trace(b, movesB, interner);
    this.compare = compareTraces(this.sideA.trace, this.sideB.trace);
    this.paneA.setSolution(this.sideA.trace.moves, `A · ${this.sideA.label}`);
    this.paneB.setSolution(this.sideB.trace.moves, `B · ${this.sideB.label}`);
    this.drawTimeline();
    if (this.ui.isOpen) this.ui.setResult(this.result());
    this.paused = false;
    if (this.compare.diffs.length > 0) this.jumpToDiff(0);
    else this.startAt(0, 0, 0);
  }

  private trace(choice: SourceChoice, moves: string, interner: StateInterner): Side {
    const labels: Record<SourceKind, string> = {
      best: t("cmp_src_best"),
      old: t("cmp_src_old"),
      mine: t("cmp_src_mine"),
      paste: t("cmp_src_paste").replace(/…$/, ""),
    };
    return { choice, label: labels[choice.kind], trace: traceSolution(this.levelData, moves, interner) };
  }

  private sideText(side: Side): string {
    const tr = side.trace;
    const parts = [t("cmp_moves", tr.moves.length)];
    if (tr.failedAt) parts.push(t("cmp_failed", tr.failedAt.symbol, tr.failedAt.index + 1));
    else if (tr.diedAt !== undefined) parts.push(t("cmp_died", tr.diedAt));
    parts.push(tr.solved ? `✓ ${t("cmp_solved")}` : `✗ ${t("cmp_unsolved")}`);
    return parts.join(" · ");
  }

  private summaryText(): string {
    const c = this.compare!;
    const length = c.delta < 0 ? t("cmp_shorter", -c.delta) : c.delta > 0 ? t("cmp_longer", c.delta) : t("cmp_equal");
    if (c.diffs.length === 0) return `${t("cmp_no_diffs")} ${length}`;
    return `${t("cmp_similarity", Math.floor(c.similarity * 100))} · ${t("cmp_diff_count", c.diffs.length)} · ${length}`;
  }

  private diffText(diff: DiffSegment): string {
    const la = diff.a1 - diff.a0;
    const lb = diff.b1 - diff.b0;
    const tail = diff.closed ? "" : `  (${t("cmp_open_end")})`;
    const range = (from: number, to: number): string => (to > from ? `${from + 1}–${to}` : "–");
    return `A ${range(diff.a0, diff.a1)} (${la})  ↔  B ${range(diff.b0, diff.b1)} (${lb})  ${signed(lb - la)}${tail}`;
  }

  private result(): { sideA: string; sideB: string; summary: string; rows: DiffRow[] } {
    const rows = this.compare!.diffs.map((diff, i) => ({
      text: `${String(i + 1).padStart(3)}.  ${this.diffText(diff)}`,
      color: cssColor(diffColor(diff)),
    }));
    return { sideA: this.sideText(this.sideA!), sideB: this.sideText(this.sideB!), summary: this.summaryText(), rows };
  }

  private openPanel(): void {
    const a = this.sideA?.choice ?? { kind: "paste" as SourceKind, text: "" };
    const b = this.sideB?.choice ?? { kind: "paste" as SourceKind, text: "" };
    this.ui.show(this.available, a, b, this.compare ? this.result() : null);
  }

  // ----------------------------------------------------------- playback --

  private get segments(): Segment[] {
    return this.compare?.segments ?? [];
  }

  private inDiff(): boolean {
    return this.segments[this.segIndex]?.kind === "diff";
  }

  /** Puts A at move `a`, B at move `b` (inside segment `seg`) and plays on from there. */
  private startAt(seg: number, a: number, b: number): void {
    this.paneA.seek(a);
    this.paneB.seek(b);
    this.startSegment(seg);
  }

  private startSegment(seg: number): void {
    this.segIndex = seg;
    this.paneA.setNote(null);
    this.paneB.setNote(null);
    const s = this.segments[seg];
    if (!s) {
      this.paused = true;
      this.updateStatus();
      return;
    }
    if (s.kind === "diff") {
      this.focus = this.compare!.diffs.indexOf(s);
      this.segEndA = s.a1;
      this.segEndB = s.b1;
    } else {
      // Looping: play only a few moves past the difference, then jump back.
      const loopTail = this.loop && this.focus !== null && this.segments[seg - 1] === this.compare!.diffs[this.focus];
      const len = loopTail ? Math.min(s.len, LEAD_OUT) : s.len;
      this.segEndA = s.a0 + len;
      this.segEndB = s.b0 + len;
    }
    // Paused (e.g. after a step) the panes hold where they are until resumed.
    if (!this.paused) this.resume();
    this.updateStatus();
  }

  /** Lets both panes play on to the end of the current segment. */
  private resume(): void {
    this.paneA.playTo(this.segEndA);
    this.paneB.playTo(this.segEndB);
  }

  private togglePause(): void {
    this.stepping = false;
    this.paused = !this.paused;
    if (!this.paused) this.resume();
  }

  private finishSegment(): void {
    const s = this.segments[this.segIndex];
    const focusDiff = this.focus !== null ? this.compare!.diffs[this.focus] : null;
    const endsLoop =
      this.loop &&
      focusDiff !== null &&
      (s === focusDiff ? this.segments[this.segIndex + 1]?.kind !== "same" : this.segments[this.segIndex - 1] === focusDiff);
    if (endsLoop) {
      // Paused or stepping: hold at the loop's end until playback resumes.
      if (!this.paused) this.jumpToDiff(this.focus!);
      return;
    }
    this.startSegment(this.segIndex + 1);
  }

  /** Both rooms a few moves before difference `index`, then play. */
  private jumpToDiff(index: number): void {
    const diffs = this.compare?.diffs ?? [];
    if (diffs.length === 0) return;
    const i = Math.max(0, Math.min(index, diffs.length - 1));
    const diff = diffs[i];
    const seg = this.segments.indexOf(diff);
    const before = this.segments[seg - 1];
    const lead = before?.kind === "same" ? Math.min(LEAD_IN, before.len) : 0;
    this.focus = i;
    this.paused = false;
    this.stepping = false;
    if (lead > 0) this.startAt(seg - 1, diff.a0 - lead, diff.b0 - lead);
    else this.startAt(seg, diff.a0, diff.b0);
    this.focus = i;
    this.updateStatus();
  }

  /** How far into segment `s` the rooms are: moves, the further side in a difference. */
  private cursorIn(s: Segment): number {
    if (s.kind === "same") return this.paneA.moveIndex - s.a0;
    return Math.max(this.paneA.moveIndex - s.a0, this.paneB.moveIndex - s.b0);
  }

  /** Move indexes of A and B `t` moves into segment `s` (in a difference the shorter side stops at its end). */
  private positionsAt(s: Segment, t: number): [number, number] {
    if (s.kind === "same") return [s.a0 + t, s.b0 + t];
    return [s.a0 + Math.min(t, s.a1 - s.a0), s.b0 + Math.min(t, s.b1 - s.b0)];
  }

  /** Right arrow: one move forward on both sides, animated, then paused. */
  private stepForward(): void {
    if (!this.compare || this.stepping) return;
    this.paused = true;
    let s = this.segments[this.segIndex];
    if (!s) return;
    if (this.cursorIn(s) >= this.segWidth(s)) {
      if (this.segIndex + 1 >= this.segments.length) return;
      this.startSegment(this.segIndex + 1);
      s = this.segments[this.segIndex];
    }
    const [a, b] = this.positionsAt(s, this.cursorIn(s) + 1);
    this.paneA.playTo(a);
    this.paneB.playTo(b);
    this.stepping = true;
    this.updateStatus();
  }

  /** Left arrow: one move back on both sides, at once, and paused. */
  private stepBack(): void {
    if (!this.compare || this.segments.length === 0) return;
    this.paused = true;
    this.stepping = false;
    const seg = Math.min(this.segIndex, this.segments.length - 1);
    const s = this.segments[seg];
    const t = seg < this.segIndex ? this.segWidth(s) : this.cursorIn(s);
    if (t > 0) this.seekTo(seg, t - 1);
    else if (seg > 0) this.seekTo(seg - 1, this.segWidth(this.segments[seg - 1]) - 1);
    this.updateStatus();
  }

  /** Both rooms `t` moves into segment `seg`, paused. */
  private seekTo(seg: number, t: number): void {
    const s = this.segments[seg];
    const [a, b] = this.positionsAt(s, t);
    if (s.kind === "same") this.focus = this.diffBefore(seg);
    this.startAt(seg, a, b);
  }

  private nextDiff(): void {
    const diffs = this.compare?.diffs ?? [];
    if (diffs.length === 0) return;
    if (this.focus === null) {
      const next = diffs.findIndex((d) => this.segments.indexOf(d) >= this.segIndex);
      this.jumpToDiff(next < 0 ? diffs.length - 1 : next);
    } else {
      this.jumpToDiff(Math.min(this.focus + 1, diffs.length - 1));
    }
  }

  private prevDiff(): void {
    if (!this.compare || this.compare.diffs.length === 0) return;
    this.jumpToDiff(this.focus === null ? 0 : Math.max(0, this.focus - 1));
  }

  private restart(): void {
    if (!this.compare) return;
    this.focus = null;
    this.paused = false;
    this.stepping = false;
    this.startAt(0, 0, 0);
  }

  private onKey(event: KeyboardEvent): void {
    if (this.ui.isOpen) return;
    // A held arrow keeps stepping; every other key acts once per press.
    const stepKey = (event.key === "ArrowLeft" || event.key === "ArrowRight") && !event.shiftKey;
    if (event.repeat && !stepKey) return;
    switch (event.key) {
      case " ":
        this.togglePause();
        break;
      case "ArrowRight":
        if (event.shiftKey) this.nextDiff();
        else this.stepForward();
        break;
      case "ArrowLeft":
        if (event.shiftKey) this.prevDiff();
        else this.stepBack();
        break;
      case "PageDown":
        this.nextDiff();
        break;
      case "PageUp":
        this.prevDiff();
        break;
      case "l":
      case "L":
        this.loop = !this.loop;
        break;
      case "+":
      case "=":
        this.speedIndex = Math.min(SPEEDS.length - 1, this.speedIndex + 1);
        break;
      case "-":
        this.speedIndex = Math.max(0, this.speedIndex - 1);
        break;
      case "r":
      case "R":
        this.restart();
        break;
      case "s":
      case "S":
        this.openPanel();
        break;
      case "Escape":
        this.backToLevel(true);
        return;
      default:
        return;
    }
    this.updateStatus();
  }

  /** Esc, the Back button and browser Back: the level, fully restored.
   *  `inApp`: left from inside the view, so its history entry goes too. */
  private backToLevel(inApp: boolean): void {
    if (inApp) leaveLevelView();
    this.scene.start("level", { levelData: this.levelData, poster: this.poster, depth: this.depth });
  }

  // ------------------------------------------------------------- layout --

  /** Side by side or stacked, whichever shows the rooms larger in this window. */
  private chooseLayout(): { w: number; h: number; a: Rect; b: Rect; strip: Rect } {
    const rw = this.levelData.roomWidth * GRID_SCALE;
    const rh = this.levelData.roomHeight * GRID_SCALE;
    const sideW = Math.max(2 * rw + GAP, MIN_W);
    const sideH = rh + STRIP_H;
    const stackW = Math.max(rw, MIN_W);
    const stackH = 2 * rh + GAP + STRIP_H;
    const fit = (w: number, h: number): number => Math.min(window.innerWidth / w, window.innerHeight / h);
    if (fit(sideW, sideH) >= fit(stackW, stackH)) {
      const x = (sideW - (2 * rw + GAP)) / 2;
      return {
        w: sideW,
        h: sideH,
        a: { x, y: 0, w: rw, h: rh },
        b: { x: x + rw + GAP, y: 0, w: rw, h: rh },
        strip: { x: 0, y: rh, w: sideW, h: STRIP_H },
      };
    }
    const x = (stackW - rw) / 2;
    return {
      w: stackW,
      h: stackH,
      a: { x, y: 0, w: rw, h: rh },
      b: { x, y: rh + GAP, w: rw, h: rh },
      strip: { x: 0, y: 2 * rh + GAP, w: stackW, h: STRIP_H },
    };
  }

  /**
   * Three cameras, one per region: the main one shows room A, the others room B
   * and the control strip, each looking at its own far-apart world area. Their
   * viewports are in framebuffer pixels: native layout × the render factor.
   * Re-run on every resize (fullscreen toggles re-apply the render scale).
   */
  private placeCameras(): void {
    const factor = this.scale.gameSize.width / this.layout.w;
    const place = (camera: Phaser.Cameras.Scene2D.Camera, rect: Rect, world: { x: number; y: number }): void => {
      camera.setViewport(rect.x * factor, rect.y * factor, rect.w * factor, rect.h * factor);
      camera.setOrigin(0, 0);
      camera.setZoom(factor);
      camera.setScroll(world.x, world.y);
    };
    place(this.cameras.main, this.layout.a, { x: 0, y: 0 });
    place(this.cameraB, this.layout.b, PANE_B_WORLD);
    place(this.cameraStrip, this.layout.strip, STRIP_WORLD);
  }

  // -------------------------------------------------------------- strip --

  private get barRect(): Rect {
    return { x: STRIP_WORLD.x + 10, y: STRIP_WORLD.y + 8, w: this.layout.w - 20, h: 14 };
  }

  private createStrip(): void {
    const { x, y } = STRIP_WORLD;
    const w = this.layout.w;
    this.add.rectangle(x, y, w, STRIP_H, 0x0a1f33).setOrigin(0, 0);
    this.timeline = this.add.graphics();
    const bar = this.barRect;
    this.playhead = this.add.rectangle(bar.x, bar.y - 3, 2, bar.h + 6, 0xffffff).setOrigin(0.5, 0).setDepth(10);
    // The bar is a seek target: a click jumps to that point of the timeline.
    this.add
      .zone(bar.x, bar.y - 3, bar.w, bar.h + 6)
      .setOrigin(0, 0)
      .setInteractive({ useHandCursor: true })
      .on("pointerdown", (pointer: Phaser.Input.Pointer) => {
        const world = this.cameraStrip.getWorldPoint(pointer.x, pointer.y);
        this.seekTimeline((world.x - bar.x) / bar.w);
      });

    const textStyle = crispText({ fontFamily: "sans-serif", fontSize: "13px", color: "#ffffff" });
    this.statusLeft = this.add.text(x + 10, y + 28, "", textStyle);
    this.statusRight = this.add.text(x + w - 10, y + 28, "", textStyle).setOrigin(1, 0);

    const buttons: Array<[string, () => void]> = [
      [t("cmp_btn_start"), () => this.restart()],
      [t("cmp_btn_prev"), () => this.prevDiff()],
      [t("cmp_btn_back1"), () => this.stepBack()],
      [t("cmp_btn_play"), () => this.togglePause()],
      [t("cmp_btn_fwd1"), () => this.stepForward()],
      [t("cmp_btn_next"), () => this.nextDiff()],
      [t("cmp_btn_loop"), () => (this.loop = !this.loop)],
      [t("cmp_btn_slower"), () => (this.speedIndex = Math.max(0, this.speedIndex - 1))],
      [t("cmp_btn_faster"), () => (this.speedIndex = Math.min(SPEEDS.length - 1, this.speedIndex + 1))],
      [t("cmp_btn_sources"), () => this.openPanel()],
      [t("cmp_btn_back"), () => this.backToLevel(true)],
    ];
    const buttonStyle = crispText({
      fontFamily: "sans-serif",
      fontSize: "12px",
      color: "#ffffff",
      backgroundColor: "#2a5a8a",
      padding: { x: 6, y: 3 },
    });
    const made = buttons.map(([label, onClick]) =>
      this.add
        .text(0, y + 48, label, buttonStyle)
        .setInteractive({ useHandCursor: true })
        .on("pointerdown", () => {
          if (this.ui.isOpen) return;
          onClick();
          this.updateStatus();
        }),
    );
    const total = made.reduce((sum, b) => sum + b.width, 0) + 6 * (made.length - 1);
    let bx = x + Math.max(10, (w - total) / 2);
    for (const button of made) {
      button.setX(bx);
      bx += button.width + 6;
    }
  }

  /** Width of a segment on the timeline: moves, the longer side for a difference. */
  private segWidth(s: Segment): number {
    return s.kind === "same" ? s.len : Math.max(s.a1 - s.a0, s.b1 - s.b0, 1);
  }

  private drawTimeline(): void {
    const g = this.timeline;
    g.clear();
    const bar = this.barRect;
    g.fillStyle(0x24425e).fillRect(bar.x, bar.y, bar.w, bar.h);
    const total = this.segments.reduce((sum, s) => sum + this.segWidth(s), 0) || 1;
    let at = 0;
    for (const s of this.segments) {
      const width = this.segWidth(s);
      if (s.kind === "diff") {
        g.fillStyle(diffColor(s)).fillRect(bar.x + (at / total) * bar.w, bar.y, Math.max(2, (width / total) * bar.w), bar.h);
      }
      at += width;
    }
  }

  private drawPlayhead(): void {
    const bar = this.barRect;
    const segments = this.segments;
    const total = segments.reduce((sum, s) => sum + this.segWidth(s), 0);
    if (total === 0) return;
    let at = 0;
    for (let k = 0; k < Math.min(this.segIndex, segments.length); k++) at += this.segWidth(segments[k]);
    const s = segments[this.segIndex];
    if (s) {
      const inA = this.paneA.moveIndex - s.a0;
      const inB = this.paneB.moveIndex - s.b0;
      at += Math.max(0, Math.min(this.segWidth(s), Math.max(inA, inB)));
    }
    this.playhead.setX(bar.x + (at / total) * bar.w);
  }

  /** A click on the timeline at `fraction` (0..1): play on from that point. */
  private seekTimeline(fraction: number): void {
    if (this.ui.isOpen || !this.compare) return;
    const segments = this.segments;
    const total = segments.reduce((sum, s) => sum + this.segWidth(s), 0);
    let target = Math.max(0, Math.min(1, fraction)) * total;
    for (let k = 0; k < segments.length; k++) {
      const s = segments[k];
      const width = this.segWidth(s);
      if (target <= width || k === segments.length - 1) {
        this.paused = false;
        this.stepping = false;
        if (s.kind === "same") {
          const offset = Math.min(s.len, Math.round(target));
          this.focus = this.diffBefore(k);
          this.startAt(k, s.a0 + offset, s.b0 + offset);
        } else {
          this.startAt(k, s.a0, s.b0);
        }
        return;
      }
      target -= width;
    }
  }

  /** The last difference before segment `seg`, or null. */
  private diffBefore(seg: number): number | null {
    for (let k = seg - 1; k >= 0; k--) {
      const s = this.segments[k];
      if (s.kind === "diff") return this.compare!.diffs.indexOf(s);
    }
    return null;
  }

  private updateStatus(): void {
    if (!this.compare) return;
    const diffs = this.compare.diffs;
    let left: string;
    if (this.segIndex >= this.segments.length) left = t("cmp_end");
    else if (diffs.length === 0) left = t("cmp_no_diffs");
    else if (this.focus === null) left = t("cmp_before_first");
    else left = `${t("cmp_diff_of", this.focus + 1, diffs.length)} · ${this.diffText(diffs[this.focus])}`;
    this.statusLeft.setText(left);
    const flags = [t("cmp_similarity", Math.floor(this.compare.similarity * 100)), signed(this.compare.delta), `${SPEEDS[this.speedIndex]}×`];
    if (this.loop) flags.push(t("cmp_loop"));
    if (this.paused) flags.push(t("cmp_paused"));
    this.statusRight.setText(flags.join(" · "));
  }
}
