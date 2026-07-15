import { LuaFactory } from "wasmoon";

import { fetchLegacyFile } from "./levelLoader";

/** Map/level-name language - matches DIALOG_LANG (levelScript.ts, docs/018)
 *  for consistency across the whole port, per the user's choice. */
export const MAP_LANG = "cs";

/** One entry from legacy/script/worldmap.lua's branch_addNode() calls -
 *  `datafile` is dropped (this port's loadLevelModels() assumes the
 *  `script/<codename>/` convention directly). The `poster` (7th arg) is a
 *  final level's recap cutscene, captured separately in WorldMapData.posters
 *  (docs/050). */
export interface WorldMapNode {
  codename: string;
  x: number;
  y: number;
  hidden: boolean;
  parent: string | null;
}

/** The special ending node (branch_setEnding) - a real playable level
 *  ("both fish at home") with no map position, auto-run once every other
 *  level is solved (docs/050). */
export interface EndingNode {
  codename: string;
  poster: string | null;
}

export interface BestSolution {
  moves: number;
  author: string;
}

export interface WorldMapData {
  /** Every real, positioned node - the "ending" node is deliberately
   *  excluded (see loadWorldMap doc comment). */
  nodes: WorldMapNode[];
  rootCodename: string;
  /** codename -> display name (worldmap_addDesc's `levelname`), MAP_LANG
   *  only - e.g. "start" -> "Jak to všechno začalo". */
  names: Map<string, string>;
  /** codename -> section/house name (worldmap_addDesc's `desc`), MAP_LANG
   *  only - e.g. "start" -> "Rybí domeček". The original composes a level's
   *  window caption as `<section>: <name>` (Level::initScreen) and the map's
   *  own caption from the special "menu" entry's section text
   *  (WorldMap.cpp -> findDesc("menu") = "Fish Fillets - Next Generation").
   *  See LevelScene / WorldMapScene document.title handling. */
  sections: Map<string, string>;
  bestSolutions: Map<string, BestSolution>;
  /** codename -> that level's poster cutscene (a `demo_poster.lua` DemoMode
   *  script, legacy-relative path). Only the 9 world-final levels + the
   *  ending have one; absent for the rest. See docs/050. */
  posters: Map<string, string>;
  /** The ending node (branch_setEnding), or null. Not part of `nodes` (it has
   *  no map position); auto-launched when everything else is solved. */
  ending: EndingNode | null;
  /** codename -> LevelNode::m_depth: 1 at the root, parent+1 for each child
   *  (real range here is 1..15); the ending is -1. blackjokes.lua picks its
   *  death-joke tier with `switch(level_getDepth())`. See docs/054. */
  depths: Map<string, number>;
  /** legacy Pedometer's SolverDrawer strings from script/labels.lua, keyed
   *  `<name>:<lang>` (name = solver_better/solver_equals/solver_worse). Only
   *  these 3 labels are captured. `%1`/`%2` placeholders are filled at render
   *  time with the best move count + author (Dialog::getFormatedSubtitle). */
  solverLabels: Map<string, string>;
}

/** The three SolverDrawer labels captured from labels.lua. */
const SOLVER_LABELS = new Set(["solver_better", "solver_equals", "solver_worse"]);

/**
 * One-shot (non-persistent) wasmoon parse of legacy/script/worldmap.lua -
 * mirrors levelLoader.ts's loadLevelModels() pattern (a throwaway engine
 * just to run declarative setup calls through real host bindings), not
 * levelScript.ts's persistent live-engine pattern - nothing here needs to
 * tick per round. worldmap.lua's own two file_include() calls
 * (worlddesc.lua/worldfame.lua) are pre-fetched and run directly afterward
 * (file_include itself stays a no-op host binding), the same "pre-scan
 * instead of a reentrant host callback" approach docs/008 established.
 *
 * The "ending" node (branch_setEnding()) is real in the original but is
 * never a drawable/clickable map dot there - it has no x/y position in
 * worldmap.lua, and WorldMap.cpp keeps it out of the regular node tree,
 * auto-triggering it only once every other node is solved
 * (LevelNode::areAllSolved()). It's captured here as `ending` (not added to
 * `nodes`); WorldMapScene decides when to run it (docs/050).
 */
