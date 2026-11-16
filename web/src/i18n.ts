/**
 * Tiny localization layer for the port's own UI strings (docs/073).
 *
 * Reuses the legacy `script/labels.lua` translations (already parsed once by
 * worldMapLoader.ts) exactly like FF NG's Settings menu does - `label_text(name,
 * lang, text)` registers a string per language, looked up by the current
 * language with an English fallback (legacy `Labels::getLabel`). Strings the
 * original never had (port-specific features: game size, backup/restore, the
 * two-column help, our own toasts) live in PORT_LABELS below.
 *
 * Only cs/nl are supported languages (docs/038); the legacy 14-language data is
 * reused as-is where a key exists, and PORT_LABELS carries cs/nl/en.
 */

import { loadSettings } from "./storage/settingsStorage";

/** legacy labels.lua entries, keyed `<name>:<lang>` - populated once at boot
 *  from worldMapLoader's parse (see initLabels / main.ts). */
let legacyStore = new Map<string, string>();

export function initLabels(legacy: Map<string, string>): void {
  legacyStore = legacy;
}

/** Port-specific strings with no legacy equivalent. cs drafted for a native
 *  speaker's review; nl drafted; en is the ultimate fallback. */
const PORT_LABELS: Record<string, { cs: string; nl: string; en: string }> = {
  // --- Options panel ---
  opt_title: { cs: "Nastavení", nl: "Instellingen", en: "Options" },
  opt_language: { cs: "Jazyk", nl: "Taal", en: "Language" },
  opt_gamesize: { cs: "Velikost", nl: "Grootte", en: "Game size" },
  opt_music: { cs: "Hudba", nl: "Muziek", en: "Music" },
  opt_sound: { cs: "Zvuk", nl: "Geluid", en: "Sound" },
  opt_subtitles: { cs: "Titulky", nl: "Ondertiteling", en: "Subtitles" },
  opt_progress: { cs: "Postup hry", nl: "Voortgang", en: "Game progress" },
  size_standard: { cs: "Standardní", nl: "Standaard", en: "Standard" },
  size_large: { cs: "Velká", nl: "Groot", en: "Large" },
  size_huge: { cs: "Obří", nl: "Enorm", en: "Huge" },
  toggle_on: { cs: "Zapnuto", nl: "Aan", en: "On" },
  toggle_off: { cs: "Vypnuto", nl: "Uit", en: "Off" },
  btn_backup: { cs: "Zálohovat", nl: "Back-up", en: "Backup" },
  btn_restore: { cs: "Obnovit", nl: "Herstellen", en: "Restore" },

  // --- Backup / restore status ---
  backup_validating: { cs: "Ověřování %1/%2…", nl: "Valideren %1/%2…", en: "Validating %1/%2…" },
  restore_done: {
    cs: "Obnoveno: %1 řešení, %2 uložení",
    nl: "Hersteld: %1 opgelost, %2 saves",
    en: "Restored %1 solved, %2 saves",
  },
  restore_rejected: { cs: ", %1 zamítnuto", nl: ", %1 afgewezen", en: ", %1 rejected" },
  reloading: { cs: "Načítám znovu…", nl: "Herladen…", en: "Reloading…" },
  backup_failed: { cs: "Zálohování selhalo.", nl: "Back-up mislukt.", en: "Backup failed." },
  restore_failed: { cs: "Obnovení selhalo.", nl: "Herstellen mislukt.", en: "Restore failed." },
  restore_read_err: { cs: "Soubor nelze načíst.", nl: "Kan bestand niet lezen.", en: "Could not read the file." },
  backup_err_json: { cs: "Soubor není platný JSON.", nl: "Bestand is geen geldige JSON.", en: "File is not valid JSON." },
  backup_err_format: {
    cs: "Soubor není záloha postupu FFWG.",
    nl: "Bestand is geen FFWG-voortgangsback-up.",
    en: "File is not an FFWG progress backup.",
  },
  backup_err_version: {
    cs: "Nepodporovaná verze zálohy.",
    nl: "Niet-ondersteunde back-upversie.",
    en: "Unsupported backup version.",
  },
  backup_err_big: {
    cs: "Soubor je příliš velký.",
    nl: "Bestand is te groot.",
    en: "File is too large to be a valid backup.",
  },

  // --- Help popup (keys like "F1"/"Space" stay literal; descriptions here) ---
  help_title: { cs: "Ovládání", nl: "Besturing", en: "Controls" },
  help_ok: { cs: "OK", nl: "OK", en: "OK" },
  help_move_active: { cs: "pohyb aktivní rybou", nl: "beweeg de actieve vis", en: "move the active fish" },
  help_move_big: { cs: "pohyb velkou rybou", nl: "beweeg de grote vis", en: "move the big fish" },
  help_move_small: { cs: "pohyb malou rybou", nl: "beweeg de kleine vis", en: "move the small fish" },
  help_switch: { cs: "přepni aktivní rybu", nl: "wissel van vis", en: "switch the active fish" },
  help_select: { cs: "vyber rybu", nl: "kies een vis", en: "select a fish" },
  help_swim: { cs: "plav ke kurzoru", nl: "zwem naar de cursor", en: "swim toward the cursor" },
  help_push: { cs: "tlač ke kurzoru", nl: "duw naar de cursor", en: "push toward the cursor" },
  help_restart: { cs: "restartuj místnost", nl: "herstart het veld", en: "restart the level" },
  help_replay: { cs: "přehraj řešení", nl: "bekijk de oplossing", en: "watch the solution replay" },
  help_saveload: { cs: "ulož / načti pozici", nl: "positie opslaan / laden", en: "save / load a position" },
  help_toggles: { cs: "počítadlo tahů / titulky", nl: "stappenteller / ondertiteling", en: "toggle step counter / subtitles" },
  help_settings: { cs: "nastavení", nl: "instellingen", en: "settings" },
  help_fullscreen: { cs: "celá obrazovka", nl: "volledig scherm", en: "fullscreen" },
  help_backmap: { cs: "zpět na mapu světa", nl: "terug naar wereldkaart", en: "back to the world map" },
  help_help: { cs: "zobraz / skryj nápovědu", nl: "toon / verberg deze help", en: "show / hide this help" },

  // --- Level toasts (warnings/errors kept; success toasts removed, docs/073) ---
  save_unsolvable: {
    cs: "Nelze uložit – místnost už nelze vyřešit",
    nl: "Kan niet opslaan – niet meer oplosbaar",
    en: "Can't save - no longer solvable",
  },
  save_loading: {
    cs: "Ještě se načítá, zkus to za chvíli",
    nl: "Nog aan het laden, probeer zo weer",
    en: "Still loading, try again in a moment",
  },
  save_full: {
    cs: "Všechny sloty jsou plné – smaž některý pravým tlačítkem",
    nl: "Alle slots vol – rechtsklik om er een te wissen",
    en: "All save slots full - right-click a dot to delete one",
  },
  save_none: { cs: "Žádná uložená pozice", nl: "Geen opgeslagen positie", en: "No saved position" },
  replay_none: { cs: "Není co přehrát", nl: "Geen oplossing om af te spelen", en: "No solution to replay" },
  level_unsupported: {
    cs: "Tato místnost zatím není podporována. Stiskni Esc pro mapu.",
    nl: "Dit veld wordt nog niet ondersteund. Druk op Esc voor de kaart.",
    en: "This level isn't supported yet. Press Esc for the map.",
  },

  // --- Replay UI ---
  replay_restart: { cs: "restart přehrávání", nl: "herstart replay", en: "restart replay" },
  replay_hint: { cs: "Esc = %1 · R = %2", nl: "Esc = %1 · R = %2", en: "Esc = %1 · R = %2" },
  replay_solved: { cs: "Vyřešeno! (R = %2, Esc = %1)", nl: "Opgelost! (R = %2, Esc = %1)", en: "Solved! (R = %2, Esc = %1)" },
  replay_died: {
    cs: "Konec – ryba zemřela. (R = %2, Esc = %1)",
    nl: "Einde – een vis stierf. (R = %2, Esc = %1)",
    en: "Replay ended - a fish died. (R = %2, Esc = %1)",
  },
  nav_worldmap: { cs: "mapa světa", nl: "wereldkaart", en: "world map" },

  // --- Replay video export (ReplayExportDialog + progress panel) ---
  export_title: { cs: "Export videa", nl: "Video exporteren", en: "Export video" },
  export_body: {
    cs:
      "Přehrávání se spustí znovu od začátku a na pozadí se snímek po snímku nahraje do videa " +
      "(herní plocha bude mezitím skrytá). Výsledkem je soubor MP4, který se automaticky stáhne " +
      "a je vhodný pro YouTube či Facebook. Ovládací tlačítka ve videu nebudou.",
    nl:
      "De replay start opnieuw vanaf het begin en wordt op de achtergrond beeld voor beeld " +
      "opgenomen (het speelveld is intussen verborgen). Het resultaat is een MP4-bestand dat " +
      "automatisch wordt gedownload, geschikt voor YouTube of Facebook. De video bevat geen " +
      "bedieningsknoppen.",
    en:
      "The replay restarts from the beginning and is recorded frame by frame in the background " +
      "(the game area is hidden meanwhile). The result is an MP4 file that downloads " +
      "automatically, suitable for YouTube or Facebook. The video has no on-screen controls.",
  },
  export_length: { cs: "Délka videa: přibližně %1", nl: "Videolengte: ongeveer %1", en: "Video length: about %1" },
  export_time: {
    cs:
      "Export bývá několikrát rychlejší než délka videa. Během něj uvidíš průběh a zbývající " +
      "čas. Kartu nech otevřenou.",
    nl:
      "Exporteren gaat meestal een paar keer sneller dan de videolengte. Voortgang en " +
      "resterende tijd worden getoond. Houd dit tabblad open.",
    en:
      "Exporting is usually several times faster than the video length. Progress and remaining " +
      "time are shown meanwhile. Keep this tab open.",
  },
  export_format: { cs: "Formát: MP4, %1×%2, 30 fps, %3", nl: "Formaat: MP4, %1×%2, 30 fps, %3", en: "Format: MP4, %1×%2, 30 fps, %3" },
  export_no_audio: { cs: "H.264, bez zvuku", nl: "H.264, zonder geluid", en: "H.264, no audio" },
  export_opt_steps: { cs: "Počítadlo tahů", nl: "Stappenteller", en: "Step counter" },
  export_opt_music: { cs: "Hudba z levelu", nl: "Muziek van het veld", en: "Level music" },
  export_start: { cs: "Exportovat", nl: "Exporteren", en: "Export" },
  export_cancel: { cs: "Zrušit", nl: "Annuleren", en: "Cancel" },
  export_close: { cs: "Zavřít", nl: "Sluiten", en: "Close" },
  export_unsupported: {
    cs:
      "Tento prohlížeč neumí kódovat video H.264 (WebCodecs), takže export není možný. " +
      "Zkus aktuální Chrome nebo Edge.",
    nl:
      "Deze browser kan geen H.264-video coderen (WebCodecs), dus exporteren is niet mogelijk. " +
      "Probeer een recente Chrome of Edge.",
    en:
      "This browser can't encode H.264 video (WebCodecs), so export isn't possible. " +
      "Try a current Chrome or Edge.",
  },
  export_opus_note: {
    cs:
      "Pozor: tento prohlížeč neumí zvuk AAC, hudba proto bude ve formátu Opus, který některé " +
      "weby (např. Facebook) nemusí přijmout. Použij Chrome/Edge, nebo exportuj bez hudby.",
    nl:
      "Let op: deze browser kan geen AAC-audio maken, dus de muziek wordt Opus, wat sommige " +
      "sites (bijv. Facebook) mogelijk weigeren. Gebruik Chrome/Edge of exporteer zonder muziek.",
    en:
      "Warning: this browser can't encode AAC audio, so the music will be Opus, which some " +
      "sites (e.g. Facebook) may reject. Use Chrome/Edge, or export without music.",
  },
  export_progress: { cs: "Hotovo %1 %", nl: "%1% voltooid", en: "%1% done" },
  export_eta: { cs: "Zbývá přibližně %1", nl: "Nog ongeveer %1", en: "About %1 left" },
  export_keep_tab: {
    cs: "Nech tuto kartu otevřenou, dokud export neskončí.",
    nl: "Houd dit tabblad open tot het exporteren klaar is.",
    en: "Keep this tab open until the export finishes.",
  },
  export_finalizing: { cs: "Dokončuji video…", nl: "Video afronden…", en: "Finishing the video…" },
  export_failed: { cs: "Export selhal: %1", nl: "Exporteren mislukt: %1", en: "Export failed: %1" },
  export_audio_failed: {
    cs: "%1 - prohlížeč nedokázal zakódovat zvuk. Zkus export s vypnutou hudbou.",
    nl: "%1 - de browser kon het geluid niet coderen. Probeer te exporteren zonder muziek.",
    en: "%1 - the browser could not encode the audio. Try exporting with music off.",
  },
  export_done: { cs: "Hotovo – staženo jako %1", nl: "Klaar – gedownload als %1", en: "Done - downloaded as %1" },
  export_min_sec: { cs: "%1 min %2 s", nl: "%1 min %2 s", en: "%1 min %2 s" },
  export_sec: { cs: "%1 s", nl: "%1 s", en: "%1 s" },
  nav_level: { cs: "místnost", nl: "veld", en: "level" },

  // --- World map ---
  load_failed: { cs: "Načtení selhalo: %1", nl: "Laden mislukt: %1", en: "Failed to load %1" },
  loading: { cs: "Načítání…", nl: "Laden…", en: "Loading…" },
};

/** The active dialog/UI language (cs/nl), read live so a change takes effect on
 *  the next lookup (the Options panel is rebuilt on each open). */
export function currentLang(): string {
  return loadSettings().lang;
}

function lookup(key: string, lang: string): string | undefined {
  const legacy = legacyStore.get(`${key}:${lang}`);
  if (legacy !== undefined) return legacy;
  const port = PORT_LABELS[key];
  return port ? port[lang as "cs" | "nl" | "en"] : undefined;
}

/**
 * Localized string for `key` in the current language, with English fallback and
 * then the raw key. `%1`,`%2`,… are substituted from `args` (legacy
 * `Dialog::getFormatedSubtitle`). Reused for the pedometer `solver_*` labels too.
 */
export function t(key: string, ...args: (string | number)[]): string {
  const lang = currentLang();
  const raw = lookup(key, lang) ?? lookup(key, "en") ?? key;
  let out = raw;
  args.forEach((a, i) => {
    out = out.split(`%${i + 1}`).join(String(a));
  });
  return out.trim();
}
