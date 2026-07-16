import Phaser from "phaser";

import { GRID_SCALE, type AnimFrames } from "../lua/levelLoader";
import type { LevelModel } from "../lua/levelLoader";
import type { RenderModel } from "../game/GameEngine";
import { CYCLE_MS } from "../game/timing";
import { computeBodyAnim, computeHeadAnim } from "../game/UnitAnimator";
import type { ScriptAnim, ViewShift } from "../lua/levelScript";
import { pictureToAtlas, atlasWebpUrl, atlasJsonUrl, type AtlasFrame } from "./atlas";

type Side = "left" | "right";

/** Anim's phase-advance rate: one frame per cycle, matching the original's
 *  one-frame-per-draw at the fixed `CYCLE_MS` cadence (docs/046). Always steps
 *  by 1 (the old speed-up multiplier that skipped frames is gone - speed now
 *  comes from the shared clock's cell duration, not from skipping frames). */
const PHASE_MS = CYCLE_MS;

/** Decoupled-timing design (docs/009): a triggered anim (turn, or any single-tap
 *  move) plays for a fixed window instead of being tied to round pacing. A held
 *  key keeps re-triggering the same anim each round, extending this window. */
const TRIGGER_WINDOW_MS = 300;

/** Delay before a dead fish swaps to its skeleton pose - roughly one base cell
 *  duration, so the corpse appears as the killer (usually a falling item, a
 *  3-phase move) finishes sliding into the adjacent cell, not before it visibly
 *  arrives. See docs/013 (the intent) and docs/046 (now a shared-clock slide). */
const DEATH_REACTION_DELAY_MS = 3 * CYCLE_MS;

/** How long a model fades out once it's actually removed (isLost) - covers both
 *  a disintegrated corpse (docs/011) and a goal_out/escape model vanishing at
 *  the border. A plain alpha fade after removal, a cheap stand-in for the
 *  original's pixel-dissolve (out of scope - see docs/009). */
const REMOVE_FADE_MS = 400;

/** Grid-cell delta for each of Rules.getAction()'s move directions. The move
 *  decided this round is applied by the engine *next* round (docs/007's
 *  decide/apply split), so the animator slides the sprite from its current
 *  committed cell toward `cell + moveDir` over this round - arriving exactly
 *  where next round's occupyNewPos() commits it. See docs/046. */
const MOVE_OFFSETS: Record<string, { dx: number; dy: number }> = {
  move_left: { dx: -1, dy: 0 },
  move_right: { dx: 1, dy: 0 },
  move_up: { dx: 0, dy: -1 },
  move_down: { dx: 0, dy: 1 },
};

/**
 * The distinct atlas keys a level needs: every model's animation frames plus
 * the room background (docs/042).
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
  return frames[side].length || frames[side === "left" ? "right" : "left"].length;
}

/**
 * legacy Controls::getNeededPhases - how many animation phases (fixed cycles)
 * the active fish's current move occupies, and thus how long the shared slide
 * lasts (`phases · CYCLE_MS`). Fewer phases = faster. Driven by the active fish
 * only (docs/046), so every co-moving model shares one duration - no per-model
 * desync. `speedup` is the active fish's move streak (reset on push).
 */
export function movePhases(
  anims: Record<string, AnimFrames>,
  side: Side,
  action: string,
  speedup: number,
): number {
  if (action === "turn") return frameCount(anims, "turn", side) || 3;
  if (MOVE_OFFSETS[action]) {
    const swam = frameCount(anims, "swam", side) || 6;
    if (speedup > 10) return Math.max(1, Math.floor(swam / 6));
    if (speedup > 6) return Math.max(1, Math.floor(swam / 3));
    return Math.max(1, Math.floor(swam / 2));
  }
  return 3; // a non-move active state (rest/busy) while items push/fall elsewhere
}

/** Whether an action is one the active fish drives under its own move - a swim
 *  in any direction, or a turn. Mirrors legacy Unit::isMoving (which also treats
 *  a falling fish, action "move_down", as moving). */
function isDriveAction(action: string): boolean {
  return action === "turn" || MOVE_OFFSETS[action] !== undefined;
}

/**
 * Cycles (fixed CYCLE_MS phases) the whole physics round should occupy - the
 * single shared phase-lock every co-moving model rides (docs/046). Faithful to
 * the original's `PhaseLocker` (`Room::finishRound`/`fallout`/`Level::own_update`):
 *
 * - Active fish driving a move (or itself falling, action "move_down"): its
 *   `getNeededPhases` governs the round (`movePhases`, base 3 = 300ms/cell,
 *   accelerating to 200/100ms) - and everything it pushes shares that duration.
 * - No fish driving, a model leaving the room: `fallout` ensures 3 phases, so
 *   the go-out slide is 300ms (detected via the "goout" state).
 * - No fish driving, pure gravity: `falldown` ensures *nothing*, so
 *   `getLocked()==0` and the View draws the cell in a single 100ms cycle - a
 *   released/unsupported item falls ~3x faster than a fish's base swim. This is
 *   the case docs/046 got wrong (returned 3, so falls looked as slow as swims);
 *   see docs/049.
 */
