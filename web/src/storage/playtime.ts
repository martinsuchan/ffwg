/**
 * Cumulative play time, the web equivalent of legacy GameAgent::own_shutdown()'s
 * persistent `playtime` option: it accumulates `SDL_GetTicks()/1000` (whole
 * seconds the game has run) across every session. The `ending` level reads it
 * (`optionsGetAsInt("playtime")/3600`) to tell the player how many hours the
 * whole game took ("...it took you %1 hours!"). See docs/061.
 *
 * Model: a persisted total (seconds) plus this session's elapsed time. The total
 * is always `bootTotal + floor(sessionMs/1000)`, so flushing it to storage is
 * idempotent (no double-counting) and it carries forward across reloads.
 */
const STORAGE_KEY = "ffwg:playtime";

/** performance.now() at module load (app boot) - this session's zero. */
const bootMs = performance.now();

/** The persisted total (seconds) as it was at boot; never mutated in memory, so
 *  getPlaytimeSeconds() always adds the FULL session elapsed to it. */
const bootTotalSeconds = readPersisted();

function readPersisted(): number {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    const n = raw === null ? 0 : parseInt(raw, 10);
    return Number.isFinite(n) && n >= 0 ? n : 0;
  } catch {
    return 0;
  }
}

/** Total seconds played across all sessions, including this one so far -
 *  what legacy's `playtime` option holds. */
export function getPlaytimeSeconds(): number {
  return bootTotalSeconds + Math.floor((performance.now() - bootMs) / 1000);
}

/** Persist the current total. Idempotent - writes the same running total each
 *  time, so repeated flushes never double-count. */
function flush(): void {
  try {
    localStorage.setItem(STORAGE_KEY, String(getPlaytimeSeconds()));
  } catch {
    // Storage unavailable (private mode / quota) - play time just won't persist.
  }
}

/** Merge a backed-up play-time total into storage (docs/072): keep the larger of
 *  the current running total and `seconds`, so a restore never shrinks play time
 *  and re-restoring is idempotent. A page reload after restore re-reads the new
 *  total into `bootTotalSeconds`. */
export function mergePlaytimeSeconds(seconds: number): void {
  const incoming = Number.isFinite(seconds) && seconds > 0 ? Math.floor(seconds) : 0;
  const next = Math.max(getPlaytimeSeconds(), incoming);
  try {
    localStorage.setItem(STORAGE_KEY, String(next));
  } catch {
    // Storage unavailable - nothing to persist.
  }
}

/** Start accumulating play time. Called once at app boot (main.ts). Flushes on a
 *  slow interval and whenever the page is hidden/unloaded - the browser's
 *  closest analogue to SDL's shutdown hook, and the only reliable "leaving" edge
 *  (a plain unload isn't guaranteed to fire on mobile). */
export function initPlaytimeTracking(): void {
  window.setInterval(flush, 30_000);
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "hidden") flush();
  });
  window.addEventListener("pagehide", flush);
}
