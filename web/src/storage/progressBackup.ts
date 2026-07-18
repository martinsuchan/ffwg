/**
 * Backup / restore of all game progress to a single human-readable JSON file
 * (docs/072). Progress otherwise lives only in localStorage, so clearing browser
 * data loses it; this lets the player export everything to a file and import it
 * back (or onto another device).
 *
 * The restore path is deliberately hardened - the file is user-editable and
 * untrusted: size-capped, JSON-parse-guarded, schema/version-gated, every field
 * type-coerced, and crucially every solved solution is *re-validated by actually
 * replaying it to a solved state* against freshly loaded level data before it's
 * trusted. Restore is merge-only (keep-best), never destructive.
 *
 * No Phaser dependency - pure storage + the headless level/solution machinery,
 * so it's exercised directly by the e2e suite (window.__progress).
 */

import { GameEngine } from "../game/GameEngine";
import { validateSolution } from "../game/SolutionValidator";
import { loadLevelModels, type LevelData } from "../lua/levelLoader";
import {
  MAX_SAVES,
  allSaves,
  allSolved,
  mergeSavedGames,
  saveSolvedMoves,
  type SavedGame,
} from "./levelStorage";
import { getPlaytimeSeconds, mergePlaytimeSeconds } from "./playtime";
import { loadSettings, sanitizeSettings, saveSettings, type Settings } from "./settingsStorage";

/** Magic string identifying our backup files. */
const FORMAT = "ffwg-progress";
/** Bump when the on-disk shape changes incompatibly; parseBackup accepts <= this. */
const VERSION = 1;

/** Files larger than this are rejected before parsing - a real backup of the
 *  whole game is well under 1 MB, so this only blocks garbage/huge input. */
export const MAX_BACKUP_BYTES = 5 * 1024 * 1024;

/** The on-disk backup shape (also the normalized in-memory value parseBackup
 *  returns - settings is always a full, sanitized Settings). */
export interface BackupData {
  format: typeof FORMAT;
  version: number;
  exportedAt: string;
  solved: Record<string, string>;
  saves: Record<string, SavedGame[]>;
  settings: Settings;
  playtimeSeconds: number;
}

export type ParseResult =
  | { ok: true; value: BackupData }
  | { ok: false; error: string };

export interface RestoreReport {
  solvedAccepted: string[];
  solvedRejected: Array<{ level: string; reason: string }>;
  savesAccepted: number;
  savesRejected: number;
  settingsApplied: boolean;
  playtimeSeconds: number;
}

// --------------------------------------------------------------------------
// Backup (export)
// --------------------------------------------------------------------------

/** Collects every persisted `ffwg:*` area into one JSON string (pretty-printed
 *  so the file is human-readable). */
export function serializeProgress(): string {
  const data: BackupData = {
    format: FORMAT,
    version: VERSION,
    exportedAt: new Date().toISOString(),
    solved: allSolved(),
    saves: allSaves(),
    settings: loadSettings(),
    playtimeSeconds: getPlaytimeSeconds(),
  };
  return JSON.stringify(data, null, 2);
}

/** A suggested download filename, dated (e.g. ffwg-progress-2026-07-17.json). */
export function backupFilename(): string {
  return `ffwg-progress-${new Date().toISOString().slice(0, 10)}.json`;
}

// --------------------------------------------------------------------------
// Parse (pure, sync, no level loading)
// --------------------------------------------------------------------------

function isPlainObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

/** A move string must be a non-empty string of legal move symbols. The full
 *  alphabet is fish_small/big u/d/l/r + U/D/L/R plus windoze's extra-fish
 *  w/x/y/z + W/X/Y/Z (docs/035). Anything else can't be a real recorded solution
 *  and is dropped early (loadMove would throw on it anyway). */
const MOVE_RE = /^[udlrUDLRwxyzWXYZ]+$/;

function coerceSolved(raw: unknown): Record<string, string> {
  const out: Record<string, string> = {};
  if (!isPlainObject(raw)) return out;
  for (const [level, moves] of Object.entries(raw)) {
    if (typeof level === "string" && level && typeof moves === "string" && MOVE_RE.test(moves)) {
      out[level] = moves;
    }
  }
  return out;
}

function coerceSave(raw: unknown): SavedGame | null {
  if (!isPlainObject(raw)) return null;
  const { id, moves, modelState, tutorial } = raw;
  if (typeof id !== "string" || !id) return null;
  if (typeof moves !== "string" || !MOVE_RE.test(moves)) {
    // An empty move string is a legal "just started" save; allow it.
    if (moves !== "") return null;
  }
  if (typeof modelState !== "string") return null;
  const save: SavedGame = { id, moves: moves as string, modelState };
  if (tutorial === true) save.tutorial = true;
  return save;
}

function coerceSaves(raw: unknown): Record<string, SavedGame[]> {
  const out: Record<string, SavedGame[]> = {};
  if (!isPlainObject(raw)) return out;
  for (const [level, arr] of Object.entries(raw)) {
    if (typeof level !== "string" || !level || !Array.isArray(arr)) continue;
    const saves = arr.map(coerceSave).filter((s): s is SavedGame => s !== null).slice(0, MAX_SAVES);
    if (saves.length > 0) out[level] = saves;
  }
  return out;
}

