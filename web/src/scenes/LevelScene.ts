import Phaser from "phaser";

import { GRID_SCALE, type LevelData } from "../lua/levelLoader";

/**
 * Maps a Lua-style picture path ("images/<level>/name.png") to the
 * converted web asset. scripts/convert-images.ps1 mirrors legacy/images/**
 * into web/public/assets/images/** 1:1, swapping the extension to .webp.
 */
function pictureToAssetUrl(picture: string): string {
  const withoutPrefix = picture.replace(/^images\//, "");
  const withoutExt = withoutPrefix.replace(/\.[^./]+$/, "");
  return `/assets/images/${withoutExt}.webp`;
}

/**
 * Renders a level's static layout (background + every model's current
 * frame) from the data produced by loadLevelModels(). No animation, input,
 * sound or puzzle rules - see docs/006.
 */
export class LevelScene extends Phaser.Scene {
  constructor(private readonly levelData: LevelData) {
    super("level");
  }

  preload(): void {
    this.load.image("bg", pictureToAssetUrl(this.levelData.bgPicture));
    this.levelData.models.forEach((model, index) => {
      if (model.picture) {
        this.load.image(`model-${index}`, pictureToAssetUrl(model.picture));
      }
    });
  }

  create(): void {
    this.add.image(0, 0, "bg").setOrigin(0, 0);

    this.levelData.models.forEach((model, index) => {
      if (!model.picture) {
        return;
      }
      this.add
        .image(model.x * GRID_SCALE, model.y * GRID_SCALE, `model-${index}`)
        .setOrigin(0, 0);
    });
  }
}
