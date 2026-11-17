// Every reference solution in legacy/solution/*.lua replays to a solved state
// through the ported physics engine (headless, no rendering). This is the
// suite's strongest gameplay regression net - it drives the full move/rules
// engine across all levels. Covers docs/022/023/024/035.
//
// Expectation: every solution whose level exists solves. A solution with no
// matching legacy/script/<level>/ directory is reported as "no-level" and
// skipped, not failed. None today: `redhat` was one until its content was
// imported, and the stray `cabin1-simple` solver variant was deleted (docs/083).

import { existsSync, readdirSync } from "fs";
import { gotoWorldmap, assert } from "../lib.mjs";

export const name = "all solutions: headless replay validates";

const SOLUTION_DIR = "../../../legacy/solution"; // cases -> tests -> web -> repo root
const SCRIPT_DIR = "../../../legacy/script";

export default async function ({ page, baseURL }) {
  await gotoWorldmap(page, baseURL);

  const { fileURLToPath } = await import("url");
  const { dirname, join } = await import("path");
  const here = dirname(fileURLToPath(import.meta.url));
  const all = readdirSync(join(here, SOLUTION_DIR))
    .filter((f) => f.endsWith(".lua"))
    .map((f) => f.replace(/\.lua$/, ""));
  const hasLevel = (lv) => existsSync(join(here, SCRIPT_DIR, lv));
  const levels = all.filter(hasLevel);
  const noLevel = all.filter((lv) => !hasLevel(lv));

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
  const unexpected = results.filter((r) => r.status !== "SOLVED");
  assert(
    unexpected.length === 0,
    `${unexpected.length} unexpected non-solved: ${unexpected.map((r) => `${r.lv}:${r.status}${r.err ? " " + r.err : ""}`).join(" | ")}`,
  );
  assert(solved.length > 0, "no solution with an existing level was validated");

  const skipped = noLevel.length ? `, ${noLevel.length} skipped (no-level): ${noLevel.join(", ")}` : "";
  return { detail: `${solved.length} solved${skipped}` };
}
