import Phaser from "phaser";

import { GRID_SCALE, type AnimFrames } from "../lua/levelLoader";
import type { LevelModel } from "../lua/levelLoader";
import type { RenderModel } from "../game/GameEngine";
import { ROUND_MS } from "../game/timing";
import { computeBodyAnim, computeHeadAnim } from "../game/UnitAnimator";
import type { ScriptAnim } from "../lua/levelScript";
import { pictureToAtlas, atlasWebpUrl, atlasJsonUrl, type AtlasFrame } from "./atlas";

type Side = "left" | "right";

/** Anim's original phase-advance rate: one phase per draw call, and the
 *  original engine draws once per ~100ms logic cycle (TimerAgent's default
 *  `timeinterval`, confirmed by tracing Application::run()'s single-
 *  threaded loop - VideoAgent draws every iteration, so drawing is
 *  bottlenecked by TimerAgent's SDL_Delay to the same cadence). See
 *  docs/009. */
const PHASE_MS = 100;
/** Decoupled-timing design (docs/009): a triggered anim (turn, or any
 *  single-tap move) plays for a fixed window instead of being tied to
 *  PhaseLocker-style round pacing. A held key keeps re-triggering the same
 *  anim every physics tick, which just extends this window smoothly. */
const TRIGGER_WINDOW_MS = 300;
/**
 * Visual-only "swims faster" tiers (docs/017), keyed off Rules.moveStreak
 * (consecutive non-turn, non-pushing moves). Inspired by, not a literal
 * port of, the original's SPEED_WARP1=6/SPEED_WARP2=10 thresholds - the
 * original ties speedup to shortening the round's real-world duration
 * itself (PhaseLocker), which this project deliberately doesn't do
 * (docs/010's fixed ROUND_MS); this instead scales how fast the swim
 * animation cycles and how quickly the position-slide tween completes,
 * so grid movement stays exactly one cell per round throughout.
 */
function speedStepsFor(moveStreak: number): number {
  if (moveStreak > 10) return 3;
  if (moveStreak > 6) return 2;
  return 1;
}

/** Position-slide duration must stay under ROUND_MS (docs/010) - a new
 *  RenderModel (and thus a new tween target) arrives every ROUND_MS, so a
 *  slide has to finish with margin to spare or the next round's tween
 *  stacks on top of the still-running one: visible as growing lag on a
 *  continuously-moving object (a fish's sprite falls further and further
 *  behind its true grid cell the longer a key is held), or as diagonal
 *  motion when a pushed item transitions from a horizontal push into a
 *  vertical fall mid-slide. killTweensOf() in sync() is a second line of
 *  defense for the same failure mode (e.g. a dropped frame delaying a
 *  round). */
const SLIDE_MS = Math.round(ROUND_MS * 0.8);
/**
 * Grid-cell offset for each of Rules.getAction()'s move directions - lets
 * sync() predict a model's slide target the same round the move is decided
 * (Rules.dir just got set), instead of waiting for model.x/y to actually
 * change via occupyNewPos() at the *start of the following round*
 * (docs/007's decide-this-round/apply-next-round pipeline). Without this,
 * the swim texture already starts immediately (computeBodyAnim reacts to
 * `action`, not position) but the fish stands in place for one whole extra
 * round before any real motion follows - the opposite of the original,
 * whose View::getScreenPos() computes screen position from
 * Cube::getLastMoveDir() plus a growing shift, never from the committed
 * grid location. See docs/020.
 */
const MOVE_OFFSETS: Record<string, { dx: number; dy: number }> = {
  move_left: { dx: -1, dy: 0 },
  move_right: { dx: 1, dy: 0 },
  move_up: { dx: 0, dy: -1 },
  move_down: { dx: 0, dy: 1 },
};

/** How long a model fades out once it's actually removed (isLost) - covers
 *  both a disintegrated corpse (docs/011) and a goal_out/escape model
 *  vanishing at the border. The original dissolves a corpse pixel-by-pixel
 *  over ~1.4s while it's still solid (see DEATH_REMOVE_ROUNDS in Rules.ts);
 *  this plays a plain alpha fade only *after* removal, as a cheap stand-in
 *  for that per-level-shader-adjacent effect (out of scope - see docs/009). */
const REMOVE_FADE_MS = 400;

/**
 * The distinct atlas keys a level needs: every model's animation frames plus
 * the room background. Model sprites now come from Phaser texture atlases
 * (docs/042) - one per level dir (items + bg) and one per shared fish variant -
 * so instead of registering hundreds of individual load.image() calls, the
 * scene loads a handful of atlases. Shared fish atlases collapse to one key
 * across levels (loaded once, cache-hit after). Level-scoped keys mean two
 * levels never collide (the concern docs/028 fixed for the old per-frame keys).
 */
