import { LuaFactory } from "wasmoon";

import { fetchLegacyFile } from "./levelLoader";

/** Map/level-name language - matches DIALOG_LANG (levelScript.ts, docs/018)
 *  for consistency across the whole port, per the user's choice. */
export const MAP_LANG = "cs";

/** One entry from legacy/script/worldmap.lua's branch_addNode() calls -
 *  `datafile`/`poster` are dropped: this port's loadLevelModels() already
 *  assumes the `script/<codename>/` convention directly, and posters
 *  (branch-ending recap cutscenes) are out of scope for now. */
export interface WorldMapNode {
  codename: string;
  x: number;
  y: number;
  hidden: boolean;
  parent: string | null;
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
  /** codename -> display name, MAP_LANG only. */
  names: Map<string, string>;
  bestSolutions: Map<string, BestSolution>;
}

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
 * never a drawable/clickable map dot there either - it has no x/y
 * position at all in worldmap.lua, and WorldMap.cpp keeps it out of the
 * regular node tree entirely, auto-triggering it only once every other
 * node is solved (LevelNode::areAllSolved()). That auto-trigger is out of
 * scope for this sandbox pass, so the ending node is parsed (to keep the
 * host binding real/complete) but simply not added to `nodes`.
 */
export async function loadWorldMap(): Promise<WorldMapData> {
  const mapSource = await fetchLegacyFile("script/worldmap.lua");
  const descSource = await fetchLegacyFile("script/worlddesc.lua");
  const fameSource = await fetchLegacyFile("script/worldfame.lua");

  const factory = new LuaFactory();
  const lua = await factory.createEngine();

  const nodes: WorldMapNode[] = [];
  let rootCodename = "";
  const names = new Map<string, string>();
  const bestSolutions = new Map<string, BestSolution>();

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
        _poster?: string | null,
      ) => {
        if (!parent) rootCodename = codename;
        nodes.push({ codename, x, y, hidden: hidden ?? false, parent: parent || null });
      },
    );
    // See doc comment - deliberately not added to `nodes`.
    lua.global.set("branch_setEnding", () => {});
    lua.global.set(
      "worldmap_addDesc",
      (codename: string, lang: string, name: string, _desc: string) => {
        if (lang === MAP_LANG) names.set(codename, name);
      },
    );
    lua.global.set("node_bestSolution", (codename: string, moves: number, author: string) => {
      bestSolutions.set(codename, { moves, author });
    });
    lua.global.set("file_include", () => {});

    await lua.doString(mapSource);
    await lua.doString(descSource);
    await lua.doString(fameSource);
  } finally {
    lua.global.close();
  }

  return { nodes, rootCodename, names, bestSolutions };
}
