import Phaser from "phaser";

/**
 * legacy `EffectMirror` (model_setEffect "mirror", submarine's mirror). The
 * original blits the mirror picture pixel by pixel straight onto the screen:
 *
 *     mask = colour of the picture's centre pixel           (the glass)
 *     pixel at (px, py) with px > MIRROR_BORDER (3) and colour == mask
 *         -> screen(x + px, y + py) = screen(x - px + 3, y + py)
 *     any other pixel -> drawn only if fully opaque (alpha 255)
 *
 * i.e. the glass shows the already-drawn screen just left of the mirror,
 * flipped horizontally ("mirror should be the last item" in models.lua - it
 * reflects whatever was drawn before it: the wavy background, the fish, other
 * items).
 *
 * Port: a `CaptureFrame` placed in the display list right after the mirror's
 * (hidden) body sprite grabs the frame drawn so far - background + models, but
 * not the subtitles/HUD/overlays, which come later, exactly like the original,
 * where they were drawn after the level view. Right after it, a `Shader` game
 * object the size of the picture draws the whole mirror: glass pixels sample
 * the capture at the mirrored position, opaque pixels show the picture, the
 * rest stays transparent. Later objects (UI, menus) draw over it normally.
 *
 * The picture is pre-classified on the CPU into one RGBA texture: alpha 255 =
 * opaque non-glass pixel (shown), alpha 128 = glass (reflects), alpha 0 = not
 * drawn. The glass test is an exact RGB match like PixelTool::colorEquals, which
 * is why the submarine atlas is lossless (scripts/convert-assets.ps1, docs/084).
 *
 * Needs WebGL and a camera framebuffer (`forceComposite`), so it is switched on
 * only while a mirror exists and restored afterwards; without WebGL the mirror
 * is drawn as a plain picture.
 */

const MIRROR_BORDER = 3;
/** Shared keys, reused by the next instance: `textures.remove()` during the
 *  scene's SHUTDOWN wedges the transition (docs/056), so a stale capture
 *  texture is removed when the next mirror is created instead. */
const PICTURE_KEY = "fx-mirror-picture";
const CAPTURE_KEY = "fx-mirror-capture";
let staleCapture: Phaser.GameObjects.CaptureFrame | null = null;

const FRAG_SOURCE = `
precision highp float;

uniform sampler2D uPicture;  // unit 0: the classified mirror picture
uniform sampler2D uCapture;  // unit 1: the frame drawn before the mirror
uniform vec2 uSize;          // picture size, source px
uniform vec2 uPos;           // picture top-left, world px
uniform vec2 uScroll;        // camera scroll, world px
uniform float uScale;        // framebuffer px per world px
uniform vec2 uRes;           // framebuffer (= capture) size, px
uniform float uBorder;

varying vec2 outTexCoord;

void main() {
    // Sample the picture at its texel centre (exact classification codes even
    // under linear filtering at 150/200 %).
    vec2 texel = floor(outTexCoord * uSize);
    vec4 pic = texture2D(uPicture, (texel + 0.5) / uSize);
    if (pic.a > 0.25 && pic.a < 0.75) {
        // Glass. outTexCoord is bottom-up (Phaser 4), the original's py is
        // counted from the top.
        float localX = outTexCoord.x * uSize.x;
        float localY = (1.0 - outTexCoord.y) * uSize.y;
        vec2 world = vec2(uPos.x + uBorder - localX, uPos.y + localY);
        vec2 fb = (world - uScroll) * uScale;
        // The capture is a framebuffer texture: row 0 is the bottom.
        vec2 uv = vec2(fb.x / uRes.x, 1.0 - fb.y / uRes.y);
        gl_FragColor = vec4(texture2D(uCapture, clamp(uv, 0.0, 1.0)).rgb, 1.0);
    } else if (pic.a > 0.99) {
        gl_FragColor = vec4(pic.rgb, 1.0);
    } else {
        gl_FragColor = vec4(0.0);
    }
}
`;

export class MirrorEffect {
  private readonly camera: Phaser.Cameras.Scene2D.Camera;
  private readonly prevForceComposite: boolean;
  private readonly capture: Phaser.GameObjects.CaptureFrame;
  private readonly shader: Phaser.GameObjects.Shader;
  private readonly picture: Phaser.Textures.CanvasTexture;
  private source?: Phaser.Textures.Frame;
  private width = 1;
  private height = 1;

  static isSupported(scene: Phaser.Scene): boolean {
    return scene.game.renderer.type === Phaser.WEBGL;
  }