export function collectAtlasKeys(models: LevelModel[], bgPicture: string): string[] {
  const keys = new Set<string>();
  keys.add(pictureToAtlas(bgPicture).atlasKey);
  for (const model of models) {
    for (const frames of Object.values(model.anims)) {
      for (const side of ["left", "right"] as const) {
        for (const picture of frames[side]) keys.add(pictureToAtlas(picture).atlasKey);
      }
    }
  }
  return [...keys];
}

/** Queues each atlas for loading (once) - call from a scene's preload(). Skips
 *  any already-loaded key (a shared fish atlas carried over from a prior level). */
export function preloadAtlases(scene: Phaser.Scene, atlasKeys: string[]): void {
  for (const key of atlasKeys) {
    if (scene.textures.exists(key)) continue;
    scene.load.atlas(key, atlasWebpUrl(key), atlasJsonUrl(key));
  }
}

function frameCount(anims: Record<string, AnimFrames>, name: string, side: Side): number {
  const frames = anims[name];
  if (!frames) return 0;
  // Fall back to the other side if this one is empty (e.g. an item's
  // single addItemAnim() call only ever populates "left" - items never
  // turn, so "right" is legitimately unused, not a data bug).
  return frames[side].length || frames[side === "left" ? "right" : "left"].length;
}

/** Resolves an (anim, side, phase) triple to its atlas key + frame name,
 *  falling back to the other side if this model never registered frames
 *  for the requested one (e.g. an item that never turns only has "left"
 *  populated), and wrapping phase by modulo like Anim::setAnim does. The frame
 *  identity comes straight from the picture path stored in `anims` (docs/042),
 *  so no per-level/index key synthesis is needed anymore. */
export function resolveFrame(
  anims: Record<string, AnimFrames>,
  name: string,
  side: Side,
  phase: number,
): AtlasFrame | null {
  const frames = anims[name];
  if (!frames) return null;
  const usableSide = frames[side].length > 0 ? side : side === "left" ? "right" : "left";
  if (frames[usableSide].length === 0) return null;
  return pictureToAtlas(frames[usableSide][phase % frames[usableSide].length]);
}

/**
 * Drives one model's on-screen presentation: position sliding (all
 * kinds) plus, for fish, body/head animation - computeBodyAnim/
 * computeHeadAnim (web/src/game/UnitAnimator.ts) decide *which* anim,
 * this class decides *when frames actually change on screen*. See
 * docs/009 for the overall design.
 */
export class ModelAnimator {
  private lastPxX: number;
  private lastPxY: number;
  private lastIsLeft: boolean;
  private removalStarted = false;
  private deathReactionPending = false;
  private deathReactionTimer?: Phaser.Time.TimerEvent;

  private bodyAnim = "rest";
  private bodyPhase = 0;
  private bodyRunning = true;
  private triggerExpiresAt = 0;
  /** Visual "swims faster" multiplier from the latest sync() - see speedStepsFor(). */
  private speedSteps = 1;

  private headAnim: string | null = null;
  private headPhase = 0;
  /** Latest isAlive/state/isTalking from sync() - checkHead() runs on its
   *  own timer, independent of the physics tick, so it needs a cached
   *  snapshot rather than a fresh RenderModel each time. */
  private lastIsAlive = true;
  private lastState = "normal";
  private lastIsTalking = false;
  /** Which of the 3 head_talking frames is showing - null when not
   *  talking (legacy's model.talk_phase, false/nil when idle) so the next
   *  time talking starts, it re-rolls fresh rather than resuming
   *  mid-cycle. Owned here, not read from Lua - fish stay entirely
   *  TS-owned (docs/009/013), same reasoning as body/blink animation. */
  private talkPhase: number | null = null;

  private readonly bodyTimer: Phaser.Time.TimerEvent;
  private readonly headTimer?: Phaser.Time.TimerEvent;

  constructor(
    private readonly scene: Phaser.Scene,
    private readonly anims: Record<string, AnimFrames>,
    private readonly bodySprite: Phaser.GameObjects.Image,
    private readonly isFish: boolean,
    private readonly headSprite?: Phaser.GameObjects.Image,
    initialX = 0,
    initialY = 0,
    initialIsLeft = true,
  ) {
    this.lastPxX = initialX * GRID_SCALE;
    this.lastPxY = initialY * GRID_SCALE;
    this.lastIsLeft = initialIsLeft;

    this.bodyTimer = scene.time.addEvent({
      delay: PHASE_MS,
      loop: true,
      callback: () => this.advanceBody(),
    });
    if (this.isFish) {
      this.headTimer = scene.time.addEvent({
        delay: PHASE_MS,
        loop: true,
        callback: () => this.checkHead(),
      });
    }
  }

