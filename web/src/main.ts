import Phaser from "phaser";

import { runLuaPoc } from "./lua/luaPoc";

const SPEED = 200;
const LUA_LOG_START_Y = 160;
const LUA_LOG_LINE_HEIGHT = 20;

class HelloScene extends Phaser.Scene {
  private fish!: Phaser.GameObjects.Rectangle;
  private cursors!: Phaser.Types.Input.Keyboard.CursorKeys;

  constructor() {
    super("hello");
  }

  create(): void {
    this.add
      .text(400, 80, "Fish Fillets NG - Web spike", {
        fontFamily: "sans-serif",
        fontSize: "28px",
        color: "#ffffff",
      })
      .setOrigin(0.5);

    this.add
      .text(400, 120, "Arrow keys move the fish", {
        fontFamily: "sans-serif",
        fontSize: "16px",
        color: "#88ccff",
      })
      .setOrigin(0.5);

    this.fish = this.add.rectangle(400, 300, 48, 32, 0xff8844);
    this.cursors = this.input.keyboard!.createCursorKeys();

    this.runLuaDemo();
  }

  private runLuaDemo(): void {
    this.add.text(50, LUA_LOG_START_Y - 24, "Lua POC (wasmoon):", {
      fontFamily: "sans-serif",
      fontSize: "16px",
      color: "#ffffff",
    });

    runLuaPoc("/lua/sample.lua")
      .then((result) => {
        result.logLines.forEach((line, i) => {
          this.add.text(50, LUA_LOG_START_Y + i * LUA_LOG_LINE_HEIGHT, line, {
            fontFamily: "monospace",
            fontSize: "14px",
            color: "#7cfc9c",
          });
        });
      })
      .catch((error: unknown) => {
        console.error("Lua POC failed", error);
        this.add.text(50, LUA_LOG_START_Y, `Lua POC failed: ${String(error)}`, {
          fontFamily: "monospace",
          fontSize: "14px",
          color: "#ff5555",
        });
      });
  }

  update(_time: number, delta: number): void {
    const step = (SPEED * delta) / 1000;

    if (this.cursors.left.isDown) {
      this.fish.x -= step;
    } else if (this.cursors.right.isDown) {
      this.fish.x += step;
    }

    if (this.cursors.up.isDown) {
      this.fish.y -= step;
    } else if (this.cursors.down.isDown) {
      this.fish.y += step;
    }
  }
}

new Phaser.Game({
  type: Phaser.AUTO,
  parent: "game",
  width: 800,
  height: 600,
  backgroundColor: "#0a1a2a",
  scene: HelloScene,
});
