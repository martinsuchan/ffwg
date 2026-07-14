/**
 * Animation/round timing - the phase-locked model ported from the original
 * (docs/046).
 *
 * The original runs a fixed-rate cycle (`TimerAgent::timeinterval`, 100ms) and
 * a move is "locked" for N *cycles* (`PhaseLocker`), animating one grid cell
 * over those N draws (`View::m_animShift` grows SCALE/N per cycle). N ("phases")
 * comes from the active fish's speed (`Controls::getNeededPhases`): 3 normal, 2
 * then 1 as you hold a direction; a move fully animates before the next logic
 * round runs.
 *
 * `CYCLE_MS` is that per-cycle interval - the single base-speed knob. A move
 * takes `phases · CYCLE_MS` ms, so base = 3·100 = 300ms/cell, accelerating to
 * 200 then 100ms. The 3/2/1 phase ratios are structural (the swim animation's
 * frame count), so this one constant scales base speed *and* every acceleration
 * tier proportionally - an ideal future "Advanced settings" slider.
 */
export const CYCLE_MS = 100;

/** Round interval when nothing is moving: one cycle, so held/tapped input is
 *  polled promptly (mirrors the original running a logic round every idle
 *  cycle). A moving round instead lasts `phases · CYCLE_MS` (see the scene). */
export const IDLE_ROUND_MS = CYCLE_MS;
