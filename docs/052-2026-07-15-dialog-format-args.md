# 052 - Dialog format args (`name@arg`) - gods' spoken coordinates

_2026-07-15_

## Why

Follow-up to docs/051. The two gods play battleships, but their **coordinate
announcements** ("G5.") showed no subtitle and played no audio.

## Cause

`gods/code.lua` builds those dialog names with an **`@`**:

```lua
planDialogSet(buh2.cekat, "b2-"..string.char(buh2.py-1+string.byte("a")).."@"..arg, 201, buh2, "mluveni")
```

so it talks `"b2-g@5"`. The dialog file registers `dialogId("b2-g", "font_cyan", "G%1.")` -
a **`%1` placeholder**, not the literal name.

Legacy `DialogStack::actorTalk()` splits the name on `@` into the real dialog
name + format args:

```cpp
StringTool::t_args args = StringTool::split(name, '@');
const Dialog *subtitle = m_dialogs->findDialogHard(args[0]);   // "b2-g"
subtitle->runSubtitle(args);                                    // "G%1." -> "G5."
const Dialog *dialog = m_dialogs->findDialogSpeech(args[0]);    // voice clip "b2-g"
```

`Dialog::getFormatedSubtitle()` replaces `%1`, `%2`, ... with args[1..] (every
occurrence; `%0` is never expanded). **Both the dialog and its voice clip are
found by `args[0]`; only the subtitle text is formatted.**

This port's `model_talk` looked the **raw** name up in its dialog registry -
`state.dialogRegistry.get("b2-g@5")` misses (registered as `"b2-g"`) and returned
early, so those lines produced no subtitle and no audio at all.

## Fix

New shared helpers in `web/src/lua/dialogSound.ts` (used by both talk engines):

- `splitDialogName(name)` -> `{ name, args }` (split on `@`).
- `formatSubtitle(subtitle, args)` -> `%N` substitution, mirroring
  `getFormatedSubtitle` (all occurrences, 1-based).

`model_talk` in `levelScript.ts` **and** `demoScript.ts` now look the entry up by
`args[0]` and render `formatSubtitle(entry.subtitle, args)`. `Dialog::getMinTime()`
measures the **raw** subtitle (`m_subtitle`), so the no-sound fallback keeps using
`entry.subtitle` unchanged - a real clip's length still wins (docs/018).

`@` is used by exactly two levels: **gods** (3 call sites - both gods' coordinates)
and **ending** (`room:talk("z-c-hodin@"..room.cas)` -> "it took you %1 hours!"), so
this also fixes the ending level added in docs/050.

## Verification (real browser, gods)

- `buh2:talk("b2-g@5")` -> subtitle **"G5."** in Neptun's cyan `#78ffff`.
- `buh1:talk("b1-c@10")` -> **"C10."** in Poseidon's yellow `#ffff80` (each god
  keeps its own font colour, docs/037).
- Voice clips resolve to the **base** name: `b2-g@5` -> region `b2-g`,
  `b1-c@10` -> `b1-c`, `b2-j@7` -> `b2-j`; no region ever contains a raw `@`.
- `b2-5` (the plain number half of a coordinate) stays correctly sound-only: no
  subtitle, still `isTalking` - the empty-subtitle case (docs/036).
- Screenshot: both gods announcing "C10." / "G5." stacked in their own colours.
- e2e suite 7/7; `tsc -b` clean.

## Files
- **Modify:** `web/src/lua/dialogSound.ts` (new `splitDialogName`/`formatSubtitle`),
  `web/src/lua/levelScript.ts` + `web/src/lua/demoScript.ts` (`model_talk` uses them).
