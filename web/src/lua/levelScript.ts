import { LuaFactory, LuaMultiReturn, type LuaEngine } from "wasmoon";

import { Shape } from "../game/Shape";
import type { RenderModel } from "../game/GameEngine";
import { CYCLE_MS } from "../game/timing";
import {
  fetchText,
  fetchLegacyFile,
  extractFileIncludes,
  extractRuntimeIncludes,
  getAudioManifest,
} from "./levelLoader";
import { resolveSoundPath, fetchSoundDurations, type ResolvedSound } from "./dialogSound";
import { loadSettings } from "../storage/settingsStorage";

/** Dialog language for both text and voice audio - Czech by default (docs/018:
 *  the original's home language with by far the most complete translation +
 *  voice-over coverage), now the player's Options setting (cs or nl, both fully
 *  converted). Read at level-load time, so the next level opened picks up a
 *  change; the en fallback (docs/036) is unaffected. See docs/038. */
function getDialogLang(): string {
  return loadSettings().lang;
}

/** legacy/script/share/level_creation.lua's TALK_INDEX_BOTH - a real actor
 *  value (not a wildcard) some narrator-style model_talk() calls use for
 *  lines not tied to one specific fish - see isModelTalking(). */
const TALK_INDEX_BOTH = -1;

/** The sound-sprite dirs a level draws voice/effect audio from - its own
 *  dialog voice, the built-in impact/death pool (`share`, sp-* files), and the
 *  shared joke/border dialog pools. LevelScene preloads these so the first
 *  line plays without a network-fetch delay (docs/031). */
export function levelSoundSpriteDirs(levelName: string): string[] {
  const lang = getDialogLang();
  return [
    levelDialogVoiceDir(levelName),
    "share",
    `share/border/${lang}`,
    `share/borejokes/${lang}`,
    `share/blackjokes/${lang}`,
  ];
}

/** The sprite dir holding a level's own dialog voice (`<level>/<lang>`) - the
 *  urgent one to have decoded before the first line fires. See docs/031. */
export function levelDialogVoiceDir(levelName: string): string {
  return `${levelName}/${getDialogLang()}`;
}

/** Own glue, not legacy content - wraps legacy/script/share/Pickle.lua's
 *  pickle()/loadstring() and prog_save.lua's script_loadState() (both
 *  loaded verbatim below) into two single-call primitives, so LevelScript
 *  can capture/restore Lua-side model state via a plain synchronous
 *  function reference - exactly like the existing scriptUpdate call
 *  (proven-safe, docs/014) - instead of an async lua.doString() per call,
 *  which could race a same-tick tick() into the same live engine (the
 *  class of corruption docs/008 already hit once). See docs/026. */
const SAVE_STATE_GLUE_SOURCE = `
function ffwg_captureModelState()
    return pickle(getModelsTable())
end
function ffwg_restoreModelState(serialized)
    saved_models = loadstring("return " .. serialized)()
    script_loadState()
end
`;

// Sound-path resolution + clip-duration fetching now live in ./dialogSound
// (shared with the demo movie engine, demoScript.ts). Re-exported so existing
// importers (LevelScene) keep resolving ResolvedSound from here.
export type { ResolvedSound };

/** Callbacks the live Lua engine invokes back into LevelScene for the special
 *  scripted-sequence host functions - the port's stand-in for the C++ Level's
 *  own methods these Lua bindings call (Level::newDemo / action_move / save /
 *  load / restart). Only `briefcase` uses any of these today. All default to
 *  no-ops when unset, so headless callers and ordinary levels are unaffected.
 *  See docs/031. */
export interface HostActions {
  /** level_newDemo(demoFile): launch a fullscreen movie (Phase 1). */
  newDemo(demoFile: string): void;
  /** level_action_move(sym): apply one show-driven move, legacy Room::makeMove
   *  semantics (true once consumed) - Phase 2. */
  move(symbol: string): boolean;
  /** level_action_save/load/restart(): unattended save/load/restart during a
   *  "show" (demo_help), using an in-memory demo snapshot, never a player save
   *  slot - Phase 2. */
  save(): void;
  load(): void;
  restart(): void;
}

/**
 * The one place the live Lua engine reaches into the physics GameEngine
 * (kept deliberately physics-free otherwise, docs/014). Only the windoze level
 * needs it - its code.lua swaps player control between the normal fish and the
 * extra couple, and settles the main room fast while the bonus is solved.
 * See docs/035. Absent (no-op) for every other level.
 */
export interface EngineControl {
  /** model_setBusy(index, value): freeze/unfreeze a fish for player control. */
  setBusy(index: number, busy: boolean): void;
  /** game_checkActive(): switch active fish away from one that can't drive. */
  checkActive(): void;
  /** game_setFastFalling(value): settle all falls in one round while set. */
  setFastFalling(value: boolean): void;
}

/** One level's Lua-driven animation override for a single model this round. */
export interface ScriptAnim {
  name: string;
  phase: number;
}

/** A registered dialogId()'s font+text+derived sound path, keyed by dialog
 *  name - see docs/015, docs/018. */
interface DialogEntry {
  font: string;
  subtitle: string;
  /** dataPathSound()'s result - "" when no voice file exists for this
   *  dialog in DIALOG_LANG (level_dialog.lua's own file_exists() gate). */
  soundPath: string;
}

/** One running dialog voice - the port of legacy's PlannedDialog held in
 *  DialogStack's m_running/m_cycling lists (docs/043). Multiple coexist and play
 *  concurrently (viking1's musician band), unlike docs/015's single slot. */
interface Talker {
  id: number;
  actorIndex: number;
  /** Resolved voice clip, if this dialog has real audio (docs/018) - null is a
   *  text-only / silent dialog (still counts for isTalking/subtitle timing). */
  sound: ResolvedSound | null;
  /** model_talk()'s volume arg, legacy default 75 (docs/018). */
  volume: number;
  /** cycles value at which this talker stops (non-cycling only) - computed once
   *  when model_talk() fires, matching Dialog::getMinTime()'s one-shot calc. */
  endCycle: number;
  /** loops == -1: repeats its clip until killSound (DialogStack's m_cycling). */
  cycling: boolean;
  /** Set once LevelScene has started this talker's voice (play-once). */
  played: boolean;
}

interface ScriptModel {
  w: number;
  h: number;
}

/** One sound_playSound() call this round, queued for LevelScene to
 *  actually play - see docs/018. */
interface PendingSoundEffect {
  sound: ResolvedSound;
  volume: number;
}

