# Backlog — feature status, FF NG comparison, and open items

_Recompiled 2026-11-17 as an incremental update of the 2026-07-18 full sweep of
`docs/`, code comments, and CLAUDE.md. Covers everything through docs/084._

The port is **feature-complete for normal Czech play**: all 81 levels are
solvable, with the world map, progression, save/load, replay, dialogs, voice,
music, subtitles, menus, fullscreen, and posters/ending all working. This
document tracks (1) how the port compares to the original FF NG, and (2) what's
still unfinished, skipped, stubbed, or deliberately simplified.

---

## 1. Feature comparison vs. Fish Fillets — Next Generation

Legend: ✅ full parity · ➕ port adds beyond the original · 🟡 partial/simplified ·
❌ missing.

| Area | FF NG | This port | Status |
|---|---|---|---|
| Puzzle physics (push/fall/death, weights, crush, escape) | yes | line-for-line TS port (`web/src/game/`) | ✅ |
| All levels | 81 | 81 playable (`redhat` imported from upstream data, docs/083) | ✅ |
| `windoze` bonus (extra fish pair, output plugs, fast-fall) | yes | ported (docs/035) | ✅ |
| World map + progression gating | yes | derived from solved set (docs/027/045) | ✅ |
| Branch reveal on the map | whole map shown up front | **progressive per-house reveal** (docs/074) | ➕ deliberate deviation |
| Fish animation (swim/turn/idle/blink/push/talk) | yes | ported (docs/009/013/029/061) | ✅ |
| Item/decoration animation | Lua state machines | runs the real `code.lua` live (docs/014) | ✅ |
| Move recording / solution validation | yes | ported + headless validator (docs/021/022) | ✅ |
| Replay a solution | one fixed fast pace | **pause/step/speed controls** (docs/025) | ➕ |
| Export a replay as video | no | **MP4 (H.264 + AAC) export from the replay** (docs/078–080) | ➕ |
| Save / load mid-level | 1 slot | **multi-slot** dot row (docs/026) | ➕ |
| Pedometer (best-solution screen) | yes | ported, masked art + digits (docs/039) | ✅ |
| Final-level posters + ending level | yes | ported (docs/050/061) | ✅ |
| Music (per-level, mid-level stops) | yes | ported (docs/018) | ✅ |
| Dialog voice + built-in SFX, polyphonic | yes | Web Audio engine (docs/018/043) | ✅ |
| Subtitles (per-speaker colour, stacking) | yes | ported (docs/037) | ✅ |
| Underwater bg ripple / screen shake / rope decor | yes | shader + offsets + rope (docs/055/056) | ✅ |
| Corpse disintegration | per-pixel dissolve | 14-round timing faithful, **alpha fade** visual | 🟡 |
| `mirror` / `zx` draw effects | 1 level each | ported (MirrorEffect / ZxEffect, docs/084) | ✅ |
| Undo / redo (`-`/`+`) | yes | not modelled (dead host fns) | ❌ |
| Menus: Options / Help / Intro / Credits | yes | all ported (docs/030/038) | ✅ |
| Settings: volume, subtitles toggle | yes | ported + persisted (docs/038) | ✅ |
| On-screen size (100/150/200%) + crisp text | fixed | **Game-size setting** (docs/064) | ➕ |
| Fullscreen | yes (F11) | native F11, aspect-preserving (docs/066) | ✅ |
| Keybindings (restart/help/save/load/steps/subs/menu/fullscreen) | yes | matched (docs/067/068) | ✅ |
| Localization | ~15 languages | **cs shipped; nl built but hidden** (docs/073/075) | 🟡 |
| Separate speech-vs-text language | yes | omitted (voice tied to text) | ❌ (intentional) |
| Input: keyboard + mouse | yes | ported (docs/016/017) | ✅ |
| Input: gamepad | yes | not implemented | ❌ |
| Input: touch | (SDL, n/a) | pointer partly covers it; no dedicated scheme | 🟡 |
| Backup / restore progress to a file | no | **JSON export/import, validated** (docs/072) | ➕ |
| Sandbox mode (all levels unlocked) | no | `/sandbox` endpoint (docs/045) | ➕ |

---

## 2. Open items

Rough priority within each group. Nothing here is a known crash in normal `cs`
play. Each cites the doc(s) that flagged it. Items are renumbered on each
recompile; a "was §X.N" in §3 refers to the previous compile's numbering.

