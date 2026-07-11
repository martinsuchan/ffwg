import { fetchText } from "./levelLoader";

/** A resolved playable sound: which built sprite file, and which region
 *  inside it. Works uniformly for built-in sounds (impact/death), Lua-driven
 *  one-shots (sound_playSound), and dialog/NPC voice (model_talk) - see
 *  docs/018. Shared by the live level engine (levelScript.ts) and the demo
 *  movie engine (demoScript.ts). */
export interface ResolvedSound {
  spriteDir: string;
  region: string;
}

/** "sound/<dir>/<name>.ogg" -> {spriteDir: dir, region: name} - the same
 *  transform for every sound category, since sound_addSound()'s registered
 *  file paths and dataPathSound()'s derived dialog paths are both already in
 *  this exact form (see legacy/script/share/level_creation.lua and
 *  level_dialog.lua). Returns null for anything not shaped like that (empty
 *  string - no sound available). */
export function resolveSoundPath(soundPath: string): ResolvedSound | null {
  if (!soundPath) return null;
  const withoutPrefix = soundPath.replace(/^sound\//, "");
  const lastSlash = withoutPrefix.lastIndexOf("/");
  if (lastSlash === -1) return null;
  return {
    spriteDir: withoutPrefix.slice(0, lastSlash),
    region: withoutPrefix.slice(lastSlash + 1).replace(/\.ogg$/, ""),
  };
}

/** Fetches each sprite's spritemap (JSON only, not the audio itself) and
 *  flattens every region's clip length into one "sound/<dir>/<name>.ogg" ->
 *  seconds map - tolerant of 404s (spriteDir not in our converted set), since
 *  callers already have a text-length fallback for missing durations
 *  (docs/018). */
export async function fetchSoundDurations(spriteDirs: string[]): Promise<Map<string, number>> {
  const durations = new Map<string, number>();
  await Promise.all(
    spriteDirs.map(async (spriteDir) => {
      try {
        const json = await fetchText(`/assets/sound/${spriteDir}/sprite.json`);
        // Vite's dev server SPA-fallback-serves index.html (200 OK, not a real
        // 404) for any unconverted spriteDir's missing sprite.json, so a
        // failed fetch doesn't necessarily throw here - JSON.parse on that HTML
        // is what actually fails, and needs the same tolerant treatment as a
        // real fetch error (docs/018's "tolerant of 404s").
        const { spritemap } = JSON.parse(json) as {
          spritemap: Record<string, { start: number; end: number }>;
        };
        for (const [region, { start, end }] of Object.entries(spritemap)) {
          durations.set(`sound/${spriteDir}/${region}.ogg`, end - start);
        }
      } catch {
        // Not converted yet - callers fall back to the text-length formula.
      }
    }),
  );
  return durations;
}
