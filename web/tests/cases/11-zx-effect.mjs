// emulator's room walls use the legacy "zx" draw effect (ZX Spectrum tape-loading
// stripes, web/src/scenes/zxEffect.ts). Checks the effect is live on the room
// model, paints with the exact corner colours of zx-tmp.png (needs the lossless
// emulator atlas - scripts/convert-assets.ps1), and steps once per game cycle.
// Covers docs/084.

import { gotoWorldmap, assert } from "../lib.mjs";

export const name = "emulator: zx draw effect";

const EXPECTED = ["rgb(128,0,0)", "rgb(0,128,128)", "rgb(0,0,128)", "rgb(128,128,0)"];

export default async function ({ page, baseURL }) {
  await gotoWorldmap(page, baseURL);
  await page.evaluate(() => window.__game.scene.keys.worldmap.launchLevel("emulator"));
  await page.waitForFunction(
    () => window.__game.scene.isActive("level") && !!window.__game.scene.keys.level.levelScript,
    { timeout: 25000 },
  );

  const read = () =>
    page.evaluate(() => {
      const lvl = window.__game.scene.keys.level;
      const withZx = [...lvl.animators.values()].filter((a) => a.zx);
      if (withZx.length !== 1) return { count: withZx.length };
      const a = withZx[0];
      return {
        count: 1,
        texture: a.bodySprite.texture.key,
        size: [a.bodySprite.width, a.bodySprite.height],
        colors: a.zx.colors,
        phase: a.zx.phase,
      };
    });

  const first = await read();
  assert(first.count === 1, `expected exactly one model with the zx effect, got ${first.count}`);
  assert(first.texture === "fx-zx", `room sprite shows ${first.texture}, not the effect canvas`);
  assert(first.size[0] === 705 && first.size[1] === 540, `effect canvas size ${first.size}`);
  assert(
    JSON.stringify(first.colors) === JSON.stringify(EXPECTED),
    `corner colours ${JSON.stringify(first.colors)} (lossy emulator atlas? re-run scripts\\convert-assets.ps1 -Level emulator)`,
  );

  // Right after load the scene's timers can lag (assets/engine still settling),
  // so wait for progress instead of asserting a rate after a fixed sleep.
  const t0 = Date.now();
  await page
    .waitForFunction(
      (start) => {
        const a = [...window.__game.scene.keys.level.animators.values()].find((x) => x.zx);
        return a && (a.zx.phase - start + 500) % 500 >= 5;
      },
      first.phase,
      { timeout: 5000 },
    )
    .catch(() => {});
  const later = await read();
  const advanced = (later.phase - first.phase + 500) % 500;
  assert(advanced >= 5, `effect advanced only ${advanced} cycles in ${Date.now() - t0} ms`);

  return { detail: `room painted with exact ZX colours; ${advanced} cycles in ${Date.now() - t0} ms` };
}
