using FishFillets.Physics;

namespace FishFillets.Physics.Tests;

/// <summary>
/// The port's primary correctness argument, as `dotnet test` cases: every
/// solution the game itself recorded (legacy/solution/*.lua) must drive this
/// engine to Solved. Equivalent to `ffsolve verify --all`, but one test case per
/// level, so a failure names the level that broke rather than a count.
///
/// Between them these exercise pushing, chained falls, crush deaths, escapes,
/// goal_out items, multi-cell shapes, windoze's extra fish and output plug, and
/// solutions from 39 to 2,127 moves. See solver/docs/001 section 4.
/// </summary>
[TestClass]
public sealed class ReferenceSolutionTests
{
    /// <summary>
    /// Levels with both exported geometry and a recorded solution. `ending` has a
    /// level but no solution; `redhat` has a solution but no level content in
    /// this repo (as in the browser port) - both are excluded by construction.
    /// </summary>
    public static IEnumerable<object[]> SolvableLevels =>
        TestCorpus.Instance.LevelsWithSolutions().Select(name => new object[] { name });

    [TestMethod]
    [DynamicData(nameof(SolvableLevels))]
    public void ReferenceSolution_ReachesSolved(string level)
    {
        Corpus corpus = TestCorpus.Instance;
        Assert.IsTrue(corpus.TryReadSolution(level, out string moves), $"no solution for {level}");

        var room = new Room(corpus.LoadLevel(level));
        SolutionResult result = SolutionValidator.Validate(room, moves);

        Assert.IsNull(result.Error, $"{level}: {result.Error}");
        Assert.AreEqual(moves.Length, result.Steps, $"{level}: not every move applied");
        Assert.IsTrue(result.Solved, $"{level}: all {moves.Length} moves applied but the room is not solved");
    }

    /// <summary>
    /// Guards the theory above against passing vacuously. A missing or truncated
    /// solver/levels export would otherwise silently reduce it to zero cases -
    /// a green run that proves nothing.
    /// </summary>
    [TestMethod]
    public void Corpus_CoversEveryLevelThatHasASolution()
    {
        int count = TestCorpus.Instance.LevelsWithSolutions().Count();
        Assert.AreEqual(
            80,
            count,
            $"expected 80 levels with both geometry and a solution, found {count}. " +
            "If level content changed, re-run scripts\\export-levels.ps1 and update this number.");
    }
}
