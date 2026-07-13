import Phaser from "phaser";

import { fetchText } from "../lua/levelLoader";
import { loadSettings } from "../storage/settingsStorage";

/** legacy/src/gengine/SoundAgent.cpp applies the global volume as a flat
 *  multiplier on each play (Mix_Volume(channel, m_soundVolume * volume / 100)).
 *  The global sound volume is the player's Options setting (0-100 -> 0-1,
 *  docs/038), read live at each play so a change takes effect on the next
 *  sound. Master mute/volume is additionally applied by Phaser's own
 *  masterVolumeNode/masterMuteNode, which our output routes through. */
function globalSoundVolume(): number {
  return loadSettings().soundVolume / 100;
}

/** A region within a dir's concatenated sprite (seconds), from sprite.json. */
interface Clip {
  start: number;
  end: number;
}

interface LoadedDir {
  buffer: AudioBuffer;
  spritemap: Record<string, Clip>;
}

/** A playing voice/effect - stop() cuts it early (DialogStack::killSound). */
export interface SoundHandle {
  stop(): void;
}

const NOOP_HANDLE: SoundHandle = { stop() {} };

/** Decoded buffers are cached **module-global**, not per-engine: Phaser's
 *  WebAudioSoundManager (and thus its AudioContext) is game-global, so an
 *  AudioBuffer decoded for one level stays valid for every later scene. This is
 *  what makes the ~6 MB shared SFX pool (and any revisited level) decode only
 *  **once per session** rather than on every level entry (each LevelScene builds
 *  a fresh AudioEngine). Keyed by dir; deduped by `dirLoads`. */
const dirBuffers = new Map<string, LoadedDir>();
const dirLoads = new Map<string, Promise<void>>();

/**
 * A small Web Audio playback layer that mirrors the original's in-memory
 * Mix_Chunk model (docs/043): each sound dir's concatenated sprite.mp3 is
 * fetched + `decodeAudioData`'d **once** into a single AudioBuffer held in
 * memory, and every play spawns an independent AudioBufferSourceNode that starts
 * at the clip's offset. That gives two things the old Phaser-audioSprite path
 * couldn't: **instant** playback (the decode is done up front, not on first
 * play) and **unlimited concurrency** (each source is independent, so viking1's
 * musician-band notes and overlapping impacts all sound together).
 *
 * Shares Phaser's AudioContext and routes output through its `destination` node
 * (which chains master volume + mute), so the Options sliders still apply. Music
 * stays on Phaser (a single looping stream - see AudioManager). Tolerant of
 * missing/un-converted dirs and of a non-WebAudio (HTML5-audio fallback) sound
 * manager - in either case play() is a silent no-op, never throws.
 */
export class AudioEngine {
  private readonly ctx: AudioContext | null;
  private readonly destination: AudioNode | null;
  /** group key (dialog actor index) -> live sources, so stopGroup(actor) can cut
   *  just that actor's voices (killSound). A subset of allSources. */
  private readonly groups = new Map<string | number, Set<AudioBufferSourceNode>>();
  /** Every live source (grouped or not), so stopAll() reaches ungrouped effects. */
  private readonly allSources = new Set<AudioBufferSourceNode>();

  constructor(scene: Phaser.Scene) {
    const mgr = scene.sound;
    if (mgr instanceof Phaser.Sound.WebAudioSoundManager) {
      this.ctx = mgr.context;
      this.destination = mgr.destination;
    } else {
      // HTML5-audio fallback (rare) - no buffer playback; degrade to silence.
      this.ctx = null;
      this.destination = null;
    }
  }

  get available(): boolean {
    return this.ctx !== null;
  }

  /** Fetch + decode one dir's sprite once, caching the AudioBuffer + spritemap.
   *  Deduped by dir. Resolves (never rejects) even when the dir isn't converted
   *  - callers just get silence for it, matching the original's missing-file
   *  fallback. */
  loadDir(dir: string): Promise<void> {
    const existing = dirLoads.get(dir);
    if (existing) return existing;
    const p = this.doLoadDir(dir);
    dirLoads.set(dir, p);
    return p;
  }