/** sound_playMusic(track)/sound_stopMusic() - only the *latest* command
 *  issued in a round is kept (matches SDLMusicLooper: one track at a time,
 *  always stop-before-start), null means "no new command this round, leave
 *  whatever's playing alone" - see docs/018. */
type MusicCommand = { type: "play"; track: string } | { type: "stop" };

interface LevelScriptState {
  renderModels: RenderModel[];
  scriptAnims: Map<number, ScriptAnim>;
  cycles: number;
  dialogRegistry: Map<string, DialogEntry>;
  /** All running dialog voices (DialogStack m_running + m_cycling merged, the
   *  `cycling` flag distinguishing them). Multiple play at once. See docs/043. */
  talkers: Talker[];
  /** The single *blocking* dialog (model_talk's 5th dialogFlag arg = true, i.e.
   *  planDialog's queued conversation) - legacy m_activeDialog, drives isDialog()
   *  which gates gameplay. Non-blocking talk (object:talk, the band) never sets
   *  this even while it plays. */
  activeBlocking: Talker | null;
  /** Actors whose voices were killed this round (model_killSound / a death) -
   *  drained by LevelScene to stop the audio (engine.stopGroup). */
  killedActors: number[];
  /** Monotonic id source for Talker.id. */
  nextTalkerId: number;
  /** dialog_addFont(name, r,g,b) -> CSS "#rrggbb". Each dialog names a font
   *  (dialogId's 2nd arg) whose color the subtitle is drawn in - one color per
   *  speaker, matching the original's SubTitleAgent/ResColorPack. Populated by
   *  loadFonts() (level_fonts.lua). See docs/037. */
  fontColors: Map<string, string>;
  /** New colored subtitles spawned by model_talk() this round, drained by
   *  LevelScene into its SubtitleStack (same pull pattern as
   *  pendingSoundEffects). One per non-empty model_talk() line - the visual
   *  stack is decoupled from activeDialog's talking-state. See docs/037. */
  pendingSubtitles: Array<{ text: string; color: string }>;
  /** Planner::m_plan (docs/015): a single FIFO, one command's worth of work
   *  processed per round - not one independent timer per queued action. */
  pendingActions: Array<(count: number) => boolean>;
  /** Rounds the current queue front has been waiting - resets to 0 whenever
   *  it finishes and the next one becomes the front (CommandQueue::m_count). */
  frontCount: number;
  /** name -> registered file paths, multiple sound_addSound() calls under
   *  the same name accumulate (ResourcePack's multimap - random variant
   *  selection, e.g. two impact takes) - see docs/018. */
  soundRegistry: Map<string, string[]>;
  pendingSoundEffects: PendingSoundEffect[];
  pendingMusicCommand: MusicCommand | null;
  /** "sound/<dir>/<name>.ogg" -> clip length in seconds, from pre-fetched
   *  sprite JSON spritemaps (docs/018) - backs model_talk()'s real-duration
   *  subtitle timing when a voice clip exists. */
  soundDurations: Map<string, number>;
  /** legacy's CommandQueue m_show (Level::planShow): a *separate* FIFO from
   *  pendingActions, drained by the caller (LevelScene) one command/round
   *  while it's non-empty, with player input disabled - the briefcase
   *  auto-play tutorial (demo_help.lua). See docs/031, Phase 2. */
  showActions: Array<(count: number) => boolean>;
  showCount: number;
  /** path -> pre-wrapped callable, for runtime file_include() (demo_help.lua)
   *  - run on the trigger, not at bootstrap. See docs/031, Phase 2e. */
  runtimeIncludes: Map<string, () => void>;
  /** Runtime file_include() calls made during this round's script_update,
   *  deferred to run *after* it returns - calling into Lua from inside the
   *  file_include host callback would be the docs/008 reentrancy hazard. */
  pendingIncludes: Array<() => void>;
  /** Legacy image path passed to game_changeBg() this round (corridor/rotate/
   *  steel swap the room background as the puzzle progresses), consumed by
   *  LevelScene via takeBgChange(). null = no change pending. See docs/033. */
  pendingBgChange: string | null;
  /** Last background set via game_changeBg() (backs game_getBg()). */
  currentBg: string;
}

/** Whether a *blocking* dialog is running - legacy DialogStack::isDialog()
 *  (m_activeDialog && talking). Only planDialog conversations gate gameplay;
 *  non-blocking object:talk (band/ambient) never makes this true. */
function isDialogActive(state: LevelScriptState): boolean {
  return (
    state.activeBlocking !== null &&
    (state.activeBlocking.cycling || state.cycles < state.activeBlocking.endCycle)
  );
}

/** Per-round DialogStack::updateStack(): drop finished non-cycling talkers, and
 *  clear activeBlocking once it's gone (expired or killed). Cycling talkers run
 *  until killSound. */
function updateDialogStack(state: LevelScriptState): void {
  state.talkers = state.talkers.filter((t) => t.cycling || state.cycles < t.endCycle);
  if (state.activeBlocking && !state.talkers.includes(state.activeBlocking)) {
    state.activeBlocking = null;
  }
}

/** DialogStack::killSound(actor): remove all of one actor's running talkers and
 *  flag the actor so LevelScene stops its playing audio (engine.stopGroup). */
function killTalkers(state: LevelScriptState, actor: number): void {
  const had = state.talkers.some((t) => t.actorIndex === actor);
  state.talkers = state.talkers.filter((t) => t.actorIndex !== actor);
  if (state.activeBlocking && state.activeBlocking.actorIndex === actor) {
    state.activeBlocking = null;
  }
  if (had) state.killedActors.push(actor);
}

/** dialog_addFont's (r,g,b) -> CSS "#rrggbb" (each 0-255, clamped). */
function rgbToHex(r: number, g: number, b: number): string {
  const h = (n: number) => Math.max(0, Math.min(255, Math.round(n))).toString(16).padStart(2, "0");
  return `#${h(r)}${h(g)}${h(b)}`;
}

/** The subtitle color for a dialog's font (dialog_addFont), defaulting to white
 *  for an empty/unregistered font - matches level_fonts.lua's font_white. */
function colorForFont(state: LevelScriptState, font: string): string {
  return state.fontColors.get(font) ?? "#ffffff";
}

/** Picks a random registered variant for `name` and resolves it - shared by
 *  the sound_playSound() host binding and LevelScript.resolveSound() (see
 *  docs/018). */
function resolveSoundName(state: LevelScriptState, name: string): ResolvedSound | null {
  const variants = state.soundRegistry.get(name);
  if (!variants || variants.length === 0) return null;
  const chosen = variants[Math.floor(Math.random() * variants.length)];
  return resolveSoundPath(chosen);
}

