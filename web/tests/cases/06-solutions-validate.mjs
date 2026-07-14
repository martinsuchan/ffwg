// Every reference solution in legacy/solution/*.lua replays to a solved state
// through the ported physics engine (headless, no rendering). This is the
// suite's strongest gameplay regression net - it drives the full move/rules
// engine across all levels. Covers docs/022/023/024/035.
//
// Expectation: all solve except `redhat`, whose level content doesn't exist in
// this repo (it's a solution file with no matching legacy/script/redhat/).

import { readdirSync } from "fs";
import { gotoWorldmap, assert } from "../lib.mjs";

export const name = "all solutions: headless replay validates";

const SOLUTION_DIR = "../../../legacy/solution"; // cases -> tests -> web -> repo root

export default async function ({ page, baseURL }) {
  await gotoWorldmap(page, baseURL);

  const { fileURLToPath } = await import("url");
  const { dirname, join } = await import("path");
  const here = dirname(fileURLToPath(import.meta.url));
  const levels = readdirSync(join(here, SOLUTION_DIR))
    .filter((f) => f.endsWith(".lua"))
    .map((f) => f.replace(/\.lua$/, ""));

  const results = await page.evaluate(async (levels) => {
    const { loadLevelModels, extractSavedMoves, fetchLegacyFile } = await import("/src/lua/levelLoader.ts");
    const { GameEngine } = await import("/src/game/GameEngine.ts");
    const { validateSolution } = await import("/src/game/SolutionValidator.ts");
    const out = [];
    for (const lv of levels) {
      try {
        const src = await fetchLegacyFile(`solution/${lv}.lua`);
        const moves = extractSavedMoves(src);
        if (!moves) { out.push({ lv, status: "no-moves" }); continue; }
        const levelData = await loadLevelModels(lv);
        const engine = new GameEngine(levelData);
        const res = validateSolution(engine, moves);
        out.push({ lv, status: res.solved ? "SOLVED" : "unsolved", err: res.error });
      } catch (e) {
        out.push({ lv, status: "LOAD-ERROR", err: String((e && e.message) || e) });
      }
    }
    return out;
  }, levels);

  const solved = results.filter((r) => r.status === "SOLVED");
  // redhat has no level content in this repo - its failure is expected.
  const unexpected = results.filter((r) => r.status !== "SOLVED" && r.lv !== "redhat");
  assert(
    unexpected.length === 0,
    `${unexpected.length} unexpected non-solved: ${unexpected.map((r) => `${r.lv}:${r.status}${r.err ? " " + r.err : ""}`).join(" | ")}`,
  );

  return { detail: `${solved.length}/${results.length} solved (redhat has no level content, excluded)` };
}
