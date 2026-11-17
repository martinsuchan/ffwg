// Plan interrupt + start-of-level greet (docs/082). Headless, no Phaser scene:
//  0. legacy Controls::Controls m_switch = true - the first interactive round
//     greets the active fish (activate pose) and leaves a queued key unread;
//     the key moves the fish on the next round.
//  1. A death (legacy Room::prepareRound -> LevelScript::interruptPlan): a
//     small fish under an item_heavy dies of stress in round 1 ->
//     takePlanInterrupt() true exactly once, and every busy flag is cleared.
//  2. A model going fully out (legacy Room::fallout, outDepth == -1) ->
//     takePlanInterrupt() true once it's out.
//  3. LevelScript.interruptPlan() empties a real level's plan FIFO
//     (game_isPlanning() true -> false).

import { gotoWorldmap, assert } from "../lib.mjs";

export const name = "plan interrupt (death / exit / Lua plan FIFO) + start greet";

export default async function ({ page, baseURL }) {
  await gotoWorldmap(page, baseURL);

  const physics = await page.evaluate(async () => {
    const { GameEngine } = await import("/src/game/GameEngine.ts");
    const model = (kind, x, y, goal) => ({
      kind, x, y, shape: "X", goal, anims: {},
      initialAnim: null, initialPhase: 0, initialEffect: null, isLeft: true,
    });
    const level = (models) => ({
      levelName: "test", roomWidth: 3, roomHeight: 3, bgPicture: "",
      models, waves: { amp: 0, periode: 1, speed: 0 },
    });
    const idle = { isPressed: () => false };
    const out = {};

    // 0. Start greet.
    {
      const engine = new GameEngine(level([model("fish_small", 1, 1, "goal_alive")]));
      let queued = "ArrowLeft";
      let takes = 0;
      const input = {
        isPressed: () => false,
        takeQueuedKey: () => { takes++; const k = queued; queued = null; return k; },
      };
      engine.tick(input);
      const r1 = engine.getRenderModels()[0];
      const takesRound1 = takes;
      engine.tick(input); // the queued key decides the move here...
      engine.tick(idle); // ...which commits at the next round's prepareRound
      const r3 = engine.getRenderModels()[0];
      out.greet = {
        round1Action: r1.action, round1X: r1.x, takesRound1, finalX: r3.x, moves: engine.getMoves(),
      };
    }

    // 1. Death by stress interrupts the plan and unbusies everything.
    {
      const engine = new GameEngine(level([
        model("fish_small", 1, 1, "goal_alive"),
        model("item_heavy", 1, 0, "goal_no"),
      ]));
      engine.setBusy(0, true);
      engine.setBusy(1, true);
      const before = engine.takePlanInterrupt();
      engine.tick(idle);
      const dead = engine.lastDead.map((c) => c.kind);
      const first = engine.takePlanInterrupt();
      const second = engine.takePlanInterrupt();
      const busyAfter = engine.room.models.map((c) => c.busy);
      let later = false;
      for (let i = 0; i < 20; i++) {
        engine.tick(idle);
        later ||= engine.takePlanInterrupt();
      }
      out.death = { before, dead, first, second, busyAfter, later };
    }

    // 2. A goal_escape fish at the border swims out on its own.
    {
      const engine = new GameEngine(level([model("fish_small", 0, 1, "goal_escape")]));
      const hits = [];
      let lostRound = -1;
      for (let round = 1; round <= 20; round++) {
        engine.tick(idle);
        if (engine.takePlanInterrupt()) hits.push(round);
        if (lostRound === -1 && engine.getRenderModels()[0].isLost) lostRound = round;
      }
      out.exit = { hits, lostRound, solved: engine.isSolved() };
    }
    return out;
  });

  const g = physics.greet;
  assert(g.round1Action === "activate", `round 1 should greet (action "activate"), got "${g.round1Action}"`);
  assert(g.takesRound1 === 0, "round 1 must not read the queued key");
  assert(g.round1X === 1, `round 1 must not move the fish (x=${g.round1X})`);
  assert(g.moves === "l" && g.finalX === 0, `queued key should move the fish on round 2 (moves="${g.moves}", x=${g.finalX})`);

  const d = physics.death;
  assert(d.before === false, "fresh engine must have no pending plan interrupt");
  assert(d.dead.length === 1 && d.dead[0] === "fish_small", `fish should die in round 1, died: [${d.dead}]`);
  assert(d.first === true, "death should set the plan interrupt");
  assert(d.second === false, "takePlanInterrupt() must clear the flag");
  assert(d.busyAfter.every((b) => b === false), `interrupt should unbusy all models: [${d.busyAfter}]`);
  assert(d.later === false, "a dead fish must not re-trigger the interrupt");

  const e = physics.exit;
  assert(e.lostRound > 0, "goal_escape fish should leave the room within 20 rounds");
  assert(
    e.hits.length === 1 && e.hits[0] === e.lostRound,
    `exit should interrupt exactly once, on the round it goes fully out (hits [${e.hits}], out at ${e.lostRound})`,
  );

  // 3. The live Lua half: a real level's plan FIFO is emptied.
  const lua = await page.evaluate(async () => {
    const loader = await import("/src/lua/levelLoader.ts");
    const scriptMod = await import("/src/lua/levelScript.ts");
    // Same full EngineControl stub as 05-levels-load.
    const engineControl = {
      setBusy() {},
      checkActive() {},
      setFastFalling() {},
      askFieldIndex() { return null; },
      isSolved() { return false; },
    };
    // Levels whose first update() plans an intro dialog (room.uvod == 0 ->
    // addm/addv) once both fish are ready and no dialog is running.
    const candidates = ["airplane", "broom", "barrel"];
    const tried = [];
    for (const cn of candidates) {
      const levelData = await loader.loadLevelModels(cn);
      const rm = levelData.models.map((m, i) => ({
        index: i, kind: m.kind, x: m.x, y: m.y, isLeft: m.isLeft,
        isAlive: true, isOut: false, isLost: false, action: "rest",
        state: "normal", moveStreak: 0, isAtBorder: false, touchDir: 0,
      }));
      const script = await scriptMod.createLevelScript(cn, rm, 1, undefined, engineControl);
      try {
        const isPlanning = () => Boolean(script.lua.global.get("game_isPlanning")());
        let round = 0;
        while (round < 200 && !isPlanning()) {
          script.tick(rm);
          round++;
        }
        if (!isPlanning()) {
          tried.push(`${cn}: no plan in ${round} rounds`);
          continue;
        }
        script.interruptPlan();
        const after = isPlanning();
        script.tick(rm); // the round loop survives an emptied FIFO
        return { cn, round, after, tried };
      } finally {
        script.destroy();
      }
    }
    return { cn: null, tried };
  });

  assert(lua.cn !== null, `no candidate level planned anything: ${lua.tried.join(" | ")}`);
  assert(lua.after === false, `${lua.cn}: game_isPlanning() still true after interruptPlan()`);

  return {
    detail:
      `greet ok; death interrupt once + unbusy; exit interrupt at round ${e.lostRound}; ` +
      `${lua.cn} plan cleared (planning after ${lua.round} round(s))`,
  };
}