/** Validate + normalize an untrusted backup string. Pure and synchronous - does
 *  NOT load levels or touch storage; solution *validity* is checked later in
 *  restoreProgress. Returns a normalized BackupData on success. */
export function parseBackup(text: string): ParseResult {
  // `error` is an i18n key (see web/src/i18n.ts / docs/073) so the UI can localize
  // it - parseBackup stays UI-agnostic.
  if (text.length > MAX_BACKUP_BYTES) {
    return { ok: false, error: "backup_err_big" };
  }
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch {
    return { ok: false, error: "backup_err_json" };
  }
  if (!isPlainObject(raw) || raw.format !== FORMAT) {
    return { ok: false, error: "backup_err_format" };
  }
  const version = typeof raw.version === "number" ? raw.version : 0;
  if (!(version >= 1 && version <= VERSION)) {
    return { ok: false, error: "backup_err_version" };
  }
  const playtime = typeof raw.playtimeSeconds === "number" && raw.playtimeSeconds >= 0
    ? Math.floor(raw.playtimeSeconds)
    : 0;
  return {
    ok: true,
    value: {
      format: FORMAT,
      version,
      exportedAt: typeof raw.exportedAt === "string" ? raw.exportedAt : "",
      solved: coerceSolved(raw.solved),
      saves: coerceSaves(raw.saves),
      settings: sanitizeSettings(raw.settings),
      playtimeSeconds: playtime,
    },
  };
}

// --------------------------------------------------------------------------
// Restore (async, hardened, merge/keep-best)
// --------------------------------------------------------------------------

export interface RestoreOptions {
  /** Progress callback (0..1) for a UI spinner over the level-validation loop. */
  onProgress?: (done: number, total: number) => void;
}

/** Apply a parsed backup to storage. Every solved solution is re-validated by
 *  replaying it to a solved state against freshly loaded level data before it's
 *  accepted; saves must replay without an invalid move. Unknown/failing levels
 *  are rejected per-entry, never aborting the whole restore. Writes are
 *  merge-only (saveSolvedMoves keeps the shorter, mergeSavedGames unions,
 *  mergePlaytimeSeconds keeps the larger). Caller should reload the page after,
 *  so derived state (world-map node states, playtime boot total) re-reads. */
export async function restoreProgress(
  value: BackupData,
  options: RestoreOptions = {},
): Promise<RestoreReport> {
  const report: RestoreReport = {
    solvedAccepted: [],
    solvedRejected: [],
    savesAccepted: 0,
    savesRejected: 0,
    settingsApplied: false,
    playtimeSeconds: value.playtimeSeconds,
  };

  const levels = new Set<string>([...Object.keys(value.solved), ...Object.keys(value.saves)]);
  const total = levels.size;
  let done = 0;

  // Load each referenced level once (cache), so a level with both a solved
  // solution and saves is only parsed a single time.
  const levelDataCache = new Map<string, LevelData | null>();
  const loadLevel = async (level: string): Promise<LevelData | null> => {
    if (levelDataCache.has(level)) return levelDataCache.get(level) ?? null;
    let data: LevelData | null = null;
    try {
      data = await loadLevelModels(level);
    } catch {
      data = null; // unknown level or content this port can't build
    }
    levelDataCache.set(level, data);
    return data;
  };

  for (const level of levels) {
    const levelData = await loadLevel(level);

    // --- solved solution for this level ---
    const solvedMoves = value.solved[level];
    if (solvedMoves !== undefined) {
      if (!levelData) {
        report.solvedRejected.push({ level, reason: "unknown or unsupported level" });
      } else {
        try {
          const result = validateSolution(new GameEngine(levelData), solvedMoves);
          if (result.solved) {
            saveSolvedMoves(level, solvedMoves); // keep-if-shorter merge
            report.solvedAccepted.push(level);
          } else {
            report.solvedRejected.push({
              level,
              reason: result.failedAt
                ? `invalid move at step ${result.failedAt.index}`
                : "does not solve the level",
            });
          }
        } catch (error) {
          report.solvedRejected.push({
            level,
            reason: error instanceof Error ? error.message : "validation failed",
          });
        }
      }
    }

    // --- mid-level saves for this level ---
    const saves = value.saves[level];
    if (saves && saves.length > 0) {
      if (!levelData) {
        report.savesRejected += saves.length;
      } else {
        const valid: SavedGame[] = [];
        for (const save of saves) {
          try {
            const result = validateSolution(new GameEngine(levelData), save.moves);
            // A save is a legal partial position - it just needs to replay with
            // no invalid move (it need not solve). An empty move string is fine.
            if (!result.failedAt) valid.push(save);
            else report.savesRejected++;
          } catch {
            report.savesRejected++;
          }
        }
        report.savesAccepted += mergeSavedGames(level, valid);
      }
    }

    options.onProgress?.(++done, total);
  }

  // Settings + play time (no level dependency).
  saveSettings(value.settings);
  report.settingsApplied = true;
  mergePlaytimeSeconds(value.playtimeSeconds);

  return report;
}
