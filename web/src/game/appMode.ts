/**
 * Which behavior the app runs in, decided by the URL path so a single build
 * serves both:
 *
 *   /          "standard" game - real progression gating (only solved levels
 *              unlock the next), and Replay only plays the player's own saved
 *              solutions.
 *   /sandbox   every level unlocked (incl. secret branches), and the bundled
 *              reference solutions (legacy/solution/**) are available to replay.
 *
 * In-app navigation keeps the path fixed (navigation.ts pushes history entries
 * without changing the URL), so the mode is stable for the whole session. See
 * docs/045.
 */
export function isSandboxMode(): boolean {
  // Matches /sandbox and /sandbox/ (and a base-prefixed <base>/sandbox).
  const path = window.location.pathname.replace(/\/+$/, "");
  return path.endsWith("/sandbox");
}
