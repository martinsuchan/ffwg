using System.Text;

using FishFillets.Search;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>
/// The micro-level regression suite: hand-drawn rooms of a few dozen cells
/// (<c>Micro/Levels/*.lvl</c>, format on <see cref="MicroLevel"/>), each judged
/// against an independent full-state BFS (<see cref="ReferenceBfs"/>).
///
/// <para>Every check exists twice: once over the levels it should pass, and once
/// under <c>[TestCategory("KnownBug")]</c> over the levels a level file marks as
/// broken by a known finding. The default run (<c>scripts\build-solver.ps1</c>)
/// excludes that category; run it explicitly to see the open bugs fail:</para>
/// <code>dotnet test solver\FishFillets.Solver.slnx -c Release --filter TestCategory=KnownBug</code>
/// <para>A fix is done when its rows go green there; then delete the level's
/// <c>known-bug</c> entry so it joins the normal run for good.</para>
/// </summary>
[TestClass]
public sealed class MicroTests
{
    // ---------------------------------------------------------------- oracle --

    public static IEnumerable<object[]> AllLevels => MicroCorpus.All.Select(l => new object[] { l.Name });

    /// <summary>The level file's own claim: its hand-written optimum is what the oracle finds.</summary>
    [TestMethod]
    [DynamicData(nameof(AllLevels))]
    public void Oracle_ConfirmsExpectedOptimum(string name)
    {
        MicroLevel micro = MicroCorpus.Get(name);
        StateGraph graph = ReferenceBfs.For(micro);

        Assert.AreEqual(
            micro.Expect, graph.Optimum,
            $"{name}: the file says {Show(micro.Expect)}, the oracle finds {Show(graph.Optimum)} " +
            $"over {graph.Count:N0} states (one optimum: {graph.OptimalMoves() ?? "-"})");

        if (graph.OptimalMoves() is { } moves)
        {
            Assert.IsTrue(SolutionValidator.Validate(new Room(micro.Build()), moves).Solved, $"{name}: oracle path does not replay");
        }
    }

    // ---------------------------------------------------------------- matrix --

    public static IEnumerable<object[]> MatrixRows => MicroVariants.Rows(knownBug: false);

    public static IEnumerable<object[]> MatrixKnownBugRows => MicroVariants.Rows(knownBug: true);

    /// <summary>Every solver variant on every level, held to what that variant promises.</summary>
    [TestMethod]
    [DynamicData(nameof(MatrixRows))]
    public void Matrix(string level, string variant) => CheckMatrix(level, variant);

    [TestMethod]
    [TestCategory("KnownBug")]
    [DynamicData(nameof(MatrixKnownBugRows))]
    public void Matrix_KnownBug(string level, string variant) => CheckMatrix(level, variant);

    private static void CheckMatrix(string name, string variantId)
    {
        if (name == MicroCorpus.None)
        {
            return;
        }

        MicroLevel micro = MicroCorpus.Get(name);
        MicroVariant variant = MicroVariants.Get(variantId);
        string? knownBug = MicroVariants.KnownBug(micro, variant);
        if (MicroVariants.IsHazard(knownBug))
        {
            Assert.Inconclusive($"{name} / {variantId}: not run - known bug {knownBug} hangs or exhausts memory here");
        }

        int? optimum = ReferenceBfs.For(micro).Optimum;
        SolveResult result = MicroVariants.Result(micro, variant);
        string where = $"{name} / {variantId}: {result.Status}, expanded {result.Expanded:N0}";

        if (result.Solved)
        {
            Assert.IsTrue(
                SolutionValidator.Validate(new Room(micro.Build()), result.Moves).Solved,
                $"{where}: the returned solution does not replay");
        }

        if (optimum is null)
        {
            Assert.IsFalse(result.Solved, $"{where}: claims a solution to an unsolvable level");
            if (variant.Promise == Promise.Optimal)
            {
                Assert.IsTrue(
                    result.Status.StartsWith("exhausted", StringComparison.Ordinal)
                    || result.Status.StartsWith("unsolvable", StringComparison.Ordinal),
                    $"{where}: a complete search should prove there is no solution");
            }

            return;
        }

        Assert.IsTrue(result.Solved, $"{where}: no solution, the optimum is {optimum}");
        int length = result.Moves!.Length;
        Assert.IsTrue(length >= optimum, $"{where}: {length} moves is shorter than the proven optimum {optimum} - the oracle or the replay is wrong");

        switch (variant.Promise)
        {
            case Promise.Optimal:
                Assert.AreEqual(optimum, length, $"{where}: optimum is {optimum}, got {length}");
                Assert.IsTrue(result.Optimal, $"{where}: right length but not claimed optimal");
                break;
            case Promise.Bounded:
                int bound = (int)Math.Floor(variant.Weight * optimum.Value);
                Assert.IsTrue(length <= bound, $"{where}: {length} moves exceeds weight {variant.Weight} x optimum {optimum} = {bound}");
                break;
        }
    }

