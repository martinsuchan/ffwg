# 087 - The replay's controls outside the room; browser Back from replay and compare

2026-11-22

## What was done

- **The hint line and the media buttons no longer cover the level.** Before, the replay drew the "Esc = … · R = …" line in the room's top-left corner and the five media buttons along its bottom edge. In a small room (noground is 19 × 19 cells) they hid a good part of it.
  - `ReplayScene` now adds a band above the room for the hint line and one below it for the buttons. The canvas is the room plus both bands (`applyRenderScale` with the taller native size), and the camera scrolls by the top band's height so world (0, 0) is still the room's corner. Nothing in the room moves in world coordinates: models, ropes, the export's masks and the mirror shader (which already subtracts the camera scroll) are unchanged.
  - The top band fits the tallest of the lines it can show (the hint, "solved", "died"), each wrapped to the room's width. The room therefore never jumps when the line changes. The band is 34 px in most rooms, and 52 px in noground, where the hint wraps onto two lines. The bottom band is the button height plus a 6 px margin above and below (42 px).
  - The step counter stays in the room's top-right corner, because the MP4 export (078) shows it there. The bands are never in the video: the export frames only the room.
- **Browser Back leaves a replay or compare view for the level.** Before, Back from a replay opened with P skipped the level and went straight to the map. Back from the compare view (C) did nothing visible: the view wasn't a known sub-view, so the popped entry was lost and the next Back left the page.
  - `navigation.ts`: a level pushes its own history entry right before it starts either view (`pushLevelView`). The view registers a back handler in `create()` (`setBackHandler`), which is cleared on SHUTDOWN. On `popstate`, a registered handler runs; otherwise Back goes to the map as before. The handler does what Esc does, so the level is restored with its poster and depth.
  - When the view is left in-app (Esc, or compare's Back button), `leaveLevelView` drops its entry with `history.back()` and skips the resulting `popstate`. One Back then leaves the level, with no dead press in between.
  - A replay opened from the world map (Pedometer) still goes back to the map. Back during an MP4 export still aborts the export, because leaving the scene fires its SHUTDOWN listener (078, e2e 09).
- **Test:** e2e `14-replay-bands-and-back` checks the bands in the narrowest room (noground) and a large one (barrel): canvas = bands + room, the hint line and the buttons inside their bands, the step counter in the room. It also checks Back from replay and from compare to the level and then to the map, and that one Back leaves the level after Esc. The full suite passes, 14/14.

## Open for next time

- The compare view keeps its own layout (086). Its control strip was already outside the rooms.
- Leaving a level for the map with Esc still leaves the level's history entry behind (the drift the navigation module has always tolerated); only the two new views clean up after themselves.
