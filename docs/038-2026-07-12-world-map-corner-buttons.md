# 038 - World-map corner buttons: hover, Intro, Credits, Exit, Options

_2026-07-12_

The world map's 4 large corner buttons were left inert in docs/027. They work now,
matching the original (`legacy/src/menu/WorldMap.cpp`): top-left **Intro**, top-right
**Exit**, bottom-left **Credits**, bottom-right **Options**, each with a hover
highlight.

## Corner hover + dispatch (`WorldMapScene.ts`)

The original bg is a `LayeredPicture(map.png, map_lower.png, map_mask.png)`: `map_mask`
marks each button region with a distinct flat color, and hovering reveals the prelit
`map_lower` **only through that button's exact mask pixels** (not its bounding rect).
Ported faithfully: at `create()`, both `map_mask.png` and `map_lower.png` are drawn to
an offscreen `<canvas>` and read once (`getImageData`); the 4 image-corner colors give
the four buttons. For each button a canvas texture (`corner-<action>`) is built holding
only the `map_lower` pixels inside that button's mask shape, transparent everywhere else
(`buildCornerTexture` — `addCanvas`, replacing any stale key from a prior visit since
canvas textures aren't scene-scoped). A single overlay image sits over `map.png`
(hidden); on `pointermove` the mask color under the cursor is sampled, and if it's a
button the overlay's texture is swapped to that button's masked shape + shown (pointer
cursor). This reveals just the lit letter/icon outline, e.g. hovering the bottom-right
corner lights only the "OPTIONS" glyphs, not a rectangle around them. A `pointerdown`
over an active corner dispatches its action. Node clicks are unaffected (separate
interactive images; `activeCorner` is null over them).

## The four actions

- **Intro** → `IntroScene` (new). The original plays `images/menu/intro.mpg`; browsers
  can't play MPEG-1, so `intro.mpg` is transcoded once to H.264 `assets/video/intro.mp4`
  (ffmpeg) and played via a Phaser `Video` object. Esc / click / end returns to the map.
  (The launch is a user gesture, so audio autoplay is allowed.)
- **Credits** → `CreditsScene` (new): scrolls the game's own converted `credits.png`
  bottom-to-top; Esc / click / end returns. Legacy `PosterScroller`.
- **Exit** → `window.close()`. Browsers block closing a non-script-opened tab; when
  blocked it does nothing (no fallback notice, per the user). Legacy `quitState()`.
- **Options** → `OptionsOverlay` (new): a modal owned-UI panel (HelpOverlay shape) with
  a **language** row (Čeština / Nederlands), **music** + **sound** volume sliders, and a
  **subtitles** On/Off toggle. The original's speech/game-audio selector is omitted per
  the user (only cs/nl are converted). Back / Esc closes.

## Settings (`storage/settingsStorage.ts` new + wiring)

`ffwg:settings` localStorage record `{ lang: "cs"|"nl", musicVolume, soundVolume,
subtitles }` (defaults cs/50/90/on, matching AudioManager's old constants + docs/018).
Wired live:
- **Volume** → `AudioManager`: the old `GLOBAL_*_VOLUME` consts now read the setting
  live per play; `refreshMusicVolume()` updates the currently-playing track so an
  Options change is heard immediately.
- **Subtitles** → `LevelScene`: the docs/037 `subtitleStack.add()` drain is gated on
  `settings.subtitles` (voice still plays when off).
- **Language** → `levelScript.ts`: the `DIALOG_LANG` const became `getDialogLang()`
  (reads the setting), snapshotted per level load; the next level opened uses the chosen
  cs/nl for **both** subtitle text and voice (both fully converted). `demoScript.ts`
  reads it too, so the briefcase movie follows the setting. en fallback (docs/036)
  unchanged.

## Verification (dev server, temp `window.__game`, removed after)

- **Hover:** all 4 corners detected from the mask; hovering each reveals only that
  prelit button (screenshot: OPTIONS lit, others dark); overlay hides at center.
- **Dispatch:** Intro launches and the video actually plays (playback time advances),
  Esc returns; Credits scrolls and returns; Options opens/closes; Exit calls
  `window.close`.
- **Options wiring:** language → `levelDialogVoiceDir` becomes `airplane/nl` vs
  `airplane/cs`; subtitles off → a level dialog adds 0 subtitle entries (on → 1); volume
  settings persist; all in `localStorage` across reload.
- **Regression:** briefcase demo still constructs (demoScript touched); all-levels
  `createLevelScript` sweep clean; node hover/click + Pedometer still work.

## Files
- **New:** `web/src/scenes/{IntroScene,CreditsScene,OptionsOverlay}.ts`,
  `web/src/storage/settingsStorage.ts`, `web/public/assets/video/intro.mp4`.
- **Modify:** `web/src/scenes/WorldMapScene.ts` (corners + dispatch + Options),
  `web/src/scenes/AudioManager.ts` (settings volume), `web/src/scenes/LevelScene.ts`
  (subtitles gate), `web/src/lua/levelScript.ts` + `web/src/lua/demoScript.ts`
  (`getDialogLang`/language setting), `web/src/main.ts` (register the two new scenes).

## Notes
- The port's own English UI chrome ("Solved!", help) is out of scope for the language
  setting - it governs dialog/subtitle + voice language (cs/nl) only.
- The Options sliders are a small custom track+knob (no Phaser slider); volume icons
  from `images/menu/` were left for later polish (text labels used).
