import { defineConfig } from "vite";

// Dev-only: lets the browser fetch legacy/ files (Lua scripts, images) directly
// via Vite's /@fs/ route, so the port can run the real legacy/script content
// without copying it into web/. Not used by `vite build` (production output
// still needs a real content-packaging step - see docs/005).
export default defineConfig({
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
