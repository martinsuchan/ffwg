import Phaser from "phaser";

import { loadLevelModels } from "../lua/levelLoader";
import type { WorldMapData, WorldMapNode } from "../lua/worldMapLoader";
import { computeNodeStates, type NodeState } from "../game/worldMapState";
import { loadSolvedMoves } from "../storage/levelStorage";
import { pictureToAssetUrl } from "./sceneUtils";
import { PedometerUI } from "./PedometerUI";
import { AudioManager } from "./AudioManager";

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

/** Sandbox mode (docs/027): every not-yet-solved node is treated as open,
 *  including normally-hidden secret branches, per the user's explicit
 *  request. Flip to false to restore real progression-gating - no other
 *  code needs to change, computeNodeStates() already implements the real
 *  derivation either way. */
const SANDBOX_MODE = true;

/** This port's own name, shown as the tab/window title on the world map
 *  (the original used the "menu" desc's "Fish Fillets - Next Generation";
 *  this browser port is "Web Generation"). Per-level titles still use the
 *  real section/level names from worlddesc.lua. */
const GAME_TITLE = "Fish Fillets - Web Generation";

interface NodeSprites {
  far: Phaser.GameObjects.Image;
  overlay?: Phaser.GameObjects.Image;
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
  private nodeSprites = new Map<string, NodeSprites>();
  private openOverlays: Phaser.GameObjects.Image[] = [];
  private pulsePhase = 0;
  private pulseTimer?: Phaser.Time.TimerEvent;

  private selectionRing?: Phaser.GameObjects.Arc;
  private nameLabel!: Phaser.GameObjects.Text;
  private feedbackText!: Phaser.GameObjects.Text;
  private feedbackTimer?: Phaser.Time.TimerEvent;
  private pedometerUI!: PedometerUI;
  private audioManager!: AudioManager;

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
    this.load.image("node-far", pictureToAssetUrl("images/menu/n_far.png"));
    this.load.image("node-solved", pictureToAssetUrl("images/menu/n0.png"));
    for (let i = 1; i <= 4; i++) {
      this.load.image(`node-open-${i}`, pictureToAssetUrl(`images/menu/n${i}.png`));
    }
    this.load.image("pedometer-bg", pictureToAssetUrl("images/menu/pedometer.png"));
  }

  create(): void {
    // .resize(), not .setGameSize() - see LevelScene.create()'s comment
    // (docs/029) for why: this port's Scale Manager is in NONE mode
    // (no explicit `mode` set), and only .resize() updates the canvas's
    // actual CSS display box to match, not just its internal resolution.
    this.scale.resize(MAP_WIDTH, MAP_HEIGHT);
    this.add.image(0, 0, "map-bg").setOrigin(0, 0);

    // Window/tab title - legacy's WorldMap.cpp sets the SDL window caption to
    // findDesc("menu"); this port uses its own GAME_TITLE instead. Every
    // launchLevel()/launchReplay() overwrites this with the per-level
    // caption; returning here (Esc or the solved auto-return) always re-runs
    // create() and restores the map title. All document.title changes live
    // in this scene, the only one holding the names/sections data.
    document.title = GAME_TITLE;

    const solved = new Set<string>();
    for (const node of this.mapData.nodes) {
      if (loadSolvedMoves(node.codename) !== null) solved.add(node.codename);
    }
    this.nodeStates = computeNodeStates(this.mapData, solved, SANDBOX_MODE);

    this.drawEdges();
    this.drawNodes();

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
      (codename) => void this.launchLevel(codename),
      (codename) => this.launchReplay(codename),
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
    });
  }

  private drawEdges(): void {
    const byCodename = new Map(this.mapData.nodes.map((n) => [n.codename, n]));
    const graphics = this.add.graphics().setDepth(1);
    graphics.lineStyle(1, 0xd4c840, 0.8);
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
    this.selectionRing = this.add
      .circle(node.x, node.y, NODE_HIT_RADIUS + 2, 0xffc618, 0.5)
      .setDepth(1);
    this.nameLabel.setText(this.mapData.names.get(node.codename) ?? node.codename).setVisible(true);
  }

  private deselectNode(): void {
    this.selectionRing?.destroy();
    this.selectionRing = undefined;
    this.nameLabel.setVisible(false);
  }

  private onNodeClicked(node: WorldMapNode, state: NodeState): void {
    if (state === "solved") {
      this.pedometerUI.show(node.codename);
    } else {
      void this.launchLevel(node.codename);
    }
  }

  /** Loads the level then hands off to LevelScene - shared by a direct
   *  node click and the Pedometer's "Run" button (identical in effect,
   *  matching the original: Pedometer::runLevel() plays interactively
   *  from scratch exactly like an unsolved node would). */
  private async launchLevel(codename: string): Promise<void> {
    if (this.loadingCodename) return;
    this.loadingCodename = codename;
    this.showFeedback(`Loading "${codename}"...`);
    try {
      const levelData = await loadLevelModels(codename);
      document.title = this.titleFor(codename);
      this.scene.start("level", { levelData });
    } catch (error) {
      console.error(`Failed to load level "${codename}"`, error);
      this.showFeedback(`Failed to load "${codename}"`);
    } finally {
      this.loadingCodename = null;
    }
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
        this.scene.start("replay", { levelData, moves, returnTo: "worldmap" });
      })
      .catch((error: unknown) => {
        console.error(`Failed to load level "${codename}" for replay`, error);
        this.showFeedback(`Failed to load "${codename}"`);
      })
      .finally(() => {
        this.loadingCodename = null;
      });
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
