import type { WorldMapData, WorldMapNode } from "../lua/worldMapLoader";

/** legacy's LevelNode::eState - purely derived here (a pure function over
 *  the graph + solved set), not mutated in place like the original's
 *  LevelNode::setState()/addChild() promotion cascade - see docs/027. */
export type NodeState = "hidden" | "far" | "open" | "solved";

/**
 * Derives every node's visual/interactive state from the level graph plus
 * whichever codenames are solved (this port's own `loadSolvedMoves`,
 * docs/026) - legacy's WorldBranch::prepareNode()/LevelNode::setState()'s
 * solved-parent-promotes-children cascade, computed fresh each call
 * instead of incrementally mutated, since this port always has instant
 * access to the full solved set (no incremental "just solved one node"
 * signal is needed - see docs/027's reasoning for recomputing the whole
 * map from localStorage on every return instead of tracking deltas).
 *
 * `sandboxMode` (docs/027; wired to the URL in docs/045 - true on /sandbox,
 * false on /): every node that isn't already solved is forced `"open"`,
 * including ones that would normally be `"hidden"` (secret branches) or
 * `"far"` (locked). False gives real progression-gating - the standard game.
 */
export function computeNodeStates(
  data: WorldMapData,
  solved: ReadonlySet<string>,
  sandboxMode: boolean,
): Map<string, NodeState> {
  const byCodename = new Map<string, WorldMapNode>(data.nodes.map((n) => [n.codename, n]));
  const childrenOf = new Map<string, string[]>();
  for (const node of data.nodes) {
    if (node.parent === null) continue;
    const siblings = childrenOf.get(node.parent);
    if (siblings) siblings.push(node.codename);
    else childrenOf.set(node.parent, [node.codename]);
  }

  const states = new Map<string, NodeState>();

  function resolve(codename: string): NodeState {
    const cached = states.get(codename);
    if (cached) return cached;

    if (solved.has(codename)) {
      states.set(codename, "solved");
      return "solved";
    }

    const node = byCodename.get(codename);
    const parentSolved = node?.parent ? resolve(node.parent) === "solved" : true;
    const base: NodeState = parentSolved ? "open" : node?.hidden ? "hidden" : "far";
    states.set(codename, base);
    return base;
  }

  for (const node of data.nodes) resolve(node.codename);

  if (!sandboxMode) return states;

  for (const [codename, state] of states) {
    if (state !== "solved") states.set(codename, "open");
  }
  return states;
}
