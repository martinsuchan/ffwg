# 078 - Replay video export (MP4)

2026-11-15

## Why

The user wanted to turn a level replay into a video for YouTube/Facebook: it should look
exactly like the web replay, minus the on-screen controls, and upload without any
re-encoding trouble. Three ways were on the table:

- **`MediaRecorder` on the canvas stream:** real-time only, frame pacing at the mercy of the
  main thread (dropped/duplicated frames), WebM in most browsers, and the recorded tab must
  stay visible.
- **OBS / an external tool:** no code, but every user has to set it up, crop and cut by hand.
- **Deterministic offline render in the app (chosen):** the replay scene is stepped frame by
  frame with a fixed delta, each frame goes to WebCodecs H.264, the music is rendered
  separately, and both are muxed into an MP4. That gives exact pacing, no dropped frames, and
  output that's identical from run to run. It is also faster than real time on a GPU.

## What was done

- **Dependency:** `mediabunny@1.64.0` (pinned exact; MPL-2.0, GPL-compatible; successor of
  mp4-muxer by the same author). It is `import()`ed lazily only when an export starts, so the
  initial bundle doesn't grow. `vite.config.ts` pre-bundles it (`optimizeDeps.include`):
  without that, the first cold import made the dev server re-optimize deps and reload the page
  mid-export.
- **Output format:** MP4, H.264 High (yuv420) + AAC-LC 48 kHz stereo, constant 30 fps (a
  100 ms cycle is exactly 3 frames), `fastStart: "in-memory"` (moov before mdat), VBR at about
  5 Mbps per 720p, scaled by pixel count. Mostly-static frames compress well below that.
  If the browser can't encode AAC, it falls back to Opus, and the dialog says so
  (`export_opus_note`).