  destroy(): void {
    this.bodyTimer.remove();
    this.headTimer?.remove();
    this.deathReactionTimer?.remove();
    this.bodySprite.destroy();
    this.headSprite?.destroy();
  }

  /** Called once per physics tick with the latest engine state. `scriptAnim`
   *  is the level's Lua-driven (animName, phase) override for this round
   *  (docs/014's item animation), consulted only for non-fish models - see
   *  docs/014's "Fish vs item anim ownership" for why fish never look at it.
   *  `isTalking` (fish only - see docs/029) drives the head_talking mouth
   *  overlay; callers that don't track dialog state (ReplayScene, which
   *  deliberately suppresses subtitles too, docs/025) just omit it. */
  sync(model: RenderModel, scriptAnim: ScriptAnim | null = null, isTalking = false): void {
    if (model.isLost) {
      if (!this.removalStarted) {
        this.removalStarted = true;
        const targets = this.headSprite ? [this.bodySprite, this.headSprite] : this.bodySprite;
        this.scene.tweens.killTweensOf(targets);
        this.scene.tweens.add({
          targets,
          alpha: 0,
          duration: REMOVE_FADE_MS,
          ease: "Linear",
          onComplete: () => {
            this.bodySprite.setVisible(false);
            this.headSprite?.setVisible(false);
          },
        });
      }
      return;
    }
    this.bodySprite.setVisible(true);
    this.speedSteps = speedStepsFor(model.moveStreak);

    // The "official" location this round - matches model.x/y exactly once
    // occupyNewPos() has actually applied a move (one round after it was
    // decided). Falls back to whichever target we're already sliding
    // toward if it still agrees, so a predicted-but-not-yet-official move
    // never gets silently overwritten with a stale value.
    const officialX = model.x * GRID_SCALE;
    const officialY = model.y * GRID_SCALE;
    // Predicted target from this round's own decision (MOVE_OFFSETS),
    // used only while the official position hasn't caught up to it yet.
    const offset = MOVE_OFFSETS[model.action];
    const predictedX = offset ? this.lastPxX + offset.dx * GRID_SCALE : officialX;
    const predictedY = offset ? this.lastPxY + offset.dy * GRID_SCALE : officialY;
    // Official position is ground truth whenever it disagrees with our
    // last known target (the normal case once occupyNewPos() runs, and a
    // safe fallback for any move that isn't reflected in `action` for some
    // reason - the sprite can never get stuck on a missed prediction).
    // Otherwise, a fresh direction this round predicts the next target
    // immediately instead of waiting a full round for `official` to move.
    const targetX = officialX !== this.lastPxX ? officialX : predictedX;
    const targetY = officialY !== this.lastPxY ? officialY : predictedY;
    if (targetX !== this.lastPxX || targetY !== this.lastPxY) {
      const targets = this.headSprite ? [this.bodySprite, this.headSprite] : this.bodySprite;
      // Defensive: stop any still-running slide before starting the next
      // one, so a late round (dropped frame, tab throttling) can never
      // stack a new tween on top of an old one - see the SLIDE_MS comment.
      // Snap back to the *previous* round's grid-aligned position first
      // (rather than wherever mid-flight the killed tween left us): each
      // round only ever moves a model along one axis (Rules.dir is a
      // single value - see docs/010), so every slide must start from an
      // exact grid cell or a leftover fractional offset on the old axis
      // blends into the new one as visible diagonal motion.
      this.scene.tweens.killTweensOf(targets);
      this.bodySprite.setPosition(this.lastPxX, this.lastPxY);
      this.headSprite?.setPosition(this.lastPxX, this.lastPxY);
      this.scene.tweens.add({
        targets,
        x: targetX,
        y: targetY,
        // Swims-faster streak (docs/017) shortens the glide itself, not
        // just the fin-flap rate - stays well under ROUND_MS at every tier.
        duration: SLIDE_MS / this.speedSteps,
        ease: "Linear",
      });
      this.lastPxX = targetX;
      this.lastPxY = targetY;
    }
    this.lastIsLeft = model.isLeft;

    if (!this.isFish) {
      this.applyScriptAnim(scriptAnim);
      return;
    }

    this.lastIsAlive = model.isAlive;
    this.lastState = model.state;
    this.lastIsTalking = isTalking;

    if (!model.isAlive) {
      // Dead: permanently show the skeleton pose, bypassing the trigger
      // window entirely (nothing should ever supersede it again). Delayed
      // by SLIDE_MS rather than shown instantly - the physics already
      // detects this the round the killer (e.g. a falling item) becomes
      // adjacent, one round before it would visually occupy this cell
      // (matches the original's Rules::checkDeadFall exactly - see
      // docs/013), and that killer's own position slide is still
      // animating into place for the next SLIDE_MS. Showing the corpse
      // immediately made it look like the fish died before the thing that
      // killed it ever visually arrived.
      if (this.bodyAnim !== "skeleton" && !this.deathReactionPending) {
        this.deathReactionPending = true;
        this.deathReactionTimer = this.scene.time.delayedCall(SLIDE_MS, () => {
          this.bodyAnim = "skeleton";
          this.bodyPhase = 0;
          this.applyBodyTexture();
        });
      }
      this.headSprite?.setVisible(false);
      return;
    }

    const body = computeBodyAnim(model);
    if (body.name !== this.bodyAnim) {
      this.bodyAnim = body.name;
      this.bodyPhase = 0;
      this.applyBodyTexture();
    }
    this.bodyRunning = body.running;
    if (body.name !== "rest") {
      this.triggerExpiresAt = this.scene.time.now + TRIGGER_WINDOW_MS;
    }
  }

