import Phaser from "phaser";

import { fetchText, getSoundSpriteDirs } from "../lua/levelLoader";
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

/** A play() call waiting for its dir to finish decoding before it can start. */
interface PendingPlay {
  group: string | number | undefined;
  cancelled: boolean;
  started: SoundHandle | null;
}

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
  /** Plays deferred until their dir finishes decoding (see play()) - tracked so
   *  stopGroup()/stopAll() can cancel one before it ever starts (a killSound or
   *  a scene teardown landing inside the decode window). */
  private readonly pending = new Set<PendingPlay>();

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
    // Skip dirs with no converted audio (e.g. the en voice-fallback pool most
    // levels lack, docs/060) so we don't fire a 404 per missing dir - docs/075.
    const known = await getSoundSpriteDirs();
    if (!known.has(dir)) return;
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
    if (entry) {
      const clip = entry.spritemap[region];
      if (!clip) return NOOP_HANDLE;
      return this.startSource(entry, clip, volumePercent, opts);
    }

    // Buffer not decoded yet. If a load is in flight, start the sound once it
    // lands rather than dropping it: a caller can request a clip during the
    // brief per-level decode window, and a *cycling* voice (cancan's piano
    // music - registered en-only, so it plays from the `<level>/en` fallback
    // dir the audio gate doesn't wait on) would otherwise register as "talking"
    // yet stay silent forever, since the level's script only re-triggers it
    // when isTalking() is false. A dir that was never queued (truly missing /
    // unconverted) stays a silent no-op. See docs/060.
    const loading = dirLoads.get(dir);
    if (!loading) return NOOP_HANDLE;
    const pending: PendingPlay = { group: opts.group, cancelled: false, started: null };
    this.pending.add(pending);
    void loading.then(() => {
      this.pending.delete(pending);
      if (pending.cancelled) return;
      const loaded = dirBuffers.get(dir);
      const clip = loaded?.spritemap[region];
      if (!loaded || !clip) return; // load failed, or no such region
      pending.started = this.startSource(loaded, clip, volumePercent, opts);
    });
    return {
      stop: () => {
        this.pending.delete(pending);
        pending.cancelled = true;
        pending.started?.stop();
      },
    };
  }

  /** Create + start one source for an already-decoded buffer. */
  private startSource(
    entry: LoadedDir,
    clip: Clip,
    volumePercent: number,
    opts: { loop?: boolean; group?: string | number },
  ): SoundHandle {
    // ctx/destination are non-null here: play() checked before ever reaching this.
    const ctx = this.ctx!;
    // A gesture may not have unlocked the shared context yet - resume opportunistically.
    if (ctx.state === "suspended") void ctx.resume();

    const src = ctx.createBufferSource();
    src.buffer = entry.buffer;
    const gain = ctx.createGain();
    gain.gain.value = globalSoundVolume() * (volumePercent / 100);
    src.connect(gain);
    gain.connect(this.destination!);

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

  /** Stop every live source in `group` - DialogStack::killSound(actor). Also
   *  cancels any of that group's plays still deferred behind a decode (so a
   *  killed voice never starts once its buffer lands). */
  stopGroup(group: string | number): void {
    for (const p of [...this.pending]) {
      if (p.group === group) {
        p.cancelled = true;
        this.pending.delete(p);
      }
    }
    const set = this.groups.get(group);
    if (!set) return;
    for (const src of [...set]) this.stopSource(src);
    set.clear();
  }

  /** Stop everything currently playing (scene teardown/restart), grouped or not.
   *  Loaded buffers stay cached - only playback is stopped. */
  stopAll(): void {
    for (const p of this.pending) p.cancelled = true;
    this.pending.clear();
    for (const src of [...this.allSources]) this.stopSource(src);
    this.allSources.clear();
    for (const set of this.groups.values()) set.clear();
  }
}
