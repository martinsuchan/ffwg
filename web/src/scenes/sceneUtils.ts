import { type LevelModel } from "../lua/levelLoader";
import { resolveTextureKey } from "./ModelAnimator";

/**
 * Maps a Lua-style picture path ("images/<level>/name.png") to the
 * converted web asset. scripts/convert-images.ps1 mirrors legacy/images/**
 * into web/public/assets/images/** 1:1, swapping the extension to .webp.
 * Shared by LevelScene and ReplayScene - see docs/025.
 */
export function pictureToAssetUrl(picture: string): string {
  const withoutPrefix = picture.replace(/^images\//, "");
  const withoutExt = withoutPrefix.replace(/\.[^./]+$/, "");
  return `/assets/images/${withoutExt}.webp`;
}

export function isFishKind(kind: string): boolean {
  return kind.startsWith("fish_");
}

export function resolveInitialTextureKey(
  index: number,
  levelModel: LevelModel,
): string | null {
  if (!levelModel.initialAnim) return null;
  return resolveTextureKey(
    index,
    levelModel.anims,
    levelModel.initialAnim,
    levelModel.isLeft ? "left" : "right",
    levelModel.initialPhase,
  );
}
