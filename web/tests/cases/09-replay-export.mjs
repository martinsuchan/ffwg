// Replay -> MP4 video export (docs/078): the 16:9 frame layout for every room,
// a capped export of the `cannons` reference solution parsed back with
// mediabunny (codec, size, duration, fast start, audio track per the music
// toggle, black bars / non-black room in a decoded frame), the replay scene
// restored to normal after each export, and browser Back mid-export aborting
// cleanly to the world map.
//
// Headless Chromium renders through SwiftShader at ~100 ms/frame, so every
// export here is capped with runExport's `maxFrames`. Encoded MP4 bytes differ
// between runs (browser H.264 encoder) - never compare files byte-wise.

import { readdirSync, readFileSync } from "fs";
import { fileURLToPath } from "url";
import { dirname, join } from "path";
import { gotoWorldmap, assert } from "../lib.mjs";

export const name = "replay export: 16:9 layout, MP4 output, restore, Back mid-export";

const LEVELS_DIR = "../../../solver/levels"; // cases -> tests -> web -> repo root
const GRID = 15; // px per cell (legacy View.h SCALE)
const EXPORT_TIMEOUT_MS = 45000;

/** Room sizes in px for every exported level (incl. the -simple variants). */
function roomSizes() {
  const dir = join(dirname(fileURLToPath(import.meta.url)), LEVELS_DIR);
  return readdirSync(dir)
    .filter((f) => f.endsWith(".json"))
    .map((f) => JSON.parse(readFileSync(join(dir, f), "utf8")))
    .filter((j) => typeof j.width === "number" && typeof j.height === "number") // skips index.json
    .map((j) => ({ name: j.name, w: j.width * GRID, h: j.height * GRID }));
}

/** In-page snapshot of the state an export must restore. */
async function snapshot(page) {
  return page.evaluate(() => {
    const g = window.__game;
    const c = g.canvas;
    const s = g.scene.keys.replay;
    const buttons = s && s.controlButtons ? [...s.controlButtons.values()] : [];
    return {
      running: g.loop.running,
      replayActive: g.scene.isActive("replay"),
      worldmapActive: g.scene.isActive("worldmap"),
      visibility: c.style.visibility,
      position: c.style.position,
      w: c.width,
      h: c.height,
      cssW: c.style.width,
      cssH: c.style.height,
      buttons: buttons.length,
      buttonsVisible: buttons.length > 0 && buttons.every((b) => b.visible),
      panels: [...document.querySelectorAll("body > div")].filter((d) => d.style.zIndex === "10000").length,
      recording: !!(s && s.recording),
    };
  });
}

function assertRestored(label, now, before) {
  assert(now.running === true, `${label}: game loop not running after export`);
  assert(now.replayActive, `${label}: replay scene not active after export`);
  assert(now.visibility !== "hidden", `${label}: canvas still hidden`);
  assert(now.position === before.position, `${label}: canvas position "${now.position}" != "${before.position}"`);
  assert(now.panels === 0, `${label}: export panel left in the DOM (${now.panels})`);
  assert(!now.recording, `${label}: scene still in record mode`);
  assert(
    now.w === before.w && now.h === before.h && now.cssW === before.cssW && now.cssH === before.cssH,
    `${label}: canvas ${now.w}x${now.h} (css ${now.cssW}x${now.cssH}) != pre-export ${before.w}x${before.h} (css ${before.cssW}x${before.cssH})`,
  );
  assert(now.buttonsVisible, `${label}: control buttons not visible (${now.buttons} buttons)`);
}

