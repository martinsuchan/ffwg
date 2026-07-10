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