export function roundPhases(
  activeInfo: { index: number; action: string; speedup: number } | null,
  renderModels: RenderModel[],
  models: LevelModel[],
): number {
  if (activeInfo && isDriveAction(activeInfo.action)) {
    const active = renderModels[activeInfo.index];
    const anims = models[activeInfo.index]?.anims ?? {};
    const side = active && !active.isLeft ? "right" : "left";
    return movePhases(anims, side, activeInfo.action, activeInfo.speedup);
  }
  if (renderModels.some((m) => m.state === "goout")) return 3;
  return 1; // pure fall: one cycle per cell (fast)
}

/** Resolves an (anim, side, phase) triple to its atlas key + frame name (docs/042).
 *
 *  A *negative* phase resolves to frame 0, matching legacy ResourcePack::getRes(),
 *  whose "advance to rank" loop (`for (i = 0; i < rank && ...)`) simply never runs
 *  for a negative rank, so it returns the first frame. Levels rely on this: a
 *  negative `afaze` is the scripts' idiom for "not started yet" (gods' sunken
 *  wreck parks at -1; fdto's seahorse counts up from `-random(100)`), and it
 *  reaches setAnim() live via updateAnim(). Without the guard, JS's `%` keeps the
 *  sign (`-37 % 3 === -1`), indexing off the front of the array and handing
 *  `undefined` to pictureToAtlas(). See docs/058. */
export function resolveFrame(
  anims: Record<string, AnimFrames>,
  name: string,
  side: Side,
  phase: number,
): AtlasFrame | null {
  const frames = anims[name];
  if (!frames) return null;
  const usableSide = frames[side].length > 0 ? side : side === "left" ? "right" : "left";
  const sideFrames = frames[usableSide];
  if (sideFrames.length === 0) return null;
  const index = phase < 0 ? 0 : phase % sideFrames.length;
  return pictureToAtlas(sideFrames[index]);
}

/**
 * Drives one model's on-screen presentation. Position is no longer a per-model
 * tween: `sync()` records the model's committed cell + the direction of the
 * move decided this round, and `render(progress)` (called every frame by the
 * scene with ONE shared `cellProgress`) places the sprite - so every co-moving
 * model slides in exact lockstep (docs/046). Body/head animation frames still
 * advance on this class's own ~CYCLE_MS timers; computeBodyAnim/computeHeadAnim
 * (web/src/game/UnitAnimator.ts) decide *which* anim (docs/009).
 */
export class ModelAnimator {
  /** The model's committed cell (px) this round, and the grid delta of the move
   *  decided this round - render() slides `base → base + moveDir·progress`. */
  private baseX: number;
  private baseY: number;
  private moveDx = 0;
  private moveDy = 0;
  private lastIsLeft: boolean;
  private removalStarted = false;
  private deathReactionPending = false;
  private deathReactionTimer?: Phaser.Time.TimerEvent;

  private bodyAnim = "rest";
  private bodyPhase = 0;
  private bodyRunning = true;
  private triggerExpiresAt = 0;

  private headAnim: string | null = null;
  private headPhase = 0;
  private lastIsAlive = true;
  private lastState = "normal";
  private lastIsTalking = false;
  /** Rules.getAction() from the last sync() - the real action, needed by
   *  computeHeadAnim's busy branch (the body anim name isn't the action). */
  private lastAction = "rest";
  /** Which of the 3 head_talking frames is showing - null when not talking
   *  (legacy's talk_phase), so talking re-rolls fresh rather than resuming
   *  mid-cycle. Owned here, not read from Lua (docs/009/013). */
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
    this.baseX = initialX * GRID_SCALE;
    this.baseY = initialY * GRID_SCALE;
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

