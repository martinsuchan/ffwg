import type { LevelData } from "../lua/levelLoader";
import { GameEngine, type RenderModel } from "../game/GameEngine";
import { CYCLE_MS, IDLE_ROUND_MS } from "../game/timing";
import { roundPhases } from "./ModelAnimator";

/**
 * Replay -> MP4 export helpers (docs/078): frame layout, codec detection, a
 * headless length estimate, the offline music render and a thin wrapper over
 * mediabunny's MP4 muxer. ReplayScene drives the actual frame-by-frame render;
 * everything here is scene-free. mediabunny is only ever `import()`ed lazily,
 * so it stays out of the main bundle until an export actually starts.
 */

/** Fixed render zoom (the "Large" game size), independent of the player's setting. */
export const EXPORT_ZOOM = 1.5;
/** Constant output frame rate - 30 fps puts a 100 ms cycle on exactly 3 frames. */
export const EXPORT_FPS = 30;
/** Still frames before move 1. */
export const LEAD_IN_MS = 1000;
/** Frames recorded after the replay ends (solved / died / out of moves). */
export const TAIL_MS = 2000;
/** Fixed music level - not the player's volume slider, so a muted player still gets music. */
export const EXPORT_MUSIC_GAIN = 0.8;

const MIN_K = 80; // 16·80 × 9·80 = 1280×720
const SAMPLE_RATE = 48000;
const AUDIO_CHANNELS = 2;
const AAC_BITRATE = 192_000;
const OPUS_BITRATE = 128_000;
const KEY_FRAME_INTERVAL_SEC = 2;

export interface VideoLayout { width: number; height: number; offsetX: number; offsetY: number; zoom: number }

/**
 * The smallest exact-16:9 frame (both sides even, at least 1280×720) that holds
 * the room at `zoom`, with the room centered on integer offsets: `16k × 9k` for
 * `k = max(80, ceil(max(w/16, h/9)))` rounded up to even. E.g. any room up to
 * 853×480 px -> 1280×720; `hole` (750×600 px) -> 1600×900; `puzzle` (795×585 px)
 * -> 1568×882.
 */
export function computeVideoLayout(roomWidthPx: number, roomHeightPx: number, zoom = EXPORT_ZOOM): VideoLayout {
  const w = roomWidthPx * zoom;
  const h = roomHeightPx * zoom;
  let k = Math.max(MIN_K, Math.ceil(Math.max(w / 16, h / 9)));
  if (k % 2 !== 0) k += 1;
  const width = 16 * k;
  const height = 9 * k;
  return {
    width,
    height,
    offsetX: Math.round((width - w) / 2),
    offsetY: Math.round((height - h) / 2),
    zoom,
  };
}

/** Target video bitrate: ~5 Mbps at 1280×720 (YouTube's 720p30 recommendation),
 *  scaled linearly by pixel count. Encoded VBR - the mostly-static room
 *  compresses far below it. */
export function videoBitrate(layout: VideoLayout): number {
  return Math.round((5_000_000 * layout.width * layout.height) / (1280 * 720));
}

export interface CodecSupport {
  video: "avc" | null;
  audio: "aac" | "opus" | null;
  /** Who encodes the AAC: the browser's WebCodecs, or the bundled WASM (FFmpeg)
   *  fallback for browsers whose native AAC is missing/blocked (docs/080). */
  aacEncoder?: "native" | "wasm";
}

/** Max time a trial encode may take before it counts as unsupported. */
const TRIAL_TIMEOUT_MS = 4000;

/**
 * Really encodes a tiny sample with a native WebCodecs encoder. `isConfigSupported`
 * alone is not enough: some Chromium builds answer "supported" for AAC and only
 * fail once the encoder starts ("Audio codec disabled by product policy") - which
 * used to surface at the very end of an export (docs/080). Never throws.
 */
async function trialEncode(
  make: (onChunk: () => void, onError: (e: unknown) => void) => { encodeSample(): void; flush(): Promise<void>; close(): void },
): Promise<boolean> {
  let chunks = 0;
  let failed: unknown = null;
  let enc: ReturnType<typeof make> | null = null;
  try {
    enc = make(
      () => chunks++,
      (e) => {
        failed = e;
      },
    );
    enc.encodeSample();
    await Promise.race([
      enc.flush(),
      new Promise((_, reject) => setTimeout(() => reject(new Error("trial encode timed out")), TRIAL_TIMEOUT_MS)),
    ]);
    if (failed) throw failed;
    return chunks > 0;
  } catch (error) {
    console.warn("Video export: trial encode failed -", error instanceof Error ? error.message : error);
    return false;
  } finally {
    try {
      enc?.close();
    } catch {
      // already closed by the error
    }
  }
}

