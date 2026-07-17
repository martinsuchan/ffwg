# 066 - Fullscreen: native F11, Esc stays fullscreen

_2026-07-17_

A one-decision follow-up to docs/065, from play-testing. **Supersedes docs/065's
issue 4** (which used the Fullscreen API so Esc would exit fullscreen).

## The change

docs/065 made Esc *exit* fullscreen (and guarded each scene's Esc handler so it
wouldn't also leave the level). Play-testing showed that's the wrong feel: the
player leaves a level with Esc constantly, and dropping out of fullscreen every
time is annoying. The wanted behavior: **F11 is the only fullscreen toggle; Esc
just does its normal in-game job (leave the level, close a popup) and stays
fullscreen.**

## Why this flips the mechanism back to native F11

The two fullscreen mechanisms differ exactly on Esc:

- **Fullscreen API** (docs/065's issue 4): exited by Esc, and *that exit can't be
  prevented* - browsers refuse to let a page trap you in fullscreen. So with the
  API, Esc always drops fullscreen, no matter what we do.
- **Native browser F11 fullscreen**: toggled only by F11, unaffected by Esc.

So the wanted behavior is only possible with native F11. Reverted to it:

- `fullscreen.ts` no longer captures any key or calls `requestFullscreen`/
  `exitFullscreen`. The browser's own F11 enters/exits; we only *react* to the
  `(display-mode: fullscreen)` media query (matches native F11) by sizing the
  container to the viewport and re-applying the active scene's render scale
  (FIT + fullscreen fit factor - the docs/065 layout fixes are unchanged and
  independent of the trigger).
- The Esc guards added in docs/065 (`if (isFullscreenActive()) return;`) are
  removed from `LevelScene`/`WorldMapScene`/`ReplayScene`, so Esc leaves the level
  normally. Native F11 fullscreen persists across the scene change (the media
  query stays matched, the container stays viewport-sized), and the new scene
  (the map) lays itself out fullscreen in its own `create()`.

This also drops the one un-testable risk from docs/065 (whether F11 +
`preventDefault` cleanly enters the API fullscreen in Edge vs. a native-F11
conflict): we no longer touch F11, so there's no conflict, and the black-screen
history (docs/064/065 issue 1) can't recur.

## Verification

- Real key press, simulated native fullscreen: Esc in a fullscreen level now
  leaves to the map (guard gone) AND stays fullscreen - the map re-lays-out at FIT
  1200x900, aspect preserved (still fullscreen, crisp).
- e2e 7/7 (windowed path unchanged); tsc clean.
- The aspect + crisp-text fixes (docs/065 issues 2, 3) are untouched and still
  verified. Real F11 enter/exit in Edge was confirmed working by the user before
  this change; native F11 is the same mechanism that rendered for them, now
  without the API layer.
