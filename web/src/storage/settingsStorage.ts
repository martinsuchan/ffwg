/**
 * localStorage-backed global game settings - the port's stand-in for the
 * original's OptionAgent persistent params (volume_music/volume_sound/lang/
 * subtitles). Read by AudioManager (volumes), levelScript (dialog language),
 * and LevelScene (subtitles); edited by the world map's Options overlay
 * (docs/038). All access is try/catch-guarded - storage being unavailable
 * degrades to the defaults, never crashes.
 */

const SETTINGS_KEY = "ffwg:settings";

/** Dialog/voice language. Only cs and nl are fully converted (text + audio);
 *  the selector offers exactly these (docs/038). */
export type DialogLang = "cs" | "nl";

export interface Settings {
  /** Subtitle text + voice-over language (both switch together). */
  lang: DialogLang;
  /** 0-100, applied as a flat multiplier by AudioManager (legacy
   *  volume_music/volume_sound). */
  musicVolume: number;
  soundVolume: number;
  /** Whether subtitles are drawn (voice audio plays regardless). */
  subtitles: boolean;
}

/** Legacy defaults (AudioManager's old GLOBAL_MUSIC_VOLUME/GLOBAL_SOUND_VOLUME,
 *  cs from docs/018, subtitles on). */
export const DEFAULT_SETTINGS: Settings = {
  lang: "cs",
  musicVolume: 50,
  soundVolume: 90,
  subtitles: true,
};

function clampVolume(v: unknown): number {
  const n = typeof v === "number" ? v : Number(v);
  if (!Number.isFinite(n)) return 50;
  return Math.max(0, Math.min(100, Math.round(n)));
}

/** Reads the whole settings record, filling any missing/invalid field with its
 *  default - so a partial or corrupt record still yields a usable Settings. */
export function loadSettings(): Settings {
  let raw: string | null = null;
  try {
    raw = localStorage.getItem(SETTINGS_KEY);
  } catch {
    return { ...DEFAULT_SETTINGS };
  }
  if (!raw) return { ...DEFAULT_SETTINGS };
  try {
    const p = JSON.parse(raw) as Partial<Settings>;
    return {
      lang: p.lang === "nl" ? "nl" : "cs",
      musicVolume: clampVolume(p.musicVolume ?? DEFAULT_SETTINGS.musicVolume),
      soundVolume: clampVolume(p.soundVolume ?? DEFAULT_SETTINGS.soundVolume),
      subtitles: p.subtitles !== false,
    };
  } catch {
    return { ...DEFAULT_SETTINGS };
  }
}

/** Merges `patch` into the stored settings and persists the result. Returns the
 *  new full settings so callers can apply them immediately. */
export function saveSettings(patch: Partial<Settings>): Settings {
  const next = { ...loadSettings(), ...patch };
  next.musicVolume = clampVolume(next.musicVolume);
  next.soundVolume = clampVolume(next.soundVolume);
  try {
    localStorage.setItem(SETTINGS_KEY, JSON.stringify(next));
  } catch {
    // Storage unavailable - settings won't persist, but the in-memory value
    // returned here still applies for this session.
  }
  return next;
}