  private advanceBody(): void {
    if (!this.isFish || this.bodyAnim === "skeleton") return;

    if (this.bodyAnim !== "rest" && this.scene.time.now > this.triggerExpiresAt) {
      this.bodyAnim = "rest";
      this.bodyPhase = 0;
      this.applyBodyTexture();
      return;
    }

    if (this.bodyRunning) {
      const count = frameCount(this.anims, this.bodyAnim, this.currentSide());
      if (count > 0) {
        // Swims-faster streak (docs/017) only speeds up the swim cycle
        // itself, matching the original scoping its speedup divisor to
        // non-turning moves only - vertical/turn anims always step by 1.
        const steps = this.bodyAnim === "swam" ? this.speedSteps : 1;
        this.bodyPhase = (this.bodyPhase + steps) % count;
      }
      this.applyBodyTexture();
    }
  }

  private checkHead(): void {
    if (!this.isFish || !this.headSprite) return;
    // Dead fish never show a head overlay - sync() already hides it.
    if (!this.bodySprite.visible || !this.lastIsAlive) return;

    // legacy's animateHead(): re-rolls a fresh starting phase the moment
    // talking starts, then randomly steps to a different one of the 3
    // head_talking frames on each subsequent check while it continues -
    // resets to null (legacy's talk_phase = false) the moment talking
    // stops, so the next time it starts it re-rolls rather than resuming
    // mid-cycle. Ticks on this class's existing ~100ms head timer, not
    // tied to physics rounds (docs/009's decoupled-timing design) - a
    // deliberately simpler cadence than the original's game_getCycles()-
    // gated one, not an attempt at exact parity.
    if (this.lastIsTalking) {
      this.talkPhase =
        this.talkPhase === null
          ? Math.floor(Math.random() * 3)
          : (this.talkPhase + 1 + Math.floor(Math.random() * 2)) % 3;
    } else {
      this.talkPhase = null;
    }

    const head = computeHeadAnim(
      { isAlive: this.lastIsAlive, action: this.bodyAnim, state: this.lastState },
      this.lastIsTalking,
      this.talkPhase ?? 0,
      () => Math.floor(Math.random() * 100),
    );
    if (!head) {
      this.headAnim = null;
      this.headSprite.setVisible(false);
      return;
    }
    this.headAnim = head.name;
    this.headPhase = head.phase;
    const frame = resolveFrame(this.anims, this.headAnim, this.currentSide(), this.headPhase);
    if (frame) {
      this.headSprite.setTexture(frame.atlasKey, frame.frame).setVisible(true);
    } else {
      this.headSprite.setVisible(false);
    }
  }

  /** Item animation (docs/014): applies the level's Lua-driven (name, phase)
   *  override, if any, through the same texture pathway fish body anim
   *  uses - reusing bodyAnim/bodyPhase as "this item's current anim" rather
   *  than a parallel field, since items never used them before this. */
  private applyScriptAnim(scriptAnim: ScriptAnim | null): void {
    if (!scriptAnim) return;
    if (scriptAnim.name === this.bodyAnim && scriptAnim.phase === this.bodyPhase) return;
    this.bodyAnim = scriptAnim.name;
    this.bodyPhase = scriptAnim.phase;
    this.applyBodyTexture();
  }

  private currentSide(): Side {
    return this.lastIsLeft ? "left" : "right";
  }

  private applyBodyTexture(): void {
    const frame = resolveFrame(this.anims, this.bodyAnim, this.currentSide(), this.bodyPhase);
    if (frame) {
      this.bodySprite.setTexture(frame.atlasKey, frame.frame);
    }
  }
}
