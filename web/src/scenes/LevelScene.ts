import Phaser from "phaser";

import { GRID_SCALE, type LevelData, type LevelModel } from "../lua/levelLoader";
import { createLevelScript, type LevelScript } from "../lua/levelScript";
import { GameEngine } from "../game/GameEngine";
import { ROUND_MS } from "../game/timing";
import { ModelAnimator, preloadModelFrames, resolveTextureKey } from "./ModelAnimator";

/**
 * Maps a Lua-style picture path ("images/<level>/name.png") to the
 * converted web asset. scripts/convert-images.ps1 mirrors legacy/images/**
 * into web/public/assets/images/** 1:1, swapping the extension to .webp.
 */
function pictureToAssetUrl(picture: string): string {
  const withoutPrefix = picture.replace(/^images\//, "");
  const withoutExt = withoutPrefix.replace(/\.[^./]+$/, "");
  return `/assets/images/${withoutExt}.webp`;
}

function isFishKind(kind: string): boolean {
  return kind.startsWith("fish_");
}

/**
 * Renders and plays a level's puzzle: background + every model, driven by
 * the real game-logic port (web/src/game/) on a fixed round tick, with
 * real fish body/head animation and item/fish position sliding on top
 * (web/src/scenes/ModelAnimator.ts - see docs/009). WASD drives the big
 * fish, IJKL the small fish (their real legacy key bindings - see
 * ModelFactory::createUnit). R restarts. See docs/007 for the rules this
 * plays by, docs/009 for the animation system.
 */
export class LevelScene extends Phaser.Scene {
  private engine!: GameEngine;
  private animators = new Map<number, ModelAnimator>();
  private statusText!: Phaser.GameObjects.Text;
  private heldKeys = new Set<string>();
  private gameOver = false;
  /** Item animation (docs/014) - null until the async Lua bootstrap for
   *  this play session resolves; tick()/ModelAnimator handle that gap by
   *  just not applying any override yet, not by waiting for it. */
  private levelScript: LevelScript | null = null;
  /** Guards against a superseded startEngine()'s createLevelScript() call
   *  resolving *after* a newer restart already happened - see startEngine(). */
  private scriptGeneration = 0;

  constructor(private readonly levelData: LevelData) {
    super("level");
  }

  preload(): void {
    this.load.image("bg", pictureToAssetUrl(this.levelData.bgPicture));
    this.levelData.models.forEach((model, index) => {
      preloadModelFrames(this, index, model.anims, pictureToAssetUrl);
    });
  }

  create(): void {
    this.add.image(0, 0, "bg").setOrigin(0, 0);

    this.statusText = this.add
      .text(8, 8, "", {
        fontFamily: "sans-serif",
        fontSize: "16px",
        color: "#ffffff",
        backgroundColor: "#000000a0",
        padding: { x: 6, y: 4 },
      })
      .setDepth(1000);

    this.input.keyboard!.on("keydown", (e: KeyboardEvent) =>
      this.heldKeys.add(e.code),
    );
    this.input.keyboard!.on("keyup", (e: KeyboardEvent) =>
      this.heldKeys.delete(e.code),
    );
    this.input.keyboard!.on("keydown-R", () => this.restart());

    this.startEngine();

    this.time.addEvent({
      delay: ROUND_MS,
      loop: true,
      callback: () => this.tick(),
    });
  }

  private startEngine(): void {
    for (const animator of this.animators.values()) {
      animator.destroy();
    }
    this.animators.clear();

    this.levelScript?.destroy();
    this.levelScript = null;
    this.scriptGeneration += 1;
    const generation = this.scriptGeneration;

    this.engine = new GameEngine(this.levelData);
    this.gameOver = false;
    this.statusText.setText("WASD = big fish, IJKL = small fish, R = restart");

    const initialRenderModels = this.engine.getRenderModels();

    for (const model of initialRenderModels) {
      const levelModel = this.levelData.models[model.index];
      const initialKey = resolveInitialTextureKey(model.index, levelModel);
      if (!initialKey) continue;

      const isFish = isFishKind(model.kind);
      const bodySprite = this.add
        .image(model.x * GRID_SCALE, model.y * GRID_SCALE, initialKey)
        .setOrigin(0, 0);
      const headSprite = isFish
        ? this.add
            .image(model.x * GRID_SCALE, model.y * GRID_SCALE, initialKey)
            .setOrigin(0, 0)
            .setVisible(false)
        : undefined;

      const animator = new ModelAnimator(
        this,
        model.index,
        levelModel.anims,
        bodySprite,
        isFish,
        headSprite,
        model.x,
        model.y,
        model.isLeft,
      );
      animator.sync(model);
      this.animators.set(model.index, animator);
    }

    // Fire-and-forget: physics/animators above already reset synchronously
    // (no visible restart delay), and levelScript swaps in whenever the
    // Lua bootstrap resolves - tick() just skips item-anim overrides until
    // then. The generation check discards a superseded restart's result
    // instead of resurrecting a stale engine (see docs/014).
    createLevelScript(this.levelData.levelName, initialRenderModels)
      .then((script) => {
        if (generation !== this.scriptGeneration) {
          script.destroy();
          return;
        }
        this.levelScript = script;
      })
      .catch((error: unknown) => {
        console.error(
          `Failed to load level script for "${this.levelData.levelName}"`,
          error,
        );
      });
  }

  private restart(): void {
    this.startEngine();
  }

  private tick(): void {
    // The round loop keeps running even after the level is unwinnable, so
    // e.g. a dead fish's corpse still disintegrates and drops whatever was
    // resting on it (see docs/011) - only the status text/flag latch once.
    const input = { isPressed: (code: string) => this.heldKeys.has(code) };
    this.engine.tick(input);

    const renderModels = this.engine.getRenderModels();
    this.levelScript?.tick(renderModels);

    for (const model of renderModels) {
      // Fish stay entirely TS-owned (docs/009/013) - only items consult the
      // level's Lua-driven anim override (docs/014).
      const scriptAnim = isFishKind(model.kind)
        ? null
        : (this.levelScript?.getScriptAnim(model.index) ?? null);
      this.animators.get(model.index)?.sync(model, scriptAnim);
    }

    if (this.gameOver) return;

    if (this.engine.isSolved()) {
      this.gameOver = true;
      this.statusText.setText("Solved! Both fish made it out. (R to replay)");
    } else if (!this.engine.isSolvable()) {
      this.gameOver = true;
      this.statusText.setText("A fish died - press R to restart.");
    }
  }
}

function resolveInitialTextureKey(
  index: number,
  levelModel: LevelModel,
): string | null {
  if (!levelModel.initialAnim) return null;
  return resolveTextureKey(
    index,
    levelModel.anims,
    levelModel.initialAnim,
    levelModel.isLeft ? "left" : "right",
    levelModel.initialPhase,
  );
}
