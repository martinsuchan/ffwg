import Phaser from "phaser";

import { type LevelModel } from "../lua/levelLoader";
import { type RopeDecor } from "../lua/levelScript";
import { gameRenderScale } from "../storage/settingsStorage";
import { isFullscreenActive } from "../fullscreen";
import { resolveFrame, type ModelAnimator } from "./ModelAnimator";
import { type AtlasFrame } from "./atlas";

/** The DPI multiplier every Phaser Text is rendered at, so text stays sharp
 *  under the canvas CSS zoom (docs/064). The world (sprites/photo backgrounds)
 *  is unavoidably stretched by the zoom - its source art is low-res - but Text
 *  is resolution-independent, so rasterizing it at display density keeps it
 *  crisp. Covers fullscreen too: FIT can scale a small room well past the
 *  windowed zoom, so we floor at FULLSCREEN_TEXT_RES rather than the raw zoom.
 *  Capped there so text textures stay modest. */
const FULLSCREEN_TEXT_RES = 4;
export function crispTextResolution(): number {
  return Math.max(gameRenderScale(), FULLSCREEN_TEXT_RES);
}

/** The largest framebuffer dimension we'll allocate, so a huge fullscreen fit
 *  factor on a 4K/retina display can't blow past GPU limits. */
const MAX_FRAMEBUFFER = 4096;

/** The camera-zoom/framebuffer factor for a scene of `nativeW x nativeH`.
 *  Windowed: the Standard/Large/Huge setting. Fullscreen: the factor that makes
 *  the framebuffer match the on-screen fit size (× devicePixelRatio) so FIT
 *  scales it ~1:1 and text/graphics stay crisp instead of being stretched up
 *  from the smaller windowed framebuffer (docs/065). Capped so the framebuffer
 *  never exceeds MAX_FRAMEBUFFER. */
function renderFactor(nativeW: number, nativeH: number): number {
  const windowed = gameRenderScale();
  if (!isFullscreenActive()) return windowed;
  const dpr = window.devicePixelRatio || 1;
  const fit = Math.min(window.innerWidth / nativeW, window.innerHeight / nativeH);
  const cap = MAX_FRAMEBUFFER / Math.max(nativeW, nativeH);
  return Math.max(1, Math.min(Math.max(windowed, fit * dpr), cap));
}

/** The scene + native size the last applyRenderScale ran for - re-applied when
 *  fullscreen toggles (the factor + layout mode both change). */
let lastRenderTarget: { scene: Phaser.Scene; nativeW: number; nativeH: number } | null = null;

/** Re-run the render scale for the currently-showing scene - called when
 *  entering/leaving fullscreen so its framebuffer + layout switch between the
 *  windowed (NONE, factor) and fullscreen (FIT, fit-factor) forms. */
export function reapplyRenderScale(): void {
  if (!lastRenderTarget) return;
  const { scene, nativeW, nativeH } = lastRenderTarget;
  if (scene.scene.isActive() || scene.scene.isPaused()) {
    applyRenderScale(scene, nativeW, nativeH);
  }
}

/** Text style with the crisp-render resolution baked in - a thin wrapper so
 *  every `scene.add.text(...)` call site opts into docs/064's high-DPI text
 *  with one spread. Callers pass their normal style; resolution is forced. */
export function crispText(
  style: Phaser.Types.GameObjects.Text.TextStyle,
): Phaser.Types.GameObjects.Text.TextStyle {
  return { ...style, resolution: crispTextResolution() };
}

