using System.Text;

using FishFillets.Search;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>
/// The checks themselves, as plain functions returning what went wrong, so the
/// curated suite (<see cref="MicroTests"/>) and the fuzzer
/// (<see cref="MicroFuzzer"/>) apply exactly the same rules.
/// </summary>
internal static class MicroChecks
{
    /// <summary>
    /// A violation, and the open finding it is explained by (null: unexplained -
    /// a new bug). Every finding from the review is fixed, so nothing attributes
    /// today; when a new bug is found and left open, give its check a cause here
    /// so the fuzzer can keep counting it without failing.
    /// </summary>
    internal sealed record Violation(string Message, string? Cause);

    /// <summary>
    /// Matrix cell: the variant held to its promise. <paramref name="requireSolved"/>
    /// makes a Valid variant fail when it returns nothing on a solvable level -
    /// right for curated levels, wrong for random ones, where the incomplete
    /// decompositions are expected to miss.
    /// </summary>
    public static string? Matrix(Level level, int? optimum, MicroVariant variant, SolveResult result, bool requireSolved)
    {
        string where = $"{variant.Id}: {result.Status}, expanded {result.Expanded:N0}";

        if (result.Solved && !SolutionValidator.Validate(new Room(level), result.Moves).Solved)
        {
            return $"{where}: the returned solution does not replay";
        }

        if (optimum is null)
        {
            if (result.Solved)
            {
                return $"{where}: claims a solution to an unsolvable level";
            }

            if (variant.Promise == Promise.Optimal
                && !result.Status.StartsWith("exhausted", StringComparison.Ordinal)
                && !result.Status.StartsWith("unsolvable", StringComparison.Ordinal))
            {
                return $"{where}: a complete search should prove there is no solution";
            }

            return null;
        }

        if (!result.Solved)
        {
            return variant.Promise == Promise.Valid && !requireSolved ? null : $"{where}: no solution, the optimum is {optimum}";
        }

        int length = result.Moves!.Length;
        if (length < optimum)
        {
            return $"{where}: {length} moves is shorter than the proven optimum {optimum}";
        }

        return variant.Promise switch
        {
            Promise.Optimal when length != optimum => $"{where}: optimum is {optimum}, got {length}",
            Promise.Optimal when !result.Optimal => $"{where}: right length but not claimed optimal",
            Promise.Bounded when length > (int)Math.Floor(variant.Weight * optimum.Value) =>
                $"{where}: {length} moves exceeds weight {variant.Weight} x optimum {optimum}",
            _ => null,
        };
    }

    /// <summary>h(s) &lt;= d*(s) in every state that can still be finished.</summary>
    public static List<Violation> Admissible(StateGraph graph)
    {
        var reduction = LevelReduction.Compute(graph.Level);
        var heuristic = new ExitHeuristic(graph.Level, reduction);
        var room = new Room(graph.Level);
        var violations = new List<Violation>();

        for (int i = 0; i < graph.Count; i++)
        {
            if (!graph.Solvable[i] || graph.Solved[i] || graph.DistanceToGoal[i] == StateGraph.Infinite)
            {
                continue;
            }

            room.RestoreFrom(graph.States[i]);
            int h = heuristic.Estimate(room);
            if (h > graph.DistanceToGoal[i])
            {
                violations.Add(new($"h={h} > d*={graph.DistanceToGoal[i]} at {Describe(room)}", null));
            }
        }

        return violations;
    }

    /// <summary>h(s) &lt;= h(t) + 1 along every edge between living states.</summary>
    public static List<Violation> Consistent(StateGraph graph)
    {
        var reduction = LevelReduction.Compute(graph.Level);
        var heuristic = new ExitHeuristic(graph.Level, reduction);
        var room = new Room(graph.Level);

        var h = new int[graph.Count];
        for (int i = 0; i < graph.Count; i++)
        {
            room.RestoreFrom(graph.States[i]);
            // A finished room is queued with h = 0, whatever the estimate says (Solver.Solve).
            h[i] = graph.Solved[i] ? 0 : heuristic.Estimate(room);
        }

        var violations = new List<Violation>();
        for (int s = 0; s < graph.Count; s++)
        {
            foreach ((char symbol, int t) in graph.Edges[s])
            {
                if (!graph.Solvable[t] || h[s] >= RelaxedDistance.Unreachable || h[t] >= RelaxedDistance.Unreachable)
                {
                    continue;
                }

                if (h[s] > h[t] + 1)
                {
                    room.RestoreFrom(graph.States[s]);
                    violations.Add(new($"'{symbol}': h {h[s]} -> {h[t]} from {Describe(room)}", null));
                }
            }
        }

        return violations;
    }

