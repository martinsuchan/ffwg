# 069 - F10 options on the world map + fit modals to narrow rooms

_2026-07-17_

Two bugs in the docs/067 in-level settings work, found by play-testing.

## 1. F10 didn't open settings on the world map

Legacy registers `KEY_MENU` (F10) on the **base** input state
(`StateInput` -> every screen, incl. the world map via `WorldInput`), so F10
opens the options menu everywhere. docs/067 only wired F10 in `LevelScene`.
Fixed: `WorldMapScene` now has a `keydown-F10` handler (`addCapture("F10")`, gated
on `!isModalOpen()`) that opens its existing `optionsOverlay` - the same panel the
bottom-right corner button opens.

## 2. The settings panel was clipped in tall/narrow rooms (library)

The overlays are drawn in room-world space and clipped by the room-sized camera
(docs/064). `OptionsOverlay`'s panel is a fixed 400x364; `library` is only 315px
wide, so the panel's left/right were cut off (labels read "nguage", "me size",
...). This can't be solved by a bigger canvas - at Standard 100% the whole canvas
is only 315px there - so the panel must **scale to fit** the room.

Fix: wrap the panel + controls in a Phaser **Container** and scale it down when
the room is narrower/shorter than the panel:
`s = min(1, (roomW - margin)/PANEL_W, (roomH - margin)/PANEL_H)`, `container
.setScale(s).setPosition(cx*(1-s), cy*(1-s))` (scales around the panel centre).
Wide rooms and the 640x480 world map keep scale 1. The full-room **backdrop stays
outside** the container (unscaled) so it always covers the room and absorbs
clicks. The sliders needed one change: their fraction now comes from the track's
**world-space** `getBounds()` (which includes the container's scale) + the
pointer's `worldX`, instead of local coords - so they stay correct when scaled.

`HelpOverlay` (F1) had the identical clip (its content-measured panel can exceed a
narrow room) and was already container-based, so the fix there is a one-line
`setScale(fit)` (no repositioning needed - its container is centred; no sliders).

## Verification (real browser)

- F10 opens the options panel on the world map (scale 1).
- `library` (315x555): options panel scales to 0.728, its objects' world bounds
  fit within the room (12..303 in [0,315]) - no clip (screenshot); the Subtitles
  toggle and sliders still respond to clicks/drags inside the scaled container.
- `library` F1 help scales to 0.746, bounds 8..307 - fits.
- e2e 7/7; tsc clean.
