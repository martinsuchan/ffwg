import type { LevelData } from "../lua/levelLoader";
import { extractSavedMoves } from "../lua/levelLoader";
import { GameEngine } from "./GameEngine";

/**
 * Compares two solutions of one level by the **game states** they pass
 * through, not by their move strings (docs/086). The same string means
 * something else in another state, and different strings can reach the same
 * state, so only states line the two up reliably.
 *
 * Each solution is replayed headless (Room.loadMove, as the validator does,
 * docs/022) and fingerprinted after every move. A Myers diff of the two
 * fingerprint sequences gives the states both pass through, in order; between
 * two such shared states either both play the same moves ("same") or they
 * differ ("diff"). For the window optimiser's output a diff is exactly a
 * replaced window: it starts and ends in a state both solutions share.
 */

/** Interns state fingerprints to small integers, shared by the two traces. */
export class StateInterner {
  private readonly ids = new Map<string, number>();

  id(key: string): number {
    let id = this.ids.get(key);
    if (id === undefined) {
      id = this.ids.size;
      this.ids.set(key, id);
    }
    return id;
  }
}

/**
 * Which models are interchangeable: items with the same weight, shape and goal
 * (the solver merges such twins too, so a shortened solution may leave two of
 * them swapped). Every fish is its own class. Returns a class id per model.
 */
export function modelClasses(levelData: LevelData): number[] {
  const ids = new Map<string, number>();
  return levelData.models.map((model, index) => {
    const key = model.kind.startsWith("item_") ? `${model.kind}|${model.goal}|${model.shape}` : `#${index}`;
    let id = ids.get(key);
    if (id === undefined) {
      id = ids.size;
      ids.set(key, id);
    }
    return id;
  });
}

/**
 * The physical state: every model's cell and life/exit flags, with twin items
 * (modelClasses) compared as a set. Facing is left out: turning is cosmetic
 * (Cube.isLeft never enters the rules), and the solver ignores it as well.
 */
export function stateKey(engine: GameEngine, classes: number[]): string {
  const groups: string[][] = [];
  engine.room.models.forEach((cube, index) => {
    const cell = `${cube.location.x},${cube.location.y},${cube.isAlive ? 1 : 0}${cube.isOut ? 1 : 0}${cube.isLost ? 1 : 0}`;
    (groups[classes[index]] ??= []).push(cell);
  });
  return groups.map((cells) => (cells.length > 1 ? cells.sort().join("|") : cells[0])).join(";");
}

export interface SolutionTrace {
  /** The playable moves: the input up to (not including) the first invalid one. */
  moves: string;
  /** Length of the input as given. */
  inputLength: number;
  /** State ids: keys[0] = start, keys[i] = after move i; length moves.length + 1. */
  keys: number[];
  solved: boolean;
  /** The first move that could not be played, if any. */
  failedAt?: { index: number; symbol: string };
  /** The first move after which the level could no longer be solved (a fish died). */
  diedAt?: number;
}

/**
 * Replays `moves` headless and fingerprints the state after each one. Never
 * throws for a bad move: the trace stops there and records where.
 */
export function traceSolution(levelData: LevelData, moves: string, interner: StateInterner): SolutionTrace {
  const engine = new GameEngine(levelData);
  const classes = modelClasses(levelData);
  const keys = [interner.id(stateKey(engine, classes))];
  let failedAt: SolutionTrace["failedAt"];
  let diedAt: number | undefined;
  let played = 0;
  for (; played < moves.length; played++) {
    try {
      engine.loadMove(moves[played]);
    } catch {
      failedAt = { index: played, symbol: moves[played] };
      break;
    }
    // loadMove settles *before* its move; settle now so the fingerprint shows
    // where this move left things (the next loadMove's settle is then a no-op).
    engine.settleAll();
    keys.push(interner.id(stateKey(engine, classes)));
    if (diedAt === undefined && !engine.isSolvable()) diedAt = played + 1;
  }
  engine.settleAll();
  return {
    moves: moves.slice(0, played),
    inputLength: moves.length,
    keys,
    solved: !failedAt && engine.isSolved(),
    failedAt,
    diedAt,
  };
}

