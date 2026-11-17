import Phaser from "phaser";

/**
 * legacy `EffectZx` (model_setEffect "zx", used by emulator's room walls): the
 * sprite's opaque pixels are repainted one scanline at a time in horizontal
 * stripes, like a ZX Spectrum's border while loading from tape - wide stripes
 * in the top-left/bottom-left corner colours ("pilot tone") for ~5 s, then
 * narrow ones in the top-right/bottom-right colours ("data") for ~45 s, with a
 * random jitter in the stripe height. The four colours are the sprite's corner
 * pixels (zx-tmp.png: dark red / teal / navy / olive).
 *
 * The original's state machine is ported line for line. Per draw it runs
 * `blit()` (paint the rows, carrying the row counter and the current colour
 * across draws - that's what makes the stripes crawl) and then
 * `updateEffect()` (next phase of the 500-draw cycle). It drew once per game
 * cycle; `step()` is called once per CYCLE_MS by ModelAnimator.
 *
 * Rendering: the rows are filled into a CanvasTexture and cut to the sprite's
 * shape with `destination-in` against a binary mask (opaque = not the colour
 * key in the original; here alpha >= 128). One upload of the sprite-sized
 * texture per cycle - nothing per frame. The random jitter is cosmetic
 * (legacy `Random::randomReal`, not the level's Lua `random`), so it does not
 * touch determinism or replays.
 */

const STRIPE_STANDARD = 38.5;
const STRIPE_NARROW = 3.4;
/** Shared by whatever model uses the effect (only emulator's room does). Never
 *  removed: `textures.remove()` during SHUTDOWN wedges the scene transition
 *  (docs/056), and the content is fully redrawn by every step() anyway. */
const TEXTURE_KEY = "fx-zx";

/** legacy `Random::randomReal(max)`: uniform in [0, max). */
const randomReal = (max: number) => Math.random() * max;

export class ZxEffect {
  // legacy EffectZx members, same initial values as its constructor.
  private zx = 1; // ZX1..ZX4 - which corner colour the current stripe uses
  private phase = 0;
  private countHeight = 0;
  private stripeHeight = STRIPE_STANDARD;

  private readonly texture: Phaser.Textures.CanvasTexture;
  private mask!: HTMLCanvasElement;
  /** CSS colours of the ZX1..ZX4 corners (index = zx - 1). */
  private colors: string[] = [];
  private width = 0;
  private height = 0;

  constructor(scene: Phaser.Scene, source: Phaser.Textures.Frame) {
    const existing = scene.textures.exists(TEXTURE_KEY)
      ? (scene.textures.get(TEXTURE_KEY) as Phaser.Textures.CanvasTexture)
      : null;
    this.texture = existing ?? scene.textures.createCanvas(TEXTURE_KEY, 1, 1)!;
    this.setSource(source);
  }

  get textureKey(): string {
    return TEXTURE_KEY;
  }

  /** (Re)reads the mask and the corner colours from an atlas frame - at start
   *  and whenever the model's picture changes (emulator's room has one). */
  setSource(frame: Phaser.Textures.Frame): void {
    const w = frame.realWidth;
    const h = frame.realHeight;
    // Frame.setTrim stores the trimmed rect's offset in the frame's x/y.
    const trim = frame.trimmed ? { x: frame.x, y: frame.y } : { x: 0, y: 0 };
    const mask = document.createElement("canvas");
    mask.width = w;
    mask.height = h;
    const ctx = mask.getContext("2d", { willReadFrequently: true })!;
    ctx.drawImage(
      frame.source.image as CanvasImageSource,
      frame.cutX, frame.cutY, frame.cutWidth, frame.cutHeight,
      trim.x, trim.y, frame.cutWidth, frame.cutHeight,
    );
    const img = ctx.getImageData(0, 0, w, h);
    const px = img.data;
    const color = (x: number, y: number) => {
      const i = (y * w + x) * 4;
      return `rgb(${px[i]},${px[i + 1]},${px[i + 2]})`;
    };
    // EffectZx::blit: ZX1 = (0,0), ZX2 = (0,h-1), ZX3 = (w-1,0), ZX4 = (w-1,h-1).
    this.colors = [color(0, 0), color(0, h - 1), color(w - 1, 0), color(w - 1, h - 1)];
    for (let i = 3; i < px.length; i += 4) px[i] = px[i] >= 128 ? 255 : 0;
    ctx.putImageData(img, 0, 0);
    this.mask = mask;
    this.width = w;
    this.height = h;
    if (this.texture.width !== w || this.texture.height !== h) this.texture.setSize(w, h);
  }

  /** One original draw: blit() then updateEffect(). */
  step(): void {
    this.blit();
    this.updateEffect();
  }

  /** legacy EffectZx::updateEffect - stripe height for the next draw. */
  private updateEffect(): void {
    this.phase = (this.phase + 1) % 500;
    if (this.phase === 1) {
      this.zx = 1;
      this.stripeHeight = STRIPE_STANDARD;
    } else if (this.phase >= 2 && this.phase <= 51) {
      this.stripeHeight = (this.stripeHeight * 3 * (0.97 + randomReal(0.06)) + STRIPE_STANDARD) / 4.0;
    } else if (this.phase === 52) {
      this.zx = 3;
      this.stripeHeight = STRIPE_NARROW;
    } else {
      this.stripeHeight = (this.stripeHeight * 3 * (0.95 + randomReal(0.1)) + STRIPE_NARROW) / 4.0;
    }
  }

  /** legacy EffectZx::blit - one colour per scanline; runs of equal rows are
   *  coalesced into one fillRect, then the sprite's shape is cut out. */
  private blit(): void {
    const ctx = this.texture.context;
    ctx.globalCompositeOperation = "source-over";
    let runStart = 0;
    let runZx = -1;
    for (let py = 0; py < this.height; py++) {
      this.countHeight++;
      if (this.countHeight > this.stripeHeight) {
        this.countHeight -= this.stripeHeight;
        switch (this.zx) {
          case 1: this.zx = 2; break;
          case 2: this.zx = 1; break;
          case 3: this.zx = 4; break;
          default: this.zx = 3; break;
        }
      }
      if (this.zx !== runZx) {
        if (runZx !== -1) this.fillRows(runZx, runStart, py);
        runZx = this.zx;
        runStart = py;
      }
    }
    if (runZx !== -1) this.fillRows(runZx, runStart, this.height);
    ctx.globalCompositeOperation = "destination-in";
    ctx.drawImage(this.mask, 0, 0);
    ctx.globalCompositeOperation = "source-over";
    this.texture.refresh();
  }

  private fillRows(zx: number, from: number, to: number): void {
    const ctx = this.texture.context;
    ctx.fillStyle = this.colors[zx - 1];
    ctx.fillRect(0, from, this.width, to - from);
  }
}
