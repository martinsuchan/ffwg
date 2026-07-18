# 073 - UI localization + happy-path notification cleanup

_2026-07-18_

The port rendered many of its own strings (Settings panel, Help popup, win/lose
banners, save/load + backup toasts, replay UI) hard-coded in **English** while the
rest of the game is Czech/Dutch, and it showed several redundant success
notifications. This localizes every custom string and trims the noise.

## How FF NG localizes its Settings UI (the research answer)

`MenuOptions` (`legacy/src/option/MenuOptions.cpp`) builds each row from a
language-neutral **picture icon** (`volume_sound.png`, `lang.png`, `subtitle.png`,
`back.png`…); the localized text from `script/labels.lua` (`menu_sound`,
`menu_music`, `menu_lang`, `menu_speech`, `menu_subtitles`, `menu_back`, `help`) is
shown only as a **hover tooltip**. `labels.lua` registers `label_text(name, lang,
text)` in ~14 languages into a store; `Labels::getLabel(name)` returns the current
language's string with a DEFAULT_LANG fallback. **The port already parses
`labels.lua`** in `worldMapLoader.ts` (for the 3 pedometer `solver_*` labels), so
we reuse that same data - no new parse, no reinvented mechanism.

## Localization layer

New `web/src/i18n.ts`: `initLabels(map)` stores the legacy `labels.lua` entries
(keyed `<name>:<lang>`), `t(key, ...args)` resolves legacy store -> `PORT_LABELS`
-> English -> the raw key, substituting `%1`,`%2`,… (legacy
`Dialog::getFormatedSubtitle`, reused for `solver_*`). `PORT_LABELS` holds the
cs/nl/en strings the original never had (game size, backup/restore, the
two-column help, our toasts). Only cs/nl are supported (docs/038), so the legacy
14-language data is reused as-is where keyed and PORT_LABELS covers the rest.

`worldMapLoader.ts` now captures **all** `label_text` calls into
`WorldMapData.labels` (dropping the solver-only filter and the separate
`solverLabels`); `main.ts` calls `initLabels(worldMapData.labels)` after
`loadWorldMap`. `t()` reads `loadSettings().lang` live; the Options panel rebuilds
on each open, so reopening after a language change shows the new language
(matches the existing "language applies on next load" behavior, docs/067).

Applied at every custom-string call site: `OptionsOverlay` (title, all row labels,
size/toggle/backup/restore/back buttons, all status messages), `HelpOverlay`
(title, per-row descriptions, OK; keyboard-key labels stay literal), `LevelScene`
(the surviving warning toasts + level-unsupported), `ReplayScene` (hint/solved/died
+ Esc target), `WorldMapScene` ("Failed to load"), `PedometerUI` (solver label via
`t()`). `progressBackup.parseBackup` now returns an **i18n key**
(`backup_err_json`/`_format`/`_version`/`_big`) instead of an English sentence, so
the storage module stays UI-agnostic; `OptionsOverlay` does `t(parsed.error)`.
`OptionsOverlay` row control offset bumped to `CTRL_DX = 108` so localized labels
(e.g. Dutch "Ondertiteling") don't overlap their controls; the docs/069 fit-scale
is unchanged.

## World-map names now follow the language (fold-in)

`worlddesc.lua` has cs/nl/en for all 83 levels, but the port hard-coded
`MAP_LANG = "cs"`. `worldMapLoader.ts` now captures `worldmap_addDesc` for every
language (`names`/`sections` keyed `<codename>:<lang>`) and exports `mapName`/
`mapSection` resolvers (current lang -> en -> codename). `WorldMapScene`
(node label + `document.title`) and `PedometerUI` (a `nameOf` callback) use them,
so switching to Dutch also switches the map/level names and window titles.

## Removed happy-path notifications (per the user)

- Save/Load confirmations ("Saved (N moves)" / "Loaded (N moves)") - the save-dot
  row already shows the change.
- In-level "Solved!" / "new best!" banner - the auto-return + recap poster signal
  the win; the pedometer still shows the solver result. (saveSolvedMoves + the
  return countdown are unchanged.)
- "A fish died…" and "Both fish are stuck…" banners - the death/auto-restart
  behavior stays; only the text goes.
- World-map "Loading…" toast (the "Failed to load" error stays, localized).
- Backup success toast (the browser download is the confirmation). Restore's
  result summary + all failure/warning messages stay, localized.

`LevelScene.statusText` is now used only for the localized "level not supported"
message.

## DemoScene subtitle fixes (per the user)

The movie/poster subtitle used a `backgroundColor` box (the "shadow rectangle")
that also showed as an **empty box on posters**. Switched to the level's
`SubtitleStack` style - **outlined text, no background** (`stroke` + `strokeThickness`) -
and only shown when the text is non-empty. Single-line cut-on-new behavior and the
(already-localized) Lua dialog text are unchanged.

## Verification

- **Real browser (Playwright), both languages**: opened Options on the world map
  in cs and nl - every row label / button localized (cs "Nastavení/Jazyk/Velikost/
  Hudba/Zvuk/Titulky/Postup hry/Standardní/Zálohovat/Obnovit/Zpět"; nl
  "Instellingen/Taal/Grootte/Muziek/Geluid/Ondertiteling/Voortgang/Back-up/
  Herstellen/Terug") with no clipping/overlap (screenshot); help strings + backup
  error localized; `mapName("start")` = "Jak to všechno začalo" (cs) vs "Hoe het
  allemaal begon" (nl); the gods poster renders with **no empty subtitle box**.
- e2e case `04-settings.mjs` extended to assert `opt_title` (cs/nl), reused
  `menu_back` (cs "Zpět"), and localized map names; full suite green.
- `tsc -b` clean.

## Notes

- Czech drafted for a native speaker's review; Dutch drafted (legacy `nl`
  `menu_lang` is a known mistranslation - the port uses its own `opt_language`).
- Endonym language names ("Čeština"/"Nederlands") stay literal, never translated.
