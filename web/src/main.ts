import Phaser from "phaser";

import { loadWorldMap } from "./lua/worldMapLoader";
import { WorldMapScene } from "./scenes/WorldMapScene";
import { LevelScene } from "./scenes/LevelScene";
import { ReplayScene } from "./scenes/ReplayScene";
import { DemoScene } from "./scenes/DemoScene";
import { CreditsScene } from "./scenes/CreditsScene";
import { IntroScene } from "./scenes/IntroScene";
import { initHistoryNav } from "./navigation";

const ZOOM = 1.5;

async function boot(): Promise<void> {
  const worldMapData = await loadWorldMap();

  const game = new Phaser.Game({
    type: Phaser.AUTO,
    parent: "game",
    width: 640,
    height: 480,
    zoom: ZOOM,
    backgroundColor: "#0a1a2a",
    // WorldMapScene needs its graph data up front (known at boot, unlike
    // per-level data - see docs/027), so it's a ready instance; LevelScene/
    // ReplayScene/DemoScene are all launched dynamically via scene.start/
    // launch(key, data) with a level/demo picked at runtime, so they're
    // registered by class for Phaser to instantiate fresh each time.
    scene: [
      new WorldMapScene(worldMapData),
      LevelScene,
      ReplayScene,
      DemoScene,
      CreditsScene,
      IntroScene,
    ],
  });

  // Browser Back returns to the world map instead of unloading the SPA.
  initHistoryNav(game);
}

boot().catch((error: unknown) => {
  console.error("Failed to load the world map", error);
  document.body.innerHTML = `<pre style="color:#ff5555;font-family:monospace;padding:16px">Failed to load the world map:\n${String(error)}</pre>`;
});
