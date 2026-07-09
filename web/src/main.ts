import Phaser from "phaser";

import { GRID_SCALE, loadLevelModels } from "./lua/levelLoader";
import { LevelScene } from "./scenes/LevelScene";
import { ReplayScene } from "./scenes/ReplayScene";

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
    // LevelScene needs levelData up front (current single-level-boot
    // design); ReplayScene gets its data dynamically via init() each time
    // LevelScene launches it (P key - see docs/025), so it's registered by
    // class rather than a pre-built instance.
    scene: [new LevelScene(levelData), ReplayScene],
  });
}

boot().catch((error: unknown) => {
  console.error(`Failed to load level "${LEVEL_NAME}"`, error);
  document.body.innerHTML = `<pre style="color:#ff5555;font-family:monospace;padding:16px">Failed to load level "${LEVEL_NAME}":\n${String(error)}</pre>`;
});
