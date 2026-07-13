# 043 - Polyphonic audio + Web Audio buffer engine

2026-07-13

## What was done

Fixed two audio defects by mirroring the original's in-memory, multi-channel
model instead of the port's single-slot / concatenated-sprite approach.

**Problem 1 - no simultaneous playback.** The original's `DialogStack`
(`legacy/src/gengine/DialogStack.cpp`) keeps FIFO lists of concurrently-playing
`PlannedDialog`s (`m_running` + `m_cycling` for `loops==-1`), each on its own
SDL_mixer channel; `m_activeDialog` is only the single *blocking* dialog that
`isDialog()` reports (gates gameplay). The port had collapsed this into one
`state.activeDialog` slot plus a `currentDialogVoice` that **cut** the previous -
so a 2nd `model_talk` overwrote the 1st, and viking1's whistle+bass+voice trio
could only sound one note at a time (docs/036's known limitation).

**Problem 2 - 1-2 s dialog latency.** Each sound dir is one concatenated
`sprite.mp3` that Phaser decoded via Web Audio `decodeAudioData` **as a whole**
(you can't partial-decode an MP3) - briefcase/cs = 2.7 MB → ~1-2 s. docs/031
only gated the first line on *one* dir's decode; any line in a not-yet-decoded
dir (shared joke/border pools, viking's `en` instrument dir) then hit a cold
mid-game decode ("every other line late").

### The model: pre-decoded buffers + a talker stack

- **New `web/src/scenes/audioEngine.ts`** - a small Web Audio layer (the port of
  the SDL_mixer Mix_Chunk model). Each dir's sprite is fetched + `decodeAudioData`'d
  **once** into a single `AudioBuffer` (kept whole; play regions by offset - the
  A1 packaging choice, no per-clip slicing). Every play spawns an independent
  `AudioBufferSourceNode` → GainNode(volume) → Phaser's `destination` node
  (so master volume/mute still apply). That gives **instant** start (decode is
  done up front) and **unlimited concurrency** (each source is independent).
  Sources are tracked per **group** (dialog actor index) so `stopGroup(actor)`
  implements `killSound(actor)`, and a module-global `dirBuffers`/`dirLoads`
  cache means the ~6 MB shared SFX pool (and any revisited level) decodes only
  **once per session** even though each `LevelScene` builds a fresh engine
  (Phaser's AudioContext is game-global, so the buffers stay valid). Degrades to
  a silent no-op on the HTML5-audio fallback (no `.context`) and tolerates
  missing/un-converted dirs.
- **`web/src/scenes/AudioManager.ts`** - music stays on Phaser (a single looping
  stream; single-channel is correct for it). Voices + one-shot effects now route
  through the engine: `preloadAll(dirs)` (decode up front, returns a promise),
  `playDialogVoice(sound, vol, actor, loop)` (grouped, concurrent),
  `playSoundEffect` (overlapping), `stopDialogVoice(actor?)` (one actor or all).
  Dropped the `currentDialogVoice` single slot and the Phaser `addAudioSprite`
  path; kept a Phaser loader only for music.
- **`web/src/lua/levelScript.ts`** - ported `DialogStack`: `state.activeDialog`
  → `state.talkers: Talker[]` (all running voices) + `state.activeBlocking`
  (only the blocking one, for `isDialog()`) + `state.killedActors`. `model_talk`
  now honors its real **5th `dialogFlag` arg** (`planDialog` passes `true` =
  blocking conversation; `object:talk` passes 4 args = non-blocking band/ambient)
  and **pushes** a talker instead of overwriting. `updateDialogStack()` (called
  each tick) drops finished talkers; `killTalkers(actor)` removes an actor's
  talkers + flags `killedActors`. `isModelTalking`/`model_isTalking` now scan all
  talkers (so a non-blocking talker animates its fish's mouth too). New drains
  `takePendingVoices()` (play-once, replaces the `getActiveDialogId`/`lastDialogId`
  diff) and `takeKilledActors()`; removed `getActiveSubtitle`/`getActiveDialogId`.
- **`web/src/scenes/LevelScene.ts`** - `create()` now `preloadAll`s the level's
  own voice dir + shared pools + the `<level>/en` fallback dir (viking's
  instruments live only there, docs/036), in parallel with the Lua bootstrap +
  atlas load; the existing gate still holds the level's dialog until the level's
  *own* voice dir is decoded (small → fast) so the first line is in sync.
  `tickAudio()` plays each `takePendingVoices()` talker concurrently (grouped by
  actor) and stops each `takeKilledActors()` actor; a death cuts just the dying
  fish's voices (`stopDialogVoice(dead.index)` + `killSound`).
- **`web/src/scenes/DemoScene.ts`** - the briefcase movie is inherently
  sequential (`waitForTalker`), so it uses one voice group (0) and cuts the
  previous line - the degenerate case of the new concurrent path. `demoScript.ts`
  keeps its own single-slot dialog (unchanged); `ReplayScene` (music only)
  unaffected.

## Verified
- `tsc -b` + `vite build` clean; dev server transforms all changed modules (200)
  and the app boots.
- sprite.json format the engine reads (`spritemap[name] = {start, end}` seconds)
  matches the pipeline output; `viking1/en` has the `d1-z-{b,p,v}*` band regions
  the concurrent path needs.

## Open for next time
- **Interactive real-browser drive still pending** (no browser-automation tool
  this session): confirm viking1's band notes sound **together**; first line of a
  heavy level (briefcase) in sync and no mid-game cold-decode stalls on later
  lines / shared-pool effects; rapid impacts overlap; a death cuts the dying
  fish's voice; Options volume/mute + subtitles still apply; no audio bleeds
  across restart/Esc/replay. Dev server is at localhost:5173.
- A big level's **first** line still pays a one-time own-voice decode at level
  entry (hidden behind the load, never per-line) - the A1 sprite trade-off.
  Splitting voices into per-clip files (A2) would remove even that, at the cost
  of many small files.
- Decoded buffers are cached for the whole session with no eviction - fine for
  normal play; revisit if memory becomes a concern on very long sessions.
- Concurrent dialog is now truly polyphonic, so docs/036's "single activeDialog /
  whistle dominates" limitation is resolved.