- **`web/src/scenes/replayExport.ts` (new, scene-free):** `computeVideoLayout`,
  `detectCodecs`, `estimateReplayMs` (headless dry run with ReplayScene's pacing), a
  `MusicCommandLog`, `renderMusicTrack`, a `MessageChannel` `yieldToEventLoop` (unlike
  `setTimeout`, it isn't throttled in a background tab), and a thin `VideoWriter` over
  mediabunny (`CanvasSource` + `AudioBufferSource`).
- **Layout rule:** always render at zoom **Large (1.5×)**, whatever the player's Game-size
  setting, in the smallest exact-16:9 frame of at least 720p that holds the room:
  `k = max(80, ceil(max(w/16, h/9)))` with `w, h` = room px · 1.5, rounded up to even, video =
  `16k × 9k`, room centred on integer offsets. 53 of the 88 rooms in `solver/levels/` give
  1280×720. `hole` (50×40) gives 1600×900 and `puzzle` (53×39) gives 1568×882.
- **`web/src/scenes/ReplayExportDialog.ts` (new):** a Phaser confirm dialog styled like
  `OptionsOverlay`. It explains what will happen and shows the estimated video length, the
  output size/format, and two toggles: **step counter** and **level music** (off = no audio
  track at all). Export/Cancel; Esc cancels, Enter exports. If there is no H.264 encoder, it
  shows a message and only a Close button.
- **`ReplayScene.ts`:** a 5th control button `⬇` opens the dialog. **Record mode** hides the
  controls and hint line, shows the step text per the toggle, and logs music commands instead
  of playing them. On solved/died/out-of-moves it records a 2 s tail instead of pausing or
  showing the poster, and a 1 s still lead-in precedes move 1. The public
  `runExport({showSteps, music, maxFrames?})` returns
  `{blob, width, height, frames, durationSec, audio, filename, musicSegments}`. Only the UI
  path downloads `<level>-replay.mp4`, so `runExport` doubles as the e2e hook. It always ends
  by restarting the scene into its normal state. Cancel, Esc, or leaving the scene (browser
  Back) makes it reject with an `AbortError`.
- **Progress panel in the DOM, not the canvas:** anything drawn in the canvas would end up in
  the video. The canvas is `visibility: hidden` and moved out of the page flow while it
  renders at video size. The panel shows the percentage, a bar, a live ETA from the measured
  frame rate, "keep this tab open", and Cancel.
- **Letterbox recipe:** `scale.resize(W, H)` + `setZoom(1)`. The main camera gets the full
  viewport, origin 0, zoom 1.5, scroll `-offset/1.5`, and a black background. Four black
  rectangles around the room (depth 999, below the step text) clip anything drawn outside it.
  A camera viewport alone did **not** work: the area outside it kept stale pixels (the old
  world-map frame showed through the bars). `sceneUtils.setRenderScaleLocked` stops a
  fullscreen toggle from resizing the canvas mid-export.
- **Determinism** (rendered frames are identical across exports; the encoded bytes are not,
  because the browser's H.264 encoder isn't):
  - `game.loop.sleep()` + a manual `game.step(t, 1000/30)`. Step times are whole ms
    (`t0 + round(i·33.33)`) while the delta stays exactly 1000/30, so ModelAnimator's
    `now > expiry` checks can't flip on float rounding that depends on `t0`.
  - The round clock compares with an epsilon (`roundClock >= interval - 1e-6`), so three
    33.33 ms frames fire exactly one 100 ms cycle. This also applies to the live replay
    (harmless there).
  - `Math.random` is seeded (mulberry32) only *inside* each export `game.step`, so the fish's
    random idle blinks/talk come out the same. Async encoder work between frames can't consume
    the sequence, and the live Lua's own randomness keeps the native one.
  - Tweens (the fish exit fade) run on a fixed `FRAME_MS` step instead of `Date.now()`.
  - The background ripple runs on video time, so every export starts from the same wave
    phase.
- **Music:** the live Lua's music commands are stamped on the video clock through
  `MusicCommandLog`, which dedupes like `AudioManager.applyMusicCommand`. After the last
  frame they are rendered in an `OfflineAudioContext` (looping sources, fixed gain
  `EXPORT_MUSIC_GAIN` = 0.8) and added as the audio track. The player's volume slider is not
  applied, so a muted player still gets music in the video. The buffers come from the new
  `AudioManager.getMusicBuffer(track)`, which reuses `loadMusic` + `scene.cache.audio`.
- **i18n:** `export_*` keys in `PORT_LABELS` (cs/nl/en).
- Standard and sandbox modes both work. In a level, sandbox `P` replays the bundled
  reference solution when there is no own solution, so reference solutions can be exported
  too.

## Measurements

- `cannons`, full export in Edge on a GPU: about 2.4 s for a 14.0 s video (9.3 MB). Headless
  Playwright Chromium renders via SwiftShader at about 100 ms/frame, which is why the e2e case
  caps the frames.
- Full-export duration ≈ `estimateReplayMs` + lead-in + tail, within about 0.25 s.
- Full-length exports in headed Edge (GPU), music on:
  - `hole` (465 moves): 1600×900, 135.4 s video, rendered in 18.8 s (7.2× real time),
    136 MB (8.1 Mbps).
  - `map` (2127 moves, the longest reference solution): 1344×756, 593.6 s video, rendered in
    64.6 s (9.2× real time), 424 MB (5.7 Mbps), JS heap about 480 MB at the end.
  - The encoder spends close to the whole VBR target: the background ripple animates every
    frame, so the frames are not static.
- Real sandbox UI path (`/sandbox` → level → `P` → `⬇` → Export): `cannons-replay.mp4`
  downloaded in about 2 s; Esc then returns to the level.
- `viking1`: 36.4 s. Its mid-level music-stop gag does not fire within the replay. That is
  the level's own Lua countdown (level logic), and the live replay behaves the same.

## Verification

- New e2e case `web/tests/cases/09-replay-export.mjs` (about 30 s headless):
  - `computeVideoLayout` over all 88 `solver/levels/*.json` rooms: even, exact 16:9, ≥ 720p,
    the room fits with offsets ≥ 0, rooms that fit 720p get exactly 1280×720, plus the `hole`
    and `puzzle` spot checks.
  - Two capped `cannons` exports (75 frames with music, 30 without) parsed back with
    mediabunny: avc 1280×720, duration = frames/30 ± 0.1 s, `ftyp > moov > mdat`, an AAC
    track only when music is on, and a frame decoded at 2.0 s with black corners and a
    non-black room centre.
  - After each export: loop running, canvas visible and back to its pre-export size, no panel
    left, controls visible.
  - A browser Back (`popstate`) mid-export rejects with `AbortError` and lands on the world
    map with the loop running and the canvas restored.
- `npx tsc -b` and the full e2e suite (8/9; the 06 failure is unrelated, see below).

## Open for next time

- The sandbox **Pedometer** replay still uses own solutions only. Reference solutions export
  via `P` inside a level.
- The small fish stays visible at the exit during the 2 s tail.
- The ETA is pessimistic at the start and settles as the measured frame rate stabilizes.
- The `⬇` glyph renders thinner than the other control symbols.
- A fullscreen toggle during an export is only covered by the render-scale lock; it isn't
  otherwise tested.
- The whole file is kept in memory until the download (`fastStart: "in-memory"`). The
  longest replay (`map`) reaches about 424 MB, plus the offline-rendered audio buffer. If
  that is too heavy, lower `videoBitrate()` or stream to disk via the File System Access API.
- `scripts\test.ps1` was 8/9 green: 06 fails on `legacy/solution/cabin1-simple.lua` (added in
  `ceaf4e4` for the solver editor), which has no `legacy/script/cabin1-simple/`. This is
  unrelated to the export; case 06 should skip solution files without a level script.
- The Opus fallback (e.g. a browser without an AAC encoder) is untested in a real browser.
  Some platforms may not accept Opus in MP4.