/**
 * Sizes a scene at the current game-size factor (Standard/Large/Huge) using
 * **camera zoom**, not a CSS stretch - the key to crisp rendering (docs/064).
 *
 * `nativeW/nativeH` are the scene's own pixel size in game units (a room, or the
 * 640x480 map). We set the WebGL framebuffer to `native * factor` (so it renders
 * at display resolution - text/vector graphics are sharp, not upscaled) and zoom
 * the main camera by `factor` so the game-unit world fills that framebuffer.
 * World coordinates are unchanged, so all gameplay/click math (which uses
 * camera-aware `pointer.worldX`) is unaffected.
 *
 * Phaser quirk handled here: in NONE mode `resize()` only rewrites the canvas
 * CSS box when `_resetZoom` is set, so we resize first then `setZoom(1)` to force
 * CSS == framebuffer (1:1, no browser stretch). While fullscreen, FIT owns the
 * CSS box (letterboxing to the screen), so we only refresh instead.
 */
export function applyRenderScale(scene: Phaser.Scene, nativeW: number, nativeH: number): void {
  lastRenderTarget = { scene, nativeW, nativeH };
  const scale = scene.scale;
  const fullscreen = isFullscreenActive();
  const factor = renderFactor(nativeW, nativeH);
  const fbW = nativeW * factor;
  const fbH = nativeH * factor;

  if (fullscreen) {
    // FIT letterbox-fits the framebuffer into the (viewport-sized) container,
    // preserving aspect. Use setGameSize(), NOT resize(): resize() sets
    // displaySize.setSize() which keeps the OLD aspect ratio, so switching rooms
    // in fullscreen would stretch the new room into the old room's shape;
    // setGameSize() calls setAspectRatio() so FIT re-letterboxes to the new
    // framebuffer shape (docs/065). The Scale Manager only syncs displaySize's
    // aspect mode from scaleMode at boot, so set it explicitly when flipping.
    scale.scaleMode = Phaser.Scale.FIT;
    scale.displaySize.setAspectMode(Phaser.Scale.FIT);
    scale.setGameSize(fbW, fbH);
  } else {
    // NONE mode: CSS box == framebuffer, 1:1, no browser stretch. resize() also
    // writes the CSS box (setGameSize() would not - docs/029).
    scale.scaleMode = Phaser.Scale.NONE;
    scale.displaySize.setAspectMode(Phaser.Scale.NONE);
    scale.resize(fbW, fbH);
  }

  const cam = scene.cameras.main;
  cam.setZoom(factor);
  cam.setOrigin(0, 0); // pivot at the top-left so world (0,0) stays at screen (0,0)

  if (fullscreen) {
    // refresh() re-measures parentSize only at the END of its own layout pass,
    // so measure the (viewport-sized) container first or FIT never grows the
    // canvas to the screen (docs/065).
    scale.getParentBounds();
    scale.refresh();
  } else {
    scale.setZoom(1);
  }
}

/** An offscreen-read RGBA pixel buffer of a loaded texture. */
export interface TexturePixels {
  data: Uint8ClampedArray;
  w: number;
  h: number;
}

/**
 * Read a loaded texture's RGBA pixels via an offscreen `<canvas>`. Used for
 * the legacy LayeredPicture button masks (world map corners + pedometer):
 * the flat mask colors identify each button region. Returns undefined if the
 * texture isn't ready or a 2D context can't be obtained.
 */
export function readTexturePixels(
  scene: Phaser.Scene,
  key: string,
): TexturePixels | undefined {
  const src = scene.textures.get(key).getSourceImage() as HTMLImageElement | HTMLCanvasElement;
  if (!src || !src.width) return undefined;
  const canvas = document.createElement("canvas");
  canvas.width = src.width;
  canvas.height = src.height;
  const ctx = canvas.getContext("2d", { willReadFrequently: true });
  if (!ctx) return undefined;
  ctx.drawImage(src, 0, 0);
  const { data } = ctx.getImageData(0, 0, src.width, src.height);
  return { data, w: src.width, h: src.height };
}

/** Packed 0xRRGGBB of the pixel at flat index `i` (i = pixel number, not byte). */
export function packRgb(pixels: TexturePixels, x: number, y: number): number {
  if (x < 0 || y < 0 || x >= pixels.w || y >= pixels.h) return -1;
  const j = (y * pixels.w + x) * 4;
  return (pixels.data[j] << 16) | (pixels.data[j + 1] << 8) | pixels.data[j + 2];
}

