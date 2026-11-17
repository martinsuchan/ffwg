using FishFillets.Physics.Tests.Micro;
using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// <see cref="LayoutSolver"/> (<c>--layouts</c>) on real levels with proven
/// optima. It promises a valid solution, not a shortest one, so a result is
/// held to three things: it replays, it is never shorter than the proven
/// optimum (that would mean a replay or physics bug), and it stays within a
/// tolerance measured when the solver was written - the ratchet that catches
/// the decomposition quietly losing its cheap edges.
///
/// <para>Measured (solver/docs/024 and after): <c>start</c> 54, <c>submarine</c>
/// 83 and <c>wc</c> 104 against optima 54, 83 and 100, each in about 2 s. wc
/// solves only since the interaction children (the bowl comes down onto the
/// small fish's back, which the generic edge forbids) and start reaches 54
/// only since the footprint's margin is the cell below rather than all four
/// neighbours. The tolerance is the measured length plus two.</para>
/// </summary>
[TestClass]
public sealed class LayoutSolverTests
{
    [TestMethod]
    [DataRow("start", 54, 56)]
    [DataRow("submarine", 83, 85)]
    [DataRow("wc", 100, 106)]
    public void FindsAValidSolutionNearTheOptimum(string levelName, int optimum, int tolerance)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        var solver = new LayoutSolver(level, LevelReduction.Verified(level, TestCorpus.Solution(levelName)));

        SolveResult result = solver.Solve(new SolveOptions { TimeLimit = TimeSpan.FromMinutes(2) });

        Assert.IsTrue(result.Solved, $"{levelName}: {result.Status} (last reject: {solver.LastRejectReason ?? "none"})");
        Assert.IsFalse(result.Optimal, $"{levelName}: the layout search never claims optimality");
        Assert.IsTrue(
            SolutionValidator.Validate(new Room(level), result.Moves).Solved,
            $"{levelName}: the layout answer does not replay");
        Assert.AreEqual(0, solver.ReplayFailures, $"{levelName}: a reconstructed string failed validation");
        Assert.IsGreaterThanOrEqualTo(
            optimum, result.Moves!.Length, $"{levelName}: shorter than the proven optimum - a replay or physics bug");
        Assert.IsLessThanOrEqualTo(
            tolerance, result.Moves.Length, $"{levelName}: {result.Moves.Length} moves, measured {tolerance} or better");
    }

    /// <summary>
    /// The footprint and interaction rules (<c>MicroChecks.Footprint</c>) along
    /// a real level's recorded solution. The micro-levels never put the idle
    /// fish where it changes an event without being killed, so there the
    /// interaction half of the check has nothing to find. Along <c>wc</c>'s
    /// recorded solution it does (the bowl coming down onto the small fish):
    /// emptying the interaction cells makes the wc row fail. The other rows
    /// exercise the footprint half on real rooms; none of them made the
    /// interaction half fail when sabotaged.
    /// </summary>
    [TestMethod]
    [DataRow("wc")]
    [DataRow("cannons")]
    [DataRow("start")]
    [DataRow("submarine")]
    [DataRow("noground")]
    [DataRow("broom")]
    public void FootprintRules_HoldAlongTheRecordedSolution(string levelName)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        string moves = TestCorpus.Solution(levelName);
        var room = new Room(level);
        var states = new List<RoomSnapshot>();
        for (int i = 0; i < moves.Length; i++)
        {
            if (room.IsSolvable() && !room.IsSolved())
            {
                states.Add(room.Capture());
            }

            Assert.IsTrue(room.ApplyMove(moves[i]), $"{levelName}: the recorded solution is refused at move {i}");
        }

        List<MicroChecks.Violation> violations = MicroChecks.Footprint(level, states);
        Assert.IsEmpty(
            violations,
            $"{levelName}: {violations.Count} footprint counter-examples, e.g. " +
            string.Join(" | ", violations.Take(5).Select(v => v.Message)));
    }
}
