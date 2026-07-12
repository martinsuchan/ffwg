import Phaser from "phaser";

import type { ResolvedSound } from "../lua/levelScript";
import { loadSettings } from "../storage/settingsStorage";

/** legacy/src/gengine/SoundAgent.cpp applies the global volume as a flat
 *  multiplier on top of each call's own volume (Mix_Volume(channel,
 *  m_soundVolume * volume / 100)). The global volumes are the player's
 *  Options settings now (0-100 -> 0-1, docs/038), read live on each play so a
 *  change takes effect on the next sound; the currently-playing music is also
 *  updated in place via setMusicVolume(). */
function globalSoundVolume(): number {
  return loadSettings().soundVolume / 100;
}
function globalMusicVolume(): number {
  return loadSettings().musicVolume / 100;
}

type MusicCommand = { type: "play"; track: string } | { type: "stop" };

/**
 * Owns actual Phaser sound/music playback for one LevelScene - lazy-loads
 * audio sprites/tracks on first use (which level content needs which sound
 * files isn't known until the async Lua bootstrap runs, so nothing can be
 * preloaded up front), and is tolerant of missing/un-converted content
 * (silently no sound, matching the original's own missing-file fallback -
 * never throws or blocks gameplay). See docs/018.
 */
export class AudioManager {
  /** key -> promise that resolves when that key's load finishes. Doubles as the
   *  dedup set (a key present here is already loading/loaded) and lets callers
   *  wait on one specific sprite rather than the whole load chain - see
   *  ensureLoaded()/whenLoaded(). */
  private readonly loadPromises = new Map<string, Promise<void>>();
  private loadingChain: Promise<void> = Promise.resolve();
  private currentMusicTrack: string | null = null;
  private currentMusic: Phaser.Sound.BaseSound | null = null;
  /** The dialog/NPC voice currently playing, tracked so a new dialog line
   *  cuts the previous one (legacy Dialogs::killSound - avoids two voices
   *  overlapping) and so leaving the level stops it (docs/031). */
  private currentDialogVoice: Phaser.Sound.BaseSound | null = null;
  /** Bumped by reset() so an in-flight load/play from a superseded session
   *  (pre-restart) can't resurrect stale audio after resolving late - same
   *  guard shape as LevelScene's own scriptGeneration. */
  private generation = 0;

  constructor(private readonly scene: Phaser.Scene) {}

  /** Restart: stop everything playing and invalidate in-flight commands from
   *  the previous session. Already-loaded sprites/tracks stay cached - a level
   *  restart doesn't invalidate audio assets, only "what's playing now". */
  reset(): void {
    this.generation += 1;
    this.stopAll();
  }

  /** Warm the loader cache for sprite dirs this level will need, so the first
   *  dialog/effect plays immediately instead of after a ~0.5-1s network fetch
   *  (docs/031). Fire-and-forget and tolerant of un-converted dirs. */
  preload(spriteDirs: string[]): void {
    for (const dir of spriteDirs) {
      void this.ensureLoaded(dir, () =>
        this.scene.load.audioSprite(
          dir,
          `/assets/sound/${dir}/sprite.json`,
          `/assets/sound/${dir}/sprite.mp3`,
        ),
      );
    }
  }

  stopMusic(): void {
    this.currentMusic?.stop();
    this.currentMusic?.destroy();
    this.currentMusic = null;
    this.currentMusicTrack = null;
  }

  /** Stops the currently-playing dialog/NPC voice, if any - legacy's
   *  Dialogs::killSound(). */
  stopDialogVoice(): void {
    this.currentDialogVoice?.stop();
    this.currentDialogVoice?.destroy();
    this.currentDialogVoice = null;
  }

  /** Stops music, the dialog voice, and every one-shot effect still playing -
   *  used when leaving a level (Esc -> world map) or restarting, so no level
   *  audio bleeds into the next scene (docs/031). The Sound Manager is
   *  game-global (docs/025), so stopAll() reaches sounds this scene started
   *  even as it tears down. */
  stopAll(): void {
    this.stopMusic();
    this.stopDialogVoice();
    this.scene.sound.stopAll();
  }

