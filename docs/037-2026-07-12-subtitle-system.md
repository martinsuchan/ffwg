# 037 - Full-featured subtitle system (per-speaker colors, stacking, dismissal)

_2026-07-12_

The port's subtitle system was a deliberate placeholder from docs/015: one white
`Phaser.Text` at the bottom showing a single line at a time. The original gives
each speaker (fish/NPC/item) its own **color**, **stacks** several subtitles at
once (scrolling up), and **dismisses each on its own timer** a few seconds later.
This brings the port up to that.

## How the original works (legacy `src/plan/SubTitleAgent.*`, `src/widget/Title.*`)

Two decoupled systems: `DialogStack` tracks *running dialogs* (sound +
`model_isTalking` per actor); `SubTitleAgent` owns the *visual* subtitles,
independent of the sound (a subtitle keeps living after its clip ends). Colors are
**per font**: `dialog_addFont(name, r,g,b)` registers a font→RGB
(`SubTitleAgent::addFont`), and `level_fonts.lua`'s `loadFonts()` registers one per
speaker (`font_small`=255,197,102 orange = small fish; `font_big`=162,244,255 cyan =
big fish; per-NPC/per-viking colors). Each `dialogId(name, font, subtitle)` names a
font. `newSubtitle` spawns a `Title` at the bottom; each cycle all titles shift up
`TITLE_SPEED` and a new one pushes older ones up `TITLE_ROW`, so they stack (~5
rows). Each `Title` lives `utf8len*TIME_PER_CHAR(2) + bonus` cycles (min 40), then
`isGone()` and gets popped.

## What changed

### Colors (`web/src/lua/levelScript.ts`)
- `dialog_addFont` was a **no-op**; now it stores `state.fontColors: Map<font,
  "#rrggbb">` (new `rgbToHex`). `loadFonts()` already runs via `initModels()`, so
  all ~27 colors populate automatically. `colorForFont()` resolves a dialog's
  color, defaulting to `#ffffff` (matches `font_white`).

### Subtitle-stack feed (`web/src/lua/levelScript.ts`)
- `model_talk` still sets the single `state.activeDialog` (talking-state/sound/
  `dialog_isDialog` plan-gating — unchanged), but **also** pushes `{text, color}`
  onto a new `state.pendingSubtitles` when the subtitle is non-empty (empty
  "sound-only" dialogs like viking `d1-z-*` add nothing). New `takePendingSubtitles()`
  drains it, same pull pattern as `getPendingSoundEffects()`.

### Visual stack (`web/src/scenes/SubtitleStack.ts`, new)
- A port of `SubTitleAgent`/`Title`: a bottom-anchored stack of colored,
  outlined `Phaser.Text` lines (stroke, no background box — matches the original's
  `renderTextOutlined`). Newest at the bottom, older pushed up; each glides toward
  its stacked target (`SETTLE_SPEED`) giving the upward-scroll feel; each carries a
  lifetime (`utf8len*TIME_PER_CHAR + TIME_MIN`) and is removed when it expires;
  capped at `MAX_LINES` (5). Ticked on its own steady `TICK_MS`≈100ms timer, not the
  `ROUND_MS` round loop — matching the original's timer-driven `own_update`.
- `LevelScene`: the single `subtitleText` is gone; `create()` builds a
  `SubtitleStack`, `tick()` drains `takePendingSubtitles()` into it, `startEngine()`
  clears it on restart, `SHUTDOWN` destroys it. The post-solve return countdown now
  lengthens on `subtitleStack.hasVisible()` instead of `getActiveSubtitle()`.
- `DemoScene`'s own single narrator subtitle (briefcase movie) is untouched.

## Verification (dev server, temp `window.__game`, removed after)

- **Colors:** `viking1` — `state.fontColors` has 27 entries (`font_small`=#ffc566,
  `font_big`=#a2f4ff, `font_viking1`=#8080ff …). Forcing the intro dialogs, the
  small-fish line renders `#ffc566` (orange) and the big-fish line `#a2f4ff` (cyan).
  Screenshot confirms two outlined, differently-colored lines.
- **Stacking:** two `model_talk`s close together → 2 live stack entries at distinct
  Y, distinct colors, newest at the bottom.
- **Dismissal:** entry count drops over ~seconds as lines time out.
- **Regression:** all-levels `createLevelScript` sweep **81/81** clean; no subtitle
  `Text` leak across 5 restarts (count returns to 0 each time, never accumulates);
  normal levels show one correctly-colored line.

## Deferred (documented, out of scope)

- **Concurrent dialog *sound*** (multi-actor `DialogStack`): the single
  `activeDialog` + `playDialogVoice` still plays one voice at a time (docs/036's
  viking-band note). This work fixed the *visual* stack only; a true simultaneous-
  audio band needs a per-actor running-dialog model — a larger, separable follow-up.
  `model_isTalking` stays single-actor.
- The original's wavy-text effect (a TODO even in legacy `Title::drawOn`).
- Per-visual-line splitting: the port stacks one entry per `model_talk` (wrapped via
  `Phaser.Text` wordWrap) rather than the original's per-line `Title` split — visually
  equivalent, simpler.