### A. Input (target set per CLAUDE.md: keyboard, mouse, gamepad, touch)
1. **Gamepad — not implemented.** No `input.gamepad` handling anywhere. (backlog §A)
2. **Touch — no dedicated scheme.** Pointer events partly cover it; no tap/drag
   scheme, no on-screen controls, unverified on a real device. (docs/017)

### B. Gameplay / mechanics
3. **Undo/redo — not ported.** The C++ undo path (`level_save`/`level_load`/
   `model_change_set*`/`model_getExtraParams`) is left unbound/dead — a latent
   freeze risk (docs/033 class) if undo is ever attempted. `Cube` snapshot/undo
   isn't modelled. (docs/026/054)
4. ~~`mirror` / `zx` draw effects~~ — **done** (docs/084), see §3. Number kept
   until the next recompile.
5. **Corpse removal is a ~400 ms alpha fade**, not the per-pixel
   `EffectDisintegrate` dissolve. Timing/solidity faithful (14 rounds), visual is
   a stand-in. (docs/009/011)
6. **Swim speed-up is visual-only** (anim rate + slide), doesn't shorten real grid
   traversal like the original `PhaseLocker` — deliberate, from the fixed
   `CYCLE_MS` clock. (docs/017/046)
7. **Item decorative animation advances once per round**, not per fixed cycle, so
   it tracks movement speed slightly at variable round durations. Cosmetic. (docs/046)