  private async doLoadDir(dir: string): Promise<void> {
    if (!this.ctx || dirBuffers.has(dir)) return;
    try {
      // sprite.json first: the dev server SPA-fallback-serves index.html (200)
      // for an unconverted dir, so JSON.parse failing is the real "missing"
      // signal (same tolerance as fetchSoundDurations, docs/018). Bail before
      // wastefully decoding HTML as audio.
      const jsonText = await fetchText(`/assets/sound/${dir}/sprite.json`);
      const { spritemap } = JSON.parse(jsonText) as { spritemap: Record<string, Clip> };

      const res = await fetch(`/assets/sound/${dir}/sprite.mp3`);
      if (!res.ok) return;
      const arrayBuffer = await res.arrayBuffer();
      const buffer = await this.ctx.decodeAudioData(arrayBuffer);
      dirBuffers.set(dir, { buffer, spritemap });
    } catch {
      // Un-converted dir or decode failure - silent, no buffer.
    }
  }

  /** Resolves once `dir` has finished loading+decoding (or immediately if it
   *  already has / was never queued) - lets a caller gate on audio being ready. */
  whenLoaded(dir: string): Promise<void> {
    return dirLoads.get(dir) ?? Promise.resolve();
  }

  /**
   * Play `region` of `dir` immediately as its own source. `volumePercent` is the
   * call's own volume (model_talk/sound_playSound arg); it's multiplied by the
   * live global sound volume and routed through Phaser's master node. `group`
   * (a dialog actor index) lets stopGroup() cut it later; `loop` makes a cycling
   * dialog (loops == -1) repeat its clip. Returns a handle to stop it early, or
   * a no-op handle when the dir/region/context isn't available.
   */
  play(
    dir: string,
    region: string,
    volumePercent: number,
    opts: { loop?: boolean; group?: string | number } = {},
  ): SoundHandle {
    if (!this.ctx || !this.destination) return NOOP_HANDLE;
    const entry = dirBuffers.get(dir);
    const clip = entry?.spritemap[region];
    if (!entry || !clip) return NOOP_HANDLE;

    // A gesture may not have unlocked the shared context yet - resume opportunistically.
    if (this.ctx.state === "suspended") void this.ctx.resume();

    const src = this.ctx.createBufferSource();
    src.buffer = entry.buffer;
    const gain = this.ctx.createGain();
    gain.gain.value = globalSoundVolume() * (volumePercent / 100);
    src.connect(gain);
    gain.connect(this.destination);

    const duration = clip.end - clip.start;
    if (opts.loop) {
      src.loop = true;
      src.loopStart = clip.start;
      src.loopEnd = clip.end;
      src.start(0, clip.start);
    } else {
      src.start(0, clip.start, duration);
    }

    this.allSources.add(src);
    const group = opts.group;
    if (group !== undefined) {
      let set = this.groups.get(group);
      if (!set) this.groups.set(group, (set = new Set()));
      set.add(src);
    }
    const cleanup = () => {
      this.allSources.delete(src);
      if (group !== undefined) this.groups.get(group)?.delete(src);
    };
    src.onended = cleanup;

    return {
      stop: () => {
        this.stopSource(src);
        cleanup();
      },
    };
  }

  private stopSource(src: AudioBufferSourceNode): void {
    try {
      src.onended = null;
      src.stop();
    } catch {
      // Already stopped/ended.
    }
    this.allSources.delete(src);
  }

  /** Stop every live source in `group` - DialogStack::killSound(actor). */
  stopGroup(group: string | number): void {
    const set = this.groups.get(group);
    if (!set) return;
    for (const src of [...set]) this.stopSource(src);
    set.clear();
  }

  /** Stop everything currently playing (scene teardown/restart), grouped or not.
   *  Loaded buffers stay cached - only playback is stopped. */
  stopAll(): void {
    for (const src of [...this.allSources]) this.stopSource(src);
    this.allSources.clear();
    for (const set of this.groups.values()) set.clear();
  }
}
