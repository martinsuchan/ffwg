using FishFillets.Search;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>
/// <see cref="SolveOptions.SkipReversals"/> drops, before simulating it, the
/// opposite of the fast move a state was reached by. Turning or stepping
/// straight back returns to the predecessor; turning round right after a
/// horizontal step reaches a new state that no shortest solution needs. The
/// second really prunes, so the counts may fall - but the answer's length, the
/// status and (for a finished search) the proven bound must not change, on 1
/// thread and on 6. That the answer is the true optimum is checked against
/// the full-state BFS on every micro-level and fuzz room by the matrix.
/// </summary>
[TestClass]
public sealed class ReversalSkipTests
{
    public static IEnumerable<object[]> Levels =>
        MicroCorpus.All.Select(l => "micro:" + l.Name)
            .Concat(new[] { "start", "wc", "noground", "cannons", "wreck", "submarine" }.Select(n => "corpus:" + n))
            .SelectMany(id => new[] { new object[] { id, 1 }, new object[] { id, 6 } });

    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void SkippingReversals_KeepsTheOptimum(string id, int threads)
    {
        Level level = id.StartsWith("micro:", StringComparison.Ordinal)
            ? MicroCorpus.Get(id["micro:".Length..]).Build()
            : TestCorpus.Instance.LoadLevel(id["corpus:".Length..]);
        LevelReduction reduction = LevelReduction.Compute(level);

        SolveResult plain = new Solver(level, reduction).Solve(new SolveOptions { MaxNodes = 3_000_000, Threads = threads, SkipReversals = false });
        SolveResult skipping = new Solver(level, reduction).Solve(new SolveOptions { MaxNodes = 3_000_000, Threads = threads, SkipReversals = true });

        Assert.AreEqual(plain.Status, skipping.Status, $"{id}: status");
        Assert.AreEqual(plain.Moves?.Length, skipping.Moves?.Length, $"{id}: answer length");
        Assert.AreEqual(plain.DeepestF, skipping.DeepestF, $"{id}: bound");
        if (skipping.Moves is { } moves)
        {
            Assert.IsTrue(SolutionValidator.Validate(new Room(level), moves).Solved, $"{id}: the answer does not replay");
        }
    }

    /// <summary>On a real level the skip has to fire, and the pruning has to save expansions.</summary>
    [TestMethod]
    public void SkippingReversals_SavesWorkOnAConventionalLevel()
    {
        Level level = TestCorpus.Instance.LoadLevel("wreck");
        LevelReduction reduction = LevelReduction.Compute(level);
        SolveResult plain = new Solver(level, reduction).Solve(new SolveOptions { MaxNodes = 3_000_000, Threads = 1, SkipReversals = false });
        SolveResult skipping = new Solver(level, reduction).Solve(new SolveOptions { MaxNodes = 3_000_000, Threads = 1, SkipReversals = true });
        Assert.IsTrue(skipping.Generated < plain.Generated * 0.97, $"generated {skipping.Generated:N0} vs {plain.Generated:N0}");
        Assert.IsTrue(skipping.Expanded < plain.Expanded, $"expanded {skipping.Expanded:N0} vs {plain.Expanded:N0}");
    }
}
