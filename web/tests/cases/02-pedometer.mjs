// Pedometer (clicking a solved node): masked Run/Replay/Cancel buttons, the
// step count drawn from numbers.png, the real localized best-solution text, a
// clean map underneath (dots + edges hidden, no dark overlay), and Cancel
// restoring the dots. Covers docs/039/040.

import { gotoWorldmap, canvasMapper, nodeXY, assert } from "../lib.mjs";

export const name = "pedometer: masked buttons + digits + clean map";

export default async function ({ page, baseURL }) {
  // Seed a solved solution (12 moves, < the record) before the app loads.
  await page.addInitScript(() => {
    localStorage.setItem("ffwg:solved:viking1", "aabbccddeeff");
  });
  await gotoWorldmap(page, baseURL);

  const toPage = await canvasMapper(page);
  const vk = await nodeXY(page, "viking1");
  const p = toPage(vk.x, vk.y);
  await page.mouse.click(p.x, p.y);
  await page.waitForFunction(() => window.__game.scene.keys.worldmap.pedometerUI.isShowing, { timeout: 5000 });
  await page.waitForTimeout(700); // let the count-up tween finish

  const st = await page.evaluate(() => {
    const s = window.__game.scene.keys.worldmap;
    const u = s.pedometerUI;
    return {
      backdropAlpha: u.backdrop?.fillAlpha,
      buttons: u.buttons.map((b) => b.action).sort(),
      digitCount: u.digits.length,
      digitReadout: u.digits.map((d) => 9 - d.frame.name).join(""),
      solverText: u.compareText?.text ?? "",
      solverHasBg: !!(u.compareText?.style?.backgroundColor),
      anyNodeVisible: [...s.nodeSprites.values()].some((sp) => sp.far.visible),
      edgesVisible: s.edges?.visible,
    };
  });
  assert(st.backdropAlpha === 0, `backdrop alpha ${st.backdropAlpha} (should be 0 - no dark overlay)`);
  assert(JSON.stringify(st.buttons) === JSON.stringify(["cancel", "replay", "run"]), `buttons=${st.buttons}`);
  assert(st.digitCount === 5 && st.digitReadout === "00012", `digits=${st.digitCount} readout=${st.digitReadout}`);
  assert(st.solverText.length > 0 && !st.solverHasBg, `solverText="${st.solverText}" hasBg=${st.solverHasBg}`);
  assert(st.anyNodeVisible === false && st.edgesVisible === false, `map not clean: nodes=${st.anyNodeVisible} edges=${st.edgesVisible}`);

  // Cancel (panel-relative 170,100) restores the node graph.
  const cancel = toPage(193 + 170, 141 + 100);
  await page.mouse.click(cancel.x, cancel.y);
  await page.waitForTimeout(300);
  const after = await page.evaluate(() => {
    const s = window.__game.scene.keys.worldmap;
    return {
      showing: s.pedometerUI.isShowing,
      nodesVisible: [...s.nodeSprites.values()].every((sp) => sp.far.visible),
      edgesVisible: s.edges?.visible,
    };
  });
  assert(!after.showing && after.nodesVisible && after.edgesVisible, `after cancel=${JSON.stringify(after)}`);

  return { detail: `digits "${st.digitReadout}", solver "${st.solverText.split("\n")[0]}", clean map + cancel OK` };
}
