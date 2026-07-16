import Phaser from "phaser";

import { loadLevelModels } from "../lua/levelLoader";
import { createLevelScript, type EngineControl } from "../lua/levelScript";
import { GameEngine } from "../game/GameEngine";
import type { WorldMapData, WorldMapNode } from "../lua/worldMapLoader";
import { computeNodeStates, type NodeState } from "../game/worldMapState";
import { isSandboxMode } from "../game/appMode";
import { loadSolvedMoves } from "../storage/levelStorage";
import {
  pictureToAssetUrl,
  readTexturePixels,
  buildMaskedTexture,
  packRgb,
  type TexturePixels,
} from "./sceneUtils";
import { PedometerUI } from "./PedometerUI";
import { AudioManager } from "./AudioManager";
import { OptionsOverlay } from "./OptionsOverlay";
import { markWorldMap, pushSubView } from "../navigation";

/** legacy/images/menu/map.png's real, fixed dimensions - the world map
 *  never scrolls/pans/zooms (docs/027). */
const MAP_WIDTH = 640;
const MAP_HEIGHT = 480;

/** legacy's LevelNode::DOT_RADIUS - circular hover/click hit-test radius. */
const NODE_HIT_RADIUS = 13;

/** How often an "open" node's overlay icon advances one frame of its idle
 *  pulse - a plain guess at a good default, not measured against
 *  anything (matches this project's other first-guess timing constants,
 *  e.g. docs/025's FAST_MULTIPLIER). */
const PULSE_FRAME_MS = 150;

/** This port's own name, shown as the tab/window title on the world map
 *  (the original used the "menu" desc's "Fish Fillets - Next Generation";
 *  this browser port is "Web Generation"). Per-level titles still use the
 *  real section/level names from worlddesc.lua. */
const GAME_TITLE = "Fish Fillets - Web Generation";

interface NodeSprites {
  far: Phaser.GameObjects.Image;
  overlay?: Phaser.GameObjects.Image;
}

/** Where the sandbox's synthetic ending node sits (top centre). The ending has
 *  no position in worldmap.lua - it's not a map node at all (docs/050) - so
 *  this is the port's own choice, clear of the real graph and the corners. */
const ENDING_NODE_Y = 18;

type CornerAction = "intro" | "exit" | "credits" | "options";

/** One of the map's 4 corner buttons, defined by a region of map_mask.png -
 *  legacy WorldMap::prepareBg()'s getMaskAt() of the image corners. `color` is
 *  the mask's flat fill for this button; `textureKey` is a canvas texture
 *  holding only the prelit map_lower.png pixels that fall inside this button's
 *  exact mask shape (transparent everywhere else), so hovering reveals the
 *  pixel-perfect button outline, not its bounding rectangle. */
interface CornerButton {
  action: CornerAction;
  color: number;
  textureKey: string;
}

/**
 * The level-select hub, shown at boot and returned to after every level -
 * legacy's WorldMap.cpp/LevelNode.cpp/NodeDrawer.cpp. Unlike the
 * original's session-long singleton kept alive underneath a pushed Level
 * state, this scene is fully torn down and recreated on every visit
 * (this port's existing `scene.start()` pattern, docs/025) - all
 * persistent state (which levels are solved) already lives in
 * `localStorage`, so `create()` simply re-derives every node's state
 * fresh each time rather than needing an incremental "this one node just
 * got solved" signal like the original's `WorldMap::markSolved()`. See
 * docs/027.
 */
export class WorldMapScene extends Phaser.Scene {
  private nodeStates!: Map<string, NodeState>;
  /** True on /sandbox (every node unlocked); false for the standard game. */
  private sandbox = false;
  private nodeSprites = new Map<string, NodeSprites>();
  private openOverlays: Phaser.GameObjects.Image[] = [];
  private edges?: Phaser.GameObjects.Graphics;
  private pulsePhase = 0;
  private pulseTimer?: Phaser.Time.TimerEvent;

  private selectionRing?: Phaser.GameObjects.Arc;
  private nameLabel!: Phaser.GameObjects.Text;
  private feedbackText!: Phaser.GameObjects.Text;
  private feedbackTimer?: Phaser.Time.TimerEvent;
  private pedometerUI!: PedometerUI;
  private audioManager!: AudioManager;

