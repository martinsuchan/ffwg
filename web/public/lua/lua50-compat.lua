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