/** One 1024-frame block of stereo silence through a native AudioEncoder. */
function trialEncodeAudio(codec: string, bitrate: number): Promise<boolean> {
  return trialEncode((onChunk, onError) => {
    const encoder = new AudioEncoder({ output: onChunk, error: onError });
    encoder.configure({ codec, sampleRate: SAMPLE_RATE, numberOfChannels: AUDIO_CHANNELS, bitrate });
    return {
      encodeSample: () => {
        // Several blocks: AAC needs more than one input frame before it emits.
        for (let i = 0; i < 4; i++) {
          const data = new AudioData({
            format: "f32-planar",
            sampleRate: SAMPLE_RATE,
            numberOfFrames: 1024,
            numberOfChannels: AUDIO_CHANNELS,
            timestamp: (i * 1024 * 1e6) / SAMPLE_RATE,
            data: new Float32Array(1024 * AUDIO_CHANNELS),
          });
          encoder.encode(data);
          data.close();
        }
      },
      flush: () => encoder.flush(),
      close: () => encoder.close(),
    };
  });
}

/** One small frame through a native H.264 VideoEncoder at the layout's size. */
function trialEncodeVideo(layout: VideoLayout): Promise<boolean> {
  return trialEncode((onChunk, onError) => {
    const encoder = new VideoEncoder({ output: onChunk, error: onError });
    encoder.configure({
      codec: "avc1.640028", // High@4.0 - covers every export size (up to 1600×900)
      width: layout.width,
      height: layout.height,
      bitrate: videoBitrate(layout),
      framerate: EXPORT_FPS,
      avc: { format: "avc" },
    });
    return {
      encodeSample: () => {
        const pixels = new Uint8Array(layout.width * layout.height * 4);
        const frame = new VideoFrame(pixels, {
          format: "RGBA",
          codedWidth: layout.width,
          codedHeight: layout.height,
          timestamp: 0,
        });
        encoder.encode(frame, { keyFrame: true });
        frame.close();
      },
      flush: () => encoder.flush(),
      close: () => encoder.close(),
    };
  });
}

let wasmAacRegistered = false;

/** Lazily loads + registers the WASM AAC encoder (`@mediabunny/aac-encoder`,
 *  FFmpeg's native AAC encoder - LGPL, GPL-compatible). Once registered,
 *  mediabunny prefers it over the browser's for the rest of the session. */
async function registerWasmAac(): Promise<boolean> {
  if (wasmAacRegistered) return true;
  try {
    const { registerAacEncoder } = await import("@mediabunny/aac-encoder");
    registerAacEncoder();
    wasmAacRegistered = true;
    return true;
  } catch (error) {
    console.warn("Video export: WASM AAC encoder unavailable", error);
    return false;
  }
}

/** What this browser can encode, verified by real trial encodes: H.264 at the
 *  layout's size (null = export unavailable); audio AAC - natively, else via the
 *  bundled WASM encoder - else Opus, else none. Never throws - no WebCodecs (or a
 *  failed import) is simply `{ video: null, audio: null }`. */
export async function detectCodecs(layout: VideoLayout): Promise<CodecSupport> {
  const result: CodecSupport = { video: null, audio: null };
  try {
    if (typeof VideoEncoder === "undefined") return result;
    const mb = await import("mediabunny");
    const videoOk = await mb.canEncodeVideo("avc", {
      width: layout.width,
      height: layout.height,
      quality: new mb.Quality({ bitrate: videoBitrate(layout), bitrateMode: "variable" }),
      frameRate: EXPORT_FPS,
    });
    if (videoOk && (await trialEncodeVideo(layout))) result.video = "avc";

    const nativeAudio = typeof AudioEncoder !== "undefined" && typeof AudioData !== "undefined";
    const audioOpts = (bitrate: number) => ({
      numberOfChannels: AUDIO_CHANNELS,
      sampleRate: SAMPLE_RATE,
      quality: new mb.Quality({ bitrate }),
    });
    if (wasmAacRegistered) {
      result.audio = "aac";
      result.aacEncoder = "wasm";
    } else if (
      nativeAudio &&
      (await mb.canEncodeAudio("aac", audioOpts(AAC_BITRATE))) &&
      (await trialEncodeAudio("mp4a.40.2", AAC_BITRATE))
    ) {
      result.audio = "aac";
      result.aacEncoder = "native";
    } else if (await registerWasmAac()) {
      result.audio = "aac";
      result.aacEncoder = "wasm";
    } else if (
      nativeAudio &&
      (await mb.canEncodeAudio("opus", audioOpts(OPUS_BITRATE))) &&
      (await trialEncodeAudio("opus", OPUS_BITRATE))
    ) {
      result.audio = "opus";
    }
  } catch (error) {
    console.warn("Video export: codec detection failed", error);
  }
  return result;
}

