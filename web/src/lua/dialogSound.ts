import { fetchText, getSoundSpriteDirs } from "./levelLoader";

/**
 * legacy DialogStack::actorTalk() splits a talk() name on '@' into the real
 * dialog name plus format arguments: `"b2-g@5"` = dialog `"b2-g"` with `%1`=5.
 * The dialog AND its voice clip are both looked up by args[0]; only the
 * subtitle TEXT is formatted. gods' two gods announce their battleship
 * coordinates this way ("G5."), and the ending level reports its play time
 * (`"z-c-hodin@"..room.cas` -> "it took you %1 hours!"). See docs/052.
 */
export function splitDialogName(name: string): { name: string; args: string[] } {
  const parts = name.split("@");
  return { name: parts[0], args: parts.slice(1) };
}

/**
 * legacy Dialog::getFormatedSubtitle(): replace %1, %2, ... with the args
 * (every occurrence, matching StringTool::replace's loop). %0 is never
 * expanded. Returns the subtitle unchanged when there are no args.
 */
export function formatSubtitle(subtitle: string, args: string[]): string {
  let out = subtitle;
  for (let i = 0; i < args.length; i++) {
    out = out.split(`%${i + 1}`).join(args[i]);
  }
  return out;
}

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
  // Skip dirs with no converted audio (e.g. the en fallback pools most levels
  // lack, docs/060) so we don't fire a 404 per missing dir - docs/075.
  const known = await getSoundSpriteDirs();
  await Promise.all(
    spriteDirs
      .filter((spriteDir) => known.has(spriteDir))
      .map(async (spriteDir) => {
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