  /** The 4 corner buttons (Intro/Exit/Credits/Options) and their prelight. */
  private cornerButtons: CornerButton[] = [];
  private lowerOverlay?: Phaser.GameObjects.Image;
  private activeCorner: CornerButton | null = null;
  private optionsOverlay!: OptionsOverlay;
  /** map_mask.png's pixels, read once for hover hit-testing. */
  private maskPixels?: TexturePixels;

  /** Guards against double-launching while a click's loadLevelModels()
   *  fetch is still in flight - same pattern as LevelScene.launchingReplay. */
  private loadingCodename: string | null = null;

  // Named `mapData`, not `data` - Phaser.Scene already has a public `data`
  // property (its own DataManager) that a same-named field would shadow.
  constructor(private readonly mapData: WorldMapData) {
    super("worldmap");
  }

  preload(): void {
    this.load.image("map-bg", pictureToAssetUrl("images/menu/map.png"));
    // Prelit map + button-region mask, for the corner-button hover (docs/038).
    this.load.image("map-lower", pictureToAssetUrl("images/menu/map_lower.png"));
    this.load.image("map-mask", pictureToAssetUrl("images/menu/map_mask.png"));
    this.load.image("node-far", pictureToAssetUrl("images/menu/n_far.png"));
    this.load.image("node-solved", pictureToAssetUrl("images/menu/n0.png"));
    for (let i = 1; i <= 4; i++) {
      this.load.image(`node-open-${i}`, pictureToAssetUrl(`images/menu/n${i}.png`));
    }
    this.load.image("pedometer-bg", pictureToAssetUrl("images/menu/pedometer.png"));
    // Pedometer's prelit hover art + button-region mask + digit strip (docs/039).
    this.load.image("pedometer-lower", pictureToAssetUrl("images/menu/pedometer_lower.png"));
    this.load.image("pedometer-mask", pictureToAssetUrl("images/menu/pedometer_mask.png"));
    this.load.spritesheet("pedometer-numbers", pictureToAssetUrl("images/menu/numbers.png"), {
      frameWidth: 19,
      frameHeight: 24,
    });
  }

