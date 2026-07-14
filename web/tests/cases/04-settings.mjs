// Settings wiring: the dialog/voice language setting drives the resolved voice
// directory (cs/nl), and the setting round-trips through localStorage. Covers
// docs/038.

import { gotoWorldmap, assert } from "../lib.mjs";

export const name = "settings: language wiring (cs/nl)";

export default async function ({ page, baseURL }) {
  await gotoWorldmap(page, baseURL);

  const r = await page.evaluate(async () => {
    const ls = await import("/src/lua/levelScript.ts");
    const ss = await import("/src/storage/settingsStorage.ts");
    ss.saveSettings({ lang: "nl" });
    const nlDir = ls.levelDialogVoiceDir("airplane");
    const nlSetting = ss.loadSettings().lang;
    ss.saveSettings({ lang: "cs" }); // restore default
    const csDir = ls.levelDialogVoiceDir("airplane");
    return { nlDir, nlSetting, csDir };
  });

  assert(r.nlSetting === "nl", `setting did not persist: ${r.nlSetting}`);
  assert(r.nlDir === "airplane/nl", `nl voice dir = ${r.nlDir}`);
  assert(r.csDir === "airplane/cs", `cs voice dir = ${r.csDir}`);

  return { detail: "language setting drives voice dir + persists" };
}