  constructor(
    scene: Phaser.Scene,
    private readonly body: Phaser.GameObjects.Image,
  ) {
    this.camera = scene.cameras.main;
    this.prevForceComposite = this.camera.forceComposite;
    this.camera.setForceComposite(true);

    const existing = scene.textures.exists(PICTURE_KEY)
      ? (scene.textures.get(PICTURE_KEY) as Phaser.Textures.CanvasTexture)
      : null;
    this.picture = existing ?? scene.textures.createCanvas(PICTURE_KEY, 1, 1)!;
    this.setSource(body.frame);

    // Free the previous instance's capture (texture + framebuffer) now that
    // no scene is shutting down, then reuse the key.
    if (scene.textures.exists(CAPTURE_KEY)) scene.textures.remove(CAPTURE_KEY);
    staleCapture?.drawingContext?.destroy();
    staleCapture = null;
    this.capture = scene.add.captureFrame(CAPTURE_KEY);

    this.shader = scene.add
      .shader(
        {
          name: "ffwgMirror",
          fragmentSource: FRAG_SOURCE,
          setupUniforms: (
            setUniform: (name: string, value: unknown) => void,
            drawingContext: Phaser.Renderer.WebGL.DrawingContext,
          ) => {
            const cam = this.camera;
            setUniform("uPicture", 0);
            setUniform("uCapture", 1);
            setUniform("uSize", [this.width, this.height]);
            setUniform("uPos", [this.shader.x, this.shader.y]);
            setUniform("uScroll", [cam.scrollX, cam.scrollY]);
            setUniform("uScale", drawingContext.width / (cam.width / cam.zoom));
            setUniform("uRes", [drawingContext.width, drawingContext.height]);
            setUniform("uBorder", MIRROR_BORDER);
          },
        },
        body.x,
        body.y,
        this.width,
        this.height,
        [PICTURE_KEY, CAPTURE_KEY],
      )
      .setOrigin(0, 0);

    // Display order = the original's draw order: everything up to the mirror,
    // capture, mirror, then the rest (UI) on top.
    const list = scene.children;
    this.capture.setDepth(body.depth);
    this.shader.setDepth(body.depth);
    list.moveTo(this.capture, list.getIndex(body) + 1);
    list.moveTo(this.shader, list.getIndex(this.capture) + 1);

    // The body sprite stays as the position/visibility/picture carrier only.
    body.setAlpha(0);
  }

  /** Called every frame after the body sprite was placed. */
  place(): void {
    const body = this.body;
    if (body.frame !== this.source) this.setSource(body.frame);
    this.shader.setPosition(body.x, body.y);
    this.shader.setVisible(body.visible);
    this.capture.setVisible(body.visible);
  }

  /** (Re)classifies the picture's pixels (see the class comment). */
  private setSource(frame: Phaser.Textures.Frame): void {
    this.source = frame;
    const w = frame.realWidth;
    const h = frame.realHeight;
    const trimX = frame.trimmed ? frame.x : 0;
    const trimY = frame.trimmed ? frame.y : 0;
    const scratch = document.createElement("canvas");
    scratch.width = w;
    scratch.height = h;
    const sctx = scratch.getContext("2d", { willReadFrequently: true })!;
    sctx.drawImage(
      frame.source.image as CanvasImageSource,
      frame.cutX, frame.cutY, frame.cutWidth, frame.cutHeight,
      trimX, trimY, frame.cutWidth, frame.cutHeight,
    );
    const img = sctx.getImageData(0, 0, w, h);
    const px = img.data;
    const mid = (Math.floor(h / 2) * w + Math.floor(w / 2)) * 4;
    const [mr, mg, mb] = [px[mid], px[mid + 1], px[mid + 2]];
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        const i = (y * w + x) * 4;
        const glass = x > MIRROR_BORDER && px[i] === mr && px[i + 1] === mg && px[i + 2] === mb;
        px[i + 3] = glass ? 128 : px[i + 3] === 255 ? 255 : 0;
      }
    }
    if (this.picture.width !== w || this.picture.height !== h) this.picture.setSize(w, h);
    this.picture.context.putImageData(img, 0, 0);
    this.picture.refresh();
    if (this.shader && (w !== this.width || h !== this.height)) this.shader.setSize(w, h);
    this.width = w;
    this.height = h;
  }

  /** Effect switched off (or the model's animator torn down). */
  destroy(): void {
    this.shader.destroy();
    this.capture.destroy();
    staleCapture = this.capture;
    if (this.body.active) this.body.setAlpha(1);
    if (this.camera.scene) this.camera.setForceComposite(this.prevForceComposite);
  }
}
