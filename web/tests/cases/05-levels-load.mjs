// Every world-map level loads its real Lua content cleanly: loadLevelModels +
// createLevelScript + a few live per-round ticks (which is what catches an
// unbound host function that only fires from a per-round closure, not at
// bootstrap - see docs/033). Runs headless (no Phaser scene/UI). Covers
// docs/024/028/033/035.

import { gotoWorldmap, nodeCodenames, assert } from "../lib.mjs";

export const name = "all levels: Lua load + live-tick sweep";

export default async function ({ page, baseURL }) {
  await gotoWorldmap(page, baseURL);
  const codenames = await nodeCodenames(page);

  const results = await page.evaluate(async (cns) => {
    const loader = await import("/src/lua/levelLoader.ts");
    const scriptMod = await import("/src/lua/levelScript.ts");
    // A stub EngineControl so levels whose code.lua reaches into the engine
    // don't throw during the sweep. Must implement EVERY member of the real
    // EngineControl interface (web/src/lua/levelScript.ts) - this file is plain
    // JS, so TypeScript can't catch it drifting, and a missing method surfaces
    // as "engineControl?.X is not a function" on every level. askFieldIndex
    // returns null (= empty water) since there's no real Field here.
    const engineControl = {
      setBusy() {},
      checkActive() {},
      setFastFalling() {},
      askFieldIndex() { return null; },
      isSolved() { return false; },
    };
    const out = [];
    for (const cn of cns) {
      try {
        const levelData = await loader.loadLevelModels(cn);
        const rm = levelData.models.map((m, i) => ({
          index: i, kind: m.kind, x: m.x, y: m.y, isLeft: m.isLeft,
          isAlive: true, isOut: false, isLost: false, action: "rest",
          state: "normal", moveStreak: 0, touchDir: 0,
        }));
        const script = await scriptMod.createLevelScript(cn, rm, 1, undefined, engineControl);
        for (let i = 0; i < 5; i++) script.tick(rm);
        script.destroy();
        out.push({ cn, ok: true });
      } catch (e) {
        out.push({ cn, ok: false, err: String((e && e.message) || e) });
      }
    }
    return out;
  }, codenames);

  const failed = results.filter((r) => !r.ok);
  assert(
    failed.length === 0,
    `${failed.length}/${results.length} level(s) failed: ${failed.map((f) => `${f.cn}: ${f.err}`).join(" | ")}`,
  );

  return { detail: `${results.length} levels loaded + ticked clean` };
}
