import { GameEngine } from "./GameEngine";

export interface SolutionResult {
  solved: boolean;
  /** Number of moves actually applied before stopping - equals moves.length
   *  on success, or the index of the first bad move on failure. */
  steps: number;
  /** Set only when a move failed - the symbol and its index in the string. */
  failedAt?: { index: number; symbol: string };
  error?: string;
}

/**
 * Fast, render-free replay of a full move string against a fresh engine -
 * legacy's Room::loadMove() driven end to end, used to check a solution (or
 * any candidate move string) without Phaser/LevelScene/AudioManager at all.
 * Stops at the first invalid move rather than throwing past it, so a bad
 * solution reports *where* it broke instead of just "no". See docs/022.
 */
export function validateSolution(engine: GameEngine, moves: string): SolutionResult {
  for (let index = 0; index < moves.length; index++) {
    const symbol = moves[index];
    try {
      engine.loadMove(symbol);
    } catch (error) {
      return {
        solved: false,
        steps: index,
        failedAt: { index, symbol },
        error: error instanceof Error ? error.message : String(error),
      };
    }
  }
  engine.settleAll();
  return { solved: engine.isSolved(), steps: moves.length };
}
