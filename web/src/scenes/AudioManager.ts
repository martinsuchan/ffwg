import Phaser from "phaser";

import type { ResolvedSound } from "../lua/levelScript";
import { loadSettings } from "../storage/settingsStorage";
import { AudioEngine } from "./audioEngine";

/** legacy/src/gengine/SoundAgent.cpp applies the global volume as a flat
 *  multiplier on top of each call's own volume (Mix_Volume(channel,
 *  m_soundVolume * volume / 100)). The global volumes are the player's
 *  Options settings now (0-100 -> 0-1, docs/038), read live on each play so a
 *  change takes effect on the next sound; the currently-playing music is also
 *  updated in place via setMusicVolume(). Effect/voice volume is applied by the
 *  Web Audio engine (audioEngine.ts); music volume is applied here. */
function globalMusicVolume(): number {
  return loadSettings().musicVolume / 100;
}

type MusicCommand = { type: "play"; track: string } | { type: "stop" };

/**
 * Owns audio playback for one LevelScene. Voices + one-shot effects go through
 * the Web Audio buffer engine (audioEngine.ts) - pre-decoded, instant, and
 * polyphonic (docs/043); music stays on Phaser (a single looping stream, decoded
 * once - single-channel is correct for it). Lazy-loads on demand and is tolerant
 * of missing/un-converted content (silently no sound, matching the original's
 * own missing-file fallback - never throws or blocks gameplay). See docs/018.
 */
export class AudioManager {
  private readonly engine: AudioEngine;
  /** key -> promise for a *music* track load (Phaser). Voice/effect dir loads
   *  are tracked by the engine itself. */
  private readonly musicLoads = new Map<string, Promise<void>>();
  private currentMusicTrack: string | null = null;
  private currentMusic: Phaser.Sound.BaseSound | null = null;
  /** Bumped by reset() so an in-flight music load from a superseded session
   *  (pre-restart) can't resurrect stale audio after resolving late - same
   *  guard shape as LevelScene's own scriptGeneration. */
  private generation = 0;

  constructor(private readonly scene: Phaser.Scene) {
    this.engine = new AudioEngine(scene);
  }

  /** Restart: stop everything playing and invalidate in-flight commands from
   *  the previous session. Already-loaded buffers/tracks stay cached - a level
   *  restart doesn't invalidate audio assets, only "what's playing now". */
  reset(): void {
    this.generation += 1;
    this.stopAll();
  }

  /** Pre-decode the sprite dirs this level needs, so playback is instant instead
   *  of decoding on first use (docs/043). Returns a promise so a caller can gate
   *  on a specific dir being ready (see whenLoaded); tolerant of un-converted
   *  dirs. */
  preloadAll(spriteDirs: string[]): Promise<void> {
    return Promise.all(spriteDirs.map((dir) => this.engine.loadDir(dir))).then(() => {});
  }

  /** Resolves once `dir`'s sprite is decoded (or immediately if it already is /
   *  was never queued) - lets LevelScene hold its dialog logic until the level's
   *  own voice is ready, so the first line is in sync (docs/031/043). */
  whenLoaded(dir: string): Promise<void> {
    return this.engine.whenLoaded(dir);
  }

  stopMusic(): void {
    this.currentMusic?.stop();
    this.currentMusic?.destroy();
    this.currentMusic = null;
    this.currentMusicTrack = null;
  }

  /** Stops the dialog/NPC voices of one actor (Dialogs::killSound(actor)), or
   *  every voice+effect when no actor is given. A death cuts just the dying
   *  fish's own voice (docs/018); teardown cuts all. */
  stopDialogVoice(actor?: number): void {
    if (actor === undefined) this.engine.stopAll();
    else this.engine.stopGroup(actor);
  }

  /** Stops music, all voices/effects still playing - used when leaving a level
   *  (Esc -> world map) or restarting, so no level audio bleeds into the next
   *  scene (docs/031). The Phaser Sound Manager is game-global (docs/025), so
   *  scene.sound.stopAll() reaches any Phaser sound; engine.stopAll() reaches the
   *  Web Audio voices/effects. */
  stopAll(): void {
    this.stopMusic();
    this.engine.stopAll();
    this.scene.sound.stopAll();
  }

  /** Dialog/NPC voice (model_talk) - played on the Web Audio engine, grouped by
   *  `actor` so a later killSound(actor)/death can cut just this actor's voices.
   *  Multiple actors' voices play concurrently (viking1's band), unlike the old
   *  single-slot cutting. `loop` is a cycling dialog (loops == -1). See docs/043. */
  playDialogVoice(
    sound: ResolvedSound,
    volumePercent: number,
    actor: number,
    loop = false,
  ): void {
    this.engine.play(sound.spriteDir, sound.region, volumePercent, { group: actor, loop });
  }

  /** One sound_playSound()/built-in (impact/death) effect - fire and forget,
   *  overlapping (multiple impacts ring together). */
  playSoundEffect(sound: ResolvedSound, volumePercent: number): void {
    this.engine.play(sound.spriteDir, sound.region, volumePercent);
  }

  /** This round's sound_playMusic()/sound_stopMusic() command, if any -
   *  null means "no new command, leave whatever's playing alone". Only one
   *  track plays at a time, always stop-before-start (SDLMusicLooper). */
  async applyMusicCommand(command: MusicCommand | null): Promise<void> {
    if (!command) return;
    if (command.type === "stop") {
      this.stopMusic();
      return;
    }
    if (this.currentMusicTrack === command.track) return;
    this.stopMusic();

    const generation = this.generation;
    const key = `music:${command.track}`;
    await this.loadMusic(key, command.track);
    if (generation !== this.generation) return;
    try {
      const music = this.scene.sound.add(key, { loop: true, volume: globalMusicVolume() });
      music.play();
      this.currentMusic = music;
      this.currentMusicTrack = command.track;
    } catch {
      // Track failed to load (un-converted content) - silent, no music.
    }
  }

  /** Apply the current musicVolume setting to the track playing right now, so
   *  an Options change is heard immediately (not only on the next track) -
   *  docs/038. New sounds already read the setting live via globalMusicVolume(). */
  refreshMusicVolume(): void {
    (this.currentMusic as Phaser.Sound.BaseSound & { setVolume?: (v: number) => void })?.setVolume?.(
      globalMusicVolume(),
    );
  }

  /** Loads one music track through Phaser's loader (music stays on Phaser - a
   *  single looping stream). Deduped by key so a track is fetched once; resolves
   *  when the load pass completes. */
  private loadMusic(key: string, track: string): Promise<void> {
    const existing = this.musicLoads.get(key);
    if (existing) return existing;
    const p = new Promise<void>((resolve) => {
      if (this.scene.cache.audio.exists(key)) {
        resolve();
        return;
      }
      this.scene.load.audio(key, `/assets/music/${track}.mp3`);
      // Key-specific events, not the generic COMPLETE: the scene loader is
      // shared with the level-atlas / game_changeBg image loads (docs/033), so a
      // generic once(COMPLETE) could be consumed by an unrelated load finishing.
      this.scene.load.once(`filecomplete-audio-${key}`, () => resolve());
      this.scene.load.once("loaderror", (file: Phaser.Loader.File) => {
        if (file.key === key) resolve();
      });
      this.scene.load.start();
    });
    this.musicLoads.set(key, p);
    return p;
  }

  destroy(): void {
    this.stopAll();
  }
}

export type { MusicCommand };
