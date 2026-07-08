# 018 - Sound and Music

2026-07-09

## Goal

Real audio: per-level looping background music (some levels stop it mid-
game, e.g. `viking1`'s musician-band gag), fish/NPC dialog voice, built-in
event sounds (impact, death), Lua-driven ambient sound (bubbles), and
level-specific one-off sounds. Every sound-related Lua host binding was a
deliberate no-op until now (`docs/015`).

## Investigated before implementing

Researched via 3 parallel agents + direct source reads:

- **Engine split**: SDL_mixer distinguishes one-shot/looped *sound*
  (`Mix_Chunk`, `ResourcePack`-style multi-variant-per-name registry for
  random selection) from *music* (one seamless-looping track at a time,
  always stopped before a new one starts). No built-in turn/escape sounds
  exist - only impact (`Room::playImpact`, weight-based) and death
  (`Room::playDead`, power-based, also cuts the dying model's in-progress
  talk sound) are C++-triggered with **no Lua call site at all**. Bubbles
  are a Lua-driven ~2%/cycle ambient effect (`bubles.lua`'s `stdBubles()`,
  already running every round via `docs/014`'s live engine).
- **Dialog sound is derived, not authored**: `dialogId(name,font,text)`
  never passes a sound path - `level_dialog.lua`'s `dataPathSound()` builds
  `sound/<prefix>/<lang>/<name>.ogg` and gates on `file_exists()`, which was
  hardcoded `() => false` in the live engine - so every dialog's resolved
  sound was always `""` regardless of the (already-flowing, previously
  discarded) `soundPath` argument.
- **Asset pipeline already existed, unused** (`docs/003`): `scripts/
  convert-images.ps1`/`convert-music.ps1`/`build-audio-sprite.ps1`/
  `convert-assets.ps1` already convert legacy `.ogg` to web MP3 (tracks) or
  MP3+JSON audio sprites (Phaser's native `load.audioSprite()` format).
  Only ever run for one level before this phase.
- **`Landslip.ts` already tracked impact weight** (`getImpact(): Weight`,
  ported since `docs/007`) but `Room.falldown()` discarded it - the same
  "ported but unreachable" situation `docs/016`/`docs/017` already hit.
- **User decisions**: (1) convert only a small verification set now
  (`airplane` + `viking1` + the shared sound pool + all music - the full
  ~80-level batch stays a documented follow-up); (2) **switch the game's
  default dialog language from English to Czech**, text and audio both -
  `airplane` has zero English voice-over and only 30/82 levels have any,
  while Czech (the original's home language) has near-universal coverage.
  Supersedes `docs/015`'s "English only" framing.

## What was built

- **Asset conversion run**: `convert-assets.ps1 -Level airplane|viking1|
  share`, `convert-music.ps1` (whole `legacy/music/`, small). New
  `web/tools/build-audio-manifest.mjs` + `scripts/build-audio-manifest.ps1`
  (mirrors the image-manifest tooling) walk the **converted web output**
  (`web/public/assets/sound/**/sprite.json`), not raw `legacy/sound/**` -
  sound is sprite-packed, so "exists" has to mean "is in a built sprite",
  keeping `file_exists` correctly `false` for un-converted levels instead
  of resolving a path that would 404 at playback time.
- **`file_exists` is sound-aware** (`levelLoader.ts`, `levelScript.ts`):
  checks the new audio manifest for `"sound/..."` paths.
- **Language switch**: `levelScript.ts`'s 4 hardcoded dialog-file fetches
  (`shout_dialogs_en.lua` etc., `${levelName}/dialogs_en.lua`) become
  `_cs.lua`.
- **Real sound host bindings** (`levelScript.ts`): `sound_addSound`
  (name -> file[] registry, multi-variant), `sound_playSound` (resolves a
  random variant, queues it), `sound_playMusic`/`sound_stopMusic` (records
  the latest command each round), `model_talk` (real sound + real-duration
  subtitle timing when a voice clip resolved, falling back to the
  text-length formula otherwise - matching the original's actual
  behavior), `model_killSound` (cuts a model's current dialog, needed for
  `viking1`'s instrument-swapping NPCs). A shared path resolver
  (`sound/<dir>/<name>.ogg` -> `{spriteDir, region}`) works uniformly for
  built-in sounds, dialogs, and shared-pool dialogs.
- **Two real bugs caught during implementation** (not by planning):
  1. `level_dialog.lua`'s `DialogState.lang` is a Lua-local variable only
     ever set by the (deliberately bypassed) `dialogLoad()` - it stays
     `""` forever in this engine, so `dataPathSound()`'s own path
     computation is unusable (`sound/airplane//name.ogg`, malformed,
     never exists). Fixed by having `dialog_addDialog` ignore Lua's
     computed path entirely and recompute it in TS from a tracked
     "which dialog file is loading right now" prefix (matching each
     file's real `dialogLoad(prefix, soundPrefix)` call) + the real audio
     manifest.
  2. `sound_playMusic("music/rybky14.ogg")` passes the raw legacy path
     straight through - naively used as a cache key produced `/assets/
     music/music/rybky14.ogg.mp3` (double "music/", stray ".ogg"),
     surfacing as a real browser "Unable to decode audio data" error
     against whatever 404 page Vite returned. Fixed by stripping the
     `music/` prefix and `.ogg` suffix in the `sound_playMusic` binding
     itself, down to the bare basename `convert-music.ps1` names its
     output files with.
  3. (Design-time catch, not a runtime bug) `sound_playMusic()` fires from
     `code.lua`'s top-level `prog_init()` - during the async bootstrap,
     *before* the engine's first `tick()` ever runs. The original plan
     reset `pendingSoundEffects`/`pendingMusicCommand` at the *start* of
     `tick()`, which would have silently discarded that initial call
     before `LevelScene` ever got to read it. Fixed by switching to
     drain-on-read semantics (`getMusicCommand()`/`getPendingSoundEffects()`
     clear their own state when called, not `tick()`) - correctly captures
     anything set during bootstrap too.
- **Built-in impact/death sounds** (`Room.ts`/`GameEngine.ts`): new
  `lastImpact: Weight`, sourced from `Landslip.getImpact()` (reset every
  round regardless of which branch runs, so a round where nothing falls
  correctly reports `NONE`). `LevelScene.tick()` resolves+plays
  `impact_light`/`impact_heavy`/`dead_small`/`dead_big` through the same
  registry Lua's `sound_addSound` populates (via `level_creation.lua`,
  loaded for every level) - no Lua call site needed for these.
- **`web/src/scenes/AudioManager.ts`** (new): owns actual Phaser
  playback. Lazy-loads audio sprites/tracks on first use (asset needs
  aren't known until the async Lua bootstrap runs), serialized one load at
  a time so the loader's single `'complete'` event unambiguously belongs
  to the right request, deduped by key so a missing/un-converted asset is
  never retried (silent no-sound, matching the original's own
  missing-file fallback - never blocks gameplay). A generation counter
  (mirroring `LevelScene`'s own `scriptGeneration` guard) stops an
  in-flight load/play from a superseded pre-restart session resurrecting
  stale audio after resolving late. Volume defaults hardcoded to match the
  original's own (`volume_sound` 90%, `volume_music` 50%) - no options UI
  yet, nothing to hang one on.
- Dialog/NPC voice plays once per new dialog (identity-tracked, not
  replayed every round it's still showing).

## Verification

- Direct engine-driven tests: confirmed the bootstrap-timing music-command
  fix (airplane's initial `sound_playMusic` call is captured before any
  `tick()`); confirmed `impact_light`/`dead_small` resolve through the
  real registry with correct random-variant coverage over many draws;
  confirmed airplane's dialog resolves real Czech text *and* a real sound
  reference; confirmed `model_killSound` clears the right dialog only;
  confirmed `Room.lastImpact`/`GameEngine.lastImpact` fire on a real
  synthetic fall and reset to `NONE` afterward.
- `viking1` music-stop verified end-to-end over 800 driven rounds (no
  real-time waiting): initial track resolves (`rybky04`), the stop fires
  once within the expected 300-700-cycle random window, music never
  restarts afterward, and the NPC "band" plays real instrument audio in
  its place - exactly the mechanic the user asked about by name.
- Real-browser check on both `airplane` and `viking1`: confirmed music
  audibly plays (`AudioManager`'s internal state reports the right track
  + `isPlaying`), confirmed Phaser's autoplay-lock correctly unlocks on
  the first user gesture, zero console/page errors, rendering unaffected.
- Full regression suite (`docs/007`-`017`) re-run clean, including the
  existing dialog test (which now shows/asserts nothing English-specific -
  none of the scratchpad scripts hard-compare against literal English
  text, they just print whatever the engine produces, so nothing needed
  updating for the language switch itself; the dialog test's own output
  now shows real Czech text with a resolved sound reference).
- Update `CLAUDE.md`, memory.

## Open for next time

- Full ~80-level asset batch conversion (long-running offline job).
- Volume options UI / persisted settings.
- Runtime language switching (still one hardcoded language, now `cs`).
- Precise stop-on-completion for dialog audio (this port ends a subtitle
  by round-cycle countdown backed by the clip's real duration when known,
  not by listening for the actual audio element's completion event).
- `prog_border.lua`'s own `dialogLoad("script/share/border_", ...)` call
  (a second, differently-prefixed registration path some "final levels"
  use alongside `bordershout.lua`'s) isn't covered by the 4 pre-fetched
  sound-prefix categories - out of scope here since neither `airplane` nor
  `viking1` are final levels, but would need a 5th tracked prefix to work
  correctly for those that are.