  create(): void {
    // .resize(), not .setGameSize() - see LevelScene.create()'s comment
    // (docs/029) for why: this port's Scale Manager is in NONE mode
    // (no explicit `mode` set), and only .resize() updates the canvas's
    // actual CSS display box to match, not just its internal resolution.
    this.scale.resize(MAP_WIDTH, MAP_HEIGHT);
    this.add.image(0, 0, "map-bg").setOrigin(0, 0);
    this.setupCorners();

    // Window/tab title - legacy's WorldMap.cpp sets the SDL window caption to
    // findDesc("menu"); this port uses its own GAME_TITLE instead. Every
    // launchLevel()/launchReplay() overwrites this with the per-level
    // caption; returning here (Esc or the solved auto-return) always re-runs
    // create() and restores the map title. All document.title changes live
    // in this scene, the only one holding the names/sections data.
    this.sandbox = isSandboxMode();
    document.title = this.sandbox ? `${GAME_TITLE} - Sandbox` : GAME_TITLE;
    // Base history entry, so browser Back from a level returns here (see
    // navigation.ts), not to a blank tab.
    markWorldMap();

    const solved = new Set<string>();
    for (const node of this.mapData.nodes) {
      if (loadSolvedMoves(node.codename) !== null) solved.add(node.codename);
    }
    // /sandbox unlocks every node; / gates on the solved set (docs/045).
    this.nodeStates = computeNodeStates(this.mapData, solved, this.sandbox);

    this.drawEdges();
    this.drawNodes();
    this.setupEnding(solved);

    this.pulseTimer = this.time.addEvent({
      delay: PULSE_FRAME_MS,
      loop: true,
      callback: () => this.advancePulse(),
    });

    this.nameLabel = this.add
      .text(MAP_WIDTH / 2, MAP_HEIGHT - 24, "", {
        fontFamily: "sans-serif",
        fontSize: "18px",
        color: "#ffffcc",
        stroke: "#000000",
        strokeThickness: 3,
      })
      .setOrigin(0.5)
      .setDepth(1000)
      .setVisible(false);

    this.feedbackText = this.add
      .text(MAP_WIDTH - 8, 8, "", {
        fontFamily: "sans-serif",
        fontSize: "14px",
        color: "#ffffff",
        backgroundColor: "#000000a0",
        padding: { x: 6, y: 4 },
      })
      .setOrigin(1, 0)
      .setDepth(1000)
      .setVisible(false);

    this.pedometerUI = new PedometerUI(
      this,
      MAP_WIDTH,
      MAP_HEIGHT,
      this.mapData.names,
      this.mapData.bestSolutions,
      this.mapData.solverLabels,
      (codename) => void this.launchLevel(codename),
      (codename) => this.launchReplay(codename),
      () => this.closePedometer(),
    );

    // Esc closes the pedometer (restoring the hidden dots), like its Cancel
    // button - the map's only modal keyboard shortcut.
    this.input.keyboard?.on("keydown-ESC", () => {
      if (this.pedometerUI.isShowing) this.closePedometer();
    });

    // Options panel (bottom-right corner button) - live-updates music volume.
    this.optionsOverlay = new OptionsOverlay(this, MAP_WIDTH, MAP_HEIGHT, () =>
      this.audioManager.refreshMusicVolume(),
    );

    // legacy's WorldMap::own_resumeState() plays menu music every time the
    // map becomes active - this scene is always freshly created (docs/027),
    // so once here has the same effect. AudioManager is scene-agnostic
    // (already used by LevelScene/ReplayScene, docs/018) - reused as-is.
    this.audioManager = new AudioManager(this);
    void this.audioManager.applyMusicCommand({ type: "play", track: "menu" });

    this.events.once(Phaser.Scenes.Events.SHUTDOWN, () => {
      this.pulseTimer?.remove();
      this.audioManager.destroy();
      // Remove the options overlay's window keydown listener if left open.
      this.optionsOverlay.hide();
    });
  }

  private drawEdges(): void {
    const byCodename = new Map(this.mapData.nodes.map((n) => [n.codename, n]));
    const graphics = this.add.graphics().setDepth(1);
    this.edges = graphics;
    // legacy NodeDrawer::drawEdge draws solid yellow (0xdea500) as 5 overlaid
    // aalines (centre + 4 diagonal ±1 offsets) - a ~3px stroke. Match with a
    // single 3px-wide yellow line.
    graphics.lineStyle(3, 0xdea500, 1);
    for (const node of this.mapData.nodes) {
      if (!node.parent) continue;
      if (this.nodeStates.get(node.codename) === "hidden") continue;
      const parent = byCodename.get(node.parent);
      if (!parent || this.nodeStates.get(parent.codename) === "hidden") continue;
      graphics.lineBetween(parent.x, parent.y, node.x, node.y);
    }
  }

  private drawNodes(): void {
    for (const node of this.mapData.nodes) {
      const state = this.nodeStates.get(node.codename) ?? "far";
      if (state === "hidden") continue;

      const far = this.add.image(node.x, node.y, "node-far").setOrigin(0.5).setDepth(2);
      const sprites: NodeSprites = { far };

      if (state === "solved") {
        sprites.overlay = this.add.image(node.x, node.y, "node-solved").setOrigin(0.5).setDepth(3);
      } else if (state === "open") {
        const overlay = this.add.image(node.x, node.y, "node-open-1").setOrigin(0.5).setDepth(3);
        sprites.overlay = overlay;
        this.openOverlays.push(overlay);
      }

      if (state !== "far") {
        // Phaser hit-areas are in texture-local space (0,0 = top-left of
        // the frame), not centered on the object's origin - since `far`
        // is origin (0.5, 0.5), the circle center must be the texture's
        // own midpoint, not (0,0), or only its top-left quadrant would
        // ever register a hit.
        far
          .setInteractive({
            hitArea: new Phaser.Geom.Circle(far.width / 2, far.height / 2, NODE_HIT_RADIUS),
            hitAreaCallback: Phaser.Geom.Circle.Contains,
            useHandCursor: true,
          })
          .on("pointerover", () => this.selectNode(node))
          .on("pointerout", () => this.deselectNode())
          .on("pointerdown", (pointer: Phaser.Input.Pointer) => {
            if (pointer.leftButtonDown()) this.onNodeClicked(node, state);
          });
      }

      this.nodeSprites.set(node.codename, sprites);
    }
  }

