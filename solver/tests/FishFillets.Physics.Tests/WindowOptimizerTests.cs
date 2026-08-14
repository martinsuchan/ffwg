using FishFillets.Physics;
using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// The window optimiser has two properties worth testing, and they need
/// different setups: that it never breaks a solution (easy to check on real
/// input), and that it actually finds savings (needs input with a known,
/// deliberate detour, because the bundled solutions have none - see
/// solver/docs/003).
/// </summary>
[TestClass]
public sealed class WindowOptimizerTests
{
    /// <summary>
    /// A two-move detour that provably returns to the exact same state: turning
    /// against your facing and back moves the fish nowhere (Unit::goLeft/goRight
    /// only flips the side when you are not already facing that way), so it is a
    /// guaranteed no-op regardless of level or position.
    /// </summary>
    [TestMethod]
    [DataRow("cannons")]
    [DataRow("start")]
    public void RemovesADeliberateDetour(string levelName)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        string original = TestCorpus.Solution(levelName);

        var room = new Room(level);
        bool facingLeft = room.Position(level.Units[0].Model).IsLeft;
        string detour = facingLeft ? "rl" : "lr";
        if (level.Units[0].Left != 'l')
        {
            detour = facingLeft
                ? $"{level.Units[0].Right}{level.Units[0].Left}"
                : $"{level.Units[0].Left}{level.Units[0].Right}";
        }

        string padded = detour + original;
        Assert.IsTrue(
            SolutionValidator.Validate(new Room(level), padded).Solved,
            "the padded solution should still solve the level - the detour must be a no-op");
        Assert.AreEqual(original.Length + 2, padded.Length);

        OptimizeResult result = new WindowOptimizer(level).Optimize(padded, window: 8);

        Assert.AreEqual(original.Length, result.Moves.Length, "the two wasted moves should have been removed");
        Assert.IsTrue(SolutionValidator.Validate(new Room(level), result.Moves).Solved);
    }

    [TestMethod]
    [DataRow("cannons")]
    [DataRow("gems")]
    public void NeverBreaksASolution(string levelName)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        string original = TestCorpus.Solution(levelName);

        OptimizeResult result = new WindowOptimizer(level).Optimize(original, window: 10);

        Assert.IsLessThanOrEqualTo(original.Length, result.Moves.Length, "a result can never be longer");
        Assert.IsTrue(
            SolutionValidator.Validate(new Room(level), result.Moves).Solved,
            "the optimised solution must still solve the level");
    }

    [TestMethod]
    public void RejectsAMoveStringThatIsNotAValidSolution()
    {
        Level level = TestCorpus.Instance.LoadLevel("cannons");

        Assert.ThrowsExactly<InvalidOperationException>(
            () => new WindowOptimizer(level).Optimize("uuXuu", window: 4));
    }
}
