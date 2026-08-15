using System.Text;

using FishFillets.Physics;
using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// Locates the missing edges in <see cref="ItemSolver"/>'s expansion instead of
/// guessing at them.
///
/// <para><c>--items</c> solves <c>start</c> at its known optimum but exhausts on
/// <c>wc</c>, <c>submarine</c> and <c>noground</c> - the frontier closes without
/// reaching a solved state, which means the expansion cannot express some edge,
/// not that it ran out of budget. Three plausible causes were implemented and
/// measured and none of them was it (solver/docs/009).</para>
///
/// <para>This is the direct question instead. A known optimum is cut at every
/// point where an item moved or a fish left - exactly the states
/// <c>ItemSolver</c> records - and each consecutive pair is checked by asking the
/// real expansion whether it produces that successor from that predecessor. The
/// first pair it cannot produce names the missing edge, and because every edge
/// reports why it was dropped, a successor that <i>is</i> generated but then
/// filtered away is distinguished from one that is never built at all.</para>
///
/// <para><b>What it found.</b> Every unreachable case across all three levels is
/// one cause: <see cref="InertRouter.IsFreeStep"/> refuses <i>every</i> step from
/// a placement where <see cref="InertRouter.IsHoldingSomethingUp"/> is true, and
/// that test is coarse by design - anything mobile directly overhead counts, with
/// no question of whether it is also propped up elsewhere. Its comment says
/// guessing wrong this way "only costs a search node", which is true of
/// <see cref="MacroSolver"/>, where a false positive turns travel into an action.
/// Here it turns travel into an <i>event edge</i>, which <c>TryEdge</c> then
/// throws away because nothing actually fell - so a fish that walks under a wide
/// item supported elsewhere can never move again, and the frontier closes. That
/// is the exhaustion.</para>
///
/// <para>The remaining gaps are the step-aside model: the other fish in a real
/// optimum travels to where its own next job is, up to 14 moves and cheapness
/// rank 54, while the expansion tries its four nearest resting places. Widening
/// that cap is not the fix - the ranking is wrong, not the number.</para>
/// </summary>
[TestClass]
public sealed class ItemExpressibilityTests
{
    /// <summary>
    /// <paramref name="expectedGaps"/> is transitions whose successor the
    /// expansion never produces at any price; <paramref name="expectedOvercost"/>
    /// is those it produces only more expensively than the optimum does, which
    /// costs moves rather than solvability.
    /// </summary>
    /// <para><b>These counts are a snapshot of a known incompleteness.</b> Every
    /// gap below has the same cause - see the class comment - and they should all
    /// go to zero when it is fixed. <c>start</c> is at zero already, which is why
    /// it is the one level <c>--items</c> solves.</para>
    [TestMethod]
    [DataRow("start", 0, 0)]
    [DataRow("wc", 8, 0)]
    [DataRow("submarine", 4, 0)]
    [DataRow("noground", 4, 0)]
    public void EveryEventInAKnownOptimumIsGeneratedFromTheOneBefore(
        string levelName, int expectedGaps, int expectedOvercost)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        string moves = TestCorpus.Solution(levelName);
        LevelReduction reduction = LevelReduction.Verified(level, moves);

        List<Checkpoint> checkpoints = CutIntoEvents(level, reduction, moves);

        var report = new StringBuilder();
        int gaps = 0, overcost = 0;

        for (int k = 1; k < checkpoints.Count; k++)
        {
            Checkpoint from = checkpoints[k - 1], to = checkpoints[k];
            int expectedCost = to.Move - from.Move;

            var seen = new List<(int Cost, ItemSolver.EdgeOutcome Outcome)>();
            var solver = new ItemSolver(level, reduction);
            solver.ProbeSuccessors(
                from.Key,
                (childKey, cost, outcome) =>
                {
                    if (childKey.SequenceEqual(to.Key))
                    {
                        seen.Add((cost, outcome));
                    }
                });

            int cheapestStored = int.MaxValue;
            foreach ((int cost, ItemSolver.EdgeOutcome outcome) in seen)
            {
                if (outcome == ItemSolver.EdgeOutcome.Stored && cost < cheapestStored)
                {
                    cheapestStored = cost;
                }
            }

            if (cheapestStored <= expectedCost)
            {
                continue;
            }

            string what = cheapestStored == int.MaxValue ? "NOT GENERATED" : $"cost {cheapestStored} > {expectedCost}";
            if (cheapestStored == int.MaxValue)
            {
                gaps++;
            }
            else
            {
                overcost++;
            }

            report.Append(
                $"\n  event {k,3}  moves {from.Move + 1}..{to.Move} ('{moves[to.Move]}')  {what}");
            report.Append(Describe(level, reduction, from, to, moves));
            report.Append(Diagnose(level, reduction, from, to, moves));

            if (seen.Count > 0)
            {
                report.Append("\n      the expansion did produce it, as: ");
                foreach ((int cost, ItemSolver.EdgeOutcome outcome) in seen.Distinct())
                {
                    report.Append($"{outcome}@{cost} ");
                }
            }
        }

