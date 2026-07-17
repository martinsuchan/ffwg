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

/** On-screen render scale (CSS zoom of the whole canvas). Standard/Large/Huge -
 *  the only three values the Options selector offers (docs/064). */
export type GameSize = 1 | 1.5 | 2;
export const GAME_SIZES: GameSize[] = [1, 1.5, 2];

export interface Settings {
  /** Subtitle text + voice-over language (both switch together). */
  lang: DialogLang;
  /** 0-100, applied as a flat multiplier by AudioManager (legacy
   *  volume_music/volume_sound). */
  musicVolume: number;
  soundVolume: number;
  /** Whether subtitles are drawn (voice audio plays regardless). */
  subtitles: boolean;
  /** Canvas zoom factor: 1 = Standard (100%), 1.5 = Large, 2 = Huge. The world
   *  (sprites/photo backgrounds) is CSS-stretched by this; text is rendered at
   *  a higher resolution so it stays crisp (docs/064). */
  gameSize: GameSize;
}

/** Legacy defaults (AudioManager's old GLOBAL_MUSIC_VOLUME/GLOBAL_SOUND_VOLUME,
 *  cs from docs/018, subtitles on). gameSize defaults to 1.5 to preserve the
 *  port's original on-screen size (docs/064). */
export const DEFAULT_SETTINGS: Settings = {
  lang: "cs",
  musicVolume: 50,
  soundVolume: 90,
  subtitles: true,
  gameSize: 1.5,
};

function clampGameSize(v: unknown): GameSize {
  const n = typeof v === "number" ? v : Number(v);
  return (GAME_SIZES as number[]).includes(n) ? (n as GameSize) : DEFAULT_SETTINGS.gameSize;
}

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
      gameSize: clampGameSize(p.gameSize),
    };
  } catch {
    return { ...DEFAULT_SETTINGS };
  }
}

/** The current canvas zoom factor (Standard/Large/Huge). Single source of truth
 *  for the Phaser game `zoom` and text-resolution helpers (docs/064). */
export function gameRenderScale(): GameSize {
  return loadSettings().gameSize;
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
