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
    /// <summary>A violation, and the open finding it is explained by (null: unexplained - a new bug).</summary>
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
                // C3: items charged a turn. If the estimate with item facing
                // ignored is admissible here, that is the whole story.
                string? cause = EstimateIgnoringItemFacing(graph.Level, reduction, room) <= graph.DistanceToGoal[i] ? "C3" : null;
                violations.Add(new($"h={h} > d*={graph.DistanceToGoal[i]} at {Describe(room)}", cause));
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
        var fixedH = new int[graph.Count];
        for (int i = 0; i < graph.Count; i++)
        {
            room.RestoreFrom(graph.States[i]);
            // A finished room is queued with h = 0, whatever the estimate says (Solver.Solve).
            h[i] = graph.Solved[i] ? 0 : heuristic.Estimate(room);
            fixedH[i] = graph.Solved[i] ? 0 : EstimateIgnoringItemFacing(graph.Level, reduction, room);
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
                    string? cause = fixedH[s] <= fixedH[t] + 1 ? "C3" : null;
                    violations.Add(new($"'{symbol}': h {h[s]} -> {h[t]} from {Describe(room)}", cause));
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
    /// The stuck-item rule, re-derived uncached exactly as <c>Solver.NoWayOut</c>
    /// computes it, never condemns a state that can still be finished.
    /// </summary>
    public static List<Violation> Stuck(StateGraph graph)
    {
        Level level = graph.Level;
        LevelReduction reduction = LevelReduction.Compute(level);
        var room = new Room(level);
        var violations = new List<Violation>();

        for (int i = 0; i < graph.Count; i++)
        {
            if (graph.Solved[i] || graph.DistanceToGoal[i] == StateGraph.Infinite)
            {
                continue;
            }

            room.RestoreFrom(graph.States[i]);
            bool[] mobile = StuckAnalysis.Mobile(level, room, fishAnywhere: true);
            bool[] solid = StuckAnalysis.SolidCells(level, room, mobile);
            for (int c = 0; c < solid.Length; c++)
            {
                solid[c] |= reduction.Walls[c];
            }

            foreach (UnitDef unit in level.Units)
            {
                ref readonly ModelState s = ref room.State(unit.Model);
                if (s.IsOut || !s.IsAlive)
                {
                    continue;
                }

                bool[] reach = StuckAnalysis.CanStillLeave(level, room, solid, unit.Model);
                if (!reach[(s.Y * level.Width) + s.X])
                {
                    // C7: NoWayOut demands a way out even of a fish that never has to leave.
                    string? cause = level.Models[unit.Model].Goal is not (GoalKind.Escape or GoalKind.Out) ? "C7" : null;
                    violations.Add(new($"d*={graph.DistanceToGoal[i]}, fish {unit.Model} judged walled in at {Describe(room)}", cause));
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// <see cref="ExitHeuristic.Estimate"/> with the C3 fix applied by hand: an
    /// item's distance is the better of its two facings, since items never turn.
    /// Used only to attribute violations to C3.
    /// </summary>
    private static int EstimateIgnoringItemFacing(Level level, LevelReduction reduction, Room room)
    {
        int fishTotal = 0, itemMax = 0;
        for (int i = 0; i < level.ModelCount; i++)
        {
            ModelDef def = level.Models[i];
            if (def.Goal is not (GoalKind.Escape or GoalKind.Out))
            {
                continue;
            }

            ref readonly ModelState m = ref room.State(i);
            if (m.IsOut)
            {
                continue;
            }

            RelaxedDistance distance = DistanceCache.Get(level, reduction, i);
            int d = def.IsAlive ? distance.At(m.X, m.Y, m.IsLeft) : Math.Min(distance.At(m.X, m.Y, true), distance.At(m.X, m.Y, false));
            if (d >= RelaxedDistance.Unreachable)
            {
                return RelaxedDistance.Unreachable;
            }

            if (def.IsAlive)
            {
                fishTotal += d;
            }
            else
            {
                itemMax = Math.Max(itemMax, d);
            }
        }

        return Math.Max(fishTotal, itemMax);
    }

    private static class DistanceCache
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Level, Dictionary<int, RelaxedDistance>> Table = new();

        public static RelaxedDistance Get(Level level, LevelReduction reduction, int model)
        {
            Dictionary<int, RelaxedDistance> perModel = Table.GetValue(level, _ => []);
            lock (perModel)
            {
                if (!perModel.TryGetValue(model, out RelaxedDistance? distance))
                {
                    distance = RelaxedDistance.ToExit(level, model, reduction.Walls, freeFall: !level.Models[model].IsAlive);
                    perModel[model] = distance;
                }

                return distance;
            }
        }
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