  /** Idle pulse for "open" nodes - legacy's NodeDrawer.cpp triangle-wave
   *  frame selector over TimerAgent's cycle counter, reimplemented as a
   *  plain 6-step ping-pong (0,1,2,3,2,1,...) over the same 4 frames -
   *  same visual effect, much simpler than the original's branching. */
  private advancePulse(): void {
    this.pulsePhase = (this.pulsePhase + 1) % 6;
    const frame = this.pulsePhase <= 3 ? this.pulsePhase : 6 - this.pulsePhase;
    const key = `node-open-${frame + 1}`;
    // Defensive: a scene.start() transition away from this scene can leave
    // one more already-queued timer tick in flight before Phaser's own
    // Clock actually stops - touching a by-then-destroyed Image throws.
    for (const overlay of this.openOverlays) {
      if (overlay.scene) overlay.setTexture(key);
    }
  }

  private selectNode(node: WorldMapNode): void {
    this.selectionRing?.destroy();
    // legacy NodeDrawer::drawSelect: a translucent yellow (0xffc618 @ 50%) disc
    // the size of the dot itself (radius = max(dotW,dotH)/2 + 1), drawn over the
    // dot to tint it - not a large halo behind it. The "solved" dot (n0.png) is
    // the reference size.
    const dot = this.textures.get("node-solved").getSourceImage() as
      | HTMLImageElement
      | HTMLCanvasElement;
    const radius = Math.max(dot.width, dot.height) / 2 + 1;
    this.selectionRing = this.add.circle(node.x, node.y, radius, 0xffc618, 0.5).setDepth(4);
    this.nameLabel.setText(this.mapData.names.get(node.codename) ?? node.codename).setVisible(true);
  }

  private deselectNode(): void {
    this.selectionRing?.destroy();
    this.selectionRing = undefined;
    this.nameLabel.setVisible(false);
  }

  private onNodeClicked(node: WorldMapNode, state: NodeState): void {
    if (state === "solved") {
      this.showPedometer(node.codename);
    } else {
      void this.launchLevel(node.codename);
    }
  }

  /** Open the pedometer for a solved node - matches the original, which shows
   *  the rack on the plain map with every dot/edge hidden (Pedometer::prepareBg
   *  draws only map.png + the level name + solver text, no NodeDrawer path). */
  private showPedometer(codename: string): void {
    this.deselectNode();
    this.setNodesVisible(false);
    this.pedometerUI.show(codename);
  }

  private closePedometer(): void {
    this.pedometerUI.hide();
    this.setNodesVisible(true);
  }

  /** Toggle the whole node graph (dots + edges) - hidden while the pedometer
   *  is up. Hover-driven overlays (selection ring, name) stay hidden until the
   *  next hover. */
  private setNodesVisible(visible: boolean): void {
    for (const sprites of this.nodeSprites.values()) {
      sprites.far.setVisible(visible);
      sprites.overlay?.setVisible(visible);
    }
    this.edges?.setVisible(visible);
    if (!visible) {
      this.selectionRing?.setVisible(false);
      this.nameLabel.setVisible(false);
    }
  }

  // -----------------------------------------------------------------
  // Corner buttons (Intro/Exit/Credits/Options) - legacy WorldMap.cpp's
  // mask-based corner buttons with a prelit hover. See docs/038.
  // -----------------------------------------------------------------

