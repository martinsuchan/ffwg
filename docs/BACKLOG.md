# Backlog — open items, deferrals, and simplifications

_Compiled 2026-07-15 from a full sweep of `docs/`, code comments, and CLAUDE.md._

A living list of everything still unfinished, skipped, stubbed, or deliberately
simplified. Items already resolved by a later doc are **not** listed (e.g.
round-pacing → docs/046/049, `output_*`/windoze → docs/035, corner buttons/options
→ docs/038, polyphonic audio → docs/043, full asset batch → docs/027/028). Each
item cites the doc(s) that flagged it. Rough priority in each section; nothing
here is a known crash in normal `cs` play.

---

## A. Input methods (target set per CLAUDE.md: keyboard, mouse, gamepad, touch)

1. **Gamepad support — not implemented at all.** No `input.gamepad` handling
   anywhere; keyboard + mouse only. Listed as a target input in CLAUDE.md.
2. **Touch input — no dedicated scheme.** Mouse/pointer (`pointerdown`) works and
   Phaser pointer events partly cover touch, but there's no tap-to-select /
   drag-to-follow path or on-screen controls, and touch behaviour is unverified
   on a real device. (docs/017 flagged this as the natural follow-up reusing the
   `MouseControl`/pointer-field path.)

## B. Gameplay / mechanics

3. **Undo/redo (`-`/`+` in the original) — not ported.** The C++ undo path
   (`level_save`/`level_load`/`model_change_set*`/`model_getExtraParams`) is left
   as unbound/dead host functions (docs/033), and `Cube` snapshot/undo isn't
   modelled. (docs/026)
4. **Corpse removal is a 400ms alpha fade**, not the original's per-pixel
   `EffectDisintegrate` dissolve. Timing/solidity is faithful (14 rounds), only
   the visual is a stand-in. (docs/009, docs/011)
5. **Swim speed-up is visual-only** (anim frame-rate + slide), it does *not*
   shorten real grid traversal like the original `PhaseLocker` does — a
   deliberate consequence of the fixed `CYCLE_MS` clock. (docs/017, docs/046)
6. **`model_equals` is approximated** as a single-anchor-position match, since
   this engine has no access to the real multi-cell `Field` grid. (docs/028)
7. **Item decorative animation advances once per round**, not per fixed cycle, so
   at variable round durations it tracks movement speed slightly. Cosmetic. A
   fixed-cycle Lua pass is a possible refinement. (docs/046)

## C. Content / levels

8. **`redhat` has no level content in this repo** — the only permanently unsolved
   node (80/81). Confirm with whoever sourced the solutions whether it exists
   under another codename, or drop it. (docs/022, docs/023)
9. **`warcraft/dialogs_nl.lua:34` has an invalid Lua `\/` escape** (`\/etc`).
   Now *reachable*: Dutch became a selectable language in docs/038, so loading
   warcraft in `nl` would hit a Lua parse error. Needs a content patch applied
   wherever Lua is served (not by editing `legacy/`). (docs/005)
10. **briefcase `demo_help.lua` full auto-play walkthrough** is only validated to
    the graceful-abort path, never a full ~200-step run — its trigger is a
    mid-solve position that's impractical to reach in a test. (docs/031)

## D. World map / navigation / meta screens

11. ~~**Ending / recap "poster" screens**~~ **DONE (docs/050).** Final-level
    posters play after solving/replaying; the ending level auto-runs when all
    solved (standard) or via a sandbox button.
12. **No "last played level" memory / auto-resume** — the map always boots fresh.
    (docs/026, docs/027)
13. **Standard-mode auto-trigger of the ending once everything is solved** is out
    of scope. (docs/027, docs/045)

## E. Audio / dialog / subtitles

14. **Dialog language coverage is only `cs` + `nl`.** The original ships ~15
    languages via `select_lang.lua`/`select_speech.lua`; `dialogLoad()` is
    deliberately bypassed here. (docs/015, docs/018, docs/038)
15. **No separate speech-vs-text language selector** — voice is tied to the text
    language. Intentionally omitted per the user. (docs/038)
16. **`prog_border.lua`'s `dialogLoad("script/share/border_", …)` sound prefix is
    not covered** — final levels' border-shout clips would need a 5th tracked
    sound-prefix category. (docs/018)
17. **Subtitle wavy-text effect** not ported (a TODO even in legacy
    `Title::drawOn`). (docs/037)
18. **Subtitle per-visual-line splitting simplified** — one stacked entry per
    `model_talk` (word-wrapped) rather than the original's per-line `Title`
    split. Visually equivalent. (docs/037)
19. **First dialog line of a big-sprite level pays a one-time voice-sprite decode
    at level entry** (hidden behind the load, never per-line). Splitting voices
    into per-clip files would remove even that, at the cost of many small files.
    (docs/043)
20. **Decoded audio buffers are cached for the whole session with no eviction** —
    fine for normal play; revisit only for very long sessions. (docs/043)

## F. UI polish

21. **Options volume icons are text labels** — the `images/menu/` volume glyphs
    were left for later. (docs/038)
22. **Pedometer digit roll simplified** to a count-up tween, not the original's
    per-digit "slot machine" animation. (docs/039)
23. **Save-slot dots:** `MAX_SAVES = 6` and the dot layout are first guesses (not
    measured per room width), and can visually overlap a long subtitle line on a
    narrow room. (docs/026)
24. **English UI chrome ("Solved!", help text) is not localized** — these are the
    port's own strings; the language setting governs dialog/subtitle/voice only.
    (docs/038)
25. **No persistent "which fish is selected" indicator** beyond the brief greet
    flash. (docs/016, docs/017)

## G. Rendering / assets / build

26. **Multi-page texture atlas unsupported** → `menu/` (2 pages) and
    `demo_briefcase/` (4 pages) still ship as individual `.webp` rather than
    atlased. Both load through their own pathways and aren't in the hot path.
    (docs/004, docs/042)
27. **`DemoScene` loads all 293 briefcase movie frames up front** — fine for a
    one-time cutscene; lazy per-frame loading is a possible refinement. (docs/031)
28. **Build/publish scripts are Windows 11 only** — Linux/macOS out of scope.
    (docs/041)

## H. Verification / tooling debt

29. **Interactive real-browser drive checklists not formally run** for the atlas
    migration (docs/042: one-atlas-per-level network check, `game_changeBg`,
    windoze ex_* fish) and the polyphonic-audio engine (docs/043: viking1 band
    notes together, overlap, teardown). Some covered ad hoc since; not signed off
    against the written checklist.
30. **`test-binding-sweep` not promoted to a checked-in tool.** Querying a live
    engine's globals for every host-style call guards the whole unbound-host-fn
    freeze class (docs/033) and mirrors the docs/028 sweep — worth adding to
    `web/tests/` or `web/tools/`.
31. **No unit-test layer** — only the Playwright e2e suite (`web/tests/`, docs/044).

---

### Explicitly settled (not open) — for reference

Undo left aside, these earlier "open" notes are **done**: round pacing
(046/049), fast falls (049), `output_*`/windoze/extra fish (035), corner buttons
+ Options + intro/credits (038), pedometer fidelity + clean-map + Back (039/040),
polyphonic/simultaneous dialog audio (043), volume/subtitle settings + persistence
(038), full ~80-level asset conversion (027/028), atlas migration (042), the
all-solutions validator becoming the e2e suite (044), standard vs sandbox mode
(045), fish talking animation (029), step counter (047), atlas opaque-crop (048).
