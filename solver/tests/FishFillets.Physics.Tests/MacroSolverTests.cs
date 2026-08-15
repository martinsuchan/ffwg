using FishFillets.Physics;
using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// The macro expansion is complete only as far as it is argued to be: an inert
/// run has to end at an action, or at a placement <c>InertRouter.IsParking</c>
/// recognises. Narrowing that set is what makes the search fast, and also what
/// can silently start losing solutions.
///
/// <para>These are the guard. The lengths are hall-of-fame records that plain A*
/// has separately proven optimal (<see cref="SolverTests"/>), so a macro run that
/// comes back longer means the decomposition has lost something real - which is
/// exactly how the current <c>wc</c> gap was found.</para>
/// </summary>
[TestClass]
public sealed class MacroSolverTests
{
    // Kept to levels that finish in under a second. wc (32 s), cannons (55 s),
    // noground (38 s) and wreck (54 s) exercise the same property; of those only
    // wc currently comes back long - see solver/docs/008.
    [TestMethod]
    [DataRow("start", 54)]
    [DataRow("submarine", 83)]
    public void MacroExpansion_ReproducesTheKnownOptimum(string levelName, int expected)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        var solver = new MacroSolver(level, LevelReduction.Verified(level, TestCorpus.Solution(levelName)));

        SolveResult result = solver.Solve(new SolveOptions { TimeLimit = TimeSpan.FromMinutes(2) });

        Assert.IsTrue(result.Solved, $"{levelName}: {result.Status}");
        Assert.AreEqual(
            expected,
            result.Moves!.Length,
            $"{levelName}: the macro expansion should still reach the known optimum");

        Assert.IsTrue(
            SolutionValidator.Validate(new Room(level), result.Moves).Solved,
            $"{levelName}: the macro answer does not replay");
    }
}
