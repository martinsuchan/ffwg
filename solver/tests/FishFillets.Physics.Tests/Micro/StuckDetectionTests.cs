using System.Reflection;

using FishFillets.Search;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>
/// The stuck-item rule (on by default since docs/020) earns its keep: on the
/// levels built around a permanent wedge it must cut the search, and it must
/// never change the answer. Soundness on every reachable state is checked
/// separately against the oracle (<c>Stuck_NeverCondemnsASolvableState</c>);
/// this pins the benefit, so a later change that quietly disables the rule -
/// or a cache that stops recognising layouts - fails here rather than only
/// showing up as a slower run.
/// </summary>
[TestClass]
public sealed class StuckDetectionTests
{
    /// <summary>
    /// Levels whose dead subtree sits below the optimum, so plain A* has to
    /// search it: drop-trap-well and two-fish-wedge (a walled-in fish),
    /// goal-out-wedge (a goal_out item that can never move again).
    /// </summary>
    [TestMethod]
    [DataRow("drop-trap-well")]
    [DataRow("two-fish-wedge")]
    [DataRow("goal-out-wedge")]
    public void Stuck_PrunesTheDeadSubtree(string name)
    {
        MicroLevel micro = MicroCorpus.Get(name);
        SolveResult with = MicroVariants.Result(micro, MicroVariants.Get("astar"));
        SolveResult without = MicroVariants.Result(micro, MicroVariants.Get("astar-nostuck"));

        Assert.AreEqual(micro.Expect, with.Moves?.Length, $"{name}: answer with the rule");
        Assert.AreEqual(without.Moves?.Length, with.Moves?.Length, $"{name}: the rule changed the answer");
        Assert.IsTrue(with.Optimal, $"{name}: no longer flagged optimal");
        Assert.IsLessThan(without.Expanded, with.Expanded, $"{name}: expansions {without.Expanded} -> {with.Expanded}");
        Assert.IsLessThan(without.StatesStored, with.StatesStored, $"{name}: stored {without.StatesStored} -> {with.StatesStored}");
    }

    /// <summary>
    /// The stuck-wall distances (docs/021) on their own: in detour-wedge the
    /// wedged post leaves a long way out, so no state is dead and the dead-end
    /// half does nothing - only the raised estimate lifts those dear states
    /// above the optimum.
    /// </summary>
    [TestMethod]
    public void StuckDistances_CutTheDetour()
    {
        MicroLevel micro = MicroCorpus.Get("detour-wedge");
        SolveResult with = MicroVariants.Result(micro, MicroVariants.Get("astar"));
        SolveResult deadEndsOnly = MicroVariants.Result(micro, MicroVariants.Get("astar-nodist"));
        SolveResult without = MicroVariants.Result(micro, MicroVariants.Get("astar-nostuck"));

        Assert.AreEqual(micro.Expect, with.Moves?.Length);
        Assert.AreEqual(without.Expanded, deadEndsOnly.Expanded, "setup: nothing here is dead, the dead-end half should not change the search");
        Assert.IsLessThan(deadEndsOnly.Expanded, with.Expanded, $"expansions {deadEndsOnly.Expanded} -> {with.Expanded}");
    }

    /// <summary>
    /// The goal_out half of the rule, on its own: in goal-out-wedge the fish is
    /// goal_alive, so the walled-in-fish half can never fire there. Three moves
    /// left drop the item into the pocket, and from then on it can never leave.
    /// </summary>
    [TestMethod]
    public void Stuck_GoalOutItemWedgedForGood_IsDead()
    {
        Level level = MicroCorpus.Get("goal-out-wedge").Build();

        Assert.IsFalse(NoWayOut(new Solver(level), "", out _), "the start is solvable");
        Assert.IsFalse(NoWayOut(new Solver(level), "ll", out _), "one step short of the pocket the item can still be pushed out");
        Assert.IsTrue(NoWayOut(new Solver(level), "lll", out Room room), $"item wedged in the pocket: {MicroChecks.Describe(room)}");
    }