    // ------------------------------------------------------------- heuristic --

    public static IEnumerable<object[]> AdmissibleRows => MicroCorpus.Rows("admissible", knownBug: false);

    public static IEnumerable<object[]> AdmissibleKnownBugRows => MicroCorpus.Rows("admissible", knownBug: true);

    /// <summary>
    /// The A* estimate never exceeds the true remaining cost, in any state the
    /// game can reach - not just along one solution. This is what the solver's
    /// "provably shortest" rests on.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(AdmissibleRows))]
    public void Heuristic_IsAdmissible(string level) => CheckAdmissible(level);

    [TestMethod]
    [TestCategory("KnownBug")]
    [DynamicData(nameof(AdmissibleKnownBugRows))]
    public void Heuristic_IsAdmissible_KnownBug(string level) => CheckAdmissible(level);

    private static void CheckAdmissible(string name)
    {
        if (name == MicroCorpus.None)
        {
            return;
        }

        MicroLevel micro = MicroCorpus.Get(name);
        StateGraph graph = ReferenceBfs.For(micro);
        var heuristic = new ExitHeuristic(graph.Level, LevelReduction.Compute(graph.Level));
        var room = new Room(graph.Level);

        var violations = new List<string>();
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
                violations.Add($"h={h} > d*={graph.DistanceToGoal[i]} at {Describe(room)}");
            }
        }

        Assert.IsEmpty(violations, $"{name}: {violations.Count} overestimates, e.g.\n  {string.Join("\n  ", violations.Take(5))}");
    }

    public static IEnumerable<object[]> ConsistentRows => MicroCorpus.Rows("consistent", knownBug: false);

    public static IEnumerable<object[]> ConsistentKnownBugRows => MicroCorpus.Rows("consistent", knownBug: true);

    /// <summary>
    /// h never drops by more than one move along an edge. The bucket queue's
    /// "f never decreases at weight 1" relies on it.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(ConsistentRows))]
    public void Heuristic_IsConsistent(string level) => CheckConsistent(level);

    [TestMethod]
    [TestCategory("KnownBug")]
    [DynamicData(nameof(ConsistentKnownBugRows))]
    public void Heuristic_IsConsistent_KnownBug(string level) => CheckConsistent(level);

    private static void CheckConsistent(string name)
    {
        if (name == MicroCorpus.None)
        {
            return;
        }

        MicroLevel micro = MicroCorpus.Get(name);
        StateGraph graph = ReferenceBfs.For(micro);
        var heuristic = new ExitHeuristic(graph.Level, LevelReduction.Compute(graph.Level));
        var room = new Room(graph.Level);

        var h = new int[graph.Count];
        for (int i = 0; i < graph.Count; i++)
        {
            room.RestoreFrom(graph.States[i]);
            // A finished room is queued with h = 0, whatever the estimate says (Solver.Solve).
            h[i] = graph.Solved[i] ? 0 : heuristic.Estimate(room);
        }

        var violations = new List<string>();
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
                    violations.Add($"'{symbol}': h {h[s]} -> {h[t]} from {Describe(room)}");
                }
            }
        }

        Assert.IsEmpty(violations, $"{name}: {violations.Count} inconsistent edges, e.g.\n  {string.Join("\n  ", violations.Take(5))}");
    }

    // ------------------------------------------------------------- reduction --

    public static IEnumerable<object[]> ReductionRows => MicroCorpus.Rows("reduction", knownBug: false);

    public static IEnumerable<object[]> ReductionKnownBugRows => MicroCorpus.Rows("reduction", knownBug: true);

    /// <summary>
    /// Nothing the level reduction freezes ever moves, in any state the game can
    /// reach - including the states a search generates and then prunes, since a
    /// frozen model moving there would already corrupt the search's room.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(ReductionRows))]
    public void Reduction_FrozenModelsNeverMove(string level) => CheckReduction(level);

    [TestMethod]
    [TestCategory("KnownBug")]
    [DynamicData(nameof(ReductionKnownBugRows))]
    public void Reduction_FrozenModelsNeverMove_KnownBug(string level) => CheckReduction(level);

    private static void CheckReduction(string name)
    {
        if (name == MicroCorpus.None)
        {
            return;
        }

        MicroLevel micro = MicroCorpus.Get(name);
        StateGraph graph = ReferenceBfs.For(micro);
        LevelReduction reduction = LevelReduction.Compute(graph.Level);
        var room = new Room(graph.Level);

        room.RestoreFrom(graph.States[0]);
        var start = reduction.FrozenModels.ToDictionary(m => m, m => (room.State(m).X, room.State(m).Y));

        for (int i = 0; i < graph.Count; i++)
        {
            room.RestoreFrom(graph.States[i]);
            foreach (int model in reduction.FrozenModels)
            {
                ref readonly ModelState m = ref room.State(model);
                Assert.IsTrue(
                    (m.X, m.Y) == start[model] && !m.IsOut && !m.IsLost,
                    $"{name}: frozen model {model} moved to ({m.X},{m.Y}) at {Describe(room)}");
            }
        }
    }

    public static IEnumerable<object[]> StuckRows => MicroCorpus.Rows("stuck", knownBug: false);

    public static IEnumerable<object[]> StuckKnownBugRows => MicroCorpus.Rows("stuck", knownBug: true);

    /// <summary>
    /// The stuck-item rule (<c>--stuck</c>) only ever condemns states that really
    /// have no solution. Re-derives exactly what <c>Solver.NoWayOut</c> computes,
    /// uncached, for every state that can still be finished.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(StuckRows))]
    public void Stuck_NeverCondemnsASolvableState(string level) => CheckStuck(level);

    [TestMethod]
    [TestCategory("KnownBug")]
    [DynamicData(nameof(StuckKnownBugRows))]
    public void Stuck_NeverCondemnsASolvableState_KnownBug(string level) => CheckStuck(level);

    private static void CheckStuck(string name)
    {
        if (name == MicroCorpus.None)
        {
            return;
        }

        MicroLevel micro = MicroCorpus.Get(name);
        StateGraph graph = ReferenceBfs.For(micro);
        Level level = graph.Level;
        LevelReduction reduction = LevelReduction.Compute(level);
        var room = new Room(level);

        var violations = new List<string>();
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
                    violations.Add($"d*={graph.DistanceToGoal[i]}, fish {unit.Model} judged walled in at {Describe(room)}");
                }
            }
        }

        Assert.IsEmpty(violations, $"{name}: {violations.Count} solvable states condemned, e.g.\n  {string.Join("\n  ", violations.Take(5))}");
    }

    // ---------------------------------------------------------------- window --

    public static IEnumerable<object[]> WindowRows => MicroCorpus.Rows("window", knownBug: false, l => l.Detour is not null);

    public static IEnumerable<object[]> WindowKnownBugRows => MicroCorpus.Rows("window", knownBug: true, l => l.Detour is not null);

    /// <summary>The window optimiser turns the level's deliberately long <c>detour</c> solution into an optimal one.</summary>
    [TestMethod]
    [DynamicData(nameof(WindowRows))]
    public void Window_RemovesTheDetour(string level) => CheckWindow(level);

    [TestMethod]
    [TestCategory("KnownBug")]
    [DynamicData(nameof(WindowKnownBugRows))]
    public void Window_RemovesTheDetour_KnownBug(string level) => CheckWindow(level);

    private static void CheckWindow(string name)
    {
        if (name == MicroCorpus.None)
        {
            return;
        }

        MicroLevel micro = MicroCorpus.Get(name);
        Level level = micro.Build();
        string detour = micro.Detour!;
        Assert.IsTrue(SolutionValidator.Validate(new Room(level), detour).Solved, $"{name}: the detour in the file is not a solution");

        OptimizeResult result = new WindowOptimizer(level).Optimize(detour, window: 16);
        Assert.IsTrue(SolutionValidator.Validate(new Room(level), result.Moves).Solved, $"{name}: optimised solution does not replay");
        Assert.AreEqual(micro.Expect, result.Moves.Length, $"{name}: {detour.Length} -> {result.Moves} ({result.Moves.Length}), optimum {micro.Expect}");
    }

    // -------------------------------------------------------------- helpers --

    private static string Show(int? value) => value?.ToString() ?? "unsolvable";

    /// <summary>Where every model is, compactly - enough to redraw the state by hand.</summary>
    internal static string Describe(Room room)
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
