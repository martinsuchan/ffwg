# 079 - Replay: still background, vector control icons

2026-11-16

## Why

- **Background:** after docs/078, the user asked how much the background ripple adds to the
  exported MP4. Measured with the ripple phase frozen against the animated one (same exports,
  music off), the ripple accounts for about 32–51 % of the file:
  `cannons` 9.0 → 4.4 MB, `viking1` 23.2 → 14.1 MB, `map` (first 2 min) 83 → 50 MB,
  `hole` 133 → 90 MB. Render time barely changes. The user chose to turn the ripple off in
  replay.
- **Icons:** the replay controls were font glyphs (⏸ ⏭ ▶ ⏩ ⬇), a stopgap from docs/025. Their
  look and weight depended on the OS font (the ⬇ added in 078 was visibly thinner).

## What was done

- **`ReplayScene`** builds its `WavyBackground` with `amp: 0`, which takes the existing
  plain-image path (no shader), and no longer calls `background.update()`. This applies to
  both the interactive replay and the MP4 export. The 078 "ripple on video time" determinism
  step is gone with it (`Recording.timeBase` removed). Live play (`LevelScene`) keeps the
  ripple.
- **New `web/src/scenes/IconButton.ts`:** a `Container` with a rounded 38×30 background and a
  vector icon drawn with `Graphics` (pause, step, play, fast-forward, download-into-tray).
  Because it's geometry rather than a texture or font, it stays sharp at every render scale.
  The selected play state is green (`#2a7a3a`, as before) and hover adds a gold outline
  (`#ffc618`, the panel accent). `ReplayScene.controlButtons` now holds `IconButton`s, and
  `setSelected()` replaces the text background swap.
- **Full exports with music** (Edge, GPU) after the change: `cannons` 4.7 MB (was 9.3),
  `hole` 91.5 MB (was 136), `map` 255 MB (was 424). The encoder still spends 2.7–5.4 Mbps on
  mostly static frames, so `videoBitrate()` remains the bigger lever if files need to shrink
  further.

## Verification

- `npx tsc -b`; e2e `09-replay-export` and `02-pedometer` pass.
- Real-browser check in Edge: icons render crisp at Large (1.5×), clicking ⏩ switches to
  fast and highlights it, hover outline shows; the replay background is static (no shader).

## Open for next time

- No tooltips on the icon buttons yet (the ⬇ export button in particular could use one).
- The hint line and step counter are still plain text boxes, styled as before.
