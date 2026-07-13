import { defineConfig, type Plugin } from "vite";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const legacyRoot = path.join(repoRoot, "legacy");

// Dev-only middleware: serve the repo-root legacy/ tree at the URL path
// "/legacy/..." - the SAME path production uses (publish.ps1 copies legacy/script
// + legacy/solution into the deployed site under /legacy/). This lets both dev
// and prod resolve LEGACY_ROOT to a plain "/legacy/" (see levelLoader.ts), with
// no import.meta.url / @vite-ignore trickery. It replaces the older /@fs/ +
// new URL(import.meta.url) approach, whose @vite-ignore (needed so `vite build`
// didn't try to bundle the whole legacy/ tree) also silently disabled Vite's
// dev-mode /@fs/ rewrite - which fetched /legacy/*.lua as the SPA index.html and
// broke the world map with a Lua "unexpected symbol near '<'" parse error. Only
// .lua text is fetched from here at runtime (images/sound go through /assets);
// served as text/plain. See docs/042.
function serveLegacyDev(): Plugin {
  return {
    name: "serve-legacy-dev",
    apply: "serve",
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        const url = req.url;
        if (!url || !url.startsWith("/legacy/")) return next();
        const rel = decodeURIComponent(url.split("?")[0]).replace(/^\/+/, "");
        const filePath = path.join(repoRoot, rel);
        // Keep the resolved path inside legacy/ (block ../ or %2e%2e traversal).
        if (!filePath.startsWith(legacyRoot + path.sep)) {
          res.statusCode = 403;
          return res.end("Forbidden");
        }
        readFile(filePath)
          .then((data) => {
            res.setHeader("Content-Type", "text/plain; charset=utf-8");
            res.end(data);
          })
          .catch(() => {
            res.statusCode = 404;
            res.end("Not found");
          });
      });
    },
  };
}

export default defineConfig({
  plugins: [serveLegacyDev()],
  server: {
    // Pin the dev-server port so the origin (scheme+host+port) stays stable
    // across restarts. localStorage - where solved levels and saves live
    // (ffwg:solved:*/ffwg:saves:*) - is scoped per origin, so a drifting port
    // (Vite's default 5173 bumps to 5174/5175... when the port is taken) would
    // silently point the game at a different, empty storage bucket. strictPort
    // makes a busy port fail loudly instead of drifting and "losing" progress.
    port: 5173,
    strictPort: true,
    fs: {
      allow: [".."],
    },
  },
});
