import Phaser from "phaser";

import { type LevelModel } from "../lua/levelLoader";
import { type RopeDecor } from "../lua/levelScript";
import { resolveFrame, type ModelAnimator } from "./ModelAnimator";
import { type AtlasFrame } from "./atlas";

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
