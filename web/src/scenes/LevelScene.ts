import Phaser from "phaser";

import { GRID_SCALE, type LevelData, type LevelModel } from "../lua/levelLoader";
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

    this.engine = new GameEngine(this.levelData);
    this.gameOver = false;
    this.statusText.setText("WASD = big fish, IJKL = small fish, R = restart");

    for (const model of this.engine.getRenderModels()) {
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
  }

  private restart(): void {
    this.startEngine();
  }

  private tick(): void {
    if (this.gameOver) return;

    const input = { isPressed: (code: string) => this.heldKeys.has(code) };
    this.engine.tick(input);

    for (const model of this.engine.getRenderModels()) {
      this.animators.get(model.index)?.sync(model);
    }

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
