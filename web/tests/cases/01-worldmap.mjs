// World map: boots, renders nodes, and the 4 corner buttons work off the
// (lossless) mask - hovering reveals the prelit button shape. Also the node
// hover highlight is dot-sized. Covers docs/027/038/039.

import { gotoWorldmap, canvasMapper, assert } from "../lib.mjs";

export const name = "world map: boot + corner buttons + node hover";

export default async function ({ page, baseURL }) {
  await gotoWorldmap(page, baseURL);

  const info = await page.evaluate(() => {
    const s = window.__game.scene.keys.worldmap;
    const p = s.maskPixels;
    const colors = new Set();
    for (let i = 0; i < p.w * p.h; i++) {
      const j = i * 4;
      colors.add((p.data[j] << 16) | (p.data[j + 1] << 8) | p.data[j + 2]);
    }
    return {
      nodes: s.mapData.nodes.length,
      corners: s.cornerButtons.map((c) => c.action).sort(),
      maskColors: colors.size,
    };
  });
  assert(info.nodes > 50, `expected many nodes, got ${info.nodes}`);
  assert(
    JSON.stringify(info.corners) === JSON.stringify(["credits", "exit", "intro", "options"]),
    `corners = ${info.corners}`,
  );
  // Lossless mask => a small set of flat colors (lossy WebP smears them).
  assert(info.maskColors <= 12, `mask has ${info.maskColors} colors (lossy?)`);

  // Hovering the Options corner reveals its prelit masked texture.
  const toPage = await canvasMapper(page);
  const opt = toPage(636, 476);
  await page.mouse.move(opt.x, opt.y);
  await page.waitForTimeout(150);
  const hover = await page.evaluate(() => {
    const s = window.__game.scene.keys.worldmap;
    return {
      active: s.activeCorner?.action ?? null,
      visible: s.lowerOverlay?.visible,
      tex: s.lowerOverlay?.texture?.key,
    };
  });
  assert(hover.active === "options" && hover.visible && hover.tex === "corner-options", `hover=${JSON.stringify(hover)}`);

  // Hovering a node draws a dot-sized selection ring (radius ~11, depth 4).
  const startNode = toPage(320, 121);
  await page.mouse.move(startNode.x, startNode.y);
  await page.waitForTimeout(150);
  const ring = await page.evaluate(() => {
    const r = window.__game.scene.keys.worldmap.selectionRing;
    return r ? { radius: r.radius, depth: r.depth } : null;
  });
  assert(ring && Math.abs(ring.radius - 11) < 2 && ring.depth === 4, `ring=${JSON.stringify(ring)}`);

  return { detail: `${info.nodes} nodes, ${info.maskColors} mask colors, corners+hover OK` };
}