/** Runs one capped export in-page and inspects the Blob with mediabunny. */
async function exportAndParse(page, opts) {
  return page.evaluate(
    async ({ opts, timeoutMs }) => {
      // The app's own (Vite-rewritten) mediabunny URL - a bare specifier
      // doesn't resolve in evaluated code.
      const src = await (await fetch("/src/scenes/replayExport.ts")).text();
      // (the core package - not the @mediabunny/aac-encoder extension, docs/080)
      const mbUrl = (src.match(/import\(["']([^"']*\/mediabunny\.js[^"']*)["']\)/) || [])[1]
        ?? "/node_modules/.vite/deps/mediabunny.js";
      const mb = await import(mbUrl);

      const scene = window.__game.scene.keys.replay;
      const started = performance.now();
      const result = await Promise.race([
        scene.runExport(opts),
        new Promise((_, reject) => setTimeout(() => reject(new Error("runExport timed out")), timeoutMs)),
      ]);
      const exportMs = Math.round(performance.now() - started);
      const blob = result.blob;

      // Top-level MP4 box order (fast start = moov before mdat).
      const bytes = new Uint8Array(await blob.arrayBuffer());
      const view = new DataView(bytes.buffer);
      const boxes = [];
      for (let off = 0; off + 8 <= bytes.length && boxes.length < 32; ) {
        let size = view.getUint32(off);
        const type = String.fromCharCode(...bytes.subarray(off + 4, off + 8));
        if (size === 1) size = Number(view.getBigUint64(off + 8));
        else if (size === 0) size = bytes.length - off;
        boxes.push(type);
        if (size < 8) break;
        off += size;
      }

      const input = new mb.Input({ source: new mb.BlobSource(blob), formats: mb.ALL_FORMATS });
      const video = await input.getPrimaryVideoTrack();
      const audio = await input.getPrimaryAudioTrack();
      const out = {
        exportMs,
        frames: result.frames,
        width: result.width,
        height: result.height,
        resultAudio: result.audio,
        musicSegments: result.musicSegments.length,
        bytes: bytes.length,
        boxes,
        mime: await input.getMimeType(),
        video: video
          ? {
              codec: video.codec,
              w: video.displayWidth,
              h: video.displayHeight,
              duration: await video.computeDuration(),
              canDecode: await video.canDecode(),
            }
          : null,
        audio: audio ? { codec: audio.codec, channels: audio.numberOfChannels, rate: audio.sampleRate } : null,
        pixels: null,
      };

      if (video && out.video.canDecode) {
        const sink = new mb.CanvasSink(video);
        const wrapped = await sink.getCanvas(2.0);
        if (wrapped) {
          const c = wrapped.canvas;
          const ctx = c.getContext("2d", { willReadFrequently: true });
          const px = (x, y) => [...ctx.getImageData(x, y, 1, 1).data.slice(0, 3)];
          // Room centre: a small patch, any non-black pixel counts.
          const cx = Math.floor(c.width / 2);
          const cy = Math.floor(c.height / 2);
          let centerMax = 0;
          for (let dy = -20; dy <= 20; dy += 10) {
            for (let dx = -20; dx <= 20; dx += 10) {
              centerMax = Math.max(centerMax, ...px(cx + dx, cy + dy));
            }
          }
          out.pixels = {
            at: wrapped.timestamp,
            size: [c.width, c.height],
            corners: [px(2, 2), px(c.width - 3, 2), px(2, c.height - 3), px(c.width - 3, c.height - 3)],
            centerMax,
          };
        }
      }
      return out;
    },
    { opts, timeoutMs: EXPORT_TIMEOUT_MS },
  );
}

function assertMp4(label, r, { frames, expectAudio }) {
  assert(r.frames === frames, `${label}: ${r.frames} frames, expected ${frames}`);
  assert(r.video, `${label}: no video track (${r.mime})`);
  assert(r.video.codec === "avc", `${label}: video codec ${r.video.codec}, expected avc`);
  assert(r.video.w === 1280 && r.video.h === 720, `${label}: video ${r.video.w}x${r.video.h}, expected 1280x720`);
  const expectSec = frames / 30;
  assert(
    Math.abs(r.video.duration - expectSec) <= 0.1,
    `${label}: video duration ${r.video.duration.toFixed(3)} s, expected ${expectSec.toFixed(3)} ±0.1`,
  );
  const iFtyp = r.boxes.indexOf("ftyp");
  const iMoov = r.boxes.indexOf("moov");
  const iMdat = r.boxes.indexOf("mdat");
  assert(iFtyp === 0 && r.boxes[1] === "moov", `${label}: box after ftyp is "${r.boxes[1]}" (boxes ${r.boxes.join(",")}) - not fast start`);
  assert(iMoov >= 0 && iMdat > iMoov, `${label}: moov (${iMoov}) not before mdat (${iMdat})`);
  if (expectAudio) {
    assert(r.audio, `${label}: no audio track although music was on`);
    assert(r.audio.codec === "aac" || r.audio.codec === "opus", `${label}: audio codec ${r.audio.codec}`);
    assert(r.resultAudio === r.audio.codec, `${label}: result.audio ${r.resultAudio} != track codec ${r.audio.codec}`);
  } else {
    assert(!r.audio, `${label}: audio track (${r.audio && r.audio.codec}) although music was off`);
    assert(r.resultAudio === null, `${label}: result.audio ${r.resultAudio}, expected null`);
  }
  assert(r.video.canDecode, `${label}: this Chromium cannot decode H.264 - pixel check impossible`);
  assert(r.pixels, `${label}: no decoded frame at 2.0 s`);
  for (const c of r.pixels.corners) {
    assert(c.every((v) => v < 16), `${label}: corner pixel ${c.join(",")} not black (bars)`);
  }
  assert(r.pixels.centerMax >= 16, `${label}: room centre black (max channel ${r.pixels.centerMax})`);
}

async function startCannonsReplay(page) {
  await page.evaluate(async () => {
    const L = await import("/src/lua/levelLoader.ts");
    const levelData = await L.loadLevelModels("cannons");
    const moves = L.extractSavedMoves(await L.fetchLegacyFile("solution/cannons.lua"));
    // Through the world map's scene plugin, like its Pedometer "Replay" button:
    // that stops the map (the manager-level start would run both).
    window.__game.scene.keys.worldmap.scene.start("replay", { levelData, moves, returnTo: "worldmap" });
  });
  await page.waitForFunction(
    () => {
      const g = window.__game;
      const s = g.scene.keys.replay;
      return g.scene.isActive("replay") && !g.scene.isActive("worldmap") && s.levelScript && s.controlButtons.size > 0;
    },
    null,
    { timeout: 20000 },
  );
  await page.waitForTimeout(300);
}

export default async function ({ page, baseURL }) {
  // 1. Layout over every room.
  const rooms = roomSizes();
  await gotoWorldmap(page, baseURL);
  const mapBefore = await snapshot(page);

  const layout = await page.evaluate(async (rooms) => {
    const { computeVideoLayout, EXPORT_ZOOM } = await import("/src/scenes/replayExport.ts");
    const errors = [];
    const sizes = {};
    let small = 0;
    let large = 0;
    for (const r of rooms) {
      const L = computeVideoLayout(r.w, r.h);
      const rw = r.w * EXPORT_ZOOM;
      const rh = r.h * EXPORT_ZOOM;
      const bad = [];
      if (L.width % 2 || L.height % 2) bad.push("odd");
      if (L.width * 9 !== L.height * 16) bad.push("not 16:9");
      if (L.width < 1280 || L.height < 720) bad.push("< 720p");
      if (L.offsetX < 0 || L.offsetY < 0) bad.push("negative offset");
      if (L.offsetX + rw > L.width || L.offsetY + rh > L.height) bad.push("room doesn't fit");
      if (L.zoom !== EXPORT_ZOOM) bad.push(`zoom ${L.zoom}`);
      const fits720 = rw <= 1280 && rh <= 720;
      if (fits720) {
        small++;
        if (L.width !== 1280 || L.height !== 720) bad.push("small room not 1280x720");
      } else large++;
      if (bad.length) errors.push(`${r.name} ${r.w}x${r.h} -> ${L.width}x${L.height}+${L.offsetX},${L.offsetY}: ${bad.join(", ")}`);
      sizes[r.name] = `${L.width}x${L.height}`;
    }
    return { errors, small, large, hole: sizes.hole, puzzle: sizes.puzzle };
  }, rooms);
  assert(rooms.length >= 80, `only ${rooms.length} rooms in solver/levels`);
  assert(layout.errors.length === 0, `layout: ${layout.errors.join(" | ")}`);
  assert(layout.hole === "1600x900", `hole -> ${layout.hole}, expected 1600x900`);
  assert(layout.puzzle === "1568x882", `puzzle -> ${layout.puzzle}, expected 1568x882`);

  // 2-4. Two capped exports of cannons, each followed by the restore checks.
  await startCannonsReplay(page);
  const before = await snapshot(page);
  assert(before.running && before.buttonsVisible, "replay not in its normal state before the export");

  const withMusic = await exportAndParse(page, { showSteps: true, music: true, maxFrames: 75 });
  assertMp4("music on", withMusic, { frames: 75, expectAudio: true });
  assert(withMusic.musicSegments >= 1, "music on: no music segment recorded (silent audio track)");
  assertRestored("music on", await snapshot(page), before);

  const noMusic = await exportAndParse(page, { showSteps: false, music: false, maxFrames: 30 });
  assertMp4("music off", noMusic, { frames: 30, expectAudio: false });
  assertRestored("music off", await snapshot(page), before);

  // 5. Browser Back mid-export: the export rejects, the map takes over and the
  // game loop / canvas come back.
  await page.evaluate(() => {
    window.__exportSettle = { state: "pending" };
    window.__game.scene.keys.replay.runExport({ showSteps: true, music: true, maxFrames: 300 }).then(
      () => (window.__exportSettle = { state: "resolved" }),
      (e) => (window.__exportSettle = { state: "rejected", name: e && e.name, message: String(e && e.message) }),
    );
  });
  await page.waitForFunction(
    () => {
      const r = window.__game.scene.keys.replay.recording;
      return (r && r.frame >= 5) || window.__exportSettle.state !== "pending";
    },
    null,
    { timeout: 30000 },
  );
  const mid = await page.evaluate(() => ({
    settle: window.__exportSettle.state,
    hidden: window.__game.canvas.style.visibility === "hidden",
    w: window.__game.canvas.width,
    panels: [...document.querySelectorAll("body > div")].filter((d) => d.style.zIndex === "10000").length,
  }));
  assert(mid.settle === "pending", `export settled (${mid.settle}) before Back was pressed`);
  assert(mid.hidden && mid.w === 1280 && mid.panels === 1, `mid-export state off: ${JSON.stringify(mid)}`);
  await page.evaluate(() => window.dispatchEvent(new PopStateEvent("popstate")));
  await page.waitForFunction(() => window.__exportSettle.state !== "pending", null, { timeout: 15000 });
  await page.waitForFunction(() => window.__game.scene.isActive("worldmap"), null, { timeout: 15000 });
  await page.waitForTimeout(500);
  const settle = await page.evaluate(() => window.__exportSettle);
  const after = await snapshot(page);
  assert(settle.state === "rejected", `Back mid-export: export ${settle.state}, expected rejected`);
  assert(settle.name === "AbortError", `Back mid-export: rejected with ${settle.name}: ${settle.message}`);
  assert(after.worldmapActive && !after.replayActive, `Back mid-export: map ${after.worldmapActive}, replay ${after.replayActive}`);
  assert(after.running === true, "Back mid-export: game loop not running");
  assert(after.visibility !== "hidden", "Back mid-export: canvas still hidden");
  assert(after.position === mapBefore.position, `Back mid-export: canvas position "${after.position}"`);
  assert(after.panels === 0, "Back mid-export: export panel left in the DOM");
  assert(
    after.w === mapBefore.w && after.h === mapBefore.h && after.cssW === mapBefore.cssW && after.cssH === mapBefore.cssH,
    `Back mid-export: canvas ${after.w}x${after.h} (css ${after.cssW}x${after.cssH}) != map's ${mapBefore.w}x${mapBefore.h} (css ${mapBefore.cssW}x${mapBefore.cssH})`,
  );

  // 6. Native AAC blocked after isConfigSupported said yes (some Chromium builds:
  // "Audio codec disabled by product policy", docs/080): the trial encode must
  // catch it and the export must still carry AAC via the WASM encoder. Last, as
  // the WASM encoder stays registered for the rest of the session.
  await page.evaluate(() => {
    const Native = window.AudioEncoder;
    window.AudioEncoder = class extends Native {
      constructor(init) {
        super({ output: init.output, error: init.error });
        this._err = init.error;
      }
      configure(cfg) {
        if (String(cfg.codec).startsWith("mp4a")) this._blocked = true;
        else super.configure(cfg);
      }
      encode(data) {
        if (!this._blocked) return super.encode(data);
        setTimeout(() => this._err(new DOMException("Audio codec disabled by product policy.", "NotSupportedError")), 0);
      }
      flush() {
        return this._blocked ? new Promise((r) => setTimeout(r, 10)) : super.flush();
      }
      static isConfigSupported(cfg) {
        return Native.isConfigSupported(cfg);
      }
    };
  });
  await startCannonsReplay(page);
  const blockedCodecs = await page.evaluate(async () => {
    const X = await import("/src/scenes/replayExport.ts");
    return X.detectCodecs(X.computeVideoLayout(450, 405));
  });
  assert(
    blockedCodecs.audio === "aac" && blockedCodecs.aacEncoder === "wasm",
    `blocked native AAC: detectCodecs -> ${JSON.stringify(blockedCodecs)}, expected aac via wasm`,
  );
  const wasmAac = await exportAndParse(page, { showSteps: true, music: true, maxFrames: 30 });
  assertMp4("wasm aac", wasmAac, { frames: 30, expectAudio: true });
  assert(wasmAac.audio && wasmAac.audio.codec === "aac", `wasm aac: audio track ${JSON.stringify(wasmAac.audio)}`);

  const kb = (n) => `${Math.round(n / 1024)} KB`;
  return {
    detail:
      `layout ${rooms.length} rooms ok (${layout.small} at 1280x720, ${layout.large} larger; hole ${layout.hole}, puzzle ${layout.puzzle}); ` +
      `music on: ${withMusic.frames} fr ${withMusic.video.duration.toFixed(2)} s avc ${withMusic.video.w}x${withMusic.video.h} + ${withMusic.audio.codec} ${withMusic.audio.channels}ch/${withMusic.audio.rate}, ` +
      `${kb(withMusic.bytes)}, ${withMusic.exportMs} ms; music off: ${noMusic.frames} fr, no audio, ${noMusic.exportMs} ms; ` +
      `boxes ${withMusic.boxes.join(">")}; centre max ${withMusic.pixels.centerMax}; Back mid-export -> ${settle.name}, map restored; blocked native AAC -> wasm aac ${wasmAac.audio.channels}ch`,
  };
}