  /** Called once per physics round with the latest engine state - records the
   *  committed cell + this round's decided move, and updates which anim plays.
   *  `scriptAnim` is the level's Lua-driven (name, phase) override for non-fish
   *  models only (docs/014); `isTalking` (fish only, docs/029) drives the
   *  head_talking mouth. Actual on-screen placement happens in render(). */
  sync(
    model: RenderModel,
    scriptAnim: ScriptAnim | null = null,
    isTalking = false,
    viewShift: ViewShift | null = null,
    effect: string | null = null,
  ): void {
    // legacy Anim::setEffect - "invisible" draws nothing at all (gods keeps its
    // sunk-ship wreck hidden this way until a ship actually sinks); "reverse"
    // flips the sprite left/right. mirror/zx are per-pixel screen effects this
    // port doesn't reproduce, so they just draw normally (docs/051).
    if (effect === "invisible") {
      this.bodySprite.setVisible(false);
      this.headSprite?.setVisible(false);
      return;
    }
    this.bodySprite.setFlipX(effect === "reverse");

    if (model.isLost) {
      // Stays where it was; only fade its alpha out (position is frozen).
      this.moveDx = 0;
      this.moveDy = 0;
      if (!this.removalStarted) {
        this.removalStarted = true;
        const targets = this.headSprite ? [this.bodySprite, this.headSprite] : this.bodySprite;
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

    // The model's committed cell this round (occupyNewPos already applied the
    // previous round's move) and the direction of the move decided THIS round
    // (applied next round) - render() slides base → base+moveDir over the round.
    // A multi-cell fast-settle (windoze) just lands base at the settled cell
    // with no offset, so the sprite snaps there rather than smearing - the
    // "snap guard" falls out for free (docs/046).
    // legacy View::getScreenPos(): `(location + viewShift) * SCALE + moveShift`
    // - the Lua-driven view shift is in grid cells and applies before scaling.
    this.baseX = (model.x + (viewShift?.x ?? 0)) * GRID_SCALE;
    this.baseY = (model.y + (viewShift?.y ?? 0)) * GRID_SCALE;
    const offset = MOVE_OFFSETS[model.action];
    this.moveDx = offset ? offset.dx : 0;
    this.moveDy = offset ? offset.dy : 0;
    this.lastIsLeft = model.isLeft;

    if (!this.isFish) {
      this.applyScriptAnim(scriptAnim);
      return;
    }

    this.lastIsAlive = model.isAlive;
    this.lastState = model.state;
    this.lastIsTalking = isTalking;
    this.lastAction = model.action;

    if (!model.isAlive) {
      // Dead: permanently show the skeleton pose, delayed by ~one cell so the
      // corpse appears as its killer finishes sliding in (docs/013/046).
      if (this.bodyAnim !== "skeleton" && !this.deathReactionPending) {
        this.deathReactionPending = true;
        this.deathReactionTimer = this.scene.time.delayedCall(DEATH_REACTION_DELAY_MS, () => {
          this.bodyAnim = "skeleton";
          this.bodyPhase = 0;
          this.applyBodyTexture();
        });
      }
      this.headSprite?.setVisible(false);
      return;
    }

    // isTalking + talkPhase let computeBodyAnim pick the busy "talk" body pose
    // (the fish faces the player while speaking in a scripted conversation) -
    // its frame is then kept in sync with talk_phase by checkHead().
    const body = computeBodyAnim(model, isTalking, this.talkPhase ?? 0);
    if (body.name !== this.bodyAnim) {
      this.bodyAnim = body.name;
      this.bodyPhase = body.phase ?? 0;
      this.applyBodyTexture();
    }
    this.bodyRunning = body.running;
    if (body.name !== "rest") {
      this.triggerExpiresAt = this.scene.time.now + TRIGGER_WINDOW_MS;
    }
  }

  /** Places the sprite for this frame from the ONE shared cell-progress
   *  (0→1) the scene passes to every animator - the source of lockstep
   *  movement (docs/046). A resting model (moveDir 0) sits at its cell. */
  /** `shiftX/shiftY` = game_setScreenShift()'s whole-view pixel offset, added
   *  last exactly like legacy View::getScreenPos() (`… + m_screenShift`). It
   *  moves every model but never the background - see docs/055. */
  render(cellProgress: number, shiftX = 0, shiftY = 0): void {
    const px = this.baseX + this.moveDx * cellProgress * GRID_SCALE + shiftX;
    const py = this.baseY + this.moveDy * cellProgress * GRID_SCALE + shiftY;
    this.bodySprite.setPosition(px, py);
    this.headSprite?.setPosition(px, py);
  }

  /** This model's current top-left screen position - the port's equivalent of
   *  legacy View::getScreenPos() (slide + viewShift + screenShift all applied).
   *  Sprites use origin (0,0), so the sprite's own position is exactly it.
   *  Used to anchor rope decor (docs/055). */
  getScreenPos(): { x: number; y: number } {
    return { x: this.bodySprite.x, y: this.bodySprite.y };
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
        // Always step by 1 - the fixed CYCLE_MS cadence matches the original's
        // one-frame-per-draw; speed is the shared clock's cell duration, not
        // frame-skipping (which made fast swimming jumpy - docs/046).
        this.bodyPhase = (this.bodyPhase + 1) % count;
      }
      this.applyBodyTexture();
    }
  }

  private checkHead(): void {
    if (!this.isFish || !this.headSprite) return;
    if (!this.bodySprite.visible || !this.lastIsAlive) return;

    // legacy's animateHead(): re-rolls a fresh phase the moment talking starts,
    // randomly steps between the 3 head_talking frames while it continues,
    // resets to null when it stops (docs/029).
    if (this.lastIsTalking) {
      this.talkPhase =
        this.talkPhase === null
          ? Math.floor(Math.random() * 3)
          : (this.talkPhase + 1 + Math.floor(Math.random() * 2)) % 3;
    } else {
      this.talkPhase = null;
    }

    // Busy "talk" body pose: its frame follows talk_phase (a held pose, so
    // advanceBody() never cycles it) - drive it here on the same head timer that
    // cycles talk_phase, so the front-facing talking body animates. See docs/061.
    if (this.bodyAnim === "talk") {
      const phase = this.talkPhase ?? 0;
      if (phase !== this.bodyPhase) {
        this.bodyPhase = phase;
        this.applyBodyTexture();
      }
    }

    const head = computeHeadAnim(
      { isAlive: this.lastIsAlive, action: this.lastAction, state: this.lastState },
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
   *  override through the same texture pathway fish body anim uses. */
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
