# Backlog — feature status, FF NG comparison, and open items

_Recompiled 2026-07-18 from a full sweep of `docs/`, code comments, and CLAUDE.md
(supersedes the 2026-07-15 compile). Covers everything through docs/075._

The port is **feature-complete for normal Czech play**: all 80 real levels are
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
| All levels | 81 | 80 playable (`redhat` has no content in this repo) | ✅ (80/81) |
| `windoze` bonus (extra fish pair, output plugs, fast-fall) | yes | ported (docs/035) | ✅ |
| World map + progression gating | yes | derived from solved set (docs/027/045) | ✅ |
| Branch reveal on the map | whole map shown up front | **progressive per-house reveal** (docs/074) | ➕ deliberate deviation |
| Fish animation (swim/turn/idle/blink/push/talk) | yes | ported (docs/009/013/029/061) | ✅ |
| Item/decoration animation | Lua state machines | runs the real `code.lua` live (docs/014) | ✅ |
| Move recording / solution validation | yes | ported + headless validator (docs/021/022) | ✅ |
| Replay a solution | one fixed fast pace | **pause/step/speed controls** (docs/025) | ➕ |
| Save / load mid-level | 1 slot | **multi-slot** dot row (docs/026) | ➕ |
| Pedometer (best-solution screen) | yes | ported, masked art + digits (docs/039) | ✅ |
| Final-level posters + ending level | yes | ported (docs/050/061) | ✅ |
| Music (per-level, mid-level stops) | yes | ported (docs/018) | ✅ |
| Dialog voice + built-in SFX, polyphonic | yes | Web Audio engine (docs/018/043) | ✅ |
| Subtitles (per-speaker colour, stacking) | yes | ported (docs/037) | ✅ |
| Underwater bg ripple / screen shake / rope decor | yes | shader + offsets + rope (docs/055/056) | ✅ |
| Corpse disintegration | per-pixel dissolve | 14-round timing faithful, **alpha fade** visual | 🟡 |
| `mirror` / `zx` draw effects | 1 level each | recorded but drawn normally | ❌ (2 levels) |
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
play. Each cites the doc(s) that flagged it.

### A. Input (target set per CLAUDE.md: keyboard, mouse, gamepad, touch)
1. **Gamepad — not implemented.** No `input.gamepad` handling anywhere. (backlog §A)
2. **Touch — no dedicated scheme.** Pointer events partly cover it; no tap/drag
   scheme, no on-screen controls, unverified on a real device. (docs/017)

### B. Gameplay / mechanics
3. **Undo/redo — not ported.** The C++ undo path (`level_save`/`level_load`/
   `model_change_set*`/`model_getExtraParams`) is left unbound/dead — a latent
   freeze risk (docs/033 class) if undo is ever attempted. `Cube` snapshot/undo
   isn't modelled. (docs/026/054)
4. **`mirror` / `zx` per-pixel draw effects — not implemented** (submarine's
   screen reflection, the emulator colour-clash gag; 1 level each). Recorded but
   drawn normally. (docs/051, backlog §0)
5. **Corpse removal is a ~400 ms alpha fade**, not the per-pixel
   `EffectDisintegrate` dissolve. Timing/solidity faithful (14 rounds), visual is
   a stand-in. (docs/009/011)
6. **Swim speed-up is visual-only** (anim rate + slide), doesn't shorten real grid
   traversal like the original `PhaseLocker` — deliberate, from the fixed
   `CYCLE_MS` clock. (docs/017/046)
7. **Item decorative animation advances once per round**, not per fixed cycle, so
   it tracks movement speed slightly at variable round durations. Cosmetic. (docs/046)

### C. Content / levels
8. **`redhat` has no level content in this repo** — the only permanently unsolved
   node (80/81). Confirm whether it exists under another codename, or drop it.
   (docs/022/023)
9. **briefcase `demo_help.lua` full auto-play walkthrough** validated only to the
   graceful-abort path, never a full ~200-step run (its trigger is a mid-solve
   position impractical to reach in a test). (docs/031)

