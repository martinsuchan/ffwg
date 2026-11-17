// submarine's mirror uses the legacy "mirror" draw effect
// (web/src/scenes/mirrorEffect.ts): the glass shows the already-drawn screen just
// left of the mirror, flipped. Checks the display order (capture + shader right
// after the mirror, after every model, before the UI), the hidden body sprite and
// camera framebuffer, and - on real pixels - that a glass pixel equals its
// mirrored counterpart left of the mirror. Covers docs/084.

import { gotoWorldmap, assert } from "../lib.mjs";

export const name = "submarine: mirror draw effect";

export default async function ({ page, baseURL }) {
  await gotoWorldmap(page, baseURL);
  await page.evaluate(() => window.__game.scene.keys.worldmap.launchLevel("submarine"));
  await page.waitForFunction(
    () => window.__game.scene.isActive("level") && !!window.__game.scene.keys.level.levelScript,
    { timeout: 25000 },
  );
  await page.waitForTimeout(300);

  const s = await page.evaluate(() => {
    const lvl = window.__game.scene.keys.level;
    const withMirror = [...lvl.animators.values()].filter((a) => a.mirror);
    if (withMirror.length !== 1) return { count: withMirror.length };
    const a = withMirror[0];
    const list = lvl.children.list;
    const body = list.indexOf(a.bodySprite);
    const capture = list.indexOf(a.mirror.capture);
    const shader = list.indexOf(a.mirror.shader);
    const lastModel = Math.max(...[...lvl.animators.values()].flatMap((x) =>
      [x.bodySprite, x.headSprite].filter(Boolean).map((o) => list.indexOf(o))));
    const uiBefore = list.slice(0, shader).filter((o) => o.depth >= 1000).length;
    return {
      count: 1,
      body, capture, shader, lastModel, uiBefore,
      alpha: a.bodySprite.alpha,
      composite: lvl.cameras.main.forceComposite,
      size: [a.mirror.width, a.mirror.height],
    };
  });
  assert(s.count === 1, `expected exactly one model with the mirror effect, got ${s.count}`);
  assert(s.capture === s.body + 1 && s.shader === s.capture + 1,
    `display order body/capture/shader = ${s.body}/${s.capture}/${s.shader}`);
  assert(s.lastModel === s.body, `a model sprite (index ${s.lastModel}) is drawn after the mirror (${s.body})`);
  assert(s.uiBefore === 0, `${s.uiBefore} UI object(s) drawn before the mirror would be reflected`);
  assert(s.alpha === 0 && s.composite, `body alpha ${s.alpha}, camera forceComposite ${s.composite}`);
  assert(s.size[0] === 30 && s.size[1] === 45, `mirror picture size ${s.size}`);

  // Real pixels: one frame's snapshot of the row through the glass. Glass
  // column px (> 3) must show the screen at x + 3 - px (EffectMirror::blit).
  const px = await page.evaluate(
    () =>
      new Promise((resolve) => {
        const lvl = window.__game.scene.keys.level;
        const a = [...lvl.animators.values()].find((x) => x.mirror);
        const b = a.bodySprite;
        const z = lvl.cameras.main.zoom;
        const row = Math.round((b.y + 22.5) * z);
        const x0 = Math.round((b.x - 30) * z);
        const w = Math.round(60 * z);
        window.__game.renderer.snapshotArea(x0, row, w, 1, (img) => {
          const c = document.createElement("canvas");
          c.width = w;
          c.height = 1;
          const ctx = c.getContext("2d");
          ctx.drawImage(img, 0, 0);
          const d = ctx.getImageData(0, 0, w, 1).data;
          const at = (worldX) => {
            const i = Math.round((worldX - (b.x - 30)) * z) * 4;
            return [d[i], d[i + 1], d[i + 2]];
          };
          // Glass column 15 (centre) <-> screen x + 3 - 15.5 (pixel centres).
          resolve({ glass: at(b.x + 15.5), source: at(b.x + 3 - 15.5) });
        });
      }),
  );
  const diff = Math.max(...px.glass.map((v, i) => Math.abs(v - px.source[i])));
  const cyan = px.glass[0] < 60 && px.glass[1] > 200 && px.glass[2] > 200;
  assert(!cyan, `glass still shows the plain picture colour ${px.glass}`);
  assert(diff <= 24, `glass ${px.glass} != mirrored source ${px.source} (max channel diff ${diff})`);

  return { detail: `order ok (mirror last model, then capture+shader, UI after); glass ${px.glass} ~ source ${px.source}` };
}
