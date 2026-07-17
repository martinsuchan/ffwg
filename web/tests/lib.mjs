// Shared helpers for the e2e regression suite. See web/tests/README.md.

/** Load the app and wait until the world map scene is live. Relies on the
 *  dev-only window.__game handle (main.ts, gated on import.meta.env.DEV).
 *  Defaults to /sandbox so every level is unlocked/clickable (docs/045); pass
 *  { sandbox: false } to load the standard, progression-gated game. */
export async function gotoWorldmap(page, baseURL, { sandbox = true } = {}) {
  const url = sandbox ? baseURL.replace(/\/+$/, "") + "/sandbox" : baseURL;
  await page.goto(url, { waitUntil: "load", timeout: 30000 });
  await page.waitForFunction(() => !!window.__game, { timeout: 20000 });
  await page.waitForFunction(() => window.__game.scene.isActive("worldmap"), { timeout: 15000 });
  await page.waitForTimeout(400);
}

/** Returns a fn mapping world coords (the 640x480 map space) to page pixels.
 *  The render pipeline is camera-zoom now (docs/064): the framebuffer is
 *  worldSize*factor and the main camera zooms world coords by `factor`, so world
 *  -> framebuffer is `wx * cam.zoom`, then framebuffer -> CSS is `w/cw`. (Works
 *  for the old CSS-zoom path too, where cam.zoom == 1.) */
export async function canvasMapper(page) {
  const box = await page.evaluate(() => {
    const c = document.querySelector("canvas");
    const r = c.getBoundingClientRect();
    const cam = window.__game.scene.keys.worldmap.cameras.main;
    return { x: r.x, y: r.y, w: r.width, h: r.height, cw: c.width, ch: c.height, zoom: cam.zoom };
  });
  return (wx, wy) => ({
    x: box.x + wx * box.zoom * (box.w / box.cw),
    y: box.y + wy * box.zoom * (box.h / box.ch),
  });
}

/** The world-map node record for a codename (x/y are map-space coords). */
export async function nodeXY(page, codename) {
  return page.evaluate(
    (c) => window.__game.scene.keys.worldmap.mapData.nodes.find((n) => n.codename === c),
    codename,
  );
}

/** All world-map node codenames. */
export async function nodeCodenames(page) {
  return page.evaluate(() => window.__game.scene.keys.worldmap.mapData.nodes.map((n) => n.codename));
}

/** Assert a condition; throws with a message the runner surfaces as a failure. */
export function assert(condition, message) {
  if (!condition) throw new Error(message);
}
