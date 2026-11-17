using System.Text;

using FishFillets.Physics;
using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// <see cref="ItemExpressibilityTests"/>' question, asked of <see cref="LayoutSolver"/>:
/// a known optimum is cut at every event, and from each checkpoint - both fish
/// exactly where the optimum has them - the real expansion has to produce the
/// next checkpoint's layout at no more than the optimum paid for it.
///
/// <para>It found <c>cannons</c>' 41 against 39 (solver/docs/026): two lifts
/// priced 2 for 1, because a fish that holds something up everywhere was
/// stepped one square off the margin round the actor before being committed.
/// It does <b>not</b> see what a deferred fish costs later - <c>wc</c> passes
/// here and still comes back at 104 against 100 - so zero gaps is necessary
/// for the optimum, not sufficient.</para>
/// </summary>
[TestClass]
public sealed class LayoutExpressibilityTests
{
    [TestMethod]
    [DataRow("start")]
    [DataRow("cannons")]
    [DataRow("wc")]
    [DataRow("submarine")]
    [DataRow("noground")]
    public void EveryEventInAKnownOptimumIsGeneratedAtItsPrice(string levelName)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        string moves = TestCorpus.Solution(levelName);
        LevelReduction reduction = LevelReduction.Verified(level, moves);
        int[] mobile = reduction.MobileModels;
        int[] items = [.. mobile.Where(m => !level.Models[m].IsAlive)];
        var table = new LayoutTable(level, mobile, 1 + (2 * level.Units.Length));

        // Cut into events.
        var room = new Room(level);
        var cps = new List<(int Move, byte[] Key)> { (-1, KeyOf(room, reduction)) };
        var before = Positions(room, items);
        bool[] gone = Gone(room, level);
        for (int i = 0; i < moves.Length; i++)
        {
            Assert.IsTrue(room.ApplyMove(moves[i]));
            var now = Positions(room, items);
            bool[] goneNow = Gone(room, level);
            if (now.SequenceEqual(before) && goneNow.SequenceEqual(gone))
            {
                continue;
            }

            cps.Add((i, KeyOf(room, reduction)));
            before = now;
            gone = goneNow;
        }

        var report = new StringBuilder();
        report.AppendLine($"{levelName}: {moves.Length} moves, {cps.Count - 1} events");
        int gaps = 0, over = 0, overSum = 0;
        for (int k = 1; k < cps.Count; k++)
        {
            (int fromMove, byte[] fromKey) = cps[k - 1];
            (int toMove, byte[] toKey) = cps[k];
            int expected = toMove - fromMove;
            char symbol = moves[toMove];
            int actorUnit = UnitOf(level, symbol);

            // Moves of the actor alone in the segment: the other fish's travel is deferred by the solver.
            int actorMoves = 0;
            for (int i = fromMove + 1; i <= toMove; i++)
            {
                if (UnitOf(level, moves[i]) == actorUnit)
                {
                    actorMoves++;
                }
            }

            byte[] want = new byte[table.WideSize];
            table.Canonicalize(toKey, want);

            var solver = new LayoutSolver(level, reduction);
            var traces = new List<string>();
            solver.Trace = (node, unit, placement, key, why) =>
            {
                if (key == symbol)
                {
                    traces.Add($"unit {unit} p={placement} {why}");
                }
            };
            List<(byte[] Layout, int Cost)> children = solver.ProbeSuccessors(fromKey);
            int best = int.MaxValue;
            foreach ((byte[] layout, int cost) in children)
            {
                if (layout.AsSpan().SequenceEqual(want) && cost < best)
                {
                    best = cost;
                }
            }

            if (best <= actorMoves)
            {
                continue;
            }

            string what;
            if (best == int.MaxValue)
            {
                gaps++;
                what = "NOT GENERATED";
            }
            else if (best <= expected)
            {
                what = $"cost {best}: above the actor's own {actorMoves}, within the segment's {expected}";
            }
            else
            {
                over++;
                overSum += best - expected;
                what = $"OVERCOST {best} > {expected}";
            }

            report.AppendLine($"  event {k,3} moves {fromMove + 1}..{toMove} '{moves[(fromMove + 1)..(toMove + 1)]}' actor-moves {actorMoves}: {what}");
            var a = new Room(level);
            var b = new Room(level);
            a.RestoreMobile(fromKey, mobile);
            b.RestoreMobile(toKey, mobile);
            var pre = new Room(level);
            for (int i = 0; i < toMove; i++)
            {
                pre.ApplyMove(moves[i]);
            }

            foreach (int model in mobile)
            {
                (int ax, int ay, bool al) = a.Position(model);
                (int px, int py, bool pl) = pre.Position(model);
                (int bx, int by, bool bl) = b.Position(model);
                bool alive = level.Models[model].IsAlive;
                if (!alive && ax == bx && ay == by)
                {
                    continue;
                }

                report.AppendLine(alive
                    ? $"      #{model} {level.Models[model].Kind} ({ax},{ay},{(al ? 'L' : 'R')}) -> before key ({px},{py},{(pl ? 'L' : 'R')}) -> ({bx},{by},{(bl ? 'L' : 'R')})"
                    : $"      #{model} {level.Models[model].Kind} ({ax},{ay}) -> ({bx},{by})");
            }

            foreach ((byte[] layout, int cost) in children)
            {
                if (layout.AsSpan().SequenceEqual(want))
                {
                    report.AppendLine($"      child with that layout at cost {cost}");
                }
            }

            foreach (string t in traces.Distinct().Take(12))
            {
                report.AppendLine($"      trace: {t}");
            }
        }

        report.AppendLine($"  gaps {gaps}, overcost {over} (sum {overSum})");
        Assert.AreEqual(0, gaps, report.ToString());
        Assert.AreEqual(0, over, report.ToString());
    }

    private static int UnitOf(Level level, char symbol)
    {
        for (int u = 0; u < level.Units.Length; u++)
        {
            UnitDef unit = level.Units[u];
            if (symbol == unit.Up || symbol == unit.Down || symbol == unit.Left || symbol == unit.Right)
            {
                return u;
            }
        }

        return -1;
    }

    private static byte[] KeyOf(Room room, LevelReduction reduction)
    {
        byte[] key = new byte[reduction.StateKeySize];
        room.WriteStateKey(key, reduction.MobileModels);
        return key;
    }

    private static (int, int)[] Positions(Room room, int[] items) =>
        [.. items.Select(m => { (int x, int y, _) = room.Position(m); return (x, y); })];

    private static bool[] Gone(Room room, Level level) =>
        [.. level.Units.Select(u => room.State(u.Model).IsOut || room.State(u.Model).IsLost)];
}
