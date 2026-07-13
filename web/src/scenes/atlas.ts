/**
 * Maps a legacy Lua picture path to the Phaser texture **atlas** that packs it,
 * plus the frame name within that atlas. See docs/042.
 *
 * Model sprites (per-level items + backgrounds, and the shared fish frames) are
 * packed into per-dir atlases by scripts/convert-assets.ps1:
 *   legacy/images/<level>/**          -> assets/images/<level>/atlas.{webp,json}
 *   legacy/images/fishes/<variant>/** -> assets/images/fishes/<variant>/atlas.{webp,json}
 * The atlas key is just the source dir relative to images/ ("airplane",
 * "fishes/small"); the frame name is the picture's path relative to that dir,
 * minus the extension ("letadlo-p", "left/body_rest_00") - exactly the frame
 * names build-atlas.mjs writes. (menu/ and demo_briefcase/ are NOT atlased -
 * they load as individual webp via pictureToAssetUrl instead.)
 */
export interface AtlasFrame {
  /** Phaser texture key = atlas source dir relative to images/ (e.g. "airplane",
   *  "fishes/small"). Doubles as the URL subpath. Shared fish atlases collapse
   *  to one key across every level, so they load once and cache-hit after. */
  atlasKey: string;
  /** Frame name within the atlas (picture path relative to the atlas dir, no ext). */
  frame: string;
}

/** Parse "images/<root>/<rest>.png" into its atlas key + frame name. Fish live
 *  under images/fishes/<variant>/ (a shared, two-segment root); every other
 *  model/background path is under its own level dir images/<level>/. */
export function pictureToAtlas(picture: string): AtlasFrame {
  const rel = picture.replace(/^images\//, "").replace(/\.[^./]+$/, "");
  const segments = rel.split("/");
  if (segments[0] === "fishes") {
    return { atlasKey: `fishes/${segments[1]}`, frame: segments.slice(2).join("/") };
  }
  return { atlasKey: segments[0], frame: segments.slice(1).join("/") };
}

export function atlasWebpUrl(atlasKey: string): string {
  return `/assets/images/${atlasKey}/atlas.webp`;
}

export function atlasJsonUrl(atlasKey: string): string {
  return `/assets/images/${atlasKey}/atlas.json`;
}
