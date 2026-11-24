// Replay layout + browser Back from the views a level opens (docs/087): the
// replay's hint line and media buttons sit in bands above and below the room,
// not over it; Back leaves a replay or compare view for the level that opened
// it, and after Esc the next Back leaves the level for the map.

import { gotoWorldmap, assert } from "../lib.mjs";

export const name = "replay bands + browser Back from replay/compare";

export default async function ({ page, baseURL }) {
  await gotoWorldmap(page, baseURL);

  const only = (key) =>
    page.waitForFunction(
      (k) => window.__game.scene.isActive(k) && window.__game.scene.getScenes(true).length === 1,
      key,
      { timeout: 25000 },
    );
  const openLevel = async (name) => {
    await page.evaluate(async (name) => {
      const { loadLevelModels } = await import("/src/lua/levelLoader.ts");
      const { pushSubView } = await import("/src/navigation.ts");
      const levelData = await loadLevelModels(name);
      pushSubView(); // as the world map does when it launches a level
      window.__game.scene.getScenes(true).forEach((s) => window.__game.scene.stop(s.scene.key));
      window.__game.scene.start("level", { levelData });
    }, name);
    await page.waitForFunction(
      () => window.__game.scene.isActive("level") && !!window.__game.scene.keys.level.levelScript,
      { timeout: 25000 },
    );
    await page.waitForTimeout(300);
  };

  // --- 1. The bands: the narrowest room (noground, hint wraps) and a large one.
  const layouts = [];
  for (const lv of ["noground", "barrel"]) {
    await openLevel(lv);
    await page.keyboard.press("p");
    await only("replay");
    await page.waitForTimeout(300);
    const L = await page.evaluate(() => {
      const s = window.__game.scene.keys.replay;
      const cam = s.cameras.main;
      const roomW = s.levelData.roomWidth * 15;
      const roomH = s.levelData.roomHeight * 15;
      // An IconButton is centred on its y and 30 px tall (IconButton.HEIGHT).
      const ys = [...s.controlButtons.values()].map((b) => ({ top: b.y - 15, bottom: b.y + 15 }));
      return {
        roomW, roomH, top: s.topBand, bottom: s.bottomBand, scrollY: cam.scrollY,
        canvasH: window.__game.canvas.height / cam.zoom,
        hintBottom: s.statusText.y + s.statusText.height, hintTop: s.statusText.y, hintW: s.statusText.width,
        buttonsTop: Math.min(...ys.map((r) => r.top)), buttonsBottom: Math.max(...ys.map((r) => r.bottom)),
        stepTop: s.stepText.y,
      };
    });
    assert(L.scrollY === -L.top && L.top > 0 && L.bottom > 0, `${lv}: bands ${JSON.stringify(L)}`);
    assert(Math.abs(L.canvasH - (L.top + L.roomH + L.bottom)) < 1, `${lv}: canvas ${L.canvasH} != bands + room`);
    assert(L.hintTop >= -L.top && L.hintBottom <= 0 && L.hintW <= L.roomW, `${lv}: hint not in the top band ${JSON.stringify(L)}`);
    assert(L.buttonsTop >= L.roomH && L.buttonsBottom <= L.roomH + L.bottom, `${lv}: buttons not in the bottom band ${JSON.stringify(L)}`);
    assert(L.stepTop >= 0, `${lv}: the step counter left the room`);
    layouts.push(`${lv} ${L.top}/${L.bottom}`);

    // --- 2. Back: replay -> level -> map.
    await page.goBack();
    await only("level");
    await page.waitForTimeout(300);
    await page.goBack();
    await only("worldmap");
  }

  // --- 3. Esc drops the replay's history entry: one Back then leaves the level.
  await openLevel("noground");
  await page.keyboard.press("p");
  await only("replay");
  await page.keyboard.press("Escape");
  await only("level");
  await page.waitForTimeout(400);
  const top = await page.evaluate(() => history.state?.ffwg);
  assert(top === "sub", `after Esc the top history entry is "${top}", not the level's`);
  await page.goBack();
  await only("worldmap");

  // --- 4. Compare: Back returns to the level; after Esc one Back leaves the level.
  await openLevel("redhat");
  await page.keyboard.press("c");
  await page.waitForFunction(() => window.__game.scene.isActive("compare") && !!window.__game.scene.keys.compare.compare, {
    timeout: 20000,
  });
  await page.goBack();
  await only("level");
  await page.waitForTimeout(300);
  await page.keyboard.press("c");
  await page.waitForFunction(() => window.__game.scene.isActive("compare"), { timeout: 20000 });
  await page.keyboard.press("Escape");
  await only("level");
  await page.waitForTimeout(400);
  await page.goBack();
  await only("worldmap");

  return { detail: `bands (top/bottom px) ${layouts.join(", ")}; Back + Esc paths for replay and compare` };
}