  /** Read map_mask.png's 4 corner button regions and wire hover + click.
   *  Hovering a button reveals the prelit map_lower.png *masked to that
   *  button's exact mask shape* (legacy's LayeredPicture, which blits
   *  map_lower only through map_mask's matching pixels), not a bounding
   *  rectangle. */
  private setupCorners(): void {
    this.maskPixels = readTexturePixels(this, "map-mask");
    const lowerPixels = readTexturePixels(this, "map-lower");
    if (!this.maskPixels || !lowerPixels) return;
    const { w, h } = this.maskPixels;

    // legacy WorldMap::prepareBg(): the button under each image corner.
    const corners: Array<[CornerAction, number, number]> = [
      ["intro", 0, 0],
      ["exit", w - 1, 0],
      ["credits", 0, h - 1],
      ["options", w - 1, h - 1],
    ];
    for (const [action, cx, cy] of corners) {
      const color = packRgb(this.maskPixels, cx, cy);
      if (color < 0) continue;
      const key = `corner-${action}`;
      const count = buildMaskedTexture(this, key, lowerPixels, this.maskPixels, color);
      // Skip a "corner" whose colour floods most of the image (i.e. it's the
      // no-button background, not a real button region).
      if (count === 0 || count > (w * h) / 2) {
        if (this.textures.exists(key)) this.textures.remove(key);
        continue;
      }
      this.cornerButtons.push({ action, color, textureKey: key });
    }

    // Single overlay, positioned over map.png; its texture is swapped to the
    // hovered button's masked shape (the buttons never overlap).
    this.lowerOverlay = this.add.image(0, 0, "map-bg").setOrigin(0, 0).setDepth(0.5).setVisible(false);

    this.input.on("pointermove", (p: Phaser.Input.Pointer) => this.onCornerPointerMove(p));
    this.input.on("pointerdown", (p: Phaser.Input.Pointer) => {
      if (this.isModalOpen()) return;
      if (p.leftButtonDown() && this.activeCorner) this.dispatchCorner(this.activeCorner.action);
    });
  }

  /** True while an owned-UI overlay (pedometer/options) is capturing input -
   *  the corner buttons must ignore hover/click underneath it. */
  private isModalOpen(): boolean {
    return this.pedometerUI?.isShowing || this.optionsOverlay?.isShowing;
  }

  private onCornerPointerMove(pointer: Phaser.Input.Pointer): void {
    if (this.isModalOpen()) return;
    const x = Math.floor(pointer.worldX);
    const y = Math.floor(pointer.worldY);
    const color = this.maskPixels ? packRgb(this.maskPixels, x, y) : -1;
    const corner = this.cornerButtons.find((c) => c.color === color) ?? null;
    if (corner === this.activeCorner) return;
    this.activeCorner = corner;
    if (corner && this.lowerOverlay) {
      this.lowerOverlay.setTexture(corner.textureKey).setVisible(true);
      this.input.setDefaultCursor("pointer");
    } else {
      this.lowerOverlay?.setVisible(false);
      this.input.setDefaultCursor("");
    }
  }

  private dispatchCorner(action: CornerAction): void {
    // Clear the hover highlight before leaving/opening anything.
    this.activeCorner = null;
    this.lowerOverlay?.setVisible(false);
    this.input.setDefaultCursor("");
    switch (action) {
      case "exit":
        this.runExit();
        break;
      case "credits":
        pushSubView();
        this.scene.start("credits");
        break;
      case "options":
        this.optionsOverlay.show();
        break;
      case "intro":
        this.runIntro();
        break;
    }
  }

  /** Exit closes the browser tab. window.close() only works for
   *  script-opened windows; if the browser blocks it, do nothing. */
  private runExit(): void {
    window.close();
  }

  /** Intro plays the game's opening slideshow - see runIntro() below (filled in
   *  with the DemoScene launch). */
  private runIntro(): void {
    this.launchIntro();
  }