### D. World map / navigation / meta
10. **No "last played level" memory / auto-resume** — the map always boots fresh.
    (docs/026/027)

### E. Audio / dialog / subtitles
11. **Shipped language is `cs` only.** `nl` is fully built (text + per-line en
    voice fallback) but the switch is **hidden** (docs/075) and nl audio isn't in
    the deployed package (docs/073). The original ships ~15 languages via
    `select_lang.lua` (`dialogLoad()` is deliberately bypassed here). Re-enable
    `SHOW_LANGUAGE` + ship nl audio to restore it. (docs/015/018/038/073/075)
12. **No per-`(name,lang)` `ResDialogPack` fallback** — the en voice fallback is a
    targeted reproduction; country variants (`de_CH`→`de`→en) and the speech
    selector are not modelled. (docs/060)
13. **`prog_border.lua`'s border-shout sound prefix not covered** — final levels'
    border-shout clips would need a 5th tracked sound-prefix category. (docs/018)
14. **Subtitle wavy-text effect** not ported (a TODO even in legacy `Title`). (docs/037)
15. **Subtitle line-splitting simplified** — one word-wrapped entry per
    `model_talk`, not the original's per-line `Title` split. Visually equivalent.
    (docs/037)
16. **First dialog line of a big-sprite level pays a one-time voice decode** at
    level entry (hidden behind the load, never per-line). (docs/043)
17. **Decoded audio buffers cached for the whole session, no eviction** — fine for
    normal play. (docs/043)

### F. UI polish
18. **Options volume rows are sliders + text labels**, not the original's
    `images/menu/` volume glyphs. Labels are localized now (docs/073). (docs/038)
19. **Pedometer digit roll is a count-up tween**, not the per-digit "slot machine"
    animation. (docs/039)
20. **Save-slot dots:** `MAX_SAVES = 6` and the dot layout are first guesses (not
    measured per room width); can overlap a long subtitle on a narrow room. (docs/026)
21. **No persistent "selected fish" indicator** beyond the brief greet flash.
    (docs/016/017)
22. **No "are you sure?" before a restore reload** — non-destructive (merge), so a
    mistaken import can't lose data, but a confirm could be friendlier. (docs/072)

### G. Rendering / assets / build
23. **Multi-page texture atlas unsupported** → `menu/` and `demo_briefcase/` still
    ship as individual `.webp`. Off the hot path. (docs/004/042)
24. **`DemoScene` loads all ~293 briefcase movie frames up front** — fine for a
    one-time cutscene; lazy loading is a possible refinement. (docs/031)
25. **Build/publish scripts are Windows 11 only** — Linux/macOS out of scope. (docs/041)

### H. Deployment
26. **Azure Static Web Apps upload is bandwidth-limited.** The full package is
    ~184 MB; StaticSitesClient uploads it as one zip that must finish within a
    fixed 100 s timeout (≈125 MB at 10 Mbit/s), so it fails on a slow uplink. The
    working deploy **trims the Dutch audio to ~118 MB** to fit. To ship the full
    build: a faster uplink, or a GitHub Actions deploy (uploads from CI). (deploy
    session, 2026-07-18)
27. **The live Azure site is behind the working tree** — it has docs/073's cleanup
    but not docs/074 (progressive reveal), docs/075 (en-sprite gate, hidden
    language, loading screen), or the other latest fixes, pending the next deploy.
28. **Work is uncommitted** — docs/057–075 and their code changes are on the
    working tree only, not committed to git.

### I. Verification / tooling
29. **Gamepad/touch unverified** (not implemented). Fullscreen has a noted
    manual-browser check for real F11 in Edge. (docs/065/066)
30. **`test-binding-sweep` not promoted to a checked-in tool** — querying a live
    engine's globals for every host-style call guards the unbound-host-fn freeze
    class (docs/033). Worth adding to `web/tests/`. (docs/028/033)
31. **No unit-test layer** — only the Playwright e2e suite (`web/tests/`). (docs/044)

---

## 3. Resolved since the 2026-07-15 compile (docs/057–075)

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
