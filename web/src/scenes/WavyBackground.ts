import Phaser from "phaser";

import type { RoomWaves } from "../lua/levelLoader";
import { CYCLE_MS } from "../game/timing";
import { pictureToAtlas } from "./atlas";

/**
 * The room background, with legacy `WavyPicture`'s underwater ripple
 * (`game_setRoomWaves`, docs/056). The original blits the background one
 * scanline at a time, each row shifted horizontally with wrap-around:
 *
 *     shift  = TimerAgent::getCycles() * m_speed
 *     shiftX = (Sint16)(0.5 + m_amp * sin(py / m_periode + shift))
 *
 * ...which is exactly one fragment shader: sample the row at `x + shiftX`,
 * wrapped. That's one draw call for the whole effect instead of ~555 blits +
 * a per-frame texture upload. Phaser's own Shader docs call this out as the
 * intended use ("for background or special masking effects, they are extremely
 * effective").
 *
 * `amp: 0` means no waves (WavyPicture short-circuits to a plain blit) - 2 of
 * the 81 levels - and if WebGL/shader creation isn't available we fall back to
 * a plain Image too, so the background always renders.
 */

/** outTexCoord is bottom-left origin (Phaser 4 note), so the scanline number
 *  counted from the TOP is `(1 - v) * height` - matching the original's `py`. */
const FRAG_SOURCE = `
precision mediump float;

uniform sampler2D uMainSampler;
uniform vec2 uSize;
uniform float uAmp;
uniform float uPeriode;
uniform float uPhase;

varying vec2 outTexCoord;

void main() {
    float rowFromTop = (1.0 - outTexCoord.y) * uSize.y;
    float shiftX = floor(0.5 + uAmp * sin(rowFromTop / uPeriode + uPhase));
    float u = fract(outTexCoord.x + shiftX / uSize.x);
    gl_FragColor = texture2D(uMainSampler, vec2(u, outTexCoord.y));
}
`;

export class WavyBackground {
  private image?: Phaser.GameObjects.Image;
  private shader?: Phaser.GameObjects.Shader;
  /** The bg frame lifted out of the level atlas into its own texture - a
   *  Shader samples a whole texture, not an atlas frame. Rebuilt by
   *  setPicture() for game_changeBg (docs/033). */
  private textureKey: string;
  private phase = 0;

  constructor(
    private readonly scene: Phaser.Scene,
    levelName: string,
    picture: string,
    private readonly width: number,
    private readonly height: number,
    private readonly waves: RoomWaves,
  ) {
    // One shared key: only ever one background alive at a time (scene.start
    // swaps LevelScene/ReplayScene; DemoScene has no WavyBackground), so this
    // caps the extracted canvas at a single texture for the whole session
    // rather than one per level visited. `levelName` is only for debuggability.
    this.textureKey = "wavy-bg";
    void levelName;

    if (!this.wavesEnabled()) {
      const { atlasKey, frame } = pictureToAtlas(picture);
      this.image = scene.add.image(0, 0, atlasKey, frame).setOrigin(0, 0).setDepth(-1);
      return;
    }

    this.extractTexture(picture);
    this.shader = scene.add
      .shader(
        {
          name: "ffwgRoomWaves",
          fragmentSource: FRAG_SOURCE,
          setupUniforms: (setUniform: (name: string, value: unknown) => void) => {
            setUniform("uMainSampler", 0);
            setUniform("uSize", [this.width, this.height]);
            setUniform("uAmp", this.waves.amp);
            setUniform("uPeriode", this.waves.periode);
            setUniform("uPhase", this.phase);
          },
        },
        0,
        0,
        width,
        height,
        [this.textureKey],
      )
      .setOrigin(0, 0)
      .setDepth(-1);
  }

  private wavesEnabled(): boolean {
    // amp 0 = WavyPicture's plain-blit path; no WebGL = no shader to run.
    return this.waves.amp > 0 && this.scene.game.renderer.type === Phaser.WEBGL;
  }

  /** Copies the background out of the level atlas into a standalone texture.
   *  Backgrounds are opaque, so docs/048 guarantees their atlas frame is
   *  untrimmed and full-size - a straight 1:1 blit. */
  private extractTexture(picture: string): void {
    const { atlasKey, frame } = pictureToAtlas(picture);
    if (this.scene.textures.exists(this.textureKey)) {
      this.scene.textures.remove(this.textureKey);
    }
    const src = this.scene.textures.getFrame(atlasKey, frame);
    const canvasTex = this.scene.textures.createCanvas(this.textureKey, this.width, this.height);
    canvasTex?.context.drawImage(
      src.source.image as CanvasImageSource,
      src.cutX,
      src.cutY,
      this.width,
      this.height,
      0,
      0,
      this.width,
      this.height,
    );
    canvasTex?.refresh();
  }

  /** game_changeBg(): swap the room background mid-level (corridor/rotate/
   *  steel) - docs/033. */
  setPicture(picture: string): void {
    if (this.image) {
      const { atlasKey, frame } = pictureToAtlas(picture);
      this.image.setTexture(atlasKey, frame);
      return;
    }
    // Re-extract into the same key; the shader keeps sampling texture unit 0.
    this.extractTexture(picture);
    this.shader?.setTextures([this.textureKey]);
  }

  /** Advance the ripple. The original's phase is `getCycles() * speed`, i.e.
   *  one step per 100ms cycle (its draw loop runs at that rate anyway); we
   *  render at 60fps, so feeding continuous time gives the same wave at the
   *  same speed, just smoother. */
  update(timeMs: number): void {
    if (!this.shader) return;
    this.phase = (timeMs / CYCLE_MS) * this.waves.speed;
  }

  destroy(): void {
    this.image?.destroy();
    this.shader?.destroy();
    // NOTE: deliberately does NOT textures.remove() the extracted canvas here.
    // Removing it during the scene's SHUTDOWN (while the renderer still holds
    // it) wedges the scene transition. It doesn't accumulate anyway: the key is
    // shared, only one WavyBackground is ever alive at a time, and
    // extractTexture() replaces it - so at most one lingers. See docs/056.
  }
}