### C. Content / levels
8. **`redhat` is text-only** — imported from the upstream `fillets-ng-redhat.zip`
   (docs/083), which ships no voice-over (`legacy/sound/redhat/` doesn't exist) and
   only cs + en dialogs (in nl it falls back to the en subtitles). Its map position
   (a secret branch off `emulator`, 245,283) is this port's choice - the zip hung it
   off `keys`. (docs/083)
9. **briefcase `demo_help.lua` full auto-play walkthrough** validated only to the
   graceful-abort path, never a full ~200-step run (its trigger is a mid-solve
   position impractical to reach in a test). (docs/031)
10. ~~`legacy/solution/cabin1-simple.lua` (solver test data inside `legacy/`)~~ —
    resolved: deleted; nothing read it (the solver enumerates `solver/levels/index.json`,
    which never listed it). Number kept so later item numbers stay stable. (docs/083)

### D. World map / navigation / meta
11. **No "last played level" memory / auto-resume** — the map always boots fresh.
    (docs/026/027)

### E. Audio / dialog / subtitles
12. **Shipped language is `cs` only.** `nl` is fully built (text + per-line en
    voice fallback) but the switch is **hidden** (docs/075) and nl audio isn't in
    the deployed package (docs/073). The original ships ~15 languages via
    `select_lang.lua` (`dialogLoad()` is deliberately bypassed here). Re-enable
    `SHOW_LANGUAGE` + ship nl audio to restore it. (docs/015/018/038/073/075)
13. **No per-`(name,lang)` `ResDialogPack` fallback** — the en voice fallback is a
    targeted reproduction; country variants (`de_CH`→`de`→en) and the speech
    selector are not modelled. (docs/060)
14. **`prog_border.lua`'s border-shout sound prefix not covered** — final levels'
    border-shout clips would need a 5th tracked sound-prefix category. (docs/018)
15. **Subtitle wavy-text effect** not ported (a TODO even in legacy `Title`). (docs/037)
16. **Subtitle line-splitting simplified** — one word-wrapped entry per
    `model_talk`, not the original's per-line `Title` split. Visually equivalent.
    (docs/037)
17. **Voice-sprite decode is now the main level-load bottleneck.** The live script
    tick waits until the level's dialog sprite is decoded, so the first line plays
    in sync. With the Lua bootstrap down to ~1 s (docs/082), that decode dominates
    on big-sprite levels (e.g. `gods`). It is paid once per level entry, never per
    line. Options: start the decode during the world-map "Loading" phase (§G.28),
    or decode the regions needed first. (docs/031/043/082)
18. **Decoded audio buffers are cached for the whole session, with no eviction.**
    Mono sprites (docs/082) halved the cost. Decoding every sprite would now take
    ≈ 2.6 GB at 48 kHz float32 (14,335 s of audio; ≈ 5.2 GB in stereo), and the
    `gods` cs sprite alone ~57 MB (was ~113 MB). Normal play decodes only the
    levels visited, but a long session keeps growing. Evict the buffers of levels
    the player has left, and keep the shared and current ones. (docs/043/082)

### F. UI polish
19. **Options volume rows are sliders + text labels**, not the original's
    `images/menu/` volume glyphs. Labels are localized now (docs/073). (docs/038)
20. **Pedometer digit roll is a count-up tween**, not the per-digit "slot machine"
    animation. (docs/039)
21. **Save-slot dots:** `MAX_SAVES = 6` and the dot layout are first guesses (not
    measured per room width); can overlap a long subtitle on a narrow room. (docs/026)
22. **No persistent "selected fish" indicator** beyond the brief greet flash.
    (docs/016/017)
23. **No "are you sure?" before a restore reload** — non-destructive (merge), so a
    mistaken import can't lose data, but a confirm could be friendlier. (docs/072)

### G. Rendering / assets / build
24. **Multi-page texture atlas unsupported** → `menu/` and `demo_briefcase/` still
    ship as individual `.webp`. Off the hot path. (docs/004/042)
25. **`DemoScene` loads all ~293 briefcase movie frames up front** — fine for a
    one-time cutscene; lazy loading is a possible refinement. (docs/031)
26. **Build/publish scripts are Windows 11 only** — Linux/macOS out of scope. (docs/041)
27. **Two Lua bootstraps per level load.** The one-shot loader engine
    (`loadLevelModels`) and the live `LevelScript` each run the shared scripts plus
    `models.lua`/`code.lua`. Merging them into one engine would cut the remaining
    Lua load time roughly in half. It is a larger refactor, because the loader's
    stub set differs from the live bindings. (docs/082)
28. **The level's atlas and audio are not prefetched during the world-map
    "Loading" phase.** Only the live engine is prewarmed there (docs/059/075). The
    texture atlas and sound sprites start loading when the scene starts. (docs/082)
29. **Each Lua engine gets its own WASM instance, by design.** A shared
    `LuaFactory` would save some instantiation work, but it was rejected for now:
    a WASM state corrupted by one engine (docs/008) must not leak into the next
    level. Revisit only together with a reset/recovery story. (docs/082)
30. **`loadSettings()` is uncached.** It reads and parses localStorage on every
    call (~20 call sites). This is cheap, but a per-session cache invalidated by
    `saveSettings` would be cleaner. (docs/082)

### H. Deployment
31. **Azure Static Web Apps upload is bandwidth-limited.** StaticSitesClient
    uploads the package as one zip that must finish within a fixed 100 s timeout
    (≈125 MB at 10 Mbit/s), so it fails on a slow uplink. With mono sound sprites
    (docs/082), the full package (incl. nl audio) is **144.9 MB** in 2,482 files,
    down from ~184 MB. That is closer, but still over the limit. The 2026-07-18
    deploy trimmed the Dutch audio to fit (~118 MB with the old stereo audio, not
    re-measured since). To ship the full build: a faster uplink, or a GitHub
    Actions deploy (uploads from CI). (deploy session 2026-07-18, docs/082)
32. **No `Cache-Control` headers in `staticwebapp.config.json`.** The hashed
    `assets/*` build output could be cached as immutable, and the converted assets
    and `legacy/` Lua for a long period. (docs/082)
33. **Sub-path hosting is not supported.** Runtime asset paths (`/assets`, `/lua`,
    `/legacy`) are absolute, and only `LEGACY_ROOT` honours Vite's `BASE_URL`. The
    publish `README.txt` says so (docs/082). Supporting it means routing every
    runtime path through `BASE_URL`.

### I. Verification / tooling
34. **Gamepad/touch unverified** (not implemented). Fullscreen has a noted
    manual-browser check for real F11 in Edge. (docs/065/066)
35. **No unit-test layer** — only the Playwright e2e suite (`web/tests/`). (docs/044)
36. **`convert-assets.ps1` skips by output mtime only.** It doesn't notice changes
    to the conversion scripts themselves: after editing e.g.
    `build-audio-sprite.ps1`, delete `web/public/assets/sound` or use `-Force`. Yet
    it always wipes and repacks every image atlas on each run. (docs/082)

---

## 3. Resolved since the 2026-07-18 compile (docs/076–084)

- **`mirror` and `zx` draw effects ported** (was §B.4), so every original draw effect
  is now reproduced. emulator's walls blink in ZX tape-loading stripes (a
  CanvasTexture port of `EffectZx`). submarine's mirror reflects the already-drawn
  screen to its left, fish included (a `CaptureFrame` plus a `Shader` placed right
  after the mirror, so UI is never reflected). It also works in replays and the MP4
  export. emulator, submarine, tetris and warcraft now use lossless atlases. The
  lossless WebP policy is in one place: `scripts/convert-assets.ps1`. New e2e cases
  11 and 12. (docs/084)

