# 082 - Code review fixes: plan interrupt, local Lua WASM, parallel loading, mono audio

2026-11-17

## Why

A code review of `web/` (2026-11-17) found the physics port faithful to the C++ except for one
real gap (the original's `interruptPlan` was never ported) and one small start-of-level
deviation. It also found bugs in the PowerShell scripts, a failing e2e case 06 and stale docs.
Level loading had a lot of headroom: about 0.7 s for the loader engine plus 1.8 s for the live
engine per level, and 6.5 s from click to play. Lua itself took about 60 ms. The rest was 30+
**sequential** HTTP requests, plus a download of `glue.wasm` from **unpkg.com** on every
`new LuaFactory()`. Goal: fix everything cheap and low-risk, and log the larger refactors in
`BACKLOG.md`.

## What was done

### Physics / runtime fidelity

- **Plan interrupt (the real bug).** In the original, every death and every model that gets
  fully out interrupts the plan: `Room::prepareRound()` (`legacy/src/level/Room.cpp:257`) and
  `Room::fallout()` for `outDepth == -1` (`Room.cpp:281`). `Level::newDemo()` (`Level.cpp:561`)
  does the same. `LevelScript::interruptPlan()` (`LevelScript.cpp:59`) empties the plan FIFO
  (`Planner::interruptPlan` → `m_plan->removeAll()`, `Planner.cpp:76`), then runs
  `room->checkActive()` and `room->unBusyUnits()`. The port did none of this. A queued
  `planDialog` conversation went on after a fish died (the dead fish kept "talking"), and fish
  held busy by the script were never released.
  - `Room.ts`: on any death at the end of `prepareRound()`, and in `fallout()` for
    `outDepth === -1`, it calls `controls.checkActive()` + `unBusyUnits()` and sets a **sticky**
    `planInterruptPending` flag. The flag is OR-ed across rounds because
    `settleUntilGoingOut`/`loadMove`/`replayRound` run many `beginFall()` rounds in one call.
    `GameEngine` exposes `takePlanInterrupt()` (returns and clears the flag) and
    `unBusyUnits()`.
  - `LevelScript.interruptPlan()` clears the Lua plan FIFO (`pendingActions`, `frontCount`).
    This is the body of `game_killPlan` minus killing dialogs, because the original doesn't kill
    dialogs on an interrupt. The show queue (`showActions`) is left alone.
  - `LevelScene` / `ReplayScene` drain the engine flag into a scene-level pending flag and apply
    it **before** `levelScript.tick()`. It is also applied after a save-resume replay and when
    the booting script is assigned. A save-resume whose replayed moves include a death or an
    exit therefore clears the bootstrap plan, like legacy `loadMove`. `launchDemo()`
    interrupts unconditionally (plan, then `checkActive`, then `unBusyUnits`).
  - Known small deviation: on the briefcase demo path the plan is cleared after that round's
    script tick, not inside `level_newDemo`, so one round later than legacy.
- **Start-of-level greet.** `Controls` now starts with `switchPending = true`, like legacy
  `Controls::Controls()` `m_switch = true` (`legacy/src/level/Controls.cpp:28`). The first
  interactive round shows the activate pose and drives nothing. A queued key is not read and
  moves the fish on round 2. Replay, the validator, save-resume and shows go through
  `makeMove()` and are unaffected.

### Lua loading

- **Local `glue.wasm`.** New `web/src/lua/luaEngine.ts` `createLuaEngine()` is now the only place
  that constructs a `LuaFactory`. In a browser, wasmoon's `new LuaFactory()` with no URL points
  at `https://unpkg.com/wasmoon@<ver>/dist/glue.wasm`. That was an external CDN dependency,
  fetched 2-3 times per level load; a raw unpkg-backed factory took 5.9 s in one probe. The
  WASM is now bundled via `import ... from "wasmoon/dist/glue.wasm?url"` and emitted as
  `assets/glue-<hash>.wasm` (271,581 B). Each engine still gets its **own** factory and WASM
  instance on purpose: a corrupted WASM state (docs/008) must not leak into the next level's
  engine. All 4 call sites switched: `levelLoader`, `levelScript`, `demoScript`,
  `worldMapLoader`. Verified: zero external requests, in dev and in the published build.
- **Parallel fetch.** `createLevelScript()`, `loadLevelModels()`, `demoScript` and
  `worldMapLoader` fetch all their sources with one `Promise.all`. The `doString` order is
  unchanged.
- **Session cache.** `fetchLegacyFile()` and the new `fetchCompatSource()`
  (`/lua/lua50-compat.lua`) are memoized per session in a `Map<url, Promise<string>>`. A
  rejected entry is evicted, so a transient failure isn't cached. Legacy content is immutable
  within a deployment, so restarts (including the auto-restart after a death) and later levels
  reuse the `share/` files.
- Deleted dead code: `web/src/lua/luaPoc.ts` and `web/public/lua/sample.lua` (docs/002 POC). The
  `levelLoader.ts` header no longer talks about `/@fs/` or "production still open".

Load timings (headless Chromium, dev server, noisy):

| | before | after |
|---|---|---|
| Lua bootstrap of a new level (loader + live script) | ~2.5-2.7 s | ~0.9-1.3 s |
| Repeat / restart of a level (cached sources) | same as new | ~0.55 s |

The main remaining delay before a level is fully live is decoding the level's voice sprite,
because the script tick waits for it (docs/031/043). `gods` is an example.

### Tests

- **05** also sweeps host bindings, which closes the BACKLOG item "`test-binding-sweep`
  not promoted" (§I.30 in the 2026-07-18 compile). The names come from
  `registerFunc("…")` in `legacy/src/level/LevelScript.cpp`, `plan/Planner.cpp` and
  `gengine/Scripter.cpp`: 58 of them, checked as non-nil in every level's live engine right
  after bootstrap. An allowlist covers 8 undo-only/status functions the port doesn't bind:
  `level_save`, `level_load`, `model_change_setLocation`, `model_change_setExtraParams`,
  `model_getExtraParams`, `model_countAnims`, `model_getDir`, `status_readMoves`. A guard fails
  the case if the regex parses fewer than 40 names.
- **06** skips solutions that have no `legacy/script/<lv>/` and reports them as "no-level":
  `redhat` and `cabin1-simple`. The latter is a solver test variant. The solver work added it
  to `legacy/solution/` (commit `ceaf4e4`), and the solver's corpus still reads it from there,
  so it stays for now (open item below). 06 now passes with 80 solved and 2 no-level.
- **New 10-plan-interrupt** covers four things. The start greet. A death interrupts once and
  unbusies every model. A fully-out model interrupts. `LevelScript.interruptPlan()` empties a
  real plan FIFO (airplane).
- Suite **10/10 pass**. Speedups from parallel fetching and the source cache: 05 507 s → 52 s,
  06 204 s → 39 s, 09 49 s → 28 s.

### Scripts

- `publish.ps1` checks `setup.ps1`'s exit code. It was ignored before, so a failed asset prep
  could still be packaged. The unused `-Zip` switch is gone. `README.txt` now says sub-path
  hosting is **not** supported: runtime paths `/assets`, `/lua`, `/legacy` are absolute, and
  only `LEGACY_ROOT` honours `BASE_URL`.
- `setup.ps1 -NoRun` ends its success path with `exit 0`. On PowerShell 5.1, `$LASTEXITCODE`
  was left at -1 by the ffprobe version probe (its pipeline is cut short by
  `Select-Object -First 1`), which would have tripped publish's new check.
- `build.ps1` throws when `npm install` or `npm run build` fails, so `-Preview` no longer serves
  a stale `dist/`.
- `start.ps1` / `test.ps1` pass `--port $Port` to Vite. The `-Port` parameter used to be
  ignored.
- `new-doc.ps1` writes UTF-8 without BOM, with LF line endings, for both the new entry and the
  index line. Entries it created before, e.g. 080 and 081, start with a BOM.
- `web/tsconfig.tsbuildinfo` is untracked and gitignored.

### Audio: mono sprites

`build-audio-sprite.ps1` now writes **mono** sprites (`-ac 1`, gap silence `cl=mono`) and keeps
44,100 Hz on purpose. MP3 frame timing, and with it every region's start/end, stays the same,
and WebAudio resamples to the context rate anyway. All legacy sources are 22,050 Hz mono, so
the change is lossless relative to the source. All 196 sprites were regenerated. The
spritemaps' timing is byte-identical (0 s difference over 3,702 regions) and
`audio-manifest.json` is unchanged.

| | stereo | mono |
|---|---|---|
| `web/public/assets/sound` | 134.5 MiB | 98.7 MiB (-26.6 %) |
| `publish/` package (full, incl. nl audio) | ~184 MB | 144.9 MB (2,482 files) |
| Decoded PCM, every sprite (14,335 s at 48 kHz float32) | ≈ 5.2 GB | ≈ 2.6 GB |
| Decoded `gods` cs sprite | ~113 MB | ~57 MB |

The full package is still above the ~125 MB that fits in the Azure SWA 100 s upload at
10 Mbit/s, so the BACKLOG §H upload-limit item stays open, but the gap is smaller.

## Gotchas

- `convert-assets.ps1` skips outputs by mtime and doesn't notice changes to the conversion
  scripts themselves. After editing `build-audio-sprite.ps1`, delete `web/public/assets/sound`
  (or use `-Force`) to regenerate.
- `convert-assets.ps1` always wipes and repacks every image atlas on each run (existing
  behaviour, not changed here).
- On Windows PowerShell 5.1, a script that ends with `return` after a native command leaves
  that command's `$LASTEXITCODE` behind. Use an explicit `exit 0` when a caller checks it.

## Verification

- `npx tsc -b`; `scripts\test.ps1` 10/10.
- A load probe (scratch Playwright script) measured the timings above. It recorded **no
  external requests** (nothing to unpkg.com), in dev and against the `publish/` build.
- `scripts\publish.ps1 -SkipAssets` (exit 0, 2,482 files, 144.9 MB) + `web/tests/production-build.mjs`
  PASS against it; `assets/glue-<hash>.wasm` served as `application/wasm`.
- Not done: a manual play-through checking a dialog cut by a fish death, the start greet and
  the mono voice-over by ear - the e2e case 10 covers the engine/Lua halves only.

## Open for next time

All of these are in `docs/BACKLOG.md`:

- Merge the two Lua bootstraps per level load (one-shot loader + live script) into one engine.
- Prefetch the level's atlas and audio during the world-map "Loading" phase (docs/059/075).
- The voice-sprite decode gate is now the main load bottleneck on big-sprite levels.
- Evict decoded audio buffers (BACKLOG §E, numbers above).
- A shared `LuaFactory` was considered and rejected for now (WASM-state isolation, see above).
- `Cache-Control` headers in `staticwebapp.config.json`.
- `BASE_URL` support for every runtime asset path (sub-path hosting).
- Cache `loadSettings()` (it reads and parses localStorage on every call).
- Move `legacy/solution/cabin1-simple.lua` to `solver/`, with a fallback in the solver's corpus.
  It is solver test data, not original content.
