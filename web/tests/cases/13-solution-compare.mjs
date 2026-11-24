// Solution compare (sandbox, key C - docs/086): the state diff that lines two
// solutions up (SolutionDiff), checked on synthetic input and on every original
// in legacy/solution/old against its shorter replacement; then the scene itself,
// opened from a level with C, plays the difference and returns with Esc.

import { readdirSync } from "fs";
import { gotoWorldmap, assert } from "../lib.mjs";

export const name = "solution compare: state diff + side-by-side scene";

const OLD_DIR = "../../../legacy/solution/old"; // cases -> tests -> web -> repo root

export default async function ({ page, baseURL }) {
  await gotoWorldmap(page, baseURL);

  // --- 1. The diff itself, on plain arrays and inputs.
  const unit = await page.evaluate(async () => {
    const D = await import("/src/game/SolutionDiff.ts");
    const lcs = (a, b) => D.commonStates(a, b).map((p) => p.join(",")).join(" ");
    return {
      same: lcs([1, 2, 3], [1, 2, 3]),
      insert: lcs([1, 2, 3], [1, 2, 9, 3]),
      replace: lcs([1, 2, 3, 4], [1, 7, 8, 4]),
      disjoint: lcs([1, 2], [3, 4]),
      parsed: [D.parseMovesInput("saved_moves = 'uDl'\n"), D.parseMovesInput(" r R\n l "), D.parseMovesInput("  ")],
    };
  });
  assert(unit.same === "0,0 1,1 2,2", `same: ${unit.same}`);
  assert(unit.insert === "0,0 1,1 2,3", `insert: ${unit.insert}`);
  assert(unit.replace === "0,0 3,3", `replace: ${unit.replace}`);
  assert(unit.disjoint === "", `disjoint: ${unit.disjoint}`);
  assert(JSON.stringify(unit.parsed) === JSON.stringify(["uDl", "rRl", null]), `parse: ${JSON.stringify(unit.parsed)}`);

  // --- 2. Every original in old/ against the best known solution.
  const { fileURLToPath } = await import("url");
  const { dirname, join } = await import("path");
  const here = dirname(fileURLToPath(import.meta.url));
  const levels = readdirSync(join(here, OLD_DIR))
    .filter((f) => f.endsWith(".lua"))
    .map((f) => f.replace(/\.lua$/, ""));
  assert(levels.length > 0, "legacy/solution/old holds no solutions");

  const real = await page.evaluate(async (levels) => {
    const { loadLevelModels, extractSavedMoves, fetchLegacyFile } = await import("/src/lua/levelLoader.ts");
    const D = await import("/src/game/SolutionDiff.ts");
    const out = [];
    for (const lv of levels) {
      const levelData = await loadLevelModels(lv);
      const oldMoves = extractSavedMoves(await fetchLegacyFile(`solution/old/${lv}.lua`));
      const best = extractSavedMoves(await fetchLegacyFile(`solution/${lv}.lua`));
      const interner = new D.StateInterner();
      const a = D.traceSolution(levelData, oldMoves, interner);
      const b = D.traceSolution(levelData, best, interner);
      const c = D.compareTraces(a, b);
      const problems = [];
      // The segments tile both solutions, in order, from the start to the end.
      let ia = 0;
      let ib = 0;
      for (const s of c.segments) {
        if (s.a0 !== ia || s.b0 !== ib) problems.push(`gap at ${ia}/${ib}`);
        if (s.kind === "same") {
          for (let k = 0; k <= s.len; k++) if (a.keys[s.a0 + k] !== b.keys[s.b0 + k]) problems.push(`same@${s.a0 + k} differs`);
          ia = s.a0 + s.len;
          ib = s.b0 + s.len;
        } else {
          // A difference starts in a shared state and, if closed, ends in one.
          if (a.keys[s.a0] !== b.keys[s.b0]) problems.push(`diff@${s.a0} starts apart`);
          if (s.closed && a.keys[s.a1] !== b.keys[s.b1]) problems.push(`diff@${s.a1} ends apart`);
          ia = s.a1;
          ib = s.b1;
        }
      }
      if (ia !== a.moves.length || ib !== b.moves.length) problems.push(`ends at ${ia}/${ib}`);
      const deltaSum = c.diffs.reduce((sum, d) => sum + (d.b1 - d.b0) - (d.a1 - d.a0), 0);
      if (deltaSum !== c.delta) problems.push(`diff deltas ${deltaSum} != ${c.delta}`);
      out.push({
        lv,
        solved: a.solved && b.solved,
        delta: c.delta,
        diffs: c.diffs.length,
        open: c.diffs.filter((d) => !d.closed).length,
        similarity: c.similarity,
        problems,
      });
    }

    // A truncated copy: unsolved, a 100 % prefix and one open tail. A bad move: the trace stops there.
    const levelData = await loadLevelModels("redhat");
    const best = extractSavedMoves(await fetchLegacyFile("solution/redhat.lua"));
    const interner = new D.StateInterner();
    const full = D.traceSolution(levelData, best, interner);
    const half = D.traceSolution(levelData, best.slice(0, 100), interner);
    const cut = D.compareTraces(full, half);
    const bad = D.traceSolution(levelData, best.slice(0, 10) + "X" + best.slice(10), interner);
    return {
      out,
      truncated: { solved: half.solved, same: cut.sameMoves, diffs: cut.diffs.length, open: cut.diffs[0] && !cut.diffs[0].closed },
      bad: { failedAt: bad.failedAt, moves: bad.moves.length, solved: bad.solved },
    };
  }, levels);

  for (const r of real.out) {
    assert(r.problems.length === 0, `${r.lv}: ${r.problems.slice(0, 5).join("; ")}`);
    assert(r.solved, `${r.lv}: a solution no longer solves`);
    assert(r.delta < 0 && r.diffs > 0, `${r.lv}: delta ${r.delta}, ${r.diffs} diffs`);
  }
  const by = Object.fromEntries(real.out.map((r) => [r.lv, r]));
  // redhat: the window optimiser's two overlapping shortcuts are one stretch, -4.
  if (by.redhat) assert(by.redhat.diffs === 1 && by.redhat.delta === -4 && by.redhat.open === 0, `redhat: ${JSON.stringify(by.redhat)}`);
  // map: its shortened solution leaves two twin items swapped - they must still
  // line up (twins compare as a set), so no difference stays open to the end.
  // (Swapped twins used to drop it to ~22 %; at 2001 vs 2127 it is ~69 %.)
  if (by.map) assert(by.map.open === 0 && by.map.similarity > 0.5, `map: ${JSON.stringify(by.map)}`);
  const t = real.truncated;
  assert(!t.solved && t.same === 100 && t.diffs === 1 && t.open, `truncated: ${JSON.stringify(t)}`);
  assert(real.bad.failedAt?.index === 10 && real.bad.failedAt.symbol === "X" && real.bad.moves === 10 && !real.bad.solved, `bad: ${JSON.stringify(real.bad)}`);

  // --- 3. The scene: redhat, C from the level, the difference plays, Esc returns.
  await page.evaluate(async () => {
    const { loadLevelModels } = await import("/src/lua/levelLoader.ts");
    const levelData = await loadLevelModels("redhat");
    window.__game.scene.getScenes(true).forEach((s) => window.__game.scene.stop(s.scene.key));
    window.__game.scene.start("level", { levelData });
  });
  await page.waitForFunction(() => window.__game.scene.isActive("level") && !!window.__game.scene.keys.level.levelScript, {
    timeout: 25000,
  });
  await page.waitForTimeout(500);
  await page.keyboard.press("c");
  await page.waitForFunction(() => window.__game.scene.isActive("compare") && !!window.__game.scene.keys.compare.compare, {
    timeout: 20000,
  });
  const opened = await page.evaluate(() => {
    const s = window.__game.scene.keys.compare;
    const d = s.compare.diffs[0];
    return { diffs: s.compare.diffs.length, a0: d.a0, b0: d.b0, a: s.paneA.moveIndex, b: s.paneB.moveIndex, lenA: s.paneA.length, lenB: s.paneB.length };
  });
  assert(opened.diffs === 1 && opened.lenA === 287 && opened.lenB === 283, `opened: ${JSON.stringify(opened)}`);
  // It starts a few moves before the difference, both rooms in the same state.
  assert(opened.a === opened.b && opened.a >= opened.a0 - 5 && opened.a <= opened.a0, `start: ${JSON.stringify(opened)}`);

  // The level's own music plays (the track its code.lua starts, LevelData.music).
  await page.waitForFunction(
    () => {
      const s = window.__game.scene.keys.compare;
      return !!s.levelData.music && s.audioManager.currentMusicTrack === s.levelData.music;
    },
    { timeout: 15000 },
  );

  // Arrows step one move: → animates it and stays paused, ← goes back at once.
  const pos = () =>
    page.evaluate(() => {
      const s = window.__game.scene.keys.compare;
      return { a: s.paneA.moveIndex, b: s.paneB.moveIndex, paused: s.paused, stepping: s.stepping };
    });
  const landed = () => page.waitForFunction(() => !window.__game.scene.keys.compare.stepping, { timeout: 10000 });
  await page.keyboard.press("ArrowRight");
  await landed();
  const p1 = await pos();
  await page.keyboard.press("ArrowRight");
  await landed();
  await page.waitForTimeout(300); // paused: nothing moves on its own
  const p2 = await pos();
  assert(p1.paused && p2.paused, `a step pauses: ${JSON.stringify({ p1, p2 })}`);
  assert(p2.a - p1.a + (p2.b - p1.b) >= 1 && p2.a - p1.a <= 1 && p2.b - p1.b <= 1, `step forward: ${JSON.stringify({ p1, p2 })}`);
  await page.keyboard.press("ArrowLeft");
  const p3 = await pos();
  assert(p3.a === p1.a && p3.b === p1.b && p3.paused, `step back: ${JSON.stringify({ p1, p3 })}`);
  // Space plays on from there.
  await page.keyboard.press(" ");

  // Fast-forward: the shorter side (B) finishes the difference first and waits.
  await page.evaluate(() => {
    window.__game.scene.keys.compare.speedIndex = 6;
  });
  await page.waitForFunction(
    () => {
      const s = window.__game.scene.keys.compare;
      return s.segIndex > 1 || (s.segIndex === 1 && s.paneB.done() && !s.paneA.done());
    },
    { timeout: 20000 },
  );
  const waited = await page.evaluate(() => {
    const s = window.__game.scene.keys.compare;
    return { seg: s.segIndex, noteB: s.paneB.note.visible ? s.paneB.note.text : null };
  });
  assert(waited.seg > 1 || waited.noteB, `B never waited: ${JSON.stringify(waited)}`);
  // Both end the difference in the shared state and play on together.
  await page.waitForFunction(() => window.__game.scene.keys.compare.segIndex >= 2, { timeout: 20000 });
  const after = await page.evaluate(() => {
    const s = window.__game.scene.keys.compare;
    const d = s.compare.diffs[0];
    return { a: s.paneA.moveIndex - d.a1, b: s.paneB.moveIndex - d.b1 };
  });
  assert(after.a === after.b && after.a >= 0, `after the difference: ${JSON.stringify(after)}`);

  await page.keyboard.press("Escape");
  await page.waitForFunction(() => window.__game.scene.isActive("level") && !window.__game.scene.isActive("compare"), {
    timeout: 10000,
  });

  const summary = real.out.map((r) => `${r.lv} ${r.delta} (${r.diffs})`).join(", ");
  return { detail: `${real.out.length} originals lined up: ${summary}; scene played redhat's difference` };
}