  /** Loads the level then hands off to LevelScene - shared by a direct
   *  node click and the Pedometer's "Run" button (identical in effect,
   *  matching the original: Pedometer::runLevel() plays interactively
   *  from scratch exactly like an unsolved node would). */
  /** The ending node ("both fish at home") has no map position (docs/050).
   *  Legacy `WorldMap::own_resumeState`/`runSelected` presents it the moment you
   *  return to the map after finishing a *final* level once the whole game is
   *  complete: as its **Pedometer** if it's already been solved (Run/Replay/
   *  Cancel), or played straight through the first time. We reproduce that in
   *  standard mode; in sandbox it's a top-centre node so it (and its pedometer/
   *  poster) stay testable without completing the game. See docs/061. */
  private setupEnding(solved: Set<string>): void {
    const ending = this.mapData.ending;
    if (!ending) return;

    if (this.sandbox) {
      // Sandbox-only affordance: an extra map node at top centre (the corners
      // are taken by Intro/Exit/Credits/Options). Drawn and registered exactly
      // like a real node - a synthetic WorldMapNode carrying the position - so
      // it hovers/labels like one, is hidden by setNodesVisible() with the rest
      // of the graph while the pedometer is up, and (once solved) opens its
      // pedometer on click just like any solved node.
      this.drawEndingNode({
        codename: ending.codename,
        x: MAP_WIDTH / 2,
        y: ENDING_NODE_Y,
        hidden: false,
        parent: null,
      });
      return;
    }

    // Standard mode (legacy checkEnding): present the ending only right after
    // finishing a FINAL level (poster path, `fromFinal`) with every level
    // solved - never on a fresh boot, never after a non-final level, and never
    // straight back from the ending itself (`endingDone`, the port's equivalent
    // of legacy's `m_selected != m_ending` guard against an ending->poster->
    // ending loop).
    const data = this.scene.settings.data as
      | { fromFinal?: boolean; endingDone?: boolean }
      | undefined;
    if (data?.endingDone || !data?.fromFinal) return;
    if (!this.mapData.nodes.every((n) => solved.has(n.codename))) return;
    // Defer to the next tick so create() fully finishes first - the original
    // also triggers this from its update loop (WorldMap::own_updateState), not
    // scene init, and launching mid-create() is fragile (Phaser render isn't
    // ready yet).
    this.time.delayedCall(0, () => this.presentEnding());
  }

  /** Presents the ending like legacy runSelected(): its Pedometer if already
   *  solved (Run/Replay/Cancel), else played straight through (first time). */
  private presentEnding(): void {
    const ending = this.mapData.ending;
    if (!ending) return;
    if (loadSolvedMoves(ending.codename) !== null) this.showPedometer(ending.codename);
    else void this.launchLevel(ending.codename);
  }

  /** Draws the sandbox's synthetic ending node - styled solved (with its
   *  pedometer on click) once it's been beaten, open/pulsing otherwise, exactly
   *  like a real node. */
  private drawEndingNode(node: WorldMapNode): void {
    const isSolved = loadSolvedMoves(node.codename) !== null;
    const far = this.add.image(node.x, node.y, "node-far").setOrigin(0.5).setDepth(2);
    const overlay = this.add
      .image(node.x, node.y, isSolved ? "node-solved" : "node-open-1")
      .setOrigin(0.5)
      .setDepth(3);
    if (!isSolved) this.openOverlays.push(overlay);

    far
      .setInteractive({
        // Texture-local hit area, as in drawNodes() - see the note there.
        hitArea: new Phaser.Geom.Circle(far.width / 2, far.height / 2, NODE_HIT_RADIUS),
        hitAreaCallback: Phaser.Geom.Circle.Contains,
        useHandCursor: true,
      })
      .on("pointerover", () => this.selectNode(node))
      .on("pointerout", () => this.deselectNode())
      .on("pointerdown", (pointer: Phaser.Input.Pointer) => {
        if (pointer.leftButtonDown()) this.presentEnding();
      });

    this.nodeSprites.set(node.codename, { far, overlay });
  }

  private isEndingCodename(codename: string): boolean {
    return codename === this.mapData.ending?.codename;
  }

  /** A level's recap poster - the ending carries its own (docs/050), other
   *  final levels have theirs in `posters`, ordinary levels have none. */
  private posterFor(codename: string): string | null {
    if (this.isEndingCodename(codename)) return this.mapData.ending?.poster ?? null;
    return this.mapData.posters.get(codename) ?? null;
  }

