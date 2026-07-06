import { defineConfig } from "vite";

// Dev-only: lets the browser fetch legacy/ files (Lua scripts, images) directly
// via Vite's /@fs/ route, so the port can run the real legacy/script content
// without copying it into web/. Not used by `vite build` (production output
// still needs a real content-packaging step - see docs/005).
export default defineConfig({
  server: {
    fs: {
      allow: [".."],
    },
  },
});
