using FishFillets.Search;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>
/// Parallel expansion (<see cref="SolveOptions.Threads"/>, review P4) promises
/// more than "also finds the optimum": at weight 1 it expands exactly the nodes
/// the serial search does, in the same order, so the answer, the expansion count
/// and the stored-state count are all identical. That is what lets every count
/// in micro-baseline.csv, measured serially, stand for parallel runs too.
/// </summary>
[TestClass]
public sealed class ParallelSolverTests
{
    public static IEnumerable<object[]> Levels =>
        MicroCorpus.All.Select(l => "micro:" + l.Name)
            .Concat(new[] { "start", "wc", "noground", "cannons" }.Select(n => "corpus:" + n))
            .SelectMany(id => new[] { new object[] { id, "stuck" }, new object[] { id, "nostuck" }, new object[] { id, "probation" } });

    /// <param name="mode">
    /// <c>stuck</c>: the stuck-item rule (the default) - the workers share its
    /// per-layout cache, and the verdict is a pure function of the layout, so
    /// the counts must still match the serial search exactly. <c>probation</c>:
    /// the rule on a probation of 64 expansions, short enough to end part-way
    /// through a parallel batch on levels where it discards nothing - it must
    /// switch off at the same expansion as the serial loop. <c>nostuck</c>: off.
    /// </param>
    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void Parallel_IdenticalToSerial(string id, string mode)
    {
        bool stuck = mode != "nostuck";
        long probation = mode == "probation" ? 64 : 0;
        Level level = id.StartsWith("micro:", StringComparison.Ordinal)
            ? MicroCorpus.Get(id["micro:".Length..]).Build()
            : TestCorpus.Instance.LoadLevel(id["corpus:".Length..]);
        LevelReduction reduction = LevelReduction.Compute(level);

        SolveResult serial = new Solver(level, reduction).Solve(new SolveOptions { MaxNodes = 3_000_000, Threads = 1, DetectStuck = stuck, StuckProbation = probation });
        SolveResult parallel = new Solver(level, reduction).Solve(new SolveOptions { MaxNodes = 3_000_000, Threads = 6, DetectStuck = stuck, StuckProbation = probation });

        Assert.AreEqual(serial.Status, parallel.Status, $"{id}: status");
        Assert.AreEqual(serial.Moves, parallel.Moves, $"{id}: answer");
        Assert.AreEqual(serial.Expanded, parallel.Expanded, $"{id}: expansions");
        Assert.AreEqual(serial.Generated, parallel.Generated, $"{id}: generated");
        Assert.AreEqual(serial.StatesStored, parallel.StatesStored, $"{id}: stored states");
        Assert.AreEqual(serial.DeepestF, parallel.DeepestF, $"{id}: bound");
    }
}