  private async launchLevel(codename: string): Promise<void> {
    if (this.loadingCodename) return;
    this.loadingCodename = codename;
    this.showFeedback(`Loading "${codename}"...`);
    try {
      const levelData = await loadLevelModels(codename);
      const depth = this.mapData.depths.get(codename) ?? 1;
      // Prewarm the live Lua engine *during* this "Loading" phase so its
      // bootstrap init-state (each item's opening anim phase / setEffect) is on
      // screen from LevelScene's first frame, instead of appearing ~380ms-1s
      // into play once an in-scene boot finished (docs/058's flash - docs/059).
      // The engine is built here too, not just in the scene: the bootstrap
      // seeds from its render models and reads it back via engineControl
      // (creatures/cancan/turtle query model_equals in prog_init), and
      // LevelScene adopts this exact engine so the two never disagree. The
      // callbacks are temporary - LevelScene swaps in its own this-closured
      // ones on adoption (setEngineControl/setHostActions).
      const engine = new GameEngine(levelData);
      const engineControl = this.makeEngineControl(engine);
      const script = await createLevelScript(
        codename,
        engine.getRenderModels(),
        1,
        undefined,
        engineControl,
        depth,
      );
      document.title = this.titleFor(codename);
      pushSubView();
      this.scene.start("level", {
        levelData,
        poster: this.posterFor(codename),
        depth,
        engine,
        script: Promise.resolve(script),
        isEnding: this.isEndingCodename(codename),
      });
    } catch (error) {
      console.error(`Failed to load level "${codename}"`, error);
      this.showFeedback(`Failed to load "${codename}"`);
    } finally {
      this.loadingCodename = null;
    }
  }

  /** A temporary EngineControl over a specific engine, for prewarming a level's
   *  script before LevelScene exists (docs/059). LevelScene replaces it with its
   *  own this-closured version - which follows this.engine across restarts - the
   *  moment it adopts the script, so this one only ever serves the bootstrap. */
  private makeEngineControl(engine: GameEngine): EngineControl {
    return {
      setBusy: (index, busy) => engine.setBusy(index, busy),
      checkActive: () => engine.checkActive(),
      setFastFalling: (value) => engine.setFastFalling(value),
      askFieldIndex: (x, y) => engine.askFieldIndex(x, y),
      isSolved: () => engine.isSolved(),
    };
  }

  /** Pedometer's "Replay" button - watches the saved solution auto-play,
   *  returning to the world map (not a level) on Escape - see
   *  ReplayScene's returnTo. */
  private launchReplay(codename: string): void {
    if (this.loadingCodename) return;
    this.loadingCodename = codename;
    const moves = loadSolvedMoves(codename);
    if (!moves) {
      this.loadingCodename = null;
      return;
    }
    this.showFeedback(`Loading "${codename}"...`);
    loadLevelModels(codename)
      .then((levelData) => {
        document.title = this.titleFor(codename);
        pushSubView();
        this.scene.start("replay", {
          levelData,
          moves,
          returnTo: "worldmap",
          poster: this.posterFor(codename),
          depth: this.mapData.depths.get(codename) ?? 1,
          isEnding: this.isEndingCodename(codename),
        });
      })
      .catch((error: unknown) => {
        console.error(`Failed to load level "${codename}" for replay`, error);
        this.showFeedback(`Failed to load "${codename}"`);
      })
      .finally(() => {
        this.loadingCodename = null;
      });
  }

  /** Launch the opening slideshow (IntroScene / demo_intro.lua), which returns
   *  to the map when it finishes or on Esc/click. See docs/038. */
  private launchIntro(): void {
    pushSubView();
    this.scene.start("intro");
  }

  /** The window/tab caption for a level - legacy's Level::initScreen()
   *  composes it as `<section>: <levelname>`, e.g. "Rybí domeček: Jak to
   *  všechno začalo". Falls back gracefully if a codename has no desc row. */
  private titleFor(codename: string): string {
    const section = this.mapData.sections.get(codename);
    const name = this.mapData.names.get(codename) ?? codename;
    return section ? `${section}: ${name}` : name;
  }

  private showFeedback(message: string): void {
    this.feedbackTimer?.remove();
    this.feedbackText.setText(message).setVisible(true);
    this.feedbackTimer = this.time.delayedCall(2000, () => this.feedbackText.setVisible(false));
  }
}
