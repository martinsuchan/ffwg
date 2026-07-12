# 036 - viking1's musician band: no sound when the vikings sing/whistle

_2026-07-12_

User reported that in `viking1`, when the background music stops and the viking
band should start singing/whistling, nothing plays.

## Root cause

The band's instrument notes are triggered by `code.lua` calling
`model_talk(actor, "d1-z-p1" / "d1-z-b1" / "d1-z-v1" ...)` where `actor` is a
special *song code* (111/121/131), and the `d1-z-*` "dialogs" are **pure sound,
empty subtitle**. Two facts about how these are stored:

1. They're defined (via `dialogId("d1-z-b1", "", "")`) **only** in
   `dialogs_en.lua` / `dialogs_bg.lua` / `dialogs_ru.lua` — **not** in
   `dialogs_cs.lua`.
2. Their `.ogg` clips live **only** under `sound/viking1/en/` (the `en` dir is
   exactly these 8 instrument clips and nothing else).

The original's `level_dialog.lua` `dialogLoad()` registers every dialog under
the first language that defines it (its `DEFAULT_LANG`), so these always come
from English regardless of the chosen speech language — they're language-agnostic
instrument sounds. **This port bypasses `dialogLoad()` and loads only the
`DIALOG_LANG` (`cs`) file** (docs/015/018), so `d1-z-*` were never registered:
`model_talk` looked them up in `dialogRegistry`, found nothing, and returned
early → no sound. (It also broke the gag's timing: the block counter advances on
`not model_isTalking(PISKAC_SONG)`, which was always true, so blocks raced
silently.)

Even once registered, a second trap: `d1-z-*` have an **empty subtitle**, so
`model_talk`'s no-sound duration fallback would be `min(180, 0) = 0` cycles →
the "note" would be inactive the instant it started. The real clip length has to
come from the sprite.

## Fix (`web/src/lua/levelScript.ts`)

Reproduce the original's `DEFAULT_LANG` fallback: after running the localized
(`cs`) dialog file, also run the level's **`dialogs_en.lua` as a fallback**, with
`currentSoundPrefix = sound/<level>/en/`. Because `level_dialog.lua`'s `dialogId`
no-ops for an already-primed dialog (and the localized file uses the *same
English default subtitle* as its `dialogId` 3rd arg, so validation matches — no
warnings, no override), this registers **only** the dialogs the localized file
omitted (the `d1-z-*` clips), with their sounds resolved from `sound/<level>/en/`.
Localized dialogs are completely untouched. Also added `<level>/en` to the
`fetchSoundDurations` list so the song clips get a real duration (~7 s each) and
`model_talk`'s `minTime` is > 0. Playback loads the `<level>/en` sprite on demand
via the existing `AudioManager.playDialogVoice` path (docs/031).

This is general (all 81 levels load the en fallback), not a viking1 special-case,
and matches the original — any level with en-only sound-dialogs benefits.

## Verification (dev server, temp `window.__game`, removed after)

- **Songs register + play:** launched viking1, forced the band (`room.blok = 0`);
  all six `d1-z-*` clips register with `sound/viking1/en/*.ogg` + real durations,
  and the `viking1/en` sprite **actually plays** (`game.sound.getAllPlaying()`),
  with the whistle (piskac, actor 111) winning the dialog slot and pacing the
  blocks.
- **No regression:** a normal viking1 dialog (`dr-m-tojesnad`) still resolves to
  the Czech subtitle + `viking1/cs` sound; the en fallback only adds the en-only
  clips. All-levels `createLevelScript` sweep: **81/81 bootstrap clean**.

## Known limitation (documented, not fixed here)

The port keeps a **single** `activeDialog` slot and `playDialogVoice` cuts the
previous voice, so only **one** instrument is audible at a time (the whistle
dominates, since piskac is processed last and wins the slot each block). The
original plays all three parts simultaneously (independent dialog channels per
actor). Making the band a true trio would need multi-channel dialog audio — a
core dialog-system refactor touching every level — so it's deferred. The reported
bug (silence) is resolved: the vikings now audibly whistle/sing.
