import Phaser from "phaser";

/**
 * A stack of colored, self-dismissing subtitles at the bottom of the screen -
 * the port of legacy/src/plan/SubTitleAgent.* + legacy/src/widget/Title.*.
 *
 * Unlike the docs/015 placeholder (one white line at a time), several subtitles
 * are visible at once, each drawn in its speaker's color (dialog_addFont, see
 * levelScript.ts), newest at the bottom, older ones pushed up as new lines
 * arrive, and each removed on its own timer a few seconds after appearing. The
 * stack is decoupled from the dialog talking-state (LevelScript.activeDialog) -
 * a subtitle keeps living after its voice clip ends, exactly like the original.
 *
 * Ticked on its own steady timer (not the ROUND_MS round loop), matching the
 * original's timer-driven SubTitleAgent::own_update. See docs/037.
 */

/** Subtitle tick interval - the original's own_update runs at the game's ~10Hz
 *  cycle (docs/009); lifetimes below are in these ticks. */
const TICK_MS = 100;
/** legacy Title::TIME_PER_CHAR / TIME_MIN - a line lives at least TIME_MIN
 *  ticks, longer for longer text (in ticks, ~100ms each -> a few seconds). */
const TIME_PER_CHAR = 2;
const TIME_MIN = 40;
/** Most simultaneous lines to keep (legacy TITLE_LIMIT_Y caps ~5 rows). */
const MAX_LINES = 5;
/** Vertical gap between stacked lines, and margin above the canvas bottom. */
const GAP = 3;
const MARGIN_BOTTOM = 6;
/** How fast lines glide toward their stacked target (px/tick) - gives the
 *  original's upward-scroll feel when a new line pushes the older ones up. */
const SETTLE_SPEED = 8;
const FONT_SIZE = 14;

interface Entry {
  text: Phaser.GameObjects.Text;
  /** Ticks of life remaining (legacy Title::m_mintime). */
  life: number;
  /** Current top-Y in canvas pixels; glides toward its stacked target. */
  y: number;
}

export class SubtitleStack {
  private readonly entries: Entry[] = [];
  private readonly timer: Phaser.Time.TimerEvent;

  constructor(
    private readonly scene: Phaser.Scene,
    private readonly width: number,
    private readonly height: number,
  ) {
    this.timer = scene.time.addEvent({
      delay: TICK_MS,
      loop: true,
      callback: () => this.tick(),
    });
  }

  /** Spawn a new colored subtitle at the bottom (legacy SubTitleAgent::
   *  newSubtitle). `color` is a CSS hex from the speaker's font. */
  add(text: string, color: string): void {
    const label = this.scene.add
      .text(this.width / 2, this.height, text, {
        fontFamily: "sans-serif",
        fontSize: `${FONT_SIZE}px`,
        color,
        // Outlined text over the room (no background box), matching the
        // original's renderTextOutlined - keeps stacked lines legible on any
        // background without a wall of boxes.
        stroke: "#000000",
        strokeThickness: 4,
        align: "center",
        wordWrap: { width: this.width - 32 },
      })
      .setOrigin(0.5, 0)
      .setDepth(1000);
    // Start just below the visible area so it glides up into place.
    label.setY(this.height);
    const life = Math.max(TIME_MIN, [...text].length * TIME_PER_CHAR);
    this.entries.push({ text: label, life, y: this.height });

    // Drop the oldest if we exceed the row cap (legacy pops from the front).
    while (this.entries.length > MAX_LINES) {
      this.entries.shift()?.text.destroy();
    }
    this.layout(true);
  }

  /** Whether any subtitle is currently on screen - used by LevelScene's
   *  post-solve return countdown (longer while the player is still reading). */
  hasVisible(): boolean {
    return this.entries.length > 0;
  }

  /** Remove every subtitle immediately (legacy killTalks) - e.g. on restart. */
  clear(): void {
    for (const e of this.entries) e.text.destroy();
    this.entries.length = 0;
  }

  destroy(): void {
    this.timer.remove();
    this.clear();
  }

  private tick(): void {
    // Age every line; drop the expired (legacy Title::isGone on m_mintime).
    for (const e of this.entries) e.life -= 1;
    for (let i = this.entries.length - 1; i >= 0; i--) {
      if (this.entries[i].life <= 0) {
        this.entries[i].text.destroy();
        this.entries.splice(i, 1);
      }
    }
    this.layout(false);
  }

  /** Stack newest at the bottom, older ones above, and glide each toward its
   *  target Y (or snap, on a fresh add's first placement of that line). */
  private layout(snapNew: boolean): void {
    let bottom = this.height - MARGIN_BOTTOM;
    for (let i = this.entries.length - 1; i >= 0; i--) {
      const e = this.entries[i];
      const targetY = bottom - e.text.height;
      // Newly-added line (still parked at this.height) snaps its target
      // reference but still glides; existing lines ease toward the new target.
      if (snapNew && e.y >= this.height) {
        e.y = this.height;
      }
      const dy = targetY - e.y;
      e.y += Math.abs(dy) <= SETTLE_SPEED ? dy : Math.sign(dy) * SETTLE_SPEED;
      e.text.setY(e.y);
      bottom = targetY - GAP;
    }
  }
}
