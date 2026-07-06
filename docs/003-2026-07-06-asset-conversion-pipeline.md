# 003 - Asset Conversion Pipeline

2026-07-06

## Goal

Decide how legacy's ~8,900 asset files (69M images, 92M sound, 8M music - see
`legacy/images`, `legacy/sound`, `legacy/music`) get into the web port without
duplicating them in git, and settle on concrete formats for Phaser: WebP
images, MP3 audio, with audio sprites for the many short per-level/per-language
sound clips.

## Decision: single source of truth, generated build output

`legacy/` stays untouched as the canonical, version-controlled source.
`web/public/assets/` is a **generated, gitignored** build artifact produced by
new `scripts/convert-*.ps1` scripts - not committed, reproducible from
`legacy/` + the scripts at any time. Rationale: git dedupes identical blob
content by hash, so raw duplication wouldn't bloat `.git` much, but the web
copy won't stay byte-identical anyway (format conversion), and a generated
artifact avoids drift risk entirely.

Also discovered while building this: `legacy/sound/<level>/` isn't flat SFX -
it's `<level>/<language>/*.ogg` (voice lines matching `dialogs_<lang>.lua`),
plus a separate `legacy/sound/share/` pool (and its own subfolders) of
generic reusable SFX not tied to any level. The sprite builder is
structure-agnostic (packs whatever directory it's pointed at) rather than
assuming a fixed layout, so it handles all of these uniformly.

## What was done

- Installed `ffmpeg` (via `winget install Gyan.FFmpeg`) - the only external
  dependency; it has `libwebp` and `libmp3lame` built in, plus `ffprobe` for
  duration probing. Confirmed the `audiosprite` npm package (the obvious
  off-the-shelf alternative) is stale (last published 2022, ancient
  sub-dependencies) and hand-rolled the sprite builder instead, so the whole
  pipeline is just PowerShell + ffmpeg - no Node/npm dependency, no ongoing
  Claude involvement needed to re-run it.
- `scripts/lib/ffmpeg-tools.ps1` - shared `Resolve-ToolPath` helper (falls
  back to winget's per-user Links folder if PATH hasn't refreshed yet).
- `scripts/convert-images.ps1` - recursive PNG/JPG/GIF/BMP -> WebP, preserving
  directory structure, skips already-up-to-date outputs unless `-Force`.
- `scripts/convert-music.ps1` - standalone per-track OGG -> MP3 for
  `legacy/music/` (not sprited - these are long-form background tracks).
- `scripts/build-audio-sprite.ps1` - packs every `.ogg` directly inside a
  given directory into one MP3 + a Phaser-format JSON spritemap
  (`{"resources": [...], "spritemap": {name: {start, end, loop}}}`, the exact
  shape `this.load.audioSprite()` expects). Algorithm: normalize each clip to
  44100Hz/stereo/PCM, generate a silence-gap WAV (default 0.5s, prevents
  playback bleed at clip boundaries), interleave via an ffmpeg concat-demuxer
  list, concatenate, encode the result to MP3. Sound key names come from the
  source filename (no extension), so they match the Lua scripts' existing
  `addm("...", "let-m-divna")`-style calls unchanged.
- `scripts/convert-assets.ps1` - orchestrator. Converts `legacy/images` and
  `legacy/music` wholesale; for sound, recursively walks every directory
  under `legacy/sound/` and builds one sprite for each directory that
  directly contains `.ogg` files (handles per-level/per-language folders and
  the shared pool, including `share/`'s own subfolders, uniformly). Supports
  `-Level <name>` to process just one level for quick iteration/testing.
- Added `web/public/assets` to `.gitignore`.

## Verified

- Ran `scripts/convert-assets.ps1 -Level airplane` end-to-end (real legacy
  data, not synthetic): 14 images converted (0.66 MB -> 0.08 MB, WebP quality
  85, ~88% smaller with no visible artifacts - eyeballed both a background
  plate and a transparent sprite frame), 2 audio sprites built (`cs` and `nl`
  language folders, 8 clips each).
- Confirmed alpha survives conversion (`ffprobe` reports `yuva420p` on the
  WebP output vs `rgba` on the source PNG) by visually inspecting a
  transparent sprite frame before/after.
- Confirmed the sprite JSON is correct: `sprite.mp3` duration (40.71s) matches
  the computed cumulative offsets exactly (last clip end 40.21s + 0.5s gap).
- Caught and fixed two silent-failure-prone details before they became bugs:
  the local machine's culture is `cs-CZ` (comma decimal separator) - verified
  `ConvertTo-Json` still emits period-decimal JSON regardless of session
  culture; and explicitly wrote the JSON with `UTF8Encoding($false)` rather
  than `Set-Content -Encoding utf8`, since Windows PowerShell 5.1 (unlike
  PS7+) adds a BOM under that encoding name, which some JSON parsers reject.
  Confirmed no BOM via raw byte inspection.

## Open for next time

- Only tested on one level (`airplane`, 14 images + 2 language sound
  folders). The full batch (~8,900 files across 80 levels) hasn't been run -
  it's a longer offline job, reasonably left for whenever asset work is
  actually needed rather than run speculatively now.
- Texture atlas packing (combining a level's images into one atlas + JSON,
  as discussed in `docs/`-adjacent brainstorming) is a separate, not-yet-built
  step - these scripts produce individual WebP files per image, not atlases.
- No decision yet on WebP quality trade-offs across asset *types* - 85 was
  fine for the one background/sprite pair inspected, but hasn't been checked
  against, e.g., dithered or noisy source images that might compress worse.