/**
 * Index pairs (i, j) with a[i] === b[j] forming a longest common subsequence,
 * ascending in both. Myers' O((N+M)D) diff in linear space (the middle-snake
 * bisection, as in diff-match-patch's diff_bisect), after trimming the common
 * prefix and suffix - which for an optimised solution is nearly everything.
 */
export function commonStates(a: ArrayLike<number>, b: ArrayLike<number>): Array<[number, number]> {
  const out: Array<[number, number]> = [];
  diffRange(a, 0, a.length, b, 0, b.length, out);
  out.sort((p, q) => p[0] - q[0]);
  return out;
}

function diffRange(
  a: ArrayLike<number>, aLo: number, aHi: number,
  b: ArrayLike<number>, bLo: number, bHi: number,
  out: Array<[number, number]>,
): void {
  while (aLo < aHi && bLo < bHi && a[aLo] === b[bLo]) {
    out.push([aLo++, bLo++]);
  }
  while (aLo < aHi && bLo < bHi && a[aHi - 1] === b[bHi - 1]) {
    out.push([--aHi, --bHi]);
  }
  if (aLo === aHi || bLo === bHi) return;
  bisect(a, aLo, aHi, b, bLo, bHi, out);
}

function bisect(
  a: ArrayLike<number>, aLo: number, aHi: number,
  b: ArrayLike<number>, bLo: number, bHi: number,
  out: Array<[number, number]>,
): void {
  const n = aHi - aLo;
  const m = bHi - bLo;
  const maxD = Math.ceil((n + m) / 2);
  const vOffset = maxD;
  const vLength = 2 * maxD + 2;
  const v1 = new Int32Array(vLength).fill(-1);
  const v2 = new Int32Array(vLength).fill(-1);
  v1[vOffset + 1] = 0;
  v2[vOffset + 1] = 0;
  const delta = n - m;
  // With an odd delta the forward path meets the reverse one; else the reverse meets the forward.
  const front = delta % 2 !== 0;
  let k1start = 0;
  let k1end = 0;
  let k2start = 0;
  let k2end = 0;
  for (let d = 0; d < maxD; d++) {
    for (let k1 = -d + k1start; k1 <= d - k1end; k1 += 2) {
      const k1Offset = vOffset + k1;
      let x1 = k1 === -d || (k1 !== d && v1[k1Offset - 1] < v1[k1Offset + 1]) ? v1[k1Offset + 1] : v1[k1Offset - 1] + 1;
      let y1 = x1 - k1;
      while (x1 < n && y1 < m && a[aLo + x1] === b[bLo + y1]) {
        x1++;
        y1++;
      }
      v1[k1Offset] = x1;
      if (x1 > n) {
        k1end += 2;
      } else if (y1 > m) {
        k1start += 2;
      } else if (front) {
        const k2Offset = vOffset + delta - k1;
        if (k2Offset >= 0 && k2Offset < vLength && v2[k2Offset] !== -1 && x1 >= n - v2[k2Offset]) {
          split(a, aLo, aHi, b, bLo, bHi, x1, y1, out);
          return;
        }
      }
    }
    for (let k2 = -d + k2start; k2 <= d - k2end; k2 += 2) {
      const k2Offset = vOffset + k2;
      let x2 = k2 === -d || (k2 !== d && v2[k2Offset - 1] < v2[k2Offset + 1]) ? v2[k2Offset + 1] : v2[k2Offset - 1] + 1;
      let y2 = x2 - k2;
      while (x2 < n && y2 < m && a[aHi - 1 - x2] === b[bHi - 1 - y2]) {
        x2++;
        y2++;
      }
      v2[k2Offset] = x2;
      if (x2 > n) {
        k2end += 2;
      } else if (y2 > m) {
        k2start += 2;
      } else if (!front) {
        const k1Offset = vOffset + delta - k2;
        if (k1Offset >= 0 && k1Offset < vLength && v1[k1Offset] !== -1) {
          const x1 = v1[k1Offset];
          const y1 = vOffset + x1 - k1Offset;
          if (x1 >= n - x2) {
            split(a, aLo, aHi, b, bLo, bHi, x1, y1, out);
            return;
          }
        }
      }
    }
  }
  // No common element at all: nothing to match.
}

