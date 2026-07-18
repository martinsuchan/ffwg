// Settings wiring: the dialog/voice language setting drives the resolved voice
// directory (cs/nl), the setting round-trips through localStorage, and the
// port's own UI strings + world-map names localize by the setting (docs/038,
// docs/073).

import { gotoWorldmap, assert } from "../lib.mjs";

export const name = "settings: language wiring + UI localization (cs/nl)";

export default async function ({ page, baseURL }) {
  await gotoWorldmap(page, baseURL);

  const r = await page.evaluate(async () => {
    const ls = await import("/src/lua/levelScript.ts");
    const ss = await import("/src/storage/settingsStorage.ts");
    const i18n = await import("/src/i18n.ts");
    const wml = await import("/src/lua/worldMapLoader.ts");
    const mapData = window.__game.scene.keys.worldmap.mapData;

    ss.saveSettings({ lang: "nl" });
    const nlDir = ls.levelDialogVoiceDir("airplane");
    const nlSetting = ss.loadSettings().lang;
    const nlTitle = i18n.t("opt_title");
    const nlName = wml.mapName(mapData, "start");

    ss.saveSettings({ lang: "cs" }); // restore default
    const csDir = ls.levelDialogVoiceDir("airplane");
    const csTitle = i18n.t("opt_title");
    const csName = wml.mapName(mapData, "start");
    const csBack = i18n.t("menu_back"); // reused straight from legacy labels.lua

    return { nlDir, nlSetting, csDir, nlTitle, csTitle, nlName, csName, csBack };
  });

  assert(r.nlSetting === "nl", `setting did not persist: ${r.nlSetting}`);
  assert(r.nlDir === "airplane/nl", `nl voice dir = ${r.nlDir}`);
  assert(r.csDir === "airplane/cs", `cs voice dir = ${r.csDir}`);
  // UI strings localize (port label) ...
  assert(r.nlTitle === "Instellingen" && r.csTitle === "Nastavení", `opt_title cs/nl = ${r.csTitle}/${r.nlTitle}`);
  // ... legacy labels.lua reused ...
  assert(r.csBack === "Zpět", `menu_back cs = ${r.csBack}`);
  // ... and world-map names follow the language.
  assert(r.csName && r.nlName && r.csName !== r.nlName, `map name cs/nl = ${r.csName}/${r.nlName}`);

  return { detail: `voice dir + UI strings (${r.csTitle}/${r.nlTitle}) + map names localize` };
}