/**
 * Runs a level's real per-round Lua update loop (script_update(), defined
 * in legacy/script/share/level_start.lua) against a *persistent* wasmoon
 * engine - unlike levelLoader.ts's one-shot static extraction, this engine
 * stays alive for the life of a play session. See docs/014 for why running
 * the original Lua live (rather than porting each level's code.lua
 * item-animation logic to TypeScript) is the right call: item animation is
 * ~66 levels' worth of one-off hand-written state machines, not one shared
 * algorithm like physics/fish-animation - running the original gets every
 * level's content verbatim-correct for one bounded host-API effort,
 * instead of dozens of separate translate-and-verify passes. docs/015
 * extends this the same way for dialog text (English only, no audio).
 *
 * Deliberately does NOT touch GameEngine/Room/Rules - physics stays
 * entirely Lua-free (docs/007). Host bindings read live model state from
 * whatever RenderModel[] snapshot the caller passes to tick() each round -
 * the exact same array LevelScene already computes for animators.
 */
export class LevelScript {
  constructor(
    private readonly lua: LuaEngine,
    private readonly scriptUpdate: () => unknown,
    private readonly state: LevelScriptState,
    private readonly captureModelStateFn: () => string,
    private readonly restoreModelStateFn: (serialized: string) => void,
  ) {}

  /** Called once per physics round with the latest render state. `cyclesThisRound`
   *  is how many fixed CYCLE_MS cycles the round occupied (docs/046) - a moving
   *  round is `phases` cycles, an idle one is 1. Advancing `state.cycles` by it
   *  (not by a flat 1) keeps all cycle-based timing - dialog voice length,
   *  model_isTalking, the dialog FIFO, game_getCycles, pokus - locked to
   *  wall-clock regardless of movement speed, now that rounds vary in duration.
   *
   *  The show step runs before script_update(), matching the original's
   *  per-cycle order (Level::own_updateState -> nextShowAction, then
   *  updateLevel). Both are no-ops outside the briefcase auto-play tutorial. */
  tick(renderModels: RenderModel[], cyclesThisRound = 1): void {
    this.state.renderModels = renderModels;
    this.state.cycles += cyclesThisRound;
    updateDialogStack(this.state);
    this.runShowStep();
    this.scriptUpdate();
    this.processPlan();
    this.runPendingIncludes();
  }

  /** Runs any runtime file_include()s requested during this round's
   *  script_update() - deferred to here (outside the file_include host
   *  callback) so invoking the pre-wrapped Lua chunk is a plain TS->Lua call,
   *  not a reentrant call from inside a running host callback (docs/008). Only
   *  briefcase's demo_help.lua ever uses this. See docs/031. */
  private runPendingIncludes(): void {
    if (this.state.pendingIncludes.length === 0) return;
    const includes = this.state.pendingIncludes;
    this.state.pendingIncludes = [];
    for (const run of includes) run();
  }

  /** The latest Lua-driven (animName, phase) override for a model this
   *  round, if any. Callers should only consult this for non-fish models -
   *  the real script_update() also drives fish anim internally (via
   *  animateUnits()), but fish stay entirely TS-owned (docs/009/013); see
   *  docs/014's "Fish vs item anim ownership" for why that's safe even
   *  though those calls still land in this same map. */
  getScriptAnim(index: number): ScriptAnim | null {
    return this.state.scriptAnims.get(index) ?? null;
  }

  /** Whether model `index` should show its talking-mouth head overlay -
   *  legacy's animateHead(): `"talking" == state or model_isTalking(
   *  TALK_INDEX_BOTH)`. Cube::isTalking() (the "talking"-state half) just
   *  checks the model's own dialog slot by its own index; TALK_INDEX_BOTH
   *  (-1, level_creation.lua) is a real, separate actor value some
   *  narrator-style model_talk() calls use for lines not tied to one
   *  specific fish - when active, *every* fish shows the talking mouth
   *  simultaneously. Unlike getScriptAnim(), fish DO consult this - mouth
   *  animation was never ported when dialogs landed (docs/015) even
   *  though this class already tracked everything needed for it (see
   *  docs/029). */
  isModelTalking(index: number): boolean {
    // Any running talker for this actor (or the TALK_INDEX_BOTH narrator) counts
    // now, not just the single blocking dialog - legacy DialogStack::isTalking()
    // scans all running dialogs. So a fish talking via non-blocking object:talk
    // gets its mouth animated too (docs/043).
    return this.state.talkers.some(
      (t) => t.actorIndex === index || t.actorIndex === TALK_INDEX_BOTH,
    );
  }

  /** Voices that started this round and need playing - drained once per round by
   *  LevelScene, which plays each concurrently on the audio engine (grouped by
   *  actor). Play-once (marks `played`), replacing the old single-slot
   *  getActiveDialogId/lastDialogId diff. Only talkers with real audio. */
  takePendingVoices(): Array<{
    sound: ResolvedSound;
    volume: number;
    actorIndex: number;
    loop: boolean;
  }> {
    const out: Array<{ sound: ResolvedSound; volume: number; actorIndex: number; loop: boolean }> =
      [];
    for (const t of this.state.talkers) {
      if (t.played || !t.sound) continue;
      t.played = true;
      out.push({ sound: t.sound, volume: t.volume, actorIndex: t.actorIndex, loop: t.cycling });
    }
    return out;
  }

  /** Actors whose voices were killed this round (Lua model_killSound or the
   *  killSound() below) - drained by LevelScene to stop their audio. */
  takeKilledActors(): number[] {
    const killed = this.state.killedActors;
    this.state.killedActors = [];
    return killed;
  }

  /** Cuts a model's current voices short - legacy's DialogStack::killSound(),
   *  called from TS for built-in death sounds (Room::playDead does this before
   *  its own death sound - docs/018), mirroring the model_killSound Lua binding
   *  for Lua-driven calls. Removes the actor's talkers and flags the actor so
   *  LevelScene stops the playing audio. */
  killSound(index: number): void {
    killTalkers(this.state, index);
  }

  /** Every sound_playSound() call since the last read, drained on read
   *  (not reset at the start of tick()) so a call made during the async
   *  bootstrap itself - before any tick() ever runs, e.g. a level's
   *  code.lua calling sound_playMusic() from its top-level prog_init() -
   *  is never silently discarded. See docs/018. */
  getPendingSoundEffects(): PendingSoundEffect[] {
    const effects = this.state.pendingSoundEffects;
    this.state.pendingSoundEffects = [];
    return effects;
  }

