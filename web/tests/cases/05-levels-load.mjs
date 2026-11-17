// Every world-map level loads its real Lua content cleanly: loadLevelModels +
// createLevelScript + a few live per-round ticks (which is what catches an
// unbound host function that only fires from a per-round closure, not at
// bootstrap - see docs/033). Runs headless (no Phaser scene/UI). Covers
// docs/024/028/033/035.
//
// Plus a static binding sweep: every host function the original registers for
// the level script context (legacy C++ `registerFunc("name", ...)` in
// LevelScript.cpp + its bases Planner.cpp and Scripter.cpp) must be bound in
// each level's live engine. Ticks alone only catch a gap once its (delayed or
// random) branch happens to fire; this catches it deterministically.

import { readFileSync } from "fs";
import { dirname, join } from "path";
import { fileURLToPath } from "url";
import { gotoWorldmap, nodeCodenames, assert } from "../lib.mjs";

export const name = "all levels: Lua load + live-tick sweep + host bindings";

const LEGACY_SRC = "../../../legacy/src"; // cases -> tests -> web -> repo root

// The registerFunc() sites that make up the level script's host API:
// LevelScript : Planner : Scripter. Deliberately NOT included: ScriptAgent.cpp
// (global options agent: sendMsg/setParam/getParam), WorldBranch.cpp
// (worldmap), SelectLang.cpp, Labels.cpp, DemoMode.cpp (demo_display) and
// LevelStatus.cpp (a separate Scripter that reads saved status files).
const LEVEL_REGISTRATION_FILES = [
  "level/LevelScript.cpp",
  "plan/Planner.cpp",
  "gengine/Scripter.cpp",
];

// Known-unbound in this port: the original's undo/redo machinery (Level::
// saveUndo + prog_save.lua's undo/redo) and the status-file reader, which the
// port has no equivalent of and no level script calls on a live path.
const UNBOUND_ALLOWLIST = new Set([
  "level_save",
  "level_load",
  "model_change_setLocation",
  "model_change_setExtraParams",
  "model_getExtraParams",
  "model_countAnims",
  "model_getDir",
  "status_readMoves",
]);

function levelHostFunctionNames() {
  const here = dirname(fileURLToPath(import.meta.url));
  const names = new Set();
  for (const rel of LEVEL_REGISTRATION_FILES) {
    const src = readFileSync(join(here, LEGACY_SRC, rel), "utf8");
    for (const m of src.matchAll(/registerFunc\(\s*"([A-Za-z_][A-Za-z0-9_]*)"/g)) {
      names.add(m[1]);
    }
  }
  return [...names].filter((n) => !UNBOUND_ALLOWLIST.has(n)).sort();
}

export default async function ({ page, baseURL }) {
  const hostFns = levelHostFunctionNames();
  // Guard against the regex silently matching nothing (e.g. a path change).
  assert(hostFns.length > 40, `only ${hostFns.length} host fn names parsed from legacy/src`);

  await gotoWorldmap(page, baseURL);
  const codenames = await nodeCodenames(page);

  const results = await page.evaluate(async ({ cns, hostFns }) => {
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
        // `lua` is TS-private on LevelScript, but a plain field at runtime.
        // Checked before ticking, i.e. right after bootstrap.
        const unbound = hostFns.filter((fn) => {
          const v = script.lua.global.get(fn);
          return v === null || v === undefined;
        });
        for (let i = 0; i < 5; i++) script.tick(rm);
        script.destroy();
        out.push({ cn, ok: true, unbound });
      } catch (e) {
        out.push({ cn, ok: false, err: String((e && e.message) || e), unbound: [] });
      }
    }
    return out;
  }, { cns: codenames, hostFns });

  const failed = results.filter((r) => !r.ok);
  assert(
    failed.length === 0,
    `${failed.length}/${results.length} level(s) failed: ${failed.map((f) => `${f.cn}: ${f.err}`).join(" | ")}`,
  );

  const unbound = results.flatMap((r) => r.unbound.map((fn) => `${r.cn}: ${fn}`));
  assert(
    unbound.length === 0,
    `${unbound.length} unbound level host fn(s) in the live engine: ${unbound.join(" | ")}`,
  );

  return {
    detail: `${results.length} levels loaded + ticked clean; ${hostFns.length} host fns bound in each`,
  };
}