    /// <summary>No frozen model moves in any generated state (pruned ones included).</summary>
    public static List<Violation> Reduction(StateGraph graph)
    {
        LevelReduction reduction = LevelReduction.Compute(graph.Level);
        var room = new Room(graph.Level);
        room.RestoreFrom(graph.States[0]);
        var start = reduction.FrozenModels.ToDictionary(m => m, m => (room.State(m).X, room.State(m).Y));

        var violations = new List<Violation>();
        for (int i = 0; i < graph.Count; i++)
        {
            room.RestoreFrom(graph.States[i]);
            foreach (int model in reduction.FrozenModels)
            {
                ref readonly ModelState m = ref room.State(model);
                if ((m.X, m.Y) != start[model] || m.IsOut || m.IsLost)
                {
                    violations.Add(new($"frozen model {model} moved to ({m.X},{m.Y}) at {Describe(room)}", null));
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// The stuck-item rule (<c>--stuck</c>) never condemns a state that can still
    /// be finished. Asks the solver's own <c>NoWayOut</c> - cache included, on one
    /// solver for the whole graph, in the oracle's order - so this tests the real
    /// code path, including the per-layout cache that review C1 found keyed too
    /// coarsely. Reflection on private members, like <see cref="WhiteBoxTests"/>.
    /// </summary>
    public static List<Violation> Stuck(StateGraph graph)
    {
        var probe = new SolverProbe(new Solver(graph.Level));
        var violations = new List<Violation>();

        for (int i = 0; i < graph.Count; i++)
        {
            if (graph.Solved[i] || graph.DistanceToGoal[i] == StateGraph.Infinite)
            {
                continue;
            }

            if (probe.NoWayOut(graph.States[i]))
            {
                violations.Add(new($"d*={graph.DistanceToGoal[i]}, condemned at {Describe(probe.Room)}", null));
            }
        }

        return violations;
    }

    /// <summary>
    /// The stuck-item detector - the analysis made fast (per-level wall work
    /// done once, per-fish reach from precomputed fits, verdicts cached per
    /// layout, docs/020) - gives on every state the answer the same rule gives
    /// computed the slow, direct way with <see cref="StuckAnalysis"/>. Mirrors
    /// the one shortcut the detector takes: with nothing stuck beyond the
    /// opening walls it says nothing, because a fish that cannot leave then is
    /// already rejected by the walls-only heuristic.
    /// </summary>
    public static List<Violation> StuckDetectorAgrees(StateGraph graph)
    {
        Level level = graph.Level;
        var solver = new Solver(level);
        var probe = new SolverProbe(solver);
        LevelReduction reduction = solver.Reduction;
        int[] goalOut = [.. reduction.MobileModels.Where(m => !level.Models[m].IsAlive && level.Models[m].Goal == GoalKind.Out)];
        var violations = new List<Violation>();

        for (int i = 0; i < graph.Count; i++)
        {
            if (graph.Solved[i])
            {
                continue;
            }

            bool fast = probe.NoWayOut(graph.States[i]);
            bool direct = DirectNoWayOut(level, probe.Room, reduction, goalOut);
            if (fast != direct)
            {
                violations.Add(new($"detector {fast}, direct {direct} at {Describe(probe.Room)}", null));
            }
        }

        return violations;
    }

    private static bool DirectNoWayOut(Level level, Room room, LevelReduction reduction, int[] goalOut)
    {
        bool[] mobile = StuckAnalysis.Mobile(level, room, fishAnywhere: true);
        if (goalOut.Any(m => !mobile[m] && !room.State(m).IsOut))
        {
            return true;
        }

        bool[] solid = StuckAnalysis.SolidCells(level, room, mobile);
        bool extra = false;
        for (int c = 0; c < solid.Length; c++)
        {
            extra |= solid[c] && !reduction.Walls[c];
            solid[c] |= reduction.Walls[c];
        }

        if (!extra)
        {
            return false;
        }

        foreach (UnitDef unit in level.Units)
        {
            ref readonly ModelState s = ref room.State(unit.Model);
            if (level.Models[unit.Model].Goal is not (GoalKind.Escape or GoalKind.Out) || s.IsOut || !s.IsAlive
                || (uint)s.X >= (uint)level.Width || (uint)s.Y >= (uint)level.Height)
            {
                continue;
            }

            if (!StuckAnalysis.CanStillLeave(level, room, solid, unit.Model)[(s.Y * level.Width) + s.X])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Where every model is, compactly - enough to redraw the state by hand.</summary>
    public static string Describe(Room room)
    {
        var text = new StringBuilder("[");
        for (int i = 0; i < room.ModelCount; i++)
        {
            if (room.Level.Models[i].Weight == Physics.Weight.Fixed && !room.Level.Models[i].IsAlive)
            {
                continue;
            }

            ref readonly ModelState m = ref room.State(i);
            text.Append($" {i}:{(m.IsOut ? "out" : $"({m.X},{m.Y}){(room.Level.Models[i].IsAlive ? (m.IsLeft ? "<" : ">") : "")}")}");
        }

        return text.Append(" ]").ToString();
    }
}

/// <summary>Drives a <see cref="Solver"/>'s private <c>NoWayOut</c> on arbitrary states.</summary>
internal sealed class SolverProbe
{
    private const System.Reflection.BindingFlags Private =
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    private readonly Solver _solver;
    private readonly byte[] _key;
    private readonly int[] _mobile;
    private readonly System.Reflection.MethodInfo _noWayOut;

    public SolverProbe(Solver solver)
    {
        _solver = solver;
        Room = (Room)typeof(Solver).GetField("_room", Private)!.GetValue(solver)!;
        _key = (byte[])typeof(Solver).GetField("_key", Private)!.GetValue(solver)!;
        _mobile = (int[])typeof(Solver).GetField("_mobile", Private)!.GetValue(solver)!;
        _noWayOut = typeof(Solver).GetMethod("NoWayOut", Private)!;
    }

    public Room Room { get; }

    public bool NoWayOut(RoomSnapshot state)
    {
        Room.RestoreFrom(state);
        Room.WriteStateKey(_key, _mobile);
        return (bool)_noWayOut.Invoke(_solver, null)!;
    }
}
