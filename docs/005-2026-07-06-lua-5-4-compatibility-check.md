# 005 - Lua 5.4 Compatibility Check

2026-07-06

## Goal

Close the last open risk flagged back in `docs/002`: do the legacy Lua
scripts (all 1,469 of them, written for Lua 5.0 circa 2004) actually work
under wasmoon's Lua 5.4, or does the port need a different Lua runtime
(fengari) or per-file rewrites?

## Options considered for validating compatibility

- **Parse every file with the real target runtime.** wasmoon's
  `Global.loadString()` compiles a chunk onto the Lua stack without
  executing it - verified empirically first (an `error()` call inside a
  probe string did not fire) before trusting it for a 1,469-file unattended
  batch run. This is the most authoritative syntax check possible, since
  it's literally the exact Lua 5.4 parser the game will ship with - not an
  approximation of it.
- **Static linters** (`luacheck` was the candidate) exist for deeper
  reference-level analysis (flagging undefined globals in code paths a pure
  parse wouldn't reach). Considered but not needed here: the actual Lua
  5.0->5.4 incompatibility surface is a small, known, finite list of
  renamed/removed stdlib functions, so a targeted `Grep` for those exact
  names across the corpus gives equivalent coverage for this specific
  question without adding a new toolchain dependency. Worth revisiting if
  broader Lua code-quality linting (unused vars, shadowing, etc.) becomes
  useful later.
- **Why both checks are needed:** parsing only catches grammar-level breaks
  (e.g. `goto` becoming a reserved word in 5.2+ would break any old script
  using it as a variable name). It does *not* catch calls to functions that
  are syntactically fine but no longer exist at runtime (`string.gfind`,
  `table.getn`, `setfenv`, ...) - those only fail when actually executed,
  which a compile-only check never does by design.

## What was done

- `web/tools/check-lua-compat.mjs` - walks a directory, and for every `.lua`
  file calls `wasmoon`'s `loadString()` (pop the stack after every attempt,
  success or failure - confirmed via probing that failure also pushes
  exactly one value) and records failures. `scripts/check-lua-compat.ps1`
  wraps it (installs `web/` deps if needed, forwards args) matching the
  existing scripts/ convention. Ran it against all of `legacy/script/`.
- Grepped the full corpus for the known finite list of Lua 5.0 stdlib
  surface removed/renamed by 5.4: `string.gfind`, `table.getn`/`setn`,
  `setfenv`/`getfenv`, `loadstring`, `module(`, bare `unpack(`. Then
  cross-checked every one of those names directly against the real wasmoon
  build (`type(table.getn)`, `type(loadstring)`, etc.) rather than trusting
  general Lua-version knowledge - confirmed `table.getn`, `loadstring`,
  global `unpack`, `string.gfind`, and `setfenv` are all genuinely `nil`
  under wasmoon (`_VERSION` reports `Lua 5.4`), while `load`,
  `table.unpack`, and `string.gmatch` are the working modern replacements.

## Results

Better than expected - the actual incompatibility surface is tiny:

- **1,468 / 1,469 files parse cleanly** under wasmoon's Lua 5.4 with zero
  changes.
- **1 real syntax failure:** `legacy/script/warcraft/dialogs_nl.lua:35` -
  `invalid escape sequence` for a `\/` inside a Dutch dialog string. `\/`
  has never been a valid Lua escape in any version; Lua's lexer became
  stricter about rejecting unrecognized escapes after 5.0, which tolerated
  it silently. This is a genuine decades-old content typo, not a
  version-migration decision.
- **`table.getn`** used at 8 call sites across 4 files, all under
  `legacy/script/share/` (`borejokes.lua`, `level_creation.lua`,
  `Pickle.lua`, `prog_finder.lua`) - all plain sequence-length lookups, no
  hint of Lua 5.0's manual-`.n`-field tricks, so `#t` is a safe drop-in.
- **`loadstring`** used once, in `Pickle.lua:76` (a public-domain Lua
  object-serialization library vendored into the game) to deserialize a
  saved-game string back into a table. `load` is a drop-in replacement for
  the string-argument case used here.
- **Not found anywhere in the corpus:** `string.gfind`, `setfenv`,
  `getfenv`, `module()`, bare global `unpack(`.

## Fix applied

Per `docs/003`'s decision to keep `legacy/` untouched as the single source
of truth, fixed both real issues with a **shim, not file edits**:
`web/public/lua/lua50-compat.lua` -

```lua
table.getn = table.getn or function(t) return #t end
loadstring = loadstring or load
```

Loaded once before any level/shared script runs. Verified at runtime (not
just "should work"): ran the shim then called `table.getn({10,20,30,40,50})`
(-> `5`) and `loadstring("return 99")()` (-> `99`) through the real engine;
also ran the actual `legacy/script/share/borejokes.lua` through wasmoon
after the shim and confirmed its `table.getn` calls no longer error - it now
fails later on an unrelated missing host function (`random`), which is
expected at this stage (the C++ host API hasn't been ported yet) and not a
Lua-version issue.

## Open for next time

- The one real syntax bug (`warcraft/dialogs_nl.lua`) still needs an actual
  fix, but per the "don't touch legacy/" policy that should be a small patch
  applied wherever Lua content gets copied into `web/` (not yet built - the
  Lua equivalent of the asset-copy step from `docs/003`), not an edit to
  `legacy/` itself.
- This only proves individual files parse/patch cleanly in isolation. It
  doesn't prove a full level runs correctly end-to-end with the real host
  API - that's the next real integration test once more of the TypeScript
  host bindings exist.