export async function loadWorldMap(): Promise<WorldMapData> {
  const mapSource = await fetchLegacyFile("script/worldmap.lua");
  const descSource = await fetchLegacyFile("script/worlddesc.lua");
  const fameSource = await fetchLegacyFile("script/worldfame.lua");
  const labelsSource = await fetchLegacyFile("script/labels.lua");

  const factory = new LuaFactory();
  const lua = await factory.createEngine();

  const nodes: WorldMapNode[] = [];
  let rootCodename = "";
  const names = new Map<string, string>();
  const sections = new Map<string, string>();
  const bestSolutions = new Map<string, BestSolution>();
  const posters = new Map<string, string>();
  let ending: EndingNode | null = null;
  const solverLabels = new Map<string, string>();

  try {
    lua.global.set(
      "branch_addNode",
      (
        parent: string,
        codename: string,
        _datafile: string,
        x: number,
        y: number,
        hidden?: boolean | null,
        poster?: string | null,
      ) => {
        if (!parent) rootCodename = codename;
        nodes.push({ codename, x, y, hidden: hidden ?? false, parent: parent || null });
        if (poster) posters.set(codename, poster);
      },
    );
    // The ending node has no map position - captured, not added to `nodes`.
    lua.global.set(
      "branch_setEnding",
      (codename: string, _datafile: string, poster?: string | null) => {
        ending = { codename, poster: poster || null };
        if (poster) posters.set(codename, poster);
      },
    );
    lua.global.set(
      "worldmap_addDesc",
      (codename: string, lang: string, name: string, desc: string) => {
        if (lang === MAP_LANG) {
          names.set(codename, name);
          sections.set(codename, desc);
        }
      },
    );
    lua.global.set("node_bestSolution", (codename: string, moves: number, author: string) => {
      bestSolutions.set(codename, { moves, author });
    });
    lua.global.set("file_include", () => {});
    // labels.lua only calls label_text(name, lang, text) - capture the 3
    // pedometer solver labels for every language (picked by setting at render).
    lua.global.set("label_text", (name: string, lang: string, text: string) => {
      if (SOLVER_LABELS.has(name)) solverLabels.set(`${name}:${lang}`, text);
    });

    await lua.doString(mapSource);
    await lua.doString(descSource);
    await lua.doString(fameSource);
    await lua.doString(labelsSource);
  } finally {
    lua.global.close();
  }

  return {
    nodes,
    rootCodename,
    names,
    sections,
    bestSolutions,
    posters,
    ending,
    depths: computeDepths(nodes, ending),
    solverLabels,
  };
}

/** legacy LevelNode: `m_depth = 1` at construction and `addChild()` sets each
 *  child to parent+1, so a node's depth is its 1-based distance from the root.
 *  WorldBranch gives the ending -1. See docs/054. */
function computeDepths(nodes: WorldMapNode[], ending: EndingNode | null): Map<string, number> {
  const parents = new Map(nodes.map((n) => [n.codename, n.parent]));
  const depths = new Map<string, number>();
  const depthOf = (codename: string, seen = new Set<string>()): number => {
    const cached = depths.get(codename);
    if (cached !== undefined) return cached;
    const parent = parents.get(codename);
    // A missing/cyclic parent can't happen in the real map ("cycles in graph
    // are not supported"), but don't hang if the data ever changes.
    const d = !parent || seen.has(codename) ? 1 : 1 + depthOf(parent, seen.add(codename));
    depths.set(codename, d);
    return d;
  };
  for (const node of nodes) depthOf(node.codename);
  if (ending) depths.set(ending.codename, -1);
  return depths;
}
