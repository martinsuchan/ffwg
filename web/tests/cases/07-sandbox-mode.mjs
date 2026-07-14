// The URL decides the mode (docs/045): "/" is the standard game (real
// progression gating - with no solves, only the root node is open), and
// "/sandbox" unlocks every node. Both paths serve the same app (SPA fallback).

import { assert } from "../lib.mjs";

export const name = "app mode: / gates, /sandbox unlocks";

async function nodeStateCounts(page, url) {
  await page.goto(url, { waitUntil: "load", timeout: 30000 });
  await page.waitForFunction(() => !!window.__game, { timeout: 20000 });
  await page.waitForFunction(() => window.__game.scene.isActive("worldmap"), { timeout: 15000 });
  await page.waitForTimeout(300);
  return page.evaluate(() => {
    const states = [...window.__game.scene.keys.worldmap.nodeStates.values()];
    const count = (s) => states.filter((v) => v === s).length;
    return {
      total: states.length,
      open: count("open"),
      far: count("far"),
      hidden: count("hidden"),
      solved: count("solved"),
      sandbox: window.__game.scene.keys.worldmap.sandbox ?? null,
    };
  });
}

export default async function ({ page, baseURL }) {
  const root = baseURL.replace(/\/+$/, "");

  // Standard mode: with a clean solved set, only the root "start" node is open;
  // everything else is gated (far/hidden).
  const std = await nodeStateCounts(page, root + "/");
  assert(std.open <= 2, `standard mode: expected ~1 open node, got ${std.open} (gating off?)`);
  assert(std.far + std.hidden > 50, `standard mode: expected most nodes gated, got far+hidden=${std.far + std.hidden}`);

  // Sandbox mode: every not-yet-solved node is open; none gated.
  const sb = await nodeStateCounts(page, root + "/sandbox");
  assert(sb.far === 0 && sb.hidden === 0, `sandbox: expected nothing gated, got far=${sb.far} hidden=${sb.hidden}`);
  assert(sb.open > 50, `sandbox: expected most nodes open, got ${sb.open}`);

  return { detail: `standard open=${std.open} gated=${std.far + std.hidden}; sandbox open=${sb.open} gated=0` };
}