    /// <summary>
    /// Verdicts are shared between the serial loop's probe and the parallel
    /// workers' through one cache, so a verdict must not depend on which probe
    /// asked first - here, the same layouts asked in the opposite order on a
    /// second solver, with the cache warm from the first.
    /// </summary>
    [TestMethod]
    public void Stuck_VerdictIndependentOfQueryOrder()
    {
        Level level = MicroCorpus.Get("two-fish-wedge").Build();
        string[] histories = ["", "d", "dr", "dd", "ddr", "DDDDD", "drDDDDD", "DDDDDdr"];

        var forward = new Solver(level);
        bool[] expected = [.. histories.Select(h => NoWayOut(forward, h, out _))];
        Assert.IsTrue(expected.Any(v => v), "setup: some history should wedge the post");
        Assert.IsTrue(expected.Any(v => !v), "setup: some history should stay solvable");

        var backward = new Solver(level);
        for (int i = histories.Length - 1; i >= 0; i--)
        {
            Assert.AreEqual(expected[i], NoWayOut(backward, histories[i], out _), $"after '{histories[i]}'");
        }
    }

    public static IEnumerable<object[]> MicroLevels => MicroCorpus.All.Select(l => new object[] { l.Name });

    /// <summary>
    /// The detector is the analysis made fast (per-level wall work done once,
    /// per-fish reach from precomputed fits, verdicts cached per layout); this
    /// recomputes the same rule the slow, direct way on every reachable state of
    /// every micro-level and requires the same answer. Includes the shortcut
    /// the detector takes when nothing is stuck beyond the opening walls - there
    /// a fish that cannot leave is already rejected by the heuristic, so the
    /// detector need not say so.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(MicroLevels))]
    public void Stuck_DetectorMatchesDirectAnalysis(string name)
    {
        List<MicroChecks.Violation> violations = MicroChecks.StuckDetectorAgrees(ReferenceBfs.For(MicroCorpus.Get(name)));
        Assert.IsEmpty(violations, $"{name}: {violations.Count} states disagree, e.g. {violations.FirstOrDefault()?.Message}");
    }

    /// <summary>
    /// Probation (<see cref="SolveOptions.StuckProbation"/>): a rule that has
    /// discarded nothing by the deadline is switched off and the search goes on
    /// exactly as without it; one that has discarded something stays on.
    /// </summary>
    [TestMethod]
    public void Stuck_ProbationSwitchesOffOnlyAUselessRule()
    {
        // start has no dead ends at all: off after 1,000 expansions, and from
        // the start on its counts are those of a search without the rule.
        Level start = TestCorpus.Instance.LoadLevel("start");
        var useless = new Solver(start);
        SolveResult probed = useless.Solve(new SolveOptions { MaxNodes = 3_000_000, StuckProbation = 1_000 });
        SolveResult off = new Solver(start).Solve(new SolveOptions { MaxNodes = 3_000_000, DetectStuck = false });
        Assert.IsFalse(useless.StuckActive, "start: the rule should have been switched off");
        Assert.AreEqual(0, useless.StuckPruned);
        Assert.AreEqual(off.Moves, probed.Moves);
        Assert.AreEqual(off.Expanded, probed.Expanded);
        Assert.AreEqual(off.StatesStored, probed.StatesStored);

        // wc is mostly dead ends, the first of them at expansion 1,537: the
        // rule proves itself within its probation and stays on.
        Level wc = TestCorpus.Instance.LoadLevel("wc");
        var useful = new Solver(wc);
        SolveResult kept = useful.Solve(new SolveOptions { MaxNodes = 3_000_000, StuckProbation = 5_000 });
        Assert.IsTrue(useful.StuckActive, "wc: the rule should have stayed on");
        Assert.IsGreaterThan(0L, useful.StuckPruned);
        Assert.IsLessThanOrEqualTo(5_000L, useful.StuckFirstPruneAt);
        Assert.AreEqual(100, kept.Moves?.Length);
    }

    /// <summary>Puts a fresh solver's room into the state after <paramref name="moves"/> and asks its NoWayOut.</summary>
    private static bool NoWayOut(Solver solver, string moves, out Room room)
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        room = (Room)typeof(Solver).GetField("_room", Private)!.GetValue(solver)!;
        var key = (byte[])typeof(Solver).GetField("_key", Private)!.GetValue(solver)!;
        var mobile = (int[])typeof(Solver).GetField("_mobile", Private)!.GetValue(solver)!;

        room.Reset();
        foreach (char move in moves)
        {
            Assert.IsTrue(room.ApplyMove(move), $"setup: '{move}' in '{moves}' is not possible");
        }

        room.WriteStateKey(key, mobile);
        return (bool)typeof(Solver).GetMethod("NoWayOut", Private)!.Invoke(solver, null)!;
    }
}