/**
 * Replay length in ms (lead-in/tail NOT included), from a headless dry run on a
 * fresh GameEngine with ReplayScene's own pacing: a round's interval is fixed
 * by the *previous* tick (`updateRoundPacing`) - idle = IDLE_ROUND_MS, moving =
 * `roundPhases(...) · CYCLE_MS` - and the first round starts idle. Stops where
 * ReplayScene's tick() does (solved, unsolvable, or moves exhausted and the
 * room settled). Ignores windoze's live-Lua EngineControl - an estimate only.
 */
export function estimateReplayMs(levelData: LevelData, moves: string): number {
  const engine = new GameEngine(levelData);
  const maxRounds = moves.length * 20 + 10_000; // safety cap against a never-settling room
  let moveIndex = 0;
  let interval = IDLE_ROUND_MS;
  let totalMs = 0;
  for (let round = 0; round < maxRounds; round++) {
    totalMs += interval;
    const symbol = moveIndex < moves.length ? moves[moveIndex] : null;
    if (engine.tickReplay(symbol)) moveIndex++;
    if (engine.isSolved() || !engine.isSolvable()) break;
    if (moveIndex >= moves.length && engine.room.isFresh()) break;
    interval = engine.anyModelMoving()
      ? roundPhases(engine.getActiveInfo(), pacingSnapshot(engine), levelData.models, engine.wentOutThisRound()) *
        CYCLE_MS
      : IDLE_ROUND_MS;
  }
  return totalMs;
}

/** Just the RenderModel fields roundPhases() reads (`isLeft`, `state`). The full
 *  getRenderModels() also computes isAtBorder() for every model, which made a
 *  long replay's estimate take seconds (hole: ~6 ms/round). */
function pacingSnapshot(engine: GameEngine): RenderModel[] {
  return engine.room.models.map(
    (cube) => ({ isLeft: cube.isLeft, state: cube.rules.getState() }) as RenderModel,
  );
}

export type MusicCommand = import("./AudioManager").MusicCommand;
export interface MusicSegment { track: string; startSec: number; endSec: number }

/** Records the live Lua's music commands against recorded time and turns them
 *  into play segments - same semantics as AudioManager.applyMusicCommand: null
 *  = no change, stop ends the current track, replaying the current track is a
 *  no-op, another track replaces it. */
export class MusicCommandLog {
  private readonly segments: MusicSegment[] = [];
  private current: { track: string; startSec: number } | null = null;

  record(command: MusicCommand | null, timeSec: number): void {
    if (!command) return;
    if (command.type === "play" && this.current?.track === command.track) return;
    this.close(timeSec);
    if (command.type === "play") this.current = { track: command.track, startSec: timeSec };
  }

  /** Closes the open segment at `endSec`; zero-length segments are dropped. */
  finish(endSec: number): MusicSegment[] {
    this.close(endSec);
    return this.segments.filter((s) => s.endSec > s.startSec);
  }

  private close(timeSec: number): void {
    if (!this.current) return;
    this.segments.push({ track: this.current.track, startSec: this.current.startSec, endSec: timeSec });
    this.current = null;
  }
}

/** Renders the music segments into one 48 kHz stereo buffer of `durationSec`
 *  (each segment a looping source at EXPORT_MUSIC_GAIN). A track whose buffer
 *  can't be loaded is skipped (silence, like AudioManager's missing-file case);
 *  no segments -> plain silence. */
