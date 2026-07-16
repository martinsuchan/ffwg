# 060 - Localization: per-line English audio fallback (+ a decode race, + a Dutch parse crash)

_2026-07-16_

Started from a bug report: opening `cancan` the first time after a fresh launch,
the piano music doesn't play; a restart or re-open fixes it. That turned out to
be an audio **decode race**, but tracing it exposed the real, broader gap the
user then asked to close: the port doesn't do FF NG's **per-line English audio
fallback**, so a partially-voiced language (nl especially) goes silent on any
line it has a subtitle but no localized voice clip for.

Three related fixes here.

## 1. The cancan decode race (`audioEngine.play` deferral)

`cancan`'s piano music is a *cycling* dialog voice: `code.lua` re-triggers
`klavir:talk("kan-klavir-music", ..., -1)` every round while `not
klavir:isTalking()`. The clip is registered **en-only** (`dialogs_en.lua`), so it
plays from `sound/cancan/en` - a dir the audio gate (`whenLoaded(<level>/cs)`)
doesn't wait on. Measured: the talker fired at ~251ms, but `cancan/en` didn't
finish decoding until ~510ms. `AudioEngine.play()` **no-op'd on the missing
buffer**, and because the talker still registers as "talking" (a cycling talker
never expires), the level's script never retried it - silent forever, until a
restart re-plays it from the now-cached buffer.

Fix: `play()` no longer drops a sound whose dir is still decoding. If a load is
in flight (`dirLoads` has it) it **defers the start until the buffer lands**;
only a dir that was never queued (truly missing) stays a silent no-op. Deferred
plays are tracked so `stopGroup`/`stopAll` (killSound / teardown) can cancel one
before it ever starts. Verified: piano source goes live at ~557ms (right after
the ~510ms decode), nothing dangling, restart still fine.

## 2. Per-line English audio fallback (the real feature)

FF NG stores every dialog per `(name, lang)` in a `ResDialogPack` and resolves at
runtime with two distinct fallbacks (`ResDialogPack.cpp`):

- **`findDialogHard`** (subtitle): current `lang` → `DEFAULT_LANG` ("en").
- **`findDialogSpeech`** (voice): the `speech` param (defaults to lang) → en **if
  the current-lang entry is missing OR `isSpeechless()`** - i.e. it has a
  translated subtitle but no localized `.ogg`.

The port already had *text* fallback (docs/036's two-file load: current lang,
then en for anything missing entirely) and *whole-dialog* audio fallback (en-only
clips like the viking band / cancan piano). The gap was the **per-line** case: a
line with a translated subtitle but no localized voice clip resolved to
`soundPath = ""` and played **silent**, where FF NG plays en's clip.

Fix (`dialog_addDialog`): when a dialog's localized clip is absent, fall back to
the en clip if the manifest has it - keeping the localized subtitle, swapping only
the audio. The en dir is derived by swapping the trailing lang segment of
`currentSoundPrefix` (works for the level's own dialogs and the shared
border/joke pools alike). `levelSoundSpriteDirs()` now also preloads the en
fallback pools (so those clips are decoded and playable), and `fetchSoundDurations`
reuses it so every fallback clip has a real duration for the subtitle timer.

This is the "targeted now, full later" scope the user chose: it reproduces
`findDialogSpeech`'s speechless→en behavior for the languages the port exposes
(cs/nl), without the parts deliberately dropped earlier - the separate `speech`
option (docs/038) and prefix language matching (`de_CH`→`de`→en) for country
variants the port doesn't offer. **Follow-up** if those are ever added: a faithful
`ResDialogPack` port storing per-`(name, lang)` entries and resolving at talk time
(findDialogHard for text, findDialogSpeech for audio, prefix matching).

**Result (nl sweep, 80 levels):** 187 lines now fall back to en audio (30 of them
*spoken*, with visible nl subtitles - e.g. `elk`'s Russian-accented moose), and
**0** lines are left silent while en has their clip. Before, all 187 were silent.

## 3. Dutch parse crash (`warcraft/dialogs_nl.lua`)

Testing nl surfaced a hard crash: `warcraft` failed to load with an invalid
escape at `dialogs_nl.lua:35` (`"...naar \/etc om..."`). `\/` is a Lua 5.0-ism
(a needlessly-escaped slash) that wasmoon's Lua 5.4 rejects outright - a content
bug that crashes the whole level in nl. A full-corpus scan found this is the
**only** genuinely parse-breaking invalid escape (`\[ \] \?` also appear, but
only inside a comment in `prog_goanim.lua`, which parses fine). Since we never
edit `legacy/`, `fetchLegacyFile` now patches `\/`→`/` on load - the same
"fix a content incompatibility at load" approach as the Lua 5.0 compat shim
(docs/005). warcraft now loads in nl.

## Verification (real browser)
- **cancan**: first-open piano music now plays (deferred start at ~557ms, decode
  ~510ms, race confirmed at talk 251ms < decode); no dangling deferred plays;
  restart still plays.
- **nl fallback sweep** (80 levels): 187 en audio fallbacks (30 spoken across 6
  levels); **0** missed (no line silent while en has its clip); warcraft loads
  (0 failures, was 1 before the `\/` fix).
- **No cs regression**: e2e **7/7** (default cs) incl. the 80-level live-tick
  sweep + 80/81 solution replays; `tsc -b` clean.

## Files
- **Modify:** `web/src/scenes/audioEngine.ts` (defer play until the dir decodes;
  `startSource` extracted; pending-play tracking + cancel in stopGroup/stopAll),
  `web/src/lua/levelScript.ts` (`dialog_addDialog` per-line en fallback;
  `levelSoundSpriteDirs` + duration fetch include en fallback pools),
  `web/src/scenes/LevelScene.ts` (drop the now-redundant manual `<level>/en`
  append), `web/src/lua/levelLoader.ts` (`fetchLegacyFile` patches `\/`→`/`).
