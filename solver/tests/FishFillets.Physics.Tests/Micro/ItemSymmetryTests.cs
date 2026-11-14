using FishFillets.Search;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>
/// Merging identical items (docs/022). Soundness - swapping identical items
/// changes nothing the physics sees - is checked state by state by
/// <c>Symmetry_IdenticalItemsAreInterchangeable</c> and the fuzzer; these pin
/// which items count as identical and what merging buys where it matters.
/// </summary>
[TestClass]
public sealed class ItemSymmetryTests
{
    /// <summary>gems: 88 gems of one kind and shape form one class; the goal_out-free fish and walls none.</summary>
    [TestMethod]
    public void Classes_Gems()
    {
        Level level = TestCorpus.Instance.LoadLevel("gems");
        int[][] classes = ItemSymmetry.Classes(level, LevelReduction.Compute(level).MobileModels);
        Assert.HasCount(1, classes);
        Assert.HasCount(88, classes[0]);
    }

    /// <summary>Items differing in anything the physics reads - here a goal - are never merged.</summary>
    [TestMethod]
    public void Classes_GoalItemsStayApart()
    {
        Level level = MicroCorpus.Get("goal-out-push-right").Build();
        Assert.IsEmpty(ItemSymmetry.Classes(level, LevelReduction.Compute(level).MobileModels));
    }

    /// <summary>
    /// gems, where identical gems trade places all the time: in the same number
    /// of expansions, merging stores under three quarters of the states (about
    /// 0.69 at 100 k - the duplicates pile up as the search goes on), and the
    /// bound reached is at least as high. (Proving its 59-move optimum takes
    /// 0.49 M expansions merged against 2.93 M apart, docs/022.)
    /// </summary>
    [TestMethod]
    public void Merge_Gems_StoresFarFewerStates()
    {
        Level level = TestCorpus.Instance.LoadLevel("gems");
        var options = new SolveOptions { MaxNodes = 100_000, Threads = 6 };
        SolveResult merged = new Solver(level).Solve(options);
        SolveResult apart = new Solver(level).Solve(options with { MergeIdenticalItems = false });

        Assert.IsLessThan(apart.StatesStored * 3 / 4, merged.StatesStored, $"stored {apart.StatesStored} -> {merged.StatesStored}");
        Assert.IsGreaterThanOrEqualTo(apart.DeepestF, merged.DeepestF);
    }
}
