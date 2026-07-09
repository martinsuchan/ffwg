-- Compatibility shim for legacy Fish Fillets NG Lua content (written for
-- Lua 5.0) running under wasmoon's Lua 5.4. Load this once before any real
-- level/shared script runs.
--
-- Covers every removed/renamed stdlib call actually found in legacy/script/
-- (verified via web/tools/check-lua-compat.mjs + a targeted grep across all
-- 1469 files - string.gfind, setfenv, getfenv, module() and global unpack()
-- are not used anywhere in the corpus, so they're not shimmed here).
table.getn = table.getn or function(t) return #t end
loadstring = loadstring or load
-- math.mod (Lua 5.0's integer modulo, renamed math.fmod in 5.1+) wasn't
-- caught by check-lua-compat.mjs, since that tool only verifies parsing,
-- not execution - a missing global only surfaces when actually called
-- (see docs/024). Lua 5.1+'s `%` operator already implements the same
-- floored-division remainder Lua 5.0's math.mod used (unlike math.fmod,
-- which truncates instead - differs from math.mod for negative operands),
-- so this is exact, not an approximation.
math.mod = math.mod or function(a, b) return a % b end