        Assert.AreEqual(
            expectedGaps,
            gaps,
            $"{levelName}: {checkpoints.Count - 1} events, successors the expansion never builds:{report}\n");
        Assert.AreEqual(
            expectedOvercost,
            overcost,
            $"{levelName}: successors built only above the optimal cost:{report}\n");
    }

    private readonly record struct Checkpoint(int Move, byte[] Key);

    /// <summary>
    /// Cuts a solution at every point <see cref="ItemSolver"/> would record a
    /// state: an item somewhere new, or a fish gone. The comparison is against the
    /// last checkpoint rather than the last move, which is what the solver does -
    /// an item that moves and comes back inside one segment is not an event.
    /// </summary>
    private static List<Checkpoint> CutIntoEvents(Level level, LevelReduction reduction, string moves)
    {
        var room = new Room(level);
        int[] mobile = reduction.MobileModels;
        int[] items = [.. mobile.Where(m => !level.Models[m].IsAlive)];

        var checkpoints = new List<Checkpoint> { new(-1, KeyOf(room, reduction, mobile)) };
        (int X, int Y)[] before = PositionsOf(room, items);
        bool[] goneBefore = GoneFlags(room, level);

        for (int i = 0; i < moves.Length; i++)
        {
            Assert.IsTrue(room.ApplyMove(moves[i]), $"move {i} ('{moves[i]}') failed to replay");

            if (!SomethingHappened(room, level, items, before, goneBefore))
            {
                continue;
            }

            checkpoints.Add(new Checkpoint(i, KeyOf(room, reduction, mobile)));
            before = PositionsOf(room, items);
            goneBefore = GoneFlags(room, level);
        }

        Assert.IsGreaterThan(1, checkpoints.Count, "the solution moves nothing");
        return checkpoints;
    }

    private static bool SomethingHappened(
        Room room, Level level, int[] items, (int X, int Y)[] before, bool[] goneBefore)
    {
        for (int i = 0; i < items.Length; i++)
        {
            (int x, int y, _) = room.Position(items[i]);
            if (x != before[i].X || y != before[i].Y)
            {
                return true;
            }
        }

        for (int i = 0; i < level.Units.Length; i++)
        {
            if (HasGone(room, level.Units[i].Model) != goneBefore[i])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What actually changed over this segment, and where each fish was.</summary>
    private static string Describe(
        Level level, LevelReduction reduction, Checkpoint from, Checkpoint to, string moves)
    {
        var text = new StringBuilder();
        var a = new Room(level);
        var b = new Room(level);
        a.RestoreMobile(from.Key, reduction.MobileModels);
        b.RestoreMobile(to.Key, reduction.MobileModels);

        text.Append("\n      segment: '").Append(moves[(from.Move + 1)..(to.Move + 1)]).Append('\'');

        foreach (int model in reduction.MobileModels)
        {
            (int ax, int ay, bool aLeft) = a.Position(model);
            (int bx, int by, bool bLeft) = b.Position(model);
            bool alive = level.Models[model].IsAlive;
            if (ax == bx && ay == by && (!alive || aLeft == bLeft))
            {
                continue;
            }

            text.Append($"\n      #{model,-3} {level.Models[model].Kind,-12} ({ax},{ay}) -> ({bx},{by})");
            if (alive)
            {
                text.Append(aLeft == bLeft ? "" : bLeft ? " turned left" : " turned right");
                if (HasGone(b, model))
                {
                    text.Append(" GONE");
                }
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Names the stage that failed, rather than leaving it to be read off
    /// coordinates. An edge is: the other fish steps aside, the actor routes to a
    /// placement, one key is pressed. So the optimum's own version of that edge is
    /// reconstructed - replay to just before the event move, and both fish are
    /// exactly where this edge needed them - and each stage is asked whether it
    /// could have put them there.
    /// </summary>
    private static string Diagnose(
        Level level, LevelReduction reduction, Checkpoint from, Checkpoint to, string moves)
    {
        var pre = new Room(level);
        for (int i = 0; i <= to.Move - 1; i++)
        {
            pre.ApplyMove(moves[i]);
        }

        // The whole diagnosis rests on this replay agreeing with the recorded
        // checkpoints, so it is checked rather than assumed: pressing the event
        // move here has to produce exactly the state the checkpoint holds.
        var check = new Room(level);
        for (int i = 0; i <= to.Move; i++)
        {
            check.ApplyMove(moves[i]);
        }

        byte[] replayed = KeyOf(check, reduction, reduction.MobileModels);
        Assert.IsTrue(
            replayed.AsSpan().SequenceEqual(to.Key),
            "probe is inconsistent: replaying to the event move does not reproduce the checkpoint");

        int actorUnit = UnitOf(level, moves[to.Move]);
        int actor = level.Units[actorUnit].Model;
        (int ax, int ay, bool aLeft) = pre.Position(actor);

        var text = new StringBuilder();
        var parent = new Room(level);
        parent.RestoreMobile(from.Key, reduction.MobileModels);

        var router = new InertRouter(level, reduction);

        // Stage one: could the other fish have got out of the way like that?
        for (int u = 0; u < level.Units.Length; u++)
        {
            int blocker = level.Units[u].Model;
            if (blocker == actor)
            {
                continue;
            }

            (int bx, int by, bool bLeft) = pre.Position(blocker);
            (int px, int py, _) = parent.Position(blocker);
            if (bx == px && by == py)
            {
                continue; // it did not move, so no arrangement was needed
            }

            router.Route(parent, blocker);
            int wanted = router.Placement(bx, by, bLeft);
            bool reached = router.Reached.Contains(wanted);

            // Where in the cheapest-first list of destinations it sits. The
            // expansion only tries the first ItemSolver.ClearanceTries of them.
            int rank = -1;
            if (reached)
            {
                int cost = router.CostOf(wanted);
                var squares = new HashSet<int>();
                rank = 0;
                foreach (int placement in router.Reached)
                {
                    int c = router.CostOf(placement);
                    (int qx, int qy, _) = router.Decode(placement);
                    if (c > 0 && c < cost && squares.Add((qy * level.Width) + qx))
                    {
                        rank++;
                    }
                }
            }

            string note = reached ? $"reachable, cost {router.CostOf(wanted)}, cheapness rank {rank}" : "NOT REACHABLE";
            if (!reached)
            {
                // If it becomes reachable once the actor is out of the picture,
                // then the two fish have to interleave - which the expansion,
                // moving one and then the other, cannot express.
                router.Route(parent, blocker, actor);
                note += router.Reached.Contains(router.Placement(bx, by, bLeft))
                    ? " - but REACHABLE if the actor is ignored (they must interleave)"
                    : " - and still unreachable with the actor ignored";
            }

            text.Append($"\n      step-aside: {level.Models[blocker].Kind} to ({bx},{by}) {note}");
        }

        // Stage two: could the actor have got to the square it pressed the key
        // from, once the other fish is where this edge leaves it?
        byte[] arrangedKey = (byte[])from.Key.Clone();
        for (int u = 0; u < level.Units.Length; u++)
        {
            int blocker = level.Units[u].Model;
            if (blocker == actor)
            {
                continue;
            }

            (int bx, int by, bool bLeft) = pre.Position(blocker);
            WriteMoved(arrangedKey, reduction, blocker, bx, by, bLeft);
        }

        var arranged = new Room(level);
        arranged.RestoreMobile(arrangedKey, reduction.MobileModels);

        router.Route(arranged, actor);
        int need = router.Placement(ax, ay, aLeft);
        bool got = router.Reached.Contains(need);
        string actorNote = got
            ? $"reachable, cost {router.CostOf(need)}"
            : "NOT REACHABLE with the other fish where it ends up";

        if (!got)
        {
            // Same question the other way round: was it the other fish's final
            // position that shut the actor out, or the room itself?
            router.Route(parent, actor);
            bool fromParent = router.Reached.Contains(need);
            router.Route(parent, actor, level.Units[1 - actorUnit].Model);
            bool ignoring = router.Reached.Contains(need);

            actorNote += fromParent
                ? " - but REACHABLE before the other fish moves there (order matters)"
                : ignoring
                    ? " - and reachable only if the other fish is ignored entirely (they must interleave)"
                    : " - and unreachable however the other fish is treated";
        }

        text.Append(
            $"\n      actor: {level.Models[actor].Kind} to ({ax},{ay}) facing {(aLeft ? "left" : "right")} {actorNote}");
        text.Append(FirstNonInertStep(level, reduction, router, from, to, moves));

        return text.ToString();
    }

    /// <summary>
    /// Replays the segment from the parent state and names the first step the
    /// router would refuse, and why. A route the optimum walks but the router
    /// rejects is where the decomposition actually leaks.
    /// </summary>
    private static string FirstNonInertStep(
        Level level, LevelReduction reduction, InertRouter router, Checkpoint from, Checkpoint to, string moves)
    {
        var room = new Room(level);
        room.RestoreMobile(from.Key, reduction.MobileModels);

        for (int i = from.Move + 1; i < to.Move; i++)
        {
            int unit = UnitOf(level, moves[i]);
            int fish = level.Units[unit].Model;
            (int px, int py, bool pLeft) = room.Position(fish);

            Assert.IsTrue(room.ApplyMove(moves[i]), $"segment move {i} failed to replay");

            (int nx, int ny, _) = room.Position(fish);
            if (nx == px && ny == py)
            {
                continue; // a turn, which is always inert
            }

            if (router.IsFreeStep(room, fish, px, py, nx, ny))
            {
                continue;
            }

            // Re-ask on the state before the move, which is what a route sees.
            var before = new Room(level);
            before.RestoreMobile(from.Key, reduction.MobileModels);
            for (int j = from.Move + 1; j < i; j++)
            {
                before.ApplyMove(moves[j]);
            }

            string why = router.IsHoldingSomethingUp(before, fish, px, py) ? "it is holding something up"
                : router.MightEscapeFrom(before, fish, nx, ny) ? "the destination is a place it might leave from"
                : "the destination is occupied or out of bounds";

            return $"\n      first non-inert step: move {i} ('{moves[i]}') {level.Models[fish].Kind} " +
                $"({px},{py})->({nx},{ny}) refused because {why}";
        }

        return "\n      every step in the segment is inert";
    }

    /// <summary>Moves one model within a state key - the same layout ItemSolver writes.</summary>
    private static void WriteMoved(byte[] key, LevelReduction reduction, int model, int x, int y, bool isLeft)
    {
        int slot = Array.IndexOf(reduction.MobileModels, model);
        Assert.IsGreaterThanOrEqualTo(0, slot, "model is not mobile");

        int offset = slot * 5;
        key[offset] = (byte)x;
        key[offset + 1] = (byte)(x >> 8);
        key[offset + 2] = (byte)y;
        key[offset + 3] = (byte)(y >> 8);
        key[offset + 4] = (byte)((key[offset + 4] & ~1) | (isLeft ? 1 : 0));
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

    private static byte[] KeyOf(Room room, LevelReduction reduction, int[] mobile)
    {
        byte[] key = new byte[reduction.StateKeySize];
        room.WriteStateKey(key, mobile);
        return key;
    }

    private static (int X, int Y)[] PositionsOf(Room room, int[] items)
    {
        var positions = new (int, int)[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            (int x, int y, _) = room.Position(items[i]);
            positions[i] = (x, y);
        }

        return positions;
    }

    private static bool[] GoneFlags(Room room, Level level)
    {
        bool[] gone = new bool[level.Units.Length];
        for (int i = 0; i < level.Units.Length; i++)
        {
            gone[i] = HasGone(room, level.Units[i].Model);
        }

        return gone;
    }

    private static bool HasGone(Room room, int model)
    {
        ref readonly ModelState state = ref room.State(model);
        return state.IsOut || state.IsLost;
    }
}
