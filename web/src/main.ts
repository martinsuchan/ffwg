import Phaser from "phaser";

import { loadWorldMap } from "./lua/worldMapLoader";
import { WorldMapScene } from "./scenes/WorldMapScene";
import { LevelScene } from "./scenes/LevelScene";
import { ReplayScene } from "./scenes/ReplayScene";
import { DemoScene } from "./scenes/DemoScene";
import { CreditsScene } from "./scenes/CreditsScene";
import { IntroScene } from "./scenes/IntroScene";
import { initHistoryNav } from "./navigation";
import { initFullscreen } from "./fullscreen";
import { initPlaytimeTracking } from "./storage/playtime";
import { serializeProgress, parseBackup, restoreProgress } from "./storage/progressBackup";
import { initLabels } from "./i18n";

async function boot(): Promise<void> {
  // Start accumulating total play time (persisted across sessions) from app
  // boot - the ending reports it as hours (docs/061).
  initPlaytimeTracking();

  const worldMapData = await loadWorldMap();
  // Feed the legacy labels.lua translations into the port's i18n layer so our
  // own UI strings localize the same way FF NG's menu does (docs/073).
  initLabels(worldMapData.labels);

  const game = new Phaser.Game({
    type: Phaser.AUTO,
    parent: "game",
    width: 640,
    height: 480,
    // The on-screen size (Standard/Large/Huge) is applied per-scene via camera
    // zoom + a display-resolution framebuffer (applyRenderScale), NOT a CSS
    // stretch, so text/graphics stay crisp - see docs/064. So the Scale
    // Manager's own zoom stays 1.
    zoom: 1,
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

  // F11 toggles aspect-preserving fullscreen (docs/064).
  initFullscreen(game);

  // Dev-only handle for the e2e regression suite (web/tests). Statically
  // stripped from production builds (import.meta.env.DEV is false there), so
  // it never ships - see web/tests/README.md.
  if (import.meta.env.DEV) {
    (window as unknown as { __game: Phaser.Game }).__game = game;
    // Progress backup/restore internals, for the e2e suite (docs/072) - drives
    // serialize/parse/restore directly without the UI's page-reload.
    (window as unknown as { __progress: unknown }).__progress = {
      serializeProgress,
      parseBackup,
      restoreProgress,
    };
  }
}

boot().catch((error: unknown) => {
  console.error("Failed to load the world map", error);
  document.body.innerHTML = `<pre style="color:#ff5555;font-family:monospace;padding:16px">Failed to load the world map:\n${String(error)}</pre>`;
});
