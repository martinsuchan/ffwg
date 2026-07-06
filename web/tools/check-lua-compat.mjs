// Parses every .lua file under a directory with the real wasmoon/Lua 5.4
// engine (the exact runtime this project ships) and reports which files
// fail to compile.
//
// This is a *syntax*-only check: `Global.loadString()` compiles a chunk onto
// the Lua stack without executing it (verified empirically - an error()
// call inside the source did not fire), so it never runs arbitrary legacy
// script side effects. It will NOT catch calls to functions that exist
// syntactically but were removed from the language between Lua 5.0 and 5.4
// (string.gfind, table.getn, setfenv, ...) - those are valid syntax, they
// just resolve to nil at runtime. See check-lua-removed-apis in
// scripts/check-lua-compat.ps1 for that half of the picture.
//
// Usage: node check-lua-compat.mjs <sourceDir> [--report <path>]

import { LuaFactory } from "wasmoon";
import fs from "node:fs/promises";
import path from "node:path";

function parseArgs(argv) {
  const args = { report: null };
  const positional = [];
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--report") args.report = argv[++i];
    else positional.push(argv[i]);
  }
  args.sourceDir = positional[0];
  if (!args.sourceDir) {
    throw new Error("Usage: node check-lua-compat.mjs <sourceDir> [--report <path>]");
  }
  return args;
}

async function findLuaFiles(root) {
  const results = [];
  async function walk(dir) {
    const entries = await fs.readdir(dir, { withFileTypes: true });
    for (const entry of entries) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        await walk(full);
      } else if (entry.name.toLowerCase().endsWith(".lua")) {
        results.push(full);
      }
    }
  }
  await walk(root);
  return results.sort();
}

async function main() {
  const { sourceDir, report } = parseArgs(process.argv.slice(2));

  const files = await findLuaFiles(sourceDir);
  if (files.length === 0) {
    throw new Error(`No .lua files found under ${sourceDir}`);
  }

  const factory = new LuaFactory();
  const lua = await factory.createEngine();

  const failures = [];
  for (const file of files) {
    const source = await fs.readFile(file, "utf8");
    const relative = path.relative(sourceDir, file).replace(/\\/g, "/");

    try {
      lua.global.loadString(source, relative);
      lua.global.pop(1); // loadString always pushes exactly one value (chunk or error string)
    } catch (err) {
      lua.global.pop(1);
      failures.push({ file: relative, error: err.message });
    }
  }

  lua.global.close();

  console.log(`Checked ${files.length} files: ${files.length - failures.length} parsed OK, ${failures.length} failed.`);

  if (failures.length > 0) {
    console.log("\nFailures:");
    for (const f of failures) {
      console.log(`  ${f.file}: ${f.error}`);
    }
  }

  if (report) {
    await fs.writeFile(
      report,
      JSON.stringify({ checked: files.length, failed: failures.length, failures }, null, 2),
      "utf8"
    );
    console.log(`\nFull report written to ${report}`);
  }

  process.exitCode = failures.length > 0 ? 1 : 0;
}

main().catch((err) => {
  console.error(err.message);
  process.exitCode = 1;
});
