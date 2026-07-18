// Backup / restore of game progress (docs/072). Drives the serialize/parse/
// restore internals directly via the dev-only window.__progress handle - no UI
// reload - and asserts the hardening: solutions are re-validated (valid accepted,
// invalid/unknown rejected), and malformed/oversized files are refused.

import { gotoWorldmap, assert } from "../lib.mjs";

export const name = "progress: backup + hardened restore";

export default async function ({ page, baseURL }) {
  await gotoWorldmap(page, baseURL);
  await page.waitForFunction(() => !!window.__progress, { timeout: 10000 });

  const results = await page.evaluate(async () => {
    const P = window.__progress;
    const ls = await import("/src/storage/levelStorage.ts");
    const settings = await import("/src/storage/settingsStorage.ts");
    const { extractSavedMoves, fetchLegacyFile } = await import("/src/lua/levelLoader.ts");
    const out = [];
    const check = (cond, msg) => out.push({ ok: !!cond, msg });
    const clearAll = () => {
      for (const k of Object.keys(localStorage)) if (k.startsWith("ffwg:")) localStorage.removeItem(k);
    };

    const airplaneMoves = extractSavedMoves(await fetchLegacyFile("solution/airplane.lua"));

    // 1. serialize round-trips through parseBackup
    clearAll();
    ls.saveSolvedMoves("airplane", airplaneMoves);
    ls.addSavedGame("airplane", airplaneMoves.slice(0, 6), "{}");
    const parsed = P.parseBackup(P.serializeProgress());
    check(parsed.ok, "serialize -> parseBackup ok");
    check(parsed.ok && parsed.value.solved.airplane === airplaneMoves, "round-trip preserves solved moves");
    check(parsed.ok && (parsed.value.saves.airplane || []).length === 1, "round-trip preserves saves");
    check(parsed.ok && parsed.value.format === "ffwg-progress" && parsed.value.version === 1, "format+version present");

    // 2. restore re-validates solutions (valid accepted, invalid/unknown rejected)
    clearAll();
    const backup = {
      format: "ffwg-progress",
      version: 1,
      exportedAt: new Date().toISOString(),
      solved: {
        airplane: airplaneMoves, // valid -> accept
        viking1: airplaneMoves,  // real level, wrong solution -> reject
        notalevel: "udlr",       // unknown level -> reject
      },
      saves: { airplane: [{ id: "s1", moves: airplaneMoves.slice(0, 6), modelState: "{}" }] },
      settings: { lang: "nl", musicVolume: 33, soundVolume: 44, subtitles: false, showSteps: false, gameSize: 2 },
      playtimeSeconds: 4242,
    };
    const p2 = P.parseBackup(JSON.stringify(backup));
    check(p2.ok, "test2 parse ok");
    const report = await P.restoreProgress(p2.value);
    check(report.solvedAccepted.includes("airplane"), "valid solution accepted");
    check(report.solvedRejected.some((r) => r.level === "viking1"), "wrong solution rejected");
    check(report.solvedRejected.some((r) => r.level === "notalevel"), "unknown level rejected");
    check(ls.loadSolvedMoves("airplane") === airplaneMoves, "accepted solution persisted");
    check(ls.loadSolvedMoves("viking1") === null && ls.loadSolvedMoves("notalevel") === null, "rejected solutions not persisted");
    check(report.savesAccepted === 1 && ls.loadSavedGames("airplane").length === 1, "valid save restored");
    const st = settings.loadSettings();
    check(st.lang === "nl" && st.gameSize === 2 && st.musicVolume === 33, "settings applied");

    // 3. malformed / oversized rejected by parseBackup
    check(!P.parseBackup("not json at all").ok, "non-JSON rejected");
    check(!P.parseBackup(JSON.stringify({ foo: 1 })).ok, "missing format rejected");
    check(!P.parseBackup(JSON.stringify({ format: "ffwg-progress", version: 999 })).ok, "bad version rejected");
    check(!P.parseBackup("x".repeat(5 * 1024 * 1024 + 10)).ok, "oversized rejected");

    clearAll();
    return out;
  });

  const failed = results.filter((r) => !r.ok);
  assert(failed.length === 0, `failed: ${failed.map((r) => r.msg).join(" | ")}`);
  return { detail: `${results.length} checks: serialize round-trip, solution re-validation, malformed/oversized rejection` };
}
