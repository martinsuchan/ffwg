# 080 - Video export: AAC trial encode + WASM AAC fallback

2026-11-16

## Why

The user's export (docs/078) failed at the very end with **"Export selhal: Audio codec
disabled by product policy."** `detectCodecs` relied on `AudioEncoder.isConfigSupported()`
(via mediabunny's `canEncodeAudio`), and in that browser it answered "supported" for AAC
(`mp4a.40.2`). The encoder only refused once it actually started encoding. Because the audio
track is encoded after all video frames, the failure surfaced only after the whole render.
Chromium-based builds can ship WebCodecs with proprietary codecs gated off (builds without
proprietary codecs, some OEM/enterprise configurations), and `isConfigSupported` doesn't
always reflect that gate. We couldn't confirm which browser setup the user had, so the fix
doesn't depend on it.

## What was done

- **Trial encodes in `detectCodecs` (`replayExport.ts`):** before an encoder counts as
  available, it must really encode a sample: four 1024-frame blocks of stereo silence through
  a native `AudioEncoder` (AAC, and Opus if it gets that far), and one blank frame through a
  native H.264 `VideoEncoder` (`avc1.640028`) at the export size. Any `error` callback, a
  missing output chunk, or a 4 s timeout counts as "not supported". The dialog therefore
  already shows the real state before an export starts.
- **WASM AAC fallback:** new dependency `@mediabunny/aac-encoder@1.64.0` (MPL-2.0; a WASM build
  of FFmpeg's native AAC encoder, LGPL, GPL-compatible), matching `mediabunny@1.64.0`. When
  native AAC fails, `detectCodecs` lazy-`import()`s it and calls `registerAacEncoder()`, and
  mediabunny then uses it ahead of the browser's encoder. The output stays MP4 + AAC-LC, which
  YouTube and Facebook take as-is. It is a separate chunk (~990 kB, 257 kB gzip), fetched only
  when needed, and runs in a worker. `CodecSupport` gained `aacEncoder: "native" | "wasm"`.
  Fallback order: native AAC → WASM AAC → native Opus → no audio.
- **Late failure hint:** if the audio still fails during `writer.finish()`, the error reads
  `export_audio_failed` ("… Zkus export s vypnutou hudbou.").
- `vite.config.ts` pre-bundles `@mediabunny/aac-encoder` as well.

## Verification

- `npx tsc -b`; e2e `09-replay-export` gets a 6th step. It replaces `window.AudioEncoder` with
  a stub whose `isConfigSupported` delegates to the real one but whose AAC encode raises
  "Audio codec disabled by product policy.", then checks `detectCodecs` →
  `{audio: "aac", aacEncoder: "wasm"}` and that an export carries an AAC track.
- Dev server, headless (same stub): native vs WASM exports of `cannons` both give AAC-LC
  48 kHz stereo with the same loudness by ffmpeg `volumedetect` (mean −24.5 dB, max
  −8.7/−8.6 dB).
- **Production build** (`publish/` served statically, Edge, same stub, real UI path
  `/sandbox` → level → P → ⬇ → Enter): the trial encode logged the policy error, the
  `mediabunny-aac-encoder-*.js` chunk was fetched, and the downloaded `start-replay.mp4`
  probes as H.264 High + AAC-LC with audible music.
- `web/tests/production-build.mjs` was fixed along the way. It clicked the map without the
  docs/064 render-scale zoom and waited a fixed 2.5 s, while a production level load takes
  3–8 s. It now scales the click and polls up to 15 s, and passes.

- Also fixed: **`scripts\build.ps1 -Preview`** (`vite preview` of `web/dist`, port 4173)
  failed with "Failed to load the world map: … unexpected symbol near '<'". The `/legacy/`
  middleware in `vite.config.ts` was only registered for the dev server, and `web/dist` has
  no `legacy/` (only `publish.ps1` copies it), so the preview's SPA fallback answered
  `worldmap.lua` with `index.html`. The same middleware is now also registered in
  `configurePreviewServer`. Checked on a preview: Lua served (200), missing file 404,
  `../` traversal 403, `production-build.mjs` passes.

## Open for next time

- The trial encodes add a little latency (tens of ms) to opening the export dialog.
- There is no WASM fallback for H.264. A browser with H.264 encoding blocked still gets the
  "unsupported" dialog.