  /** Dialog/NPC voice (model_talk) - like playSoundEffect, but the instance
   *  is tracked so the next dialog line cuts this one (no overlap) and leaving
   *  the level stops it. See docs/031. */
  async playDialogVoice(sound: ResolvedSound, volumePercent: number): Promise<void> {
    const generation = this.generation;
    await this.ensureLoaded(sound.spriteDir, () =>
      this.scene.load.audioSprite(
        sound.spriteDir,
        `/assets/sound/${sound.spriteDir}/sprite.json`,
        `/assets/sound/${sound.spriteDir}/sprite.mp3`,
      ),
    );
    if (generation !== this.generation) return;
    this.stopDialogVoice();
    try {
      const voice = this.scene.sound.addAudioSprite(sound.spriteDir, {
        volume: globalSoundVolume() * (volumePercent / 100),
      });
      voice.play(sound.region);
      this.currentDialogVoice = voice;
      voice.once(Phaser.Sound.Events.COMPLETE, () => {
        if (this.currentDialogVoice === voice) this.currentDialogVoice = null;
        voice.destroy();
      });
    } catch {
      // Sprite failed to load (un-converted content) - silent, no voice.
    }
  }

  /** One sound_playSound()/built-in (impact/death) effect - fire and forget. */
  async playSoundEffect(sound: ResolvedSound, volumePercent: number): Promise<void> {
    const generation = this.generation;
    await this.ensureLoaded(sound.spriteDir, () =>
      this.scene.load.audioSprite(
        sound.spriteDir,
        `/assets/sound/${sound.spriteDir}/sprite.json`,
        `/assets/sound/${sound.spriteDir}/sprite.mp3`,
      ),
    );
    if (generation !== this.generation) return;
    try {
      this.scene.sound.playAudioSprite(sound.spriteDir, sound.region, {
        volume: globalSoundVolume() * (volumePercent / 100),
      });
    } catch {
      // Sprite failed to load (un-converted content) - silent, no sound.
    }
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
    await this.ensureLoaded(key, () =>
      this.scene.load.audio(key, `/assets/music/${command.track}.mp3`),
    );
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

  /** Queues `enqueue()` on the shared loader and returns a promise that
   *  resolves once *this key's* load pass completes. Loads are serialized (one
   *  at a time via loadingChain) so the loader's single 'complete' event
   *  unambiguously belongs to one request, but each key gets its *own* promise
   *  (not the whole chain) so a caller waiting on an already-loaded sprite
   *  resolves immediately instead of blocking on unrelated in-flight loads
   *  (docs/031 follow-up - that whole-chain wait was adding seconds to the
   *  first dialog). Deduped by key, so the same asset is never queued twice. */
  private ensureLoaded(key: string, enqueue: () => void): Promise<void> {
    const existing = this.loadPromises.get(key);
    if (existing) return existing;
    const p = this.loadingChain.then(
      () =>
        new Promise<void>((resolve) => {
          enqueue();
          this.scene.load.once(Phaser.Loader.Events.COMPLETE, () => resolve());
          this.scene.load.start();
        }),
    );
    this.loadPromises.set(key, p);
    this.loadingChain = p;
    return p;
  }

  /** Resolves once `spriteDir`'s sprite is fully loaded+decoded (or immediately
   *  if it already is / was never queued). Lets a caller gate on the audio
   *  being ready - e.g. hold a level's dialog logic until its voice sprite has
   *  decoded, so the first line plays in sync with its subtitle instead of ~2s
   *  late while a big sprite (briefcase's 2.8MB) is still decoding (docs/031). */
  whenLoaded(spriteDir: string): Promise<void> {
    return this.loadPromises.get(spriteDir) ?? Promise.resolve();
  }

  destroy(): void {
    this.stopAll();
  }
}

export type { MusicCommand };
