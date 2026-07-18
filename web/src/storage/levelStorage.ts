/**
 * localStorage-backed persistence for solution/save data, port-specific
 * stand-in for the original's `saves/<codename>.lua` / `solved/<codename>.
 * lua` files (see docs/026). All reads/writes are try/catch-guarded -
 * private browsing, quota limits, or storage being disabled entirely
 * should degrade to "persistence didn't happen," never crash the game.
 */

const SAVES_PREFIX = "ffwg:saves:";
const SOLVED_PREFIX = "ffwg:solved:";

/** Dot-row cap (docs/026) - a first guess at a reasonable fixed-size row,
 *  not measured against any real level's screen width. Easy to retune. */
export const MAX_SAVES = 6;

/** One mid-level save slot - legacy's saves/<codename>.lua, minus the
 *  single-slot restriction (docs/026's multi-slot upgrade). `modelState`
 *  is the level's Lua-side custom per-model state (legacy's saved_models,
 *  pickle(getModelsTable()) - see LevelScript.captureModelState()) -
 *  everything a level's own code.lua stashed onto a model beyond physics
 *  position, which `moves` alone can't reconstruct. */
export interface SavedGame {
  id: string;
  moves: string;
  modelState: string;
  /** Created by the briefcase Phase-2 auto-play tutorial (`level_action_save`),
   *  not the player - shown as a distinctly-coloured dot and loadable after
   *  the tutorial like any other save. See docs/031. */
  tutorial?: boolean;
}

function savesKey(levelName: string): string {
  return `${SAVES_PREFIX}${levelName}`;
}

function solvedKey(levelName: string): string {
  return `${SOLVED_PREFIX}${levelName}`;
}

function safeGet(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function safeSet(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Storage unavailable - persistence is a nice-to-have here, never a
    // hard requirement to keep playing.
  }
}

export function loadSavedGames(levelName: string): SavedGame[] {
  const raw = safeGet(savesKey(levelName));
  if (!raw) return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed) ? (parsed as SavedGame[]) : [];
  } catch {
    return [];
  }
}

function writeSavedGames(levelName: string, saves: SavedGame[]): void {
  safeSet(savesKey(levelName), JSON.stringify(saves));
}

/** Appends a new save slot, unless the level is already at MAX_SAVES.
 *  @return the new save, or null if there's no room for it. */
export function addSavedGame(
  levelName: string,
  moves: string,
  modelState: string,
): SavedGame | null {
  const saves = loadSavedGames(levelName);
  if (saves.length >= MAX_SAVES) return null;
  const save: SavedGame = { id: crypto.randomUUID(), moves, modelState };
  writeSavedGames(levelName, [...saves, save]);
  return save;
}

/** Upserts the single tutorial save slot for a level - the briefcase auto-play
 *  tutorial (docs/031) saves/loads repeatedly during its walkthrough, all onto
 *  one dedicated slot (overwriting), so it never spawns many slots or clobbers
 *  the player's saves. Unlike addSavedGame it bypasses the MAX_SAVES cap when
 *  creating the one tutorial slot (the tutorial must be able to save), and it
 *  persists so the player can load it once the tutorial is over.
 *  @return the tutorial save. */
export function saveTutorialGame(
  levelName: string,
  moves: string,
  modelState: string,
): SavedGame {
  const saves = loadSavedGames(levelName);
  const existing = saves.find((s) => s.tutorial);
  if (existing) {
    existing.moves = moves;
    existing.modelState = modelState;
    writeSavedGames(levelName, saves);
    return existing;
  }
  const save: SavedGame = { id: crypto.randomUUID(), moves, modelState, tutorial: true };
  writeSavedGames(levelName, [...saves, save]);
  return save;
}

/** The level's tutorial save slot, or null - see saveTutorialGame(). */
export function loadTutorialGame(levelName: string): SavedGame | null {
  return loadSavedGames(levelName).find((s) => s.tutorial) ?? null;
}

/** Every level's save slots, keyed by codename - used by the progress backup
 *  (docs/072) to collect all saves without knowing the level list up front.
 *  Scans the SAVES_PREFIX keyspace; malformed entries are skipped by
 *  loadSavedGames' own guard. */
export function allSaves(): Record<string, SavedGame[]> {
  const out: Record<string, SavedGame[]> = {};
  let keys: string[] = [];
  try {
    keys = Object.keys(localStorage);
  } catch {
    return out;
  }
  for (const key of keys) {
    if (!key.startsWith(SAVES_PREFIX)) continue;
    const level = key.slice(SAVES_PREFIX.length);
    const saves = loadSavedGames(level);
    if (saves.length > 0) out[level] = saves;
  }
  return out;
}

/** Every level's best solved move string, keyed by codename - the progress
 *  backup's progression data (docs/072). */
export function allSolved(): Record<string, string> {
  const out: Record<string, string> = {};
  let keys: string[] = [];
  try {
    keys = Object.keys(localStorage);
  } catch {
    return out;
  }
  for (const key of keys) {
    if (!key.startsWith(SOLVED_PREFIX)) continue;
    const level = key.slice(SOLVED_PREFIX.length);
    const moves = safeGet(key);
    if (moves) out[level] = moves;
  }
  return out;
}

/** Merges already-validated `incoming` save slots into a level's stored saves
 *  for a progress restore (docs/072): union by `id` (existing slots win, so a
 *  re-restore is idempotent), keeping at most one tutorial slot, capped at
 *  MAX_SAVES. Never deletes existing saves.
 *  @return how many incoming saves were actually added. */
export function mergeSavedGames(levelName: string, incoming: SavedGame[]): number {
  const existing = loadSavedGames(levelName);
  const byId = new Set(existing.map((s) => s.id));
  const hasTutorial = existing.some((s) => s.tutorial);
  const merged = [...existing];
  let added = 0;
  for (const save of incoming) {
    if (merged.length >= MAX_SAVES) break;
    if (byId.has(save.id)) continue;
    if (save.tutorial && hasTutorial) continue; // keep only one tutorial slot
    merged.push(save);
    byId.add(save.id);
    added++;
  }
  if (added > 0) writeSavedGames(levelName, merged);
  return added;
}

export function deleteSavedGame(levelName: string, id: string): void {
  writeSavedGames(
    levelName,
    loadSavedGames(levelName).filter((save) => save.id !== id),
  );
}

/** Player's best (shortest) solved move-string for a level, or null if
 *  never solved on this device - legacy's solved/<codename>.lua. */
export function loadSolvedMoves(levelName: string): string | null {
  return safeGet(solvedKey(levelName));
}

/** Records `moves` as the level's best solution, but only if strictly
 *  shorter than any already stored - legacy's LevelStatus::
 *  writeSolvedMoves() "keep only if shorter" rule (LevelCountDown::
 *  saveSolution()).
 *  @return whether it actually replaced the stored solution. */
export function saveSolvedMoves(levelName: string, moves: string): boolean {
  const existing = loadSolvedMoves(levelName);
  if (existing !== null && existing.length <= moves.length) return false;
  safeSet(solvedKey(levelName), moves);
  return true;
}