export async function renderMusicTrack(
  segments: MusicSegment[],
  durationSec: number,
  getBuffer: (track: string) => Promise<AudioBuffer | null>,
): Promise<AudioBuffer> {
  const length = Math.max(1, Math.ceil(durationSec * SAMPLE_RATE));
  const context = new OfflineAudioContext(AUDIO_CHANNELS, length, SAMPLE_RATE);
  const buffers = new Map<string, AudioBuffer | null>();
  for (const segment of segments) {
    if (!buffers.has(segment.track)) buffers.set(segment.track, await getBuffer(segment.track));
    const buffer = buffers.get(segment.track);
    if (!buffer) continue;
    const source = context.createBufferSource();
    source.buffer = buffer;
    source.loop = true;
    const gain = context.createGain();
    gain.gain.value = EXPORT_MUSIC_GAIN;
    source.connect(gain).connect(context.destination);
    source.start(segment.startSec);
    source.stop(segment.endSec);
  }
  return context.startRendering();
}

/** Macrotask yield via MessageChannel - unlike setTimeout it isn't throttled to
 *  1 s in a background tab, so a long export keeps going if the user switches away. */
export function yieldToEventLoop(): Promise<void> {
  return new Promise((resolve) => {
    const channel = new MessageChannel();
    channel.port1.onmessage = () => {
      channel.port1.close();
      resolve();
    };
    channel.port2.postMessage(null);
  });
}

export interface VideoWriter {
  /** Grabs the canvas *now* as one frame; resolves once the encoder/muxer accept more (backpressure). */
  addFrame(timestampSec: number, durationSec: number): Promise<void>;
  /** Only if created withAudio; call once, after all frames. */
  addAudio(buffer: AudioBuffer): Promise<void>;
  /** Finalizes the file: video/mp4 with the moov box first (fast start). */
  finish(): Promise<Blob>;
  cancel(): Promise<void>;
}

/**
 * MP4 writer over mediabunny: H.264 (High profile, chosen by mediabunny) from
 * `canvas` at constant EXPORT_FPS, optional AAC/Opus track. Uses
 * `fastStart: "in-memory"`, so every packet stays buffered until finish() -
 * which is also what makes adding the whole audio track after the last video
 * frame safe (the muxer only interleaves by timestamp at that point). The
 * canvas must already be layout.width × layout.height when frames are added.
 */
export async function createVideoWriter(
  canvas: HTMLCanvasElement,
  layout: VideoLayout,
  codecs: CodecSupport,
  withAudio: boolean,
): Promise<VideoWriter> {
  if (!codecs.video) throw new Error("This browser cannot encode H.264 video (WebCodecs).");
  // No usable audio encoder -> video-only file (addAudio() then becomes a no-op).
  const audioCodec = withAudio ? codecs.audio : null;

  const mb = await import("mediabunny");
  const target = new mb.BufferTarget();
  const output = new mb.Output({ format: new mb.Mp4OutputFormat({ fastStart: "in-memory" }), target });

  const videoSource = new mb.CanvasSource(canvas, {
    codec: codecs.video,
    quality: new mb.Quality({ bitrate: videoBitrate(layout), bitrateMode: "variable" }),
    keyFrameInterval: KEY_FRAME_INTERVAL_SEC,
  });
  output.addVideoTrack(videoSource, { frameRate: EXPORT_FPS });

  const audioSource = audioCodec
    ? new mb.AudioBufferSource({
        codec: audioCodec,
        quality: new mb.Quality({ bitrate: audioCodec === "aac" ? AAC_BITRATE : OPUS_BITRATE }),
      })
    : null;
  if (audioSource) output.addAudioTrack(audioSource);

  await output.start();

  let sizeChecked = false;
  return {
    async addFrame(timestampSec, durationSec) {
      if (!sizeChecked) {
        if (canvas.width !== layout.width || canvas.height !== layout.height) {
          throw new Error(
            `Canvas is ${canvas.width}×${canvas.height}, expected ${layout.width}×${layout.height}.`,
          );
        }
        sizeChecked = true;
      }
      await videoSource.add(timestampSec, durationSec);
    },
    async addAudio(buffer) {
      if (!audioSource) return;
      await audioSource.add(buffer);
    },
    async finish() {
      await output.finalize();
      if (!target.buffer) throw new Error("MP4 output is empty.");
      return new Blob([target.buffer], { type: "video/mp4" });
    },
    async cancel() {
      if (output.state === "canceled" || output.state === "finalized") return;
      await output.cancel();
    },
  };
}

/** Saves `blob` as a download via a temporary `<a download>` link. */
export function downloadBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  link.style.display = "none";
  document.body.appendChild(link);
  link.click();
  link.remove();
  // Revoke later: some browsers start reading the URL only after click() returns.
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}
