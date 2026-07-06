import Phaser from "phaser";

import { GRID_SCALE, loadLevelModels } from "./lua/levelLoader";
import { LevelScene } from "./scenes/LevelScene";

const LEVEL_NAME = "airplane";
const ZOOM = 1.5;

async function boot(): Promise<void> {
  const levelData = await loadLevelModels(LEVEL_NAME);

  new Phaser.Game({
    type: Phaser.AUTO,
    parent: "game",
    width: levelData.roomWidth * GRID_SCALE,
    height: levelData.roomHeight * GRID_SCALE,
    zoom: ZOOM,
    backgroundColor: "#0a1a2a",
    scene: new LevelScene(levelData),
  });
}

boot().catch((error: unknown) => {
  console.error(`Failed to load level "${LEVEL_NAME}"`, error);
  document.body.innerHTML = `<pre style="color:#ff5555;font-family:monospace;padding:16px">Failed to load level "${LEVEL_NAME}":\n${String(error)}</pre>`;
});
