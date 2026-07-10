import Phaser from "phaser";

import { loadWorldMap } from "./lua/worldMapLoader";
import { WorldMapScene } from "./scenes/WorldMapScene";
import { LevelScene } from "./scenes/LevelScene";
import { ReplayScene } from "./scenes/ReplayScene";

const ZOOM = 1.5;

async function boot(): Promise<void> {
  const worldMapData = await loadWorldMap();

  new Phaser.Game({
    type: Phaser.AUTO,
    parent: "game",
    width: 640,
    height: 480,
    zoom: ZOOM,
    backgroundColor: "#0a1a2a",
    // WorldMapScene needs its graph data up front (known at boot, unlike
    // per-level data - see docs/027), so it's a ready instance; LevelScene/
    // ReplayScene are both launched dynamically via scene.start(key, data)
    // with a level picked at runtime, so they're registered by class for
    // Phaser to instantiate fresh each time.
    scene: [new WorldMapScene(worldMapData), LevelScene, ReplayScene],
  });
}

boot().catch((error: unknown) => {
  console.error("Failed to load the world map", error);
  document.body.innerHTML = `<pre style="color:#ff5555;font-family:monospace;padding:16px">Failed to load the world map:\n${String(error)}</pre>`;
});
