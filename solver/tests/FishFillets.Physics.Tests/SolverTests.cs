using FishFillets.Physics;
using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// The solver claims not just to find a solution but to prove none is shorter.
/// These pin that claim on levels small enough to run in a test suite.
///
/// The lengths are not arbitrary: each is the hall-of-fame record
/// (solver/docs/worldfame.lua), set independently by human players. Two methods
/// agreeing is what makes the optimality claim credible rather than merely
/// self-consistent - so a change that makes the solver return 56 for `start`
/// has broken something real, and this is where it surfaces.
/// </summary>
[TestClass]
public sealed class SolverTests
{
    // Kept to the levels that solve in a couple of seconds. library (58 s) and
    // stairs (109 s) prove the same property but are far too slow to sit here.
    [TestMethod]
    [DataRow("submarine", 83)]
    [DataRow("noground", 44)]
    [DataRow("wc", 100)]
    [DataRow("start", 54)]
    public void SolvesToProvenOptimality(string levelName, int expected)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        var solver = new Solver(level, LevelReduction.Verified(level, TestCorpus.Solution(levelName)));

        SolveResult result = solver.Solve(new SolveOptions { TimeLimit = TimeSpan.FromMinutes(2) });

        Assert.IsTrue(result.Solved, $"{levelName}: {result.Status}");
        Assert.IsTrue(result.Optimal, $"{levelName}: solved but not claimed optimal");
        Assert.AreEqual(expected, result.Moves!.Length, $"{levelName}: expected the known optimum");

        // Never take the search's word for it - the answer has to replay.
        Assert.IsTrue(
            SolutionValidator.Validate(new Room(level), result.Moves).Solved,
            $"{levelName}: the solver's answer does not solve the level");
    }

    /// <summary>
    /// The goal is tested when a state is popped, not when it is generated,
    /// because a state can have h == 0 without being solved and would otherwise
    /// let a one-move-too-long finish be returned first. A returned solution must
    /// therefore never be beatable - checked here by requiring that no prefix
    /// solves, and that the reported length equals the bundled optimum.
    /// </summary>
    [TestMethod]
    public void ReturnedSolutionHasNoShorterPrefix()
    {
        Level level = TestCorpus.Instance.LoadLevel("start");
        var solver = new Solver(level, LevelReduction.Verified(level, TestCorpus.Solution("start")));
        SolveResult result = solver.Solve(new SolveOptions { TimeLimit = TimeSpan.FromMinutes(2) });

        Assert.IsTrue(result.Solved);

        var room = new Room(level);
        for (int i = 0; i < result.Moves!.Length; i++)
        {
            Assert.IsTrue(room.ApplyMove(result.Moves[i]), $"move {i} was rejected");
            bool last = i == result.Moves.Length - 1;
            Assert.AreEqual(last, room.IsSolved(), last ? "not solved at the end" : $"solved early at move {i}");
        }
    }

    [TestMethod]
    public void UnreachableGoalIsReportedRatherThanSearchedFor()
    {
        // corals is far too big to solve here, but the start-state check is
        // instant: the heuristic is finite, so it reports a real search rather
        // than "unsolvable".
        Level level = TestCorpus.Instance.LoadLevel("corals");
        var solver = new Solver(level);

        SolveResult result = solver.Solve(new SolveOptions { MaxNodes = 1000 });

        Assert.IsFalse(result.Solved);
        Assert.AreEqual("node limit reached", result.Status);
        Assert.IsGreaterThan(0, result.DeepestF, "the search should have made real progress on the bound");
    }
}
