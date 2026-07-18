# 076 - Backlog refresh, FF NG feature comparison, README rewrite

_2026-07-18_

Documentation-only pass: brought the status docs up to date after ~20 milestones
(docs/057–075) had landed since the last backlog compile, and rewrote the
human-facing README, which still described the project as a pre-playable spike.

## `docs/BACKLOG.md` recompiled

Full re-sweep of `docs/`, code comments, and CLAUDE.md (supersedes the
2026-07-15 compile). Now has three parts:

1. **FF NG feature comparison matrix** - every major area (physics, levels, world
   map, animation, replay, save/load, audio, subtitles, effects, menus,
   fullscreen, localization, input) marked full parity / port-adds-beyond /
   partial / missing. Highlights: the port is at parity or beyond for normal
   Czech play; genuine gaps are gamepad + touch input, undo/redo, the
   `mirror`/`zx` per-pixel effects (2 levels), and shipping only Czech (nl is
   built but hidden).
2. **Open items**, regrouped and re-prioritised, with the docs/057–075
   resolutions removed and new items added (notably the §H deployment section:
   the SWA 100 s single-zip upload limit that forces the nl-audio trim, the live
   site being behind the working tree, and the uncommitted work).
3. **Resolved-since-2026-07-15** list, so the deltas are traceable (English UI
   chrome localized, Dutch `\/` parse crash, ending auto-trigger, full keybinding
   parity, en voice fallback, game-size + fullscreen, progress backup/restore,
   engine prewarm + loading screen, en-sprite gate, …).

## `README.md` rewritten

The old README said "early spike, not yet a playable game / nothing here is
playable yet". Replaced with an accurate overview: playable status (80/81
levels), a features summary, the full controls table, local run
(`scripts\setup.ps1`), publish + **Azure SWA deploy** steps (with the ~184 MB
upload-limit caveat pointing at BACKLOG §H), the e2e suite, the repo layout, and
license. Points readers at `docs/BACKLOG.md` for the feature status and open
items.

## Open for next time

- Docs-only change; no code touched, so no e2e/tsc run needed. The backlog's own
  §H27/§H28 still stand: the latest work (docs/074/075) isn't deployed and
  docs/057–075 remain uncommitted.
