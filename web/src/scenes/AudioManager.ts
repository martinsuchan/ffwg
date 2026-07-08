import Phaser from "phaser";

import type { ResolvedSound } from "../lua/levelScript";

/** legacy/src/gengine/SoundAgent.cpp's default OptionAgent values
 *  (volume_sound=90, volume_music=50, both 0-100%) - applied as a flat
 *  multiplier on top of each call's own volume, matching
 *  Mix_Volume(channel, m_soundVolume * volume / 100). No options UI yet to
 *  make these adjustable (docs/018). */
const GLOBAL_SOUND_VOLUME = 0.9;
const GLOBAL_MUSIC_VOLUME = 0.5;

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
  private readonly attempted = new Set<string>();
  private loadingChain: Promise<void> = Promise.resolve();
  private currentMusicTrack: string | null = null;
  private currentMusic: Phaser.Sound.BaseSound | null = null;
  /** Bumped by reset() so an in-flight load/play from a superseded session
   *  (pre-restart) can't resurrect stale audio after resolving late - same
   *  guard shape as LevelScene's own scriptGeneration. */
  private generation = 0;

  constructor(private readonly scene: Phaser.Scene) {}

  /** Restart: stop current music and invalidate in-flight commands from the
   *  previous session. Already-loaded sprites/tracks stay cached - a level
   *  restart doesn't invalidate audio assets, only "what's playing now". */
  reset(): void {
    this.generation += 1;
    this.stopMusic();
  }

  stopMusic(): void {
    this.currentMusic?.stop();
    this.currentMusic?.destroy();
    this.currentMusic = null;
    this.currentMusicTrack = null;
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
        volume: GLOBAL_SOUND_VOLUME * (volumePercent / 100),
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
      const music = this.scene.sound.add(key, { loop: true, volume: GLOBAL_MUSIC_VOLUME });
      music.play();
      this.currentMusic = music;
      this.currentMusicTrack = command.track;
    } catch {
      // Track failed to load (un-converted content) - silent, no music.
    }
  }

  /** Queues `enqueue()` on the shared loader and resolves once that load
   *  pass completes - serialized (one load at a time via loadingChain) so
   *  the loader's single 'complete' event unambiguously belongs to this
   *  request, and deduped by key so the same asset is never queued twice
   *  regardless of load success/failure (avoids repeat-404 spam). */
  private ensureLoaded(key: string, enqueue: () => void): Promise<void> {
    if (this.attempted.has(key)) return this.loadingChain;
    this.attempted.add(key);
    this.loadingChain = this.loadingChain.then(
      () =>
        new Promise<void>((resolve) => {
          enqueue();
          this.scene.load.once(Phaser.Loader.Events.COMPLETE, () => resolve());
          this.scene.load.start();
        }),
    );
    return this.loadingChain;
  }

  destroy(): void {
    this.stopMusic();
  }
}

export type { MusicCommand };