/**
 * Build (or replace) a canvas texture `outKey` holding only `source`'s pixels
 * where `mask` equals `color` (packed 0xRRGGBB), transparent everywhere else -
 * the pixel-perfect prelit button of legacy's LayeredPicture (map_lower.png /
 * pedometer_lower.png revealed through map_mask.png / pedometer_mask.png).
 * `source` and `mask` must share dimensions. Returns the matched pixel count.
 */
export function buildMaskedTexture(
  scene: Phaser.Scene,
  outKey: string,
  source: TexturePixels,
  mask: TexturePixels,
  color: number,
): number {
  const { w, h } = mask;
  const canvas = document.createElement("canvas");
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext("2d");
  if (!ctx) return 0;
  const out = ctx.createImageData(w, h);
  let count = 0;
  for (let i = 0; i < w * h; i++) {
    const j = i * 4;
    if (((mask.data[j] << 16) | (mask.data[j + 1] << 8) | mask.data[j + 2]) === color) {
      out.data[j] = source.data[j];
      out.data[j + 1] = source.data[j + 1];
      out.data[j + 2] = source.data[j + 2];
      out.data[j + 3] = source.data[j + 3];
      count++;
    }
  }
  ctx.putImageData(out, 0, 0);
  // Canvas textures aren't scene-scoped; a prior visit may have left the key.
  if (scene.textures.exists(outKey)) scene.textures.remove(outKey);
  scene.textures.addCanvas(outKey, canvas);
  return count;
}

/**
 * Maps a Lua-style picture path ("images/menu/name.png") to an individual
 * converted .webp asset. Only used for the dirs that are NOT atlased (docs/042):
 * menu/ (world map / credits / pedometer) and demo_briefcase/ (movie frames),
 * which convert-images.ps1 mirrors 1:1 (extension -> .webp). Model/level sprites
 * go through the atlas path instead (pictureToAtlas, ./atlas).
 */
export function pictureToAssetUrl(picture: string): string {
  const withoutPrefix = picture.replace(/^images\//, "");
  const withoutExt = withoutPrefix.replace(/\.[^./]+$/, "");
  return `/assets/images/${withoutExt}.webp`;
}

export function isFishKind(kind: string): boolean {
  return kind.startsWith("fish_");
}

/**
 * legacy RopeDecor::drawOnScreen(): for each game_addDecor("rope", ...), a 1px
 * line in the original's steel colour (0x30404e) between the two models' screen
 * positions plus each end's pixel shift. Redrawn every frame so it follows the
 * lift - only elevator1/elevator2 register any.
 *
 * Shared by LevelScene and ReplayScene: the original hangs decors off the
 * Room's View (Room::addDecor -> m_view->addDecor), and replay drives that same
 * Room via Room::loadMove(), so both draw them identically. See docs/055.
 */
export function drawRopeDecors(
  graphics: Phaser.GameObjects.Graphics,
  decors: readonly RopeDecor[],
  animators: ReadonlyMap<number, ModelAnimator>,
): void {
  graphics.clear();
  graphics.lineStyle(1, 0x30404e, 1);
  for (const rope of decors) {
    const a = animators.get(rope.index1)?.getScreenPos();
    const b = animators.get(rope.index2)?.getScreenPos();
    if (!a || !b) continue; // an anim-less model has no sprite to anchor to
    graphics.lineBetween(
      a.x + rope.shift1.x,
      a.y + rope.shift1.y,
      b.x + rope.shift2.x,
      b.y + rope.shift2.y,
    );
  }
}

export function resolveInitialFrame(levelModel: LevelModel): AtlasFrame | null {
  if (!levelModel.initialAnim) return null;
  return resolveFrame(
    levelModel.anims,
    levelModel.initialAnim,
    levelModel.isLeft ? "left" : "right",
    levelModel.initialPhase,
  );
}
