using FishFillets.Search;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>
/// The micro-level regression suite: hand-drawn rooms of a few dozen cells
/// (<c>Micro/Levels/*.lvl</c>, format on <see cref="MicroLevel"/>), each judged
/// against an independent full-state BFS (<see cref="ReferenceBfs"/>). The rules
/// themselves live in <see cref="MicroChecks"/>, shared with the fuzzer.
///
/// <para>Every check exists twice: once over the levels it should pass, and once
/// under <c>[TestCategory("KnownBug")]</c> over the levels a level file marks as
/// broken by a known finding. The default run (<c>scripts\build-solver.ps1</c>)
/// excludes that category; run it explicitly to see the open bugs fail:</para>
/// <code>scripts\build-solver.ps1 -KnownBugs</code>
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

        string? failure = MicroChecks.Matrix(
            micro.Build(), ReferenceBfs.For(micro).Optimum, variant, MicroVariants.Result(micro, variant), requireSolved: true);
        Assert.IsNull(failure, $"{name} / {failure}");
    }

    // ------------------------------------------------------- state properties --

    public static IEnumerable<object[]> AdmissibleRows => MicroCorpus.Rows("admissible", knownBug: false);

    public static IEnumerable<object[]> AdmissibleKnownBugRows => MicroCorpus.Rows("admissible", knownBug: true);

    /// <summary>
    /// The A* estimate never exceeds the true remaining cost, in any state the
    /// game can reach - not just along one solution. This is what the solver's
    /// "provably shortest" rests on.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(AdmissibleRows))]
    public void Heuristic_IsAdmissible(string level) => CheckProperty(level, "overestimates", MicroChecks.Admissible);

    [TestMethod]
    [TestCategory("KnownBug")]
    [DynamicData(nameof(AdmissibleKnownBugRows))]
    public void Heuristic_IsAdmissible_KnownBug(string level) => CheckProperty(level, "overestimates", MicroChecks.Admissible);

    public static IEnumerable<object[]> ConsistentRows => MicroCorpus.Rows("consistent", knownBug: false);

    public static IEnumerable<object[]> ConsistentKnownBugRows => MicroCorpus.Rows("consistent", knownBug: true);

    /// <summary>h never drops by more than one move along an edge - the bucket queue relies on it at weight 1.</summary>
    [TestMethod]
    [DynamicData(nameof(ConsistentRows))]
    public void Heuristic_IsConsistent(string level) => CheckProperty(level, "inconsistent edges", MicroChecks.Consistent);

    [TestMethod]
    [TestCategory("KnownBug")]
    [DynamicData(nameof(ConsistentKnownBugRows))]
    public void Heuristic_IsConsistent_KnownBug(string level) => CheckProperty(level, "inconsistent edges", MicroChecks.Consistent);

    public static IEnumerable<object[]> ReductionRows => MicroCorpus.Rows("reduction", knownBug: false);

    public static IEnumerable<object[]> ReductionKnownBugRows => MicroCorpus.Rows("reduction", knownBug: true);

    /// <summary>
    /// Nothing the level reduction freezes ever moves, in any state the game can
    /// reach - including states a search generates and then prunes, since a
    /// frozen model moving there would already corrupt the search's room.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(ReductionRows))]
    public void Reduction_FrozenModelsNeverMove(string level) => CheckProperty(level, "frozen-model moves", MicroChecks.Reduction);

    [TestMethod]
    [TestCategory("KnownBug")]
    [DynamicData(nameof(ReductionKnownBugRows))]
    public void Reduction_FrozenModelsNeverMove_KnownBug(string level) => CheckProperty(level, "frozen-model moves", MicroChecks.Reduction);

    public static IEnumerable<object[]> ExactRouterRows => MicroCorpus.Rows("exact-router", knownBug: false);

    /// <summary>
    /// The item search builds states by moving a fish in the key instead of
    /// simulating its route, so the router's idea of "this step disturbs nothing"
    /// has to be the engine's - including the step out from under a heavy item
    /// that drops nothing and crushes the other fish (solver/docs/023).
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(ExactRouterRows))]
    public void ExactRouter_AgreesWithTheEngine(string level) =>
        CheckProperty(level, "router/engine disagreements", MicroChecks.ExactRouter);

    public static IEnumerable<object[]> FootprintRows => MicroCorpus.Rows("footprint", knownBug: false);

    /// <summary>
    /// The layout search defers where the waiting fish stands, on the claim that
    /// outside an event's footprint (<c>LayoutFootprint</c>) it cannot change the
    /// event. Unproven physics, of the same kind as the support claim that failed
    /// on <c>heavy-shared</c> - so it is checked in every reachable state.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(FootprintRows))]
    public void Footprint_OtherFishOutsideItDoesNotMatter(string level) =>
        CheckProperty(level, "footprint counter-examples", MicroChecks.Footprint);

    public static IEnumerable<object[]> SymmetryRows => MicroCorpus.Rows("symmetry", knownBug: false);

    public static IEnumerable<object[]> SymmetryKnownBugRows => MicroCorpus.Rows("symmetry", knownBug: true);

    /// <summary>Swapping identical items changes nothing the physics can tell, so merging them (docs/022) is sound.</summary>
    [TestMethod]
    [DynamicData(nameof(SymmetryRows))]
    public void Symmetry_IdenticalItemsAreInterchangeable(string level) => CheckProperty(level, "asymmetric outcomes", MicroChecks.SymmetrySound);

    [TestMethod]
    [TestCategory("KnownBug")]
    [DynamicData(nameof(SymmetryKnownBugRows))]
    public void Symmetry_IdenticalItemsAreInterchangeable_KnownBug(string level) => CheckProperty(level, "asymmetric outcomes", MicroChecks.SymmetrySound);

    public static IEnumerable<object[]> StuckEstimateRows => MicroCorpus.Rows("stuck-estimate", knownBug: false);

    public static IEnumerable<object[]> StuckEstimateKnownBugRows => MicroCorpus.Rows("stuck-estimate", knownBug: true);

    /// <summary>The estimate raised by stuck-wall distances (docs/021) never exceeds the true distance.</summary>
    [TestMethod]
    [DynamicData(nameof(StuckEstimateRows))]
    public void StuckEstimate_IsAdmissible(string level) => CheckProperty(level, "overestimates", MicroChecks.StuckEstimate);

    [TestMethod]
    [TestCategory("KnownBug")]
    [DynamicData(nameof(StuckEstimateKnownBugRows))]
    public void StuckEstimate_IsAdmissible_KnownBug(string level) => CheckProperty(level, "overestimates", MicroChecks.StuckEstimate);

    public static IEnumerable<object[]> StuckRows => MicroCorpus.Rows("stuck", knownBug: false);

    public static IEnumerable<object[]> StuckKnownBugRows => MicroCorpus.Rows("stuck", knownBug: true);

    /// <summary>The stuck-item rule (<c>--stuck</c>) only ever condemns states that really have no solution.</summary>
    [TestMethod]
    [DynamicData(nameof(StuckRows))]
    public void Stuck_NeverCondemnsASolvableState(string level) => CheckProperty(level, "solvable states condemned", MicroChecks.Stuck);

    [TestMethod]
    [TestCategory("KnownBug")]
    [DynamicData(nameof(StuckKnownBugRows))]
    public void Stuck_NeverCondemnsASolvableState_KnownBug(string level) => CheckProperty(level, "solvable states condemned", MicroChecks.Stuck);

    private static void CheckProperty(string name, string what, Func<StateGraph, List<MicroChecks.Violation>> check)
    {
        if (name == MicroCorpus.None)
        {
            return;
        }

        List<MicroChecks.Violation> violations = check(ReferenceBfs.For(MicroCorpus.Get(name)));
        Assert.IsEmpty(
            violations,
            $"{name}: {violations.Count} {what}, e.g.\n  " +
            string.Join("\n  ", violations.Take(5).Select(v => v.Message + (v.Cause is null ? "" : $"  [{v.Cause}]"))));
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

    private static string Show(int? value) => value?.ToString() ?? "unsolvable";
}
