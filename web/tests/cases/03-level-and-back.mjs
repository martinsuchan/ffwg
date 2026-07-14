// Loading a level from the map, and the browser Back button returning to the
// world map (instead of unloading the SPA). Covers docs/027 launch + docs/040
// navigation.

import { gotoWorldmap, canvasMapper, nodeXY, assert } from "../lib.mjs";

export const name = "level: load + browser Back returns to map";

export default async function ({ page, baseURL }) {
  await gotoWorldmap(page, baseURL);

  const toPage = await canvasMapper(page);
  const ap = await nodeXY(page, "airplane");
  const p = toPage(ap.x, ap.y);
  await page.mouse.click(p.x, p.y);

  // The level scene goes live once its Lua script is bootstrapped.
  await page.waitForFunction(
    () => window.__game.scene.isActive("level") && !!window.__game.scene.keys.level.levelScript,
    { timeout: 25000 },
  );
  const loaded = await page.evaluate(() => ({
    level: window.__game.scene.keys.level.levelData.levelName,
    hasEngine: !!window.__game.scene.keys.level.engine,
  }));
  assert(loaded.level === "airplane" && loaded.hasEngine, `loaded=${JSON.stringify(loaded)}`);

  // Browser Back must return to the still-loaded world map.
  await page.goBack();
  await page.waitForFunction(
    () => window.__game.scene.isActive("worldmap") && !window.__game.scene.isActive("level"),
    { timeout: 8000 },
  );
  const back = await page.evaluate(() => ({
    worldmap: window.__game.scene.isActive("worldmap"),
    level: window.__game.scene.isActive("level"),
  }));
  assert(back.worldmap && !back.level, `after Back=${JSON.stringify(back)}`);

  return { detail: "airplane loaded; Back returned to the map" };
}