  /** Colored subtitles spawned by model_talk() since the last read, drained on
   *  read - LevelScene adds each to its scrolling SubtitleStack. See docs/037. */
  takePendingSubtitles(): Array<{ text: string; color: string }> {
    const subs = this.state.pendingSubtitles;
    this.state.pendingSubtitles = [];
    return subs;
  }

  /** The latest sound_playMusic()/sound_stopMusic() call since the last
   *  read, drained on read (null = no new command since the last read,
   *  leave whatever's playing alone) - see getPendingSoundEffects()'s
   *  bootstrap-timing note, same reasoning applies here. */
  getMusicCommand(): MusicCommand | null {
    const command = this.state.pendingMusicCommand;
    this.state.pendingMusicCommand = null;
    return command;
  }

  /** The room-background picture game_changeBg() requested this round (legacy
   *  image path, e.g. "images/corridor/dark.png"), or null. Consumed once by
   *  LevelScene, which swaps the background texture - see docs/033. */
  takeBgChange(): string | null {
    const picture = this.state.pendingBgChange;
    this.state.pendingBgChange = null;
    return picture;
  }

  /** Resolves a registered sound name to a playable clip, picking a random
   *  variant if several were registered (ResourcePack::getRandomRes) - used
   *  both by sound_playSound() internally and by TS-triggered built-in
   *  sounds (impact/death) which have no Lua call site at all but share the
   *  same name->file registry, populated by sound_addSound() either way -
   *  see docs/018. */
  resolveSound(name: string): ResolvedSound | null {
    return resolveSoundName(this.state, name);
  }

  /** Every plain-data field a level's own code.lua has stashed onto a
   *  model (dialogue-progress counters, "have I shown this yet" flags,
   *  decoration/texture state it tracks itself) - legacy's saved_models,
   *  `pickle(getModelsTable())`. Never includes physics position, which
   *  model tables never cache (always fetched live via getLoc()) - see
   *  docs/026. Used by LevelScene's mid-level save (F2/dot-click). */
  captureModelState(): string {
    return this.captureModelStateFn();
  }

  /** Restores a captureModelState() snapshot onto the current (freshly
   *  bootstrapped) models - legacy's script_loadState()/
   *  assignModelAttributes(). Call once, after this LevelScript's own
   *  bootstrap has finished and physics has already been fast-forwarded
   *  to the matching position, before any tick(). Throws if `serialized`
   *  doesn't parse as a valid pickled table (e.g. saved by an older,
   *  incompatible version of this level's code.lua). */
  restoreModelState(serialized: string): void {
    this.restoreModelStateFn(serialized);
  }

  /** Planner::executeFirst() (docs/015): only the queue's front command
   *  runs each round: matches the original's serialized, single-command
   *  planning queue rather than resolving every pending action in parallel. */
  private processPlan(): void {
    const { pendingActions } = this.state;
    if (pendingActions.length === 0) return;
    const done = pendingActions[0](this.state.frontCount);
    if (done) {
      pendingActions.shift();
      this.state.frontCount = 0;
    } else {
      this.state.frontCount += 1;
    }
  }

  /** legacy's Level::isShowing() - true while a level_planShow() "show"
   *  sequence is queued (the briefcase auto-play tutorial). While true,
   *  LevelScene disables player input and drives the round via runShowStep()
   *  instead of normal play. See docs/031, Phase 2. */
  isShowing(): boolean {
    return this.state.showActions.length > 0;
  }

  /** Runs the front show command (legacy's CommandQueue::executeFirst, via
   *  Level::nextShowAction) - same one-command-per-round shape as
   *  processPlan(). The command may call level_action_move/save/load/restart
   *  as side effects; it's popped once it returns truthy. See docs/031. */
  runShowStep(): void {
    const { showActions } = this.state;
    if (showActions.length === 0) return;
    const done = showActions[0](this.state.showCount);
    if (done) {
      showActions.shift();
      this.state.showCount = 0;
    } else {
      this.state.showCount += 1;
    }
  }

  /** Drops all queued show commands - called when a show command throws (a
   *  physics divergence made a scripted move impossible), so control returns
   *  to the player instead of crashing. See docs/031. */
  abortShow(): void {
    this.state.showActions.length = 0;
    this.state.showCount = 0;
    this.state.pendingIncludes.length = 0;
  }

  destroy(): void {
    this.lua.global.close();
  }
}

/**
 * Fetches and runs the full real bootstrap chain a level's script_update()
 * needs - hand-traced against legacy/script/share/level_funcs.lua's real
 * load order, then empirically verified via a spike that ran script_update()
 * up to 500 times (including a forced fish death) with zero Lua errors -
 * see docs/014. Order matters: prog_finder.lua defines dir_no/dir_up/...
 * that level_start.lua reads directly, level_dialog.lua's dialogLoad() is
 * needed by initModels()'s xLoad() calls, etc.
 *
 * `restartCount` backs level_getRestartCounter() for real (docs/015) -
 * pass the same 1-based counter LevelScene already increments once per
 * startEngine() call (Level::m_restartCounter starts at 1 and increments
 * once per real restart - an exact match already sitting in LevelScene).
 */
