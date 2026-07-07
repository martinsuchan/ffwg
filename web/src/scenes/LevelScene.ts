import Phaser from "phaser";

import { GRID_SCALE, type LevelData } from "../lua/levelLoader";
import { GameEngine } from "../game/GameEngine";

/** One simulation round per this many ms - see docs/007 "Round timing". */
const TICK_MS = 130;

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

/**
 * Renders and plays a level's puzzle: background + every model, driven by
 * the real game-logic port (web/src/game/) on a fixed round tick. No
 * sprite animation, sound or dialogs - each model just snaps to its new
 * cell every round. WASD drives the big fish, IJKL the small fish (their
 * real legacy key bindings - see ModelFactory::createUnit). R restarts.
 * See docs/007 for the rules this plays by.
 */
export class LevelScene extends Phaser.Scene {
  private engine!: GameEngine;
  private sprites = new Map<number, Phaser.GameObjects.Image>();
  private statusText!: Phaser.GameObjects.Text;
  private heldKeys = new Set<string>();
  private gameOver = false;

  constructor(private readonly levelData: LevelData) {
    super("level");
  }

  preload(): void {
    this.load.image("bg", pictureToAssetUrl(this.levelData.bgPicture));
    this.levelData.models.forEach((model, index) => {
      if (model.picture) {
        this.load.image(`model-${index}`, pictureToAssetUrl(model.picture));
      }
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
      delay: TICK_MS,
      loop: true,
      callback: () => this.tick(),
    });
  }

  private startEngine(): void {
    this.engine = new GameEngine(this.levelData);
    this.gameOver = false;
    this.statusText.setText("WASD = big fish, IJKL = small fish, R = restart");

    for (const model of this.engine.getRenderModels()) {
      if (!model.picture) continue;
      const existing = this.sprites.get(model.index);
      if (existing) {
        existing.setPosition(model.x * GRID_SCALE, model.y * GRID_SCALE);
        existing.setVisible(true);
        existing.setFlipX(!model.isLeft);
        existing.clearTint();
      } else {
        const sprite = this.add
          .image(
            model.x * GRID_SCALE,
            model.y * GRID_SCALE,
            `model-${model.index}`,
          )
          .setOrigin(0, 0);
        this.sprites.set(model.index, sprite);
      }
    }
  }

  private restart(): void {
    this.startEngine();
  }

  private tick(): void {
    if (this.gameOver) return;

    const input = { isPressed: (code: string) => this.heldKeys.has(code) };
    this.engine.tick(input);
    this.syncSprites();

    if (this.engine.isSolved()) {
      this.gameOver = true;
      this.statusText.setText("Solved! Both fish made it out. (R to replay)");
    } else if (!this.engine.isSolvable()) {
      this.gameOver = true;
      this.statusText.setText("A fish died - press R to restart.");
    }
  }

  private syncSprites(): void {
    for (const model of this.engine.getRenderModels()) {
      const sprite = this.sprites.get(model.index);
      if (!sprite) continue;

      if (model.isLost) {
        sprite.setVisible(false);
        continue;
      }

      sprite
        .setPosition(model.x * GRID_SCALE, model.y * GRID_SCALE)
        .setFlipX(!model.isLeft);

      // Only fish have a meaningful alive/dead state - every inert item is
      // "not alive" by definition, so only tint fish that actually died.
      const isDeadFish = model.kind.startsWith("fish_") && !model.isAlive;
      if (isDeadFish) {
        sprite.setTint(0xaa3333);
      } else {
        sprite.clearTint();
      }
    }
  }
}
