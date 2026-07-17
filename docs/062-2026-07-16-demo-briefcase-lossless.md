# 062 - Lossless WebP for the flat sprite art (demo_briefcase movie + fish)

_2026-07-16_

Two WebP-encoding changes from an asset-size/quality analysis. Both target
**flat cartoon/sprite art with hard edges**, where lossy WebP's block-noise is
most visible and lossless is cheap (or even smaller). The photographic level
backgrounds stay lossy Q90 - there lossless is ~3x larger for no visible gain.

## Background: the size analysis

All texture atlases, current lossy Q90 vs re-packed lossless:

| | atlas images |
|---|---|
| lossy Q90 (current) | 11.71 MB |
| lossless | 34.74 MB (~3x, +23 MB) |

The +23 MB lives almost entirely in the **photographic level backgrounds**
(barrel/grail/puzzle/briefcase balloon 3-4x). On visible pixels, Q90 there is
~1-2% mean error - imperceptible. So full lossless isn't worth it. But two
categories of **flat art** are a different story.

## 1. demo_briefcase movie frames (user-reported artifacts)

The briefcase movie (docs/031) renders 296 individual WebP frames as stacked
transparent layers; the user saw edge artifacts. Cause: the frames ship at the
pipeline's default **lossy Q85**, and lossy WebP compresses RGB in blocks that
straddle the hard alpha edges, so the illustration's colour bleeds into the
adjacent dark/transparent pixels - a dirty fringe once composited (measured
`demo_007`: mean RGB error 7.9/255 on visible pixels, **34% of channels off by
>8**; a 5x edge crop showed gold smeared into the dark navy background). The
alpha itself is fine - it's preserved losslessly (`alphaQuality` 100), and the
source alpha is binary (0/255).

The nice part: these flat frames compress *better* lossless, so it's a strict
win - **smaller AND artifact-free**:

| | size |
|---|---|
| lossy Q85 (was) | 2.89 MB |
| lossless | **2.40 MB** (-0.5 MB) |

## 2. Fish sprites

The fish atlases (4 shared variants: small/big/ex_small/ex_big) were the **worst
Q90 offenders** in the analysis - flat sprite art with hard outlines and smooth
shading, and unlike a background they're always on screen and animated, so the
player watches them closely (`fishes/small` measured 4% mean visible error, 40%
of channels off by >8). Cost of going lossless is trivial:

| | all 4 fish variants |
|---|---|
| lossy Q90 (was) | 93 KB |
| lossless | **135 KB** (+42 KB) |

## Fix

- `scripts/asset-tools/build-atlas.mjs`: opt-in `--lossless` flag (defaults off).
- `scripts/build-atlas.ps1`: `-Lossless` switch, passed through to the packer.
- `scripts/convert-assets.ps1`: builds the **fish** atlases with `-Lossless`
  (`Build-FishAtlas`) and converts **demo_briefcase** with `-Lossless`
  (`Convert-ImageDirIndividual`). Level atlases + `menu` stay lossy Q90/Q85
  (masks there are already forced lossless per-file - docs/039).
- Regenerated the local `demo_briefcase` + fish assets. The converted assets are
  gitignored (generated, not committed), so the script changes are what carry
  into publish/CI builds.

## Verification
- demo_briefcase: re-converted frames have **visible-pixel RGB maxDiff = 0** vs
  source (truly lossless where visible; the changed transparent-RGB is under
  `alpha=0`, invisible). 5x edge crop is pixel-identical to source. 2.89 -> 2.40 MB.
- fish: re-packed atlases have **visible-pixel RGB maxDiff = 0** (e.g.
  `fishes/small` body_swam_00). Total 93 -> 135 KB.
- Net asset change: demo_briefcase **-0.5 MB**, fish **+42 KB** -> overall
  slightly smaller, both artifact-free.