function split(
  a: ArrayLike<number>, aLo: number, aHi: number,
  b: ArrayLike<number>, bLo: number, bHi: number,
  x: number, y: number,
  out: Array<[number, number]>,
): void {
  diffRange(a, aLo, aLo + x, b, bLo, bLo + y, out);
  diffRange(a, aLo + x, aHi, b, bLo + y, bHi, out);
}

/** Moves a0..a0+len of A and b0..b0+len of B pass through the same states. */
export interface SameSegment {
  kind: "same";
  a0: number;
  b0: number;
  len: number;
}

/**
 * A plays moves [a0, a1), B plays [b0, b1), from one shared state to the
 * next. `closed` is false for a tail whose end states differ (one side failed,
 * or the two solutions end differently).
 */
export interface DiffSegment {
  kind: "diff";
  a0: number;
  a1: number;
  b0: number;
  b1: number;
  closed: boolean;
}

export type Segment = SameSegment | DiffSegment;

/** Same and diff segments covering both solutions' moves from start to end. */
export function alignTraces(a: SolutionTrace, b: SolutionTrace): Segment[] {
  const matches = commonStates(a.keys, b.keys);
  const segments: Segment[] = [];
  const pushSame = (i: number, j: number): void => {
    const last = segments[segments.length - 1];
    if (last?.kind === "same" && last.a0 + last.len === i && last.b0 + last.len === j) last.len++;
    else segments.push({ kind: "same", a0: i, b0: j, len: 1 });
  };
  // Both start in the level's initial state, so (0, 0) always matches.
  let [pi, pj] = matches[0] ?? [0, 0];
  if (pi !== 0 || pj !== 0) {
    segments.push({ kind: "diff", a0: 0, a1: pi, b0: 0, b1: pj, closed: true });
  }
  // Two differences that meet in one shared state are shown as one: a lone
  // coinciding state inside a replaced stretch is a sync point, not a stretch
  // both play alike.
  const pushDiff = (a0: number, a1: number, b0: number, b1: number, closed: boolean): void => {
    const last = segments[segments.length - 1];
    if (last?.kind === "diff" && last.a1 === a0 && last.b1 === b0) {
      last.a1 = a1;
      last.b1 = b1;
      last.closed = closed;
    } else {
      segments.push({ kind: "diff", a0, a1, b0, b1, closed });
    }
  };
  for (let k = 1; k < matches.length; k++) {
    const [i, j] = matches[k];
    if (i === pi + 1 && j === pj + 1) pushSame(pi, pj);
    else pushDiff(pi, i, pj, j, true);
    pi = i;
    pj = j;
  }
  const endA = a.keys.length - 1;
  const endB = b.keys.length - 1;
  if (pi < endA || pj < endB) pushDiff(pi, endA, pj, endB, false);
  return segments;
}

export interface CompareSummary {
  segments: Segment[];
  diffs: DiffSegment[];
  /** Moves both play from the same states. */
  sameMoves: number;
  /** sameMoves over the longer solution, 0..1. */
  similarity: number;
  /** B's length minus A's (negative: B is shorter). */
  delta: number;
}

export function compareTraces(a: SolutionTrace, b: SolutionTrace): CompareSummary {
  const segments = alignTraces(a, b);
  const diffs = segments.filter((s): s is DiffSegment => s.kind === "diff");
  const sameMoves = segments.reduce((sum, s) => sum + (s.kind === "same" ? s.len : 0), 0);
  const longer = Math.max(a.moves.length, b.moves.length);
  return {
    segments,
    diffs,
    sameMoves,
    similarity: longer === 0 ? 1 : sameMoves / longer,
    delta: b.moves.length - a.moves.length,
  };
}

/**
 * A pasted or dropped solution: a `.lua` file with `saved_moves = '...'`
 * (legacy/solution format, docs/021), or a bare move string. Whitespace is
 * dropped; null when nothing that looks like moves is left.
 */
export function parseMovesInput(text: string): string | null {
  const fromLua = extractSavedMoves(text);
  const moves = (fromLua ?? text).replace(/\s+/g, "");
  return moves.length > 0 ? moves : null;
}