- **`redhat` playable — all 81 levels** (was §C8). Level content imported from the
  upstream `fillets-ng-redhat.zip` (+ its newer `worlddesc.lua`/`worldfame.lua`),
  placed as a secret branch off `emulator`; the bundled 287-move solution replays to
  Solved in the web port and the solver. (docs/083)
- **`legacy/solution/cabin1-simple.lua` removed** (was §C10) - a leftover of the solver
  editor work with no level content; e2e 06 now validates every bundled solution with
  nothing skipped. (docs/083)
- **Lua plan interrupt ported.** A death, a model fully out, or the start of a demo
  now clears the live Lua plan FIFO and releases busy fish, like legacy
  `LevelScript::interruptPlan`. A queued conversation no longer continues after a
  fish dies. Also new: the start-of-level greet (`m_switch = true`). (docs/082)
- **No runtime CDN dependency.** wasmoon's `glue.wasm` is bundled instead of fetched
  from unpkg.com, so the published site makes no external requests. Lua sources
  load in parallel and are cached per session: the Lua bootstrap of a new level
  went from ~2.5 s to ~1 s. (docs/082)
- **Binding sweep promoted** (was §I.30). e2e 05 checks that every level host
  function the original registers (58 of them) is bound in each level's live
  engine. (docs/082)
- **Mono sound sprites.** Lossless relative to the 22 kHz mono sources; `sound/` is
  26.6 % smaller and decoded audio memory is halved (§E.18 and §H.31 updated).
  (docs/082)
- **e2e 06 green again.** Solutions without level content (`redhat`,
  `cabin1-simple`) are skipped as "no-level". New case 10-plan-interrupt; the suite
  passes 10/10, and 05/06 run several times faster. (docs/082)
- **Script fixes.** `publish.ps1` stops on a failed `setup.ps1`, `build.ps1` stops
  on a failed build, `start.ps1`/`test.ps1` honour `-Port`, and `new-doc.ps1`
  writes no BOM. (docs/082)
- **Work committed** (was §H.28). docs/057–081 and their code are in git history
  (commits `ff8b20b`, `93f3ca9`, `00d4a3a`, `d7479ac`). docs/082's changes were
  still on the working tree when this was written.
- **Live site status** (was §H.27). The 2026-07-18 deploy ended up being commit
  `93f3ca9` (incl. docs/074–076 - §H.27's "behind" note predated it), tagged **`v1.0`**.
  Everything after it (docs/077+) is not live until the next `scripts\publish.ps1` +
  `swa deploy`.
- Also landed: replay MP4 export, with an AAC trial encode and a WASM AAC fallback
  (docs/078/080); the replay's still background and vector control icons
  (docs/079); fish and items swim out of the room instead of fading at the edge
  (docs/081); CLAUDE.md slimmed into per-tree files (docs/077).

### Resolved since the 2026-07-15 compile (docs/057–075)

For reference — these earlier "open" items are now **done**:

- **English UI chrome localized** (was §F24) — all custom strings now cs/nl via
  the labels layer (docs/073).
- **Dutch `\/` Lua parse crash** (was §C9) — patched at load time (docs/060).
- **Standard-mode ending auto-trigger** (was §D13) — runs after the final level
  when all solved (docs/061).
- **Full keybinding parity** — F1 help, F5 steps, F6 subtitles, F10 menu,
  Backspace restart, F11 fullscreen, Tab/Enter map nav, Space skip demo
  (docs/064/067/068).
- **Per-line English voice fallback** — Czech lines with no cs voice play the en
  clip (docs/060).
- **Game-size setting + crisp high-DPI text + fullscreen** (docs/064/066).
- **Progress backup/restore to a JSON file** (docs/072).
- **Live-engine prewarm during "Loading"** + world-map loading screen
  (docs/059/075).
- **En-sprite 404s eliminated** — sound-dir fetches gated on the manifest (docs/075).
- Plus: fractional/negative anim-phase crash fixes (docs/058/063), fullscreen
  robustness (docs/064–066), held-key/mouse-bounds input fixes (docs/070/071),
  ending flow + play-time + talk pose (docs/061), lossless flat-art (docs/062),
  wavy-bg edge fix (docs/057).

### Earlier settled items (pre-057) — see the 2026-07-15 compile in git history
Round pacing/fast falls (046/049), `output_*`/windoze (035), corner buttons +
Options + intro/credits (038), pedometer fidelity + Back (039/040), polyphonic
audio (043), asset conversion + atlas migration (027/028/042/048), sandbox mode
(045), fish talking anim (029), step counter (047), room waves (056), rope +
screen shift (055), Lua host-API audit part 1 (054).