export async function createLevelScript(
  levelName: string,
  initialRenderModels: RenderModel[],
  restartCount: number,
  hostActions?: HostActions,
  engineControl?: EngineControl,
): Promise<LevelScript> {
  // Snapshot the language for the whole load (fetch paths, sound prefixes, etc.
  // below all use it) - the Options setting, read once here so this level loads
  // consistently in cs or nl. See docs/038.
  const DIALOG_LANG = getDialogLang();
  const compatSource = await fetchText("/lua/lua50-compat.lua");
  const levelCreationSource = await fetchLegacyFile("script/share/level_creation.lua");
  const levelPlanSource = await fetchLegacyFile("script/share/level_plan.lua");
  const levelUpdateSource = await fetchLegacyFile("script/share/level_update.lua");
  const levelFontsSource = await fetchLegacyFile("script/share/level_fonts.lua");
  const levelDialogSource = await fetchLegacyFile("script/share/level_dialog.lua");
  const goanimSource = await fetchLegacyFile("script/share/prog_goanim.lua");
  const finderSource = await fetchLegacyFile("script/share/prog_finder.lua");
  const compatibleSource = await fetchLegacyFile("script/share/prog_compatible.lua");
  // pickle()/unpickle_string()/script_loadState() (docs/026) - loaded
  // verbatim, not reimplemented, so mid-level save/load reuses the
  // original's exact serialization. prog_save.lua also defines undo/redo,
  // out of scope here - confirmed safe to load anyway, since those are
  // only ever *called* by a separate C++ path (Level::saveUndo()) this
  // port has no equivalent of; its one top-level side effect (seeding an
  // unused `undo` table) needs getRestartCount() (prog_compatible.lua,
  // just fetched above) to already exist by the time it runs below.
  const pickleSource = await fetchLegacyFile("script/share/Pickle.lua");
  const progSaveSource = await fetchLegacyFile("script/share/prog_save.lua");
  const borejokesSource = await fetchLegacyFile("script/share/borejokes.lua");
  const blackjokesSource = await fetchLegacyFile("script/share/blackjokes.lua");
  const bublesSource = await fetchLegacyFile("script/share/bubles.lua");
  const bordershoutSource = await fetchLegacyFile("script/share/bordershout.lua");
  const levelStartSource = await fetchLegacyFile("script/share/level_start.lua");
  // DIALOG_LANG-only dialog content (docs/015, switched en->cs per
  // docs/018): dialogLoad() would enumerate ~15 languages via
  // select_lang.lua and call file_include per match - more work than
  // needed and the same wasmoon reentrancy risk docs/008 found. Fetching
  // these 4 files directly and running them ourselves (order-independent -
  // see docs/015) sidesteps dialogLoad() entirely.
  const shoutDialogsSource = await fetchLegacyFile(
    `script/share/shout_dialogs_${DIALOG_LANG}.lua`,
  );
  const boreDialogsSource = await fetchLegacyFile(
    `script/share/bore_dialogs_${DIALOG_LANG}.lua`,
  );
  const blackDialogsSource = await fetchLegacyFile(
    `script/share/black_dialogs_${DIALOG_LANG}.lua`,
  );
  const levelDialogsSource = await fetchLegacyFile(
    `script/${levelName}/dialogs_${DIALOG_LANG}.lua`,
  );
  // English fallback for dialogs the localized file omits. Some levels define
  // language-agnostic *sound-only* dialogs (empty subtitle) only under English,
  // with their .ogg clips stored only in sound/<level>/en/ - e.g. viking1's
  // musician-band "song" clips d1-z-* (whistle/bass/melody). The original's
  // dialogLoad() registers every dialog under its DEFAULT_LANG (first-seen)
  // definition, so these always come from en; loading en after the localized
  // file reproduces that without disturbing the localized dialogs. See docs/036.
  const levelDialogsFallbackSource = await fetchLegacyFile(
    `script/${levelName}/dialogs_en.lua`,
  );
  const modelsSource = await fetchLegacyFile(`script/${levelName}/models.lua`);
  const codeSource = await fetchLegacyFile(`script/${levelName}/code.lua`);
  // code.lua's file_include() targets: top-level ones (prog_border etc.) run at
  // bootstrap as before; runtime ones (only briefcase's demo_help.lua, called
  // from inside a closure) are wrapped as callables and run on the trigger
  // instead, so they don't queue their whole "show" at load - see docs/031.
  const includePaths = extractFileIncludes(codeSource, levelName);
  const runtimeIncludePaths = extractRuntimeIncludes(codeSource, levelName);
  const includedSources = await Promise.all(
    includePaths.map(async (path) => ({ path, source: await fetchLegacyFile(path) })),
  );
  const audioManifest = await getAudioManifest();
  // Real clip lengths for the dialog sound pools this level actually loads
  // (docs/018) - tolerant of 404s (pool/level not in our converted set),
  // since model_talk() falls back to the text-length formula either way.
  const soundDurations = await fetchSoundDurations([
    `${levelName}/${DIALOG_LANG}`,
    // English fallback clips (see levelDialogsFallbackSource) - needed so an
    // en-only sound-dialog like viking1's d1-z-* gets a real clip duration, and
    // model_talk()'s minTime is > 0 (with an empty subtitle the text-length
    // fallback would be 0 and the "note" would never actually play). docs/036.
    `${levelName}/en`,
    `share/border/${DIALOG_LANG}`,
    `share/borejokes/${DIALOG_LANG}`,
    `share/blackjokes/${DIALOG_LANG}`,
  ]);

  const factory = new LuaFactory();
  const lua = await factory.createEngine();

  const state: LevelScriptState = {
    // Seeded with the real starting positions rather than left empty:
    // code.lua's own top-level prog_init() calls initModels() (level_start.lua),
    // which calls model:getLoc() for every model *during* this setup, well
    // before the first real tick() - an empty snapshot would crash that.
    renderModels: initialRenderModels,
    scriptAnims: new Map<number, ScriptAnim>(),
    cycles: 0,
    dialogRegistry: new Map<string, DialogEntry>(),
    talkers: [],
    activeBlocking: null,
    killedActors: [],
    nextTalkerId: 0,
    fontColors: new Map<string, string>(),
    pendingSubtitles: [],
    pendingActions: [],
    frontCount: 0,
    soundRegistry: new Map<string, string[]>(),
    pendingSoundEffects: [],
    pendingMusicCommand: null,
    soundDurations,
    showActions: [],
    showCount: 0,
    runtimeIncludes: new Map<string, () => void>(),
    pendingIncludes: [],
    pendingBgChange: null,
    currentBg: "",
  };
  const modelShapes: ScriptModel[] = [];
  // level_dialog.lua's own DialogState.lang is never actually set to
  // DIALOG_LANG (dialogLoad() - the only function that sets it - is
  // deliberately bypassed, same reentrancy reasoning as docs/015), so
  // dataPathSound()'s own soundPath computation is unusable (it resolves
  // against lang="" - a malformed path that never exists). We already know
  // the exact soundPrefix each of the 4 dialog files uses (matching each
  // one's real dialogLoad(prefix, soundPrefix) call - see
  // legacy/script/share/{bordershout,borejokes,blackjokes}.lua), so
  // dialog_addDialog recomputes the real path itself from this instead of
  // trusting whatever Lua passed in - see docs/018.
  let currentSoundPrefix = "";

  function setScriptAnim(index: number, name: string, phase: number): void {
    state.scriptAnims.set(index, { name, phase });
  }

  try {
    // Room/font registration - none of this is read back; the static
    // LevelData from levelLoader.ts already has everything needed to
    // render the room and resolve anim frame paths.
    lua.global.set("level_createRoom", () => {});
    lua.global.set("game_setRoomWaves", () => {});
    // dialog_addFont(name, r, g, b): register a font's subtitle color (legacy
    // SubTitleAgent::addFont). loadFonts() (level_fonts.lua, run via initModels)
    // registers one per speaker - small fish, big fish, each NPC/viking. Stored
    // as CSS hex for LevelScene's SubtitleStack. See docs/037.
    lua.global.set("dialog_addFont", (name: string, r: number, g: number, b: number) => {
      state.fontColors.set(name, rgbToHex(r, g, b));
    });
    // Real for "sound/..." (docs/018 - unlocks dataPathSound()'s dialog
    // voice resolution and level_creation.lua's sound_addSound() calls).
    // Still always "not found" for "images/..." - frame discovery doesn't
    // matter in this engine (addItemAnim's/addBodyAnim's imgList() loop
    // only builds frame lists levelLoader.ts's static pass already
    // captured), and there's no reason to risk changing that established,
    // unrelated behavior here.
    lua.global.set("file_exists", (path: string) =>
      path.startsWith("sound/") ? audioManifest.has(path) : false,
    );

    // Sound (docs/018). sound_addSound accumulates variants per name
    // (ResourcePack's multimap - e.g. two impact takes); sound_playSound
    // picks a random one (getRandomRes) and queues it for LevelScene to
    // actually play this round.
    lua.global.set("sound_addSound", (name: string, file: string) => {
      const variants = state.soundRegistry.get(name);
      if (variants) variants.push(file);
      else state.soundRegistry.set(name, [file]);
    });
    lua.global.set("sound_playSound", (name: string, volume?: number | null) => {
      const sound = resolveSoundName(state, name);
      if (sound) state.pendingSoundEffects.push({ sound, volume: volume ?? 100 });
    });
    // Only one track at a time, always stop-before-start (SDLMusicLooper) -
    // the *last* call in a round wins; LevelScene diffs this against
    // whatever's currently playing each round. track arrives as the raw
    // legacy path (e.g. "music/rybky14.ogg", matching Path::dataReadPath -
    // relative to legacy/) - strip it down to the bare basename
    // convert-music.ps1 names its output files with ("rybky14").
    lua.global.set("sound_playMusic", (track: string) => {
      const basename = track.replace(/^music\//, "").replace(/\.ogg$/, "");
      state.pendingMusicCommand = { type: "play", track: basename };
    });
    lua.global.set("sound_stopMusic", () => {
      state.pendingMusicCommand = { type: "stop" };
    });

    lua.global.set(
      "game_addModel",
      (_kind: string, _x: number, _y: number, shape: string) => {
        const parsedShape = new Shape(shape);
        modelShapes.push({ w: parsedShape.w, h: parsedShape.h });
        return modelShapes.length - 1;
      },
    );
    lua.global.set("model_getW", (index: number) => modelShapes[index].w);
    lua.global.set("model_getH", (index: number) => modelShapes[index].h);
    // addAnim itself is a no-op (see file_exists above); setAnim/runAnim/
    // useSpecialAnim are the real per-round item-animation primitive this
    // whole file exists for.
    lua.global.set("model_addAnim", () => {});
    lua.global.set("model_setAnim", (index: number, name: string, phase: number) =>
      setScriptAnim(index, name, phase),
    );
    lua.global.set(
      "model_runAnim",
      (index: number, name: string, phase?: number | null) =>
        // wasmoon marshals an omitted/nil Lua arg as null, not undefined.
        setScriptAnim(index, name, phase ?? 0),
    );
    lua.global.set(
      "model_useSpecialAnim",
      (index: number, name: string, phase: number) => setScriptAnim(index, name, phase),
    );

    lua.global.set("model_getLoc", (index: number) => {
      const model = state.renderModels[index];
      return LuaMultiReturn.of(model.x, model.y);
    });
    lua.global.set("model_getAction", (index: number) => state.renderModels[index].action);
    lua.global.set("model_getState", (index: number) => state.renderModels[index].state);
    lua.global.set("model_isAlive", (index: number) => state.renderModels[index].isAlive);
    lua.global.set("model_isOut", (index: number) => state.renderModels[index].isOut);
    lua.global.set(
      "model_isLeft",
      (index: number) => state.renderModels[index].isLeft,
    );
    // Rules::isAtBorder() - already ported (docs/007), just not previously
    // exposed to Lua. Real gameplay state (escape/goal-adjacent checks in
    // e.g. atlantis/gods/map/propulsion/turtle/barrel/floppy's code.lua),
    // not cosmetic - see docs/028.
    lua.global.set("model_isAtBorder", (index: number) => state.renderModels[index].isAtBorder);
    // Rules::getTouchDir() - the direction a model pushed against something it
    // couldn't move this round (or dir_no). Several levels' code.lua read it
    // every round via the getTouchDir() object method (level_creation.lua),
    // e.g. cabin1's screen-shake gag; leaving this unbound threw "attempt to
    // call a nil value" mid-round and froze the loop (docs/033). The Dir enum
    // values match the Lua dir_* constants exactly (NO=0..RIGHT=4).
    lua.global.set("model_getTouchDir", (index: number) => state.renderModels[index].touchDir);
    // game-script.cpp's model_equals(index, x, y): does *this* model occupy
    // field cell (x,y)? Backs isWater()/isFreePlace()-style pathfinding
    // helpers a handful of "programmed"/scripted-unit levels use (prog_
    // compatible.lua, prog_finder.lua). Approximated from the same
    // RenderModel[] snapshot every other binding here already uses (this
    // engine deliberately has no access to the real Field/Room, see this
    // file's own doc comment) - matches a model's single anchor position,
    // not its full multi-cell shape mask, and doesn't distinguish "empty
    // water" from "room border" the way the original's Field sentinel does.
    // A documented simplification, not a crash fix pretending to be exact -
    // affects only a few non-final levels' scripted-unit pathfinding.
    lua.global.set("model_equals", (index: number, x: number, y: number) => {
      const occupant = state.renderModels.find((m) => !m.isLost && m.x === x && m.y === y);
      return occupant ? occupant.index === index : index === -1;
    });
    // Per-model/per-screen view-shift (model_setViewShift/game_setScreenShift)
    // are purely cosmetic camera/render-offset effects (View::getScreenPos()
    // only) with no gameplay/goal state - safe no-ops. See docs/028.
    lua.global.set("model_setViewShift", () => {});
    lua.global.set("game_setScreenShift", () => {});
    // game_setFastFalling(value): real physics pacing, wired to the engine for
    // windoze (settle the main room fast while the bonus is solved) - docs/035.
    lua.global.set("game_setFastFalling", (value: boolean) =>
      engineControl?.setFastFalling(Boolean(value)),
    );
    // game_checkActive(): switch player control away from a now-busy fish -
    // windoze uses it when swapping control to the extra couple (docs/035).
    lua.global.set("game_checkActive", () => engineControl?.checkActive());
    // model_getViewShift(index): the getter paired with the no-op
    // model_setViewShift. View shift is a cosmetic per-model render offset this
    // port doesn't apply, so it's always (0,0) - but pyramid/code.lua *reads*
    // it every round, so leaving it unbound (unlike the stubbed setter) threw
    // "attempt to call a nil value" and froze the loop (docs/033). Returns
    // (0,0), consistent with the setter being a no-op.
    lua.global.set("model_getViewShift", () => LuaMultiReturn.of(0, 0));
    // game_changeBg(picture): swaps the whole room background at runtime -
    // corridor/rotate/steel do this as the puzzle progresses (darken, phase
    // change). Unbound, it froze those levels' loops mid-play (docs/033, same
    // class as cabin1). Recorded here; LevelScene applies the texture swap.
    lua.global.set("game_changeBg", (picture: string) => {
      state.pendingBgChange = picture;
      state.currentBg = picture;
    });
    lua.global.set("game_getBg", () => state.currentBg);
    // Level::isShowing() - true while a level_planShow() "show" is queued
    // (only the briefcase auto-play tutorial). LevelScene disables player
    // input and drives the round via runShowStep() while true. See docs/031.
    lua.global.set("level_isShowing", () => state.showActions.length > 0);
    // Level::newDemo(): launch a fullscreen movie (briefcase pushes the
    // briefcase down -> demo_briefcase.lua). Delegated to LevelScene via
    // hostActions - see docs/031, Phase 1.
    lua.global.set("level_newDemo", (demoFile: string) => hostActions?.newDemo(demoFile));
    // Level::action_move/save/load/restart() - the auto-play tutorial's
    // unattended actions, delegated to LevelScene (docs/031, Phase 2). Save/
    // load use an in-memory demo snapshot there, never a player save slot.
    lua.global.set("level_action_move", (symbol: string) => hostActions?.move(symbol) ?? false);
    lua.global.set("level_action_save", () => {
      hostActions?.save();
      return true;
    });
    lua.global.set("level_action_load", () => {
      hostActions?.load();
      return true;
    });
    lua.global.set("level_action_restart", () => {
      hostActions?.restart();
      return true;
    });

    // Dynamically changing goal/turn-side/busy/effect during play isn't a
    // scenario any level's per-round update closure exercises today
    // (verified via the docs/014 spike) - safe no-ops, matching
    // levelLoader.ts's same-named stubs for the static pass.
    lua.global.set("model_setGoal", () => {});
    lua.global.set("model_change_turnSide", () => {});
    // model_setBusy(index, value): real for windoze - freezes/unfreezes a fish
    // for player control (docs/035). No-op elsewhere (no engineControl).
    lua.global.set("model_setBusy", (index: number, value: boolean) =>
      engineControl?.setBusy(index, Boolean(value)),
    );
    lua.global.set("model_setEffect", () => {});
    // game_addDecor (purely visual) - real no-op stub, proven safe in
    // levelLoader.ts's static pass (docs/024). See docs/028.
    lua.global.set("game_addDecor", () => {});
    // Level::planShow(func): queue one show command (CommandQueue m_show).
    // Real now (docs/031, Phase 2) - only briefcase's demo_help.lua queues
    // any, and only at its runtime trigger (never at bootstrap, since
    // demo_help is a runtime file_include - see runtimeIncludes), so this
    // stays empty for every other level and level_isShowing() stays false.
    lua.global.set("level_planShow", (callback: (count: number) => unknown) => {
      state.showActions.push((count) => Boolean(callback(count)));
    });

    // Dialog text+sound (docs/015, docs/018). dialog_addDialog registers
    // name -> {font, subtitle, soundPath} the moment a dialog file runs
    // (dialogId -> dialogStr -> dialog_addDialog, all pure Lua in
    // level_dialog.lua) - Lua's own computed soundPath is unusable (see
    // currentSoundPrefix's comment above), so it's ignored and recomputed
    // from currentSoundPrefix + the real audio manifest instead.
    lua.global.set(
      "dialog_addDialog",
      (name: string, _lang: string, _soundPath: string, font: string, subtitle: string) => {
        const candidate = `${currentSoundPrefix}${name}.ogg`;
        const soundPath = audioManifest.has(candidate) ? candidate : "";
        state.dialogRegistry.set(name, { font, subtitle, soundPath });
      },
    );
    // model_talk's real 5th "immediate" arg (dialogFlag in the original)
    // isn't distinguished here - every call becomes the one active
    // subtitle slot, matching the addm/addv/planDialog path airplane
    // actually uses (always immediate=true) - see docs/015's "Deliberate
    // simplifications". Duration uses the real voice clip's length when one
    // resolved (docs/018) - matching the original's actual behavior, where
    // real playback drives subtitle duration whenever audio is available -
    // falling back to Dialog::getMinTime()'s own no-sound formula
    // (min(180, textLength) cycles) otherwise, not an invented heuristic.
    lua.global.set(
      "model_talk",
      (
        index: number,
        dialogName: string,
        volume?: number | null,
        loops?: number | null,
        dialogFlag?: boolean,
      ) => {
        const entry = state.dialogRegistry.get(dialogName);
        if (!entry) return;
        const sound = entry.soundPath ? resolveSoundPath(entry.soundPath) : null;
        const durationSeconds = entry.soundPath
          ? state.soundDurations.get(entry.soundPath)
          : undefined;
        const minTime =
          durationSeconds !== undefined
            ? Math.ceil((durationSeconds * 1000) / CYCLE_MS)
            : Math.min(180, entry.subtitle.length);
        const cycling = (loops ?? 0) === -1;
        const repeats = cycling ? 1 : (loops ?? 0) + 1;
        // Push a new talker (DialogStack::actorTalk) - concurrent, never
        // overwriting a previous one, so viking1's band notes coexist (docs/043).
        const talker: Talker = {
          id: state.nextTalkerId++,
          actorIndex: index,
          sound,
          volume: volume ?? 75,
          endCycle: state.cycles + minTime * repeats,
          cycling,
          played: false,
        };
        state.talkers.push(talker);
        // Only a blocking dialog (planDialog -> dialogFlag=true) becomes the
        // active one that gates gameplay via isDialog(); object:talk (band/
        // ambient) plays without blocking - matches the original exactly.
        if (dialogFlag) state.activeBlocking = talker;
        // Spawn a colored subtitle into the visual stack (decoupled from the
        // talking-state above), matching the original's SubTitleAgent. Empty-
        // subtitle "sound only" dialogs (viking d1-z-*) add nothing. See
        // docs/037.
        if (entry.subtitle) {
          state.pendingSubtitles.push({
            text: entry.subtitle,
            color: colorForFont(state, entry.font),
          });
        }
      },
    );
    // Cuts a model's current sound/subtitle short - legacy's DialogStack::
    // killSound(), needed by e.g. viking1's instrument-swapping NPCs
    // (melodak1/piskac/basak cut the previous note before playing the
    // next) - see docs/018.
    lua.global.set("model_killSound", (index: number) => killTalkers(state, index));
    lua.global.set("model_isTalking", (index: number) =>
      state.talkers.some((t) => t.actorIndex === index),
    );
    lua.global.set("dialog_isDialog", () => isDialogActive(state));
    // Kills the plan queue outright (Planner::killPlan) - cheap and
    // correct now that the queue is real, rather than leaving stale
    // actions to run once game_planAction stopped being a no-op.
    lua.global.set("game_killPlan", () => {
      state.pendingActions.length = 0;
      state.frontCount = 0;
    });
    lua.global.set("game_planAction", (callback: (count: number) => unknown) => {
      state.pendingActions.push((count) => Boolean(callback(count)));
    });
    lua.global.set("game_isPlanning", () => state.pendingActions.length > 0);

    lua.global.set("game_getCycles", () => state.cycles);
    // script_update() is called exactly once per physics round, always -
    // there's no separate render-vs-logic distinction to detect here.
    lua.global.set("level_isNewRound", () => true);
    lua.global.set("level_isSolved", () => false);
    // The level's static position in the world-map campaign tree
    // (LevelNode::m_depth), not a stuck/death counter - we have no
    // world-map system, so this stays a constant (docs/015).
    lua.global.set("level_getDepth", () => 0);
    // Real now (docs/015): Level::m_restartCounter starts at 1 and
    // increments once per restart, nothing fancier - restartCount is
    // LevelScene's own matching counter, passed straight through.
    lua.global.set("level_getRestartCounter", () => restartCount);

    // Top-level code.lua includes are pre-run at bootstrap (below), so a
    // file_include() call for one is a no-op here. A *runtime* include
    // (only briefcase's demo_help.lua) was wrapped as a callable instead -
    // defer running it until after script_update() returns (runPendingIncludes),
    // never from inside this host callback (docs/008 reentrancy). See docs/031.
    lua.global.set("file_include", (path: string) => {
      const run = state.runtimeIncludes.get(path);
      if (run) state.pendingIncludes.push(run);
    });
    lua.global.set("codename", levelName);
    // OptionAgent config lookup (language/subtitle settings etc.) - this
    // port has no options UI yet (docs/027's "Follow-up"). Returning ""
    // (not null/undefined - both crash/misbehave in wasmoon, docs/024)
    // matches levelLoader.ts's existing static-pass stub exactly.
    lua.global.set("options_getParam", () => "");

    await lua.doString(compatSource);
    await lua.doString("text = {}");
    await lua.doString(levelCreationSource);
    await lua.doString(levelPlanSource);
    await lua.doString(levelUpdateSource);
    await lua.doString(levelFontsSource);
    await lua.doString(levelDialogSource);
    await lua.doString(goanimSource);
    await lua.doString(finderSource);
    await lua.doString(compatibleSource);
    await lua.doString(pickleSource);
    await lua.doString(progSaveSource);
    await lua.doString(SAVE_STATE_GLUE_SOURCE);
    await lua.doString(borejokesSource);
    await lua.doString(blackjokesSource);
    await lua.doString(bublesSource);
    await lua.doString(bordershoutSource);
    await lua.doString(levelStartSource);
    // Matches each file's own real dialogLoad(prefix, soundPrefix) call -
    // legacy/script/share/bordershout.lua, borejokes.lua, blackjokes.lua -
    // and level_dialog.lua's own default soundPrefix ("sound/"..codename..
    // "/") for a level's own dialogs file.
    currentSoundPrefix = `sound/share/border/${DIALOG_LANG}/`;
    await lua.doString(shoutDialogsSource);
    currentSoundPrefix = `sound/share/borejokes/${DIALOG_LANG}/`;
    await lua.doString(boreDialogsSource);
    currentSoundPrefix = `sound/share/blackjokes/${DIALOG_LANG}/`;
    await lua.doString(blackDialogsSource);
    currentSoundPrefix = `sound/${levelName}/${DIALOG_LANG}/`;
    await lua.doString(levelDialogsSource);
    // English fallback (docs/036): registers only dialogs the localized file
    // didn't (level_dialog.lua's dialogId no-ops for already-primed ones - and
    // the localized file uses the same English default subtitle, so no
    // mismatch warnings), with sounds resolved from sound/<level>/en/. This is
    // where viking1's instrument "song" clips (d1-z-*) get registered.
    if (levelDialogsFallbackSource) {
      currentSoundPrefix = `sound/${levelName}/en/`;
      await lua.doString(levelDialogsFallbackSource);
    }
    await lua.doString(modelsSource);
    let includeIndex = 0;
    for (const { path, source } of includedSources) {
      if (runtimeIncludePaths.has(path)) {
        // Wrap (don't run) - invoked later via file_include() on the trigger.
        const fnName = `ffwg_include_${includeIndex++}`;
        await lua.doString(`function ${fnName}()\n${source}\nend`);
        const fn = lua.global.get(fnName) as () => void;
        state.runtimeIncludes.set(path, fn);
      } else {
        await lua.doString(source);
      }
    }
    // codeSource's own top-level prog_init() already calls initModels()
    // itself (confirmed for airplane; level_start.lua's own comment says
    // "Run this function in you init" - every level's code.lua is expected
    // to call it, not the driver) - calling it again here would duplicate
    // borderShoutLoad()/stdBoreJokeLoad()/etc.'s side effects.
    await lua.doString(codeSource);

    const scriptUpdate = lua.global.get("script_update") as () => unknown;
    const captureModelStateFn = lua.global.get("ffwg_captureModelState") as () => string;
    const restoreModelStateFn = lua.global.get("ffwg_restoreModelState") as (
      serialized: string,
    ) => void;
    return new LevelScript(
      lua,
      scriptUpdate,
      state,
      captureModelStateFn,
      restoreModelStateFn,
    );
  } catch (error) {
    lua.global.close();
    throw error;
  }
}
