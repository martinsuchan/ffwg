import { LuaFactory, type LuaEngine } from "wasmoon";
// Vite emits node_modules/wasmoon/dist/glue.wasm into the build as a hashed
// asset and hands back its URL (wasmoon's package.json has no "exports" map,
// so the subpath import is allowed; `?url` typing comes from vite/client).
import glueWasmUrl from "wasmoon/dist/glue.wasm?url";

/**
 * The ONLY place in web/src that constructs a wasmoon `LuaFactory` - every
 * engine (level loader, live level script, demo/poster runner, world map)
 * comes from here. Do not call `new LuaFactory()` anywhere else.
 *
 * Why: `new LuaFactory()` with no argument, when running in a browser, points
 * its WASM loader at `https://unpkg.com/wasmoon@<version>/dist/glue.wasm`
 * (wasmoon dist/index.js, LuaFactory's constructor) - an external CDN fetch of
 * ~270 KB, 2-3 times per level load, and a hard runtime dependency on unpkg
 * being up. Passing the locally bundled glue.wasm URL keeps it same-origin
 * (dev server / publish/ assets) and lets the browser cache it.
 *
 * A FRESH factory - i.e. a fresh WASM instance - per engine is deliberate, not
 * an oversight: sharing one instance would save a little instantiation work,
 * but a WASM state corrupted by one engine (see web/CLAUDE.md: doString from a
 * host callback, docs/008) must not leak into the next level's engine.
 */
export function createLuaEngine(): Promise<LuaEngine> {
  return new LuaFactory(glueWasmUrl).createEngine();
}
