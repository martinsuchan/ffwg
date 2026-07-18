# 075 - Hide language switch, gate en-sprite fetches, world-map loading screen

_2026-07-18_

Three user requests.

## 1. Hide the cs/nl language switch

Dutch content isn't shipped for now (only cs is packaged; docs/073's nl audio was
also dropped from the deploy), so the Language row in the Settings panel is
hidden. `OptionsOverlay.ts`: a `SHOW_LANGUAGE = false` const skips
`buildLanguageRow` and shrinks `PANEL_H` by one row (430->386). The setting stays
cs (default); `LANGS`/`buildLanguageRow` are kept behind the flag so it's a
one-line re-enable. The nl `PORT_LABELS`/dialog data stay (harmless, tiny).

## 2. English sprite requests failing even in Czech

Two-part question:

- **Why en is fetched at all in Czech:** the per-line **English voice fallback**
  (docs/060) - Czech has a few lines/levels voiced only in English (viking1's
  band, cancan's piano), so `levelSoundSpriteDirs` preloads the `<level>/en` +
  shared en pools alongside the cs ones to have those clips ready.
- **Why they fail:** only ~31 of ~80 levels actually have English audio, so
  `<level>/en/sprite.json` 404s for the other ~50 (real 404 on the SWA; on the dev
  server it SPA-falls-back to index.html then fails JSON.parse). Both fetch paths
  already **caught** it (silent fallback), but the failing request still showed in
  the console/network.

Fix: gate every sound-dir fetch on the set of dirs that actually have converted
audio, derived from the audio manifest. New `getSoundSpriteDirs()`
(`web/src/lua/levelLoader.ts`) turns the manifest's `sound/<dir>/<file>` entries
into a `Set<dir>` (cached). `AudioEngine.doLoadDir` and `fetchSoundDurations`
(`dialogSound.ts`) skip any dir not in it, so a missing en dir is never
requested. The en fallback still works for the 31 levels that have en audio (their
en dirs are in the manifest). Verified: `airplane` (no en) fires **0** en sprite
requests; `viking1` (has the band) still requests `viking1/en`.

## 3. World-map loading screen (slow connections)

Clicking a level takes a few seconds to load (Lua bootstrap + prewarm, docs/059);
previously the map just sat there (the docs/073 "Loading…" toast was removed).
Now `WorldMapScene.showLoadingScreen(codename)` hides the whole node graph
(`setNodesVisible(false)`, reused from the pedometer) and shows a centered
localized **"Načítání…"** with the level name below it (`mapName`), over the map
art. Called at the start of `launchLevel`/`launchReplay`; the level scene start
tears it down, and a load **failure** restores the graph
(`hideLoadingScreen` + the localized `load_failed` toast). Stale (destroyed)
handles are reset in `create()` since the scene instance is reused. New `loading`
i18n key.

## Verification (real browser)

- Options: Language row gone; Game size / Music / Sound / Subtitles / Game
  progress intact.
- en gate: `airplane` load = 0 en requests (only `airplane/cs` + cs share pools);
  `viking1` still requests `viking1/en`; no failed sound requests.
- Loading screen: clicking a node hides all 81 dots + edges and shows "Načítání…"
  + "Výška: -9000 stop" centered (screenshot); nodes restored on hide/failure.
- e2e full suite + `tsc -b` clean.

## Open for next time

- Re-enable the language switch (flip `SHOW_LANGUAGE`) once nl content is shipped
  again.
- The fixes are on the working tree only; the live Azure site still lacks them
  until the next deploy (docs/074's note about the nl-trim upload workaround).
