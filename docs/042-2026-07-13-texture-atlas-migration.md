# 042 - Texture atlas migration (atlas-only model rendering)

2026-07-13

## What was done

Finished the texture-atlas migration first scoped in `docs/003`/`docs/004`: model
sprites now render from Phaser **texture atlases** instead of one `load.image()`
per animation frame, and the individual per-sprite `.webp` files are no longer
shipped for any atlased dir. `docs/004` built the packer (`scripts/asset-tools/
build-atlas.mjs`) but never wired it into the pipeline or the runtime - the only
trace was a single leftover `airplane/atlas.{webp,json}` test artifact, so that
one level was shipping its sprites **twice**.

Two atlas families, packed one atlas per source dir by `convert-assets.ps1`:

- **Per-level** (items + background): `legacy/images/<level>/**` ->
  `assets/images/<level>/atlas.{webp,json}`. 82 level dirs, each packs into a
  single 2048² page.
- **Shared fish** (loaded by every level): `legacy/images/fishes/<variant>/**`
  (`small`/`big`/`ex_small`/`ex_big`; `ex_*` are windoze's "old couple") ->
  `assets/images/fishes/<variant>/atlas.{webp,json}`. One atlas key per variant,
  so it loads once and cache-hits on every subsequent level.

Result: **3732 individual image files -> 510** (85 atlas pairs + the two
non-atlased dirs), `assets/images` **24 MB -> 18 MB**, and a level load now
fetches one atlas (+ the shared fish atlas, cached after first) instead of up to
~188 separate sprite requests - the original WebGL-batching / request-count goal.

### Atlas key + frame convention (`web/src/scenes/atlas.ts`, new)

`pictureToAtlas("images/<root>/<rest>.png")`:
- fish -> `{ atlasKey: "fishes/<variant>", frame: "<rest>" }` (e.g.
  `left/body_rest_00`, `heads/left/head_talking_00`)
- level -> `{ atlasKey: "<level>", frame: "<rest>" }` (flat, e.g. `letadlo-p`)

Frame name = picture path relative to the atlas source dir, minus the extension -
the **same** string `build-atlas.mjs` writes as the JSON-Hash frame key. The
atlas key doubles as the URL subpath (`/assets/images/<atlasKey>/atlas.webp`).
`atlasKey` is level-scoped, preserving the collision-safety `docs/028` added for
the old per-frame keys; the shared fish keys are deliberately global.

### Runtime changes

- `scripts/asset-tools/build-atlas.mjs`: now **recurses** subdirs (was single-dir)
  and names frames by relative path (was `basename`) - required for the nested
  fish tree, and it disambiguates the `left/body_0` vs `right/body_0` basename
  collision. Also guards `sharp().trim()` for sub-3×3 images (the extra fish have
  1×1 placeholder heads - trim throws below 3×3; pack them untrimmed, as the old
  per-file ffmpeg path did).
- `scripts/convert-assets.ps1`: image handling is now "atlas every level dir +
  each fish variant; individual webp only for the two non-atlased dirs (`menu/`
  world-map UI incl. its lossless masks, `demo_briefcase/` movie frames - both
  loaded through their own pathways, and both fail single-page packing anyway)".
  Each atlas build wipes its dest dir first so no stale individual sprites from a
  pre-atlas run linger to be published. `-Level <name>` still works (atlases that
  one level + the fish).
- `web/src/scenes/ModelAnimator.ts`: `resolveTextureKey`->`resolveFrame` (returns
  `{atlasKey, frame}` derived straight from the picture path in `anims`, so the
  old `levelName`/`index` key-synthesis args are gone from the class);
  `preloadModelFrames` (N `load.image`) -> `collectAtlasKeys` + `preloadAtlases`
  (a few `load.atlas`). Apply sites use `setTexture(atlasKey, frame)`.
- `web/src/scenes/sceneUtils.ts`: `resolveInitialTextureKey`->`resolveInitialFrame`.
  `pictureToAssetUrl` stays (still used for the individual `menu/`/`demo_briefcase/`
  loads in WorldMap/Credits/Pedometer/Demo).
- `web/src/scenes/LevelScene.ts` + `ReplayScene.ts`: preload atlases; create bg +
  model sprites from `(atlasKey, frame)`. **`applyBgChange` simplified** (docs/033):
  the corridor/rotate/steel runtime bg swap targets are always another image in
  the level's own dir, hence already in the level atlas - so it's now a
  synchronous `bgImage.setTexture(atlasKey, frame)` instead of the on-demand
  `load.image` + key-specific-event dance.

## Verified

- `tsc -b` clean; `npm run build` (vite) clean.
- Full `convert-assets.ps1` run: 82 level atlases + 4 fish atlases built, biggest
  real level `experiments` (188 sprites -> 953×1052) and `gods` (113 -> 1486×1418)
  both single-page; only `menu`/`demo_briefcase` stay individual (as intended).
- **Airtight frame-resolution proof** (no browser needed): a script asserts that
  for every atlased dir, the set of atlas JSON frame names **exactly equals** the
  set of source-PNG relative paths (minus ext). Since the runtime derives its
  frame requests by the identical relative-path rule (`pictureToAtlas`), set
  equality guarantees every model/background picture path pointing at a real PNG
  resolves to an existing atlas frame. All dirs matched.
- Dev server serves the atlas URLs the runtime computes (`/assets/images/
  airplane/atlas.{webp,json}`, `fishes/small`, `fishes/ex_small`, `corridor`,
  `windoze`) - all HTTP 200 with correct byte sizes.
- Phaser's JSON-Hash format + trim-offset positioning were already verified
  field-for-field against Phaser's own parser in `docs/004`; only the frame
  *names* changed here (Phaser frame keys may contain `/`), so that parsing/
  positioning logic is unchanged.

## Dev-mode world-map load fix (found while verifying)

Trying to eyeball the atlas change surfaced a **separate, pre-existing** bug (from
the docs/041 commit, not the atlas work): the world map failed with `Error:
[string "..."]:1: unexpected symbol near '<'` - a Lua parser choking on
`<!doctype html>`.

Cause: docs/041 rewrote `LEGACY_ROOT` (`levelLoader.ts`) to branch dev vs prod and
added `/* @vite-ignore */` to the dev branch's `new URL("../../../legacy/",
import.meta.url)` so `vite build` wouldn't try to bundle the whole `legacy/` tree
(the docs/006 glob problem). But `@vite-ignore` **also disables Vite's dev-mode
`/@fs/` rewrite**, so in dev the browser resolved `/legacy/...` against the module
URL and got the SPA `index.html` back (200 HTML) instead of the `.lua` file.
docs/041 only verified the production `publish/` build, so the dev regression
slipped through. (Prod was fine: there the dev branch is dead code, DCE-stripped -
the prod JS bundle is byte-identical before and after this fix.)

Fix - remove the fragile mechanism entirely so dev and prod use the **same**
`/legacy/` URL path:
- `web/vite.config.ts`: a small dev-only middleware (`serveLegacyDev`, `apply:
  "serve"`) serves the repo-root `legacy/` tree at `/legacy/...` as `text/plain`,
  with a guard keeping the resolved path inside `legacy/` (blocks `../`/`%2e%2e`).
  Only `.lua` is fetched from here at runtime (images/sound go through `/assets`).
- `web/src/lua/levelLoader.ts`: `LEGACY_ROOT` is now just
  `new URL(\`${import.meta.env.BASE_URL}legacy/\`, window.location.origin)` - no
  `import.meta.url`, no `@vite-ignore`, no dev/prod branch. Prod still relies on
  `publish.ps1` copying `legacy/script`+`solution` into the site (docs/041,
  unchanged).

Verified: dev server serves `/legacy/script/worldmap.lua` (+ models/code/solution/
share files) as real Lua 200; encoded traversal `/legacy/%2e%2e/...` -> 403;
`vite build` clean with an **identical** JS bundle hash (prod output unchanged) and
no legacy Lua bundled into `dist`.

## Open for next time

- **Interactive real-browser drive not done this session** (no browser-automation
  tool was available): render a heavy level + fish movement, a `game_changeBg`
  level (corridor/rotate), `windoze` (ex_* fish), and a replay, watching the
  Network panel for one-atlas-per-level + a cache-hit shared fish atlas and no
  404s. Static verification above covers frame resolution and URL wiring; this
  remaining step is visual/behavioral confirmation. (The dev server loads again
  after the world-map fix above, so this is now eyeball-able at localhost:5173.)
- `demo_briefcase/` (4 pages) and `menu/` (2 pages) still can't single-page pack;
  multi-page atlas support (deferred since `docs/004`) would let those be atlased
  too, but both load through their own pathways and aren't in the hot path.
