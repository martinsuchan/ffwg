using FishFillets.Physics;

namespace FishFillets.Physics.Tests;

/// <summary>
/// The other half of the correctness argument: a validator that accepts every
/// real solution is worthless if it also accepts wrong ones. These pin down the
/// three distinct ways a candidate move string can be bad, and - importantly -
/// that each is reported at the right place.
///
/// A solver's search leans on exactly this: <see cref="Room.ApplyMove"/>
/// returning false is how an illegal successor gets discarded, and
/// <see cref="Room.IsSolved"/> is the goal test. Both being strict is what stops
/// a search "solving" a level it hasn't.
/// </summary>
[TestClass]
public sealed class InvalidSolutionTests
{
    private const string Level = "airplane";

    // -------------------------------------------------- rejected move strings --

    [TestMethod]
    public void UnknownSymbol_IsRejectedAtItsIndex()
    {
        SolutionResult result = SolutionValidator.Validate(TestCorpus.Room(Level), "uuX");

        Assert.IsFalse(result.Solved);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual(2, result.Steps, "should stop at the bad symbol, having applied the two before it");
        Assert.AreEqual('X', result.FailedSymbol);
    }

    [TestMethod]
    public void BlockedMove_IsRejected()
    {
        // airplane's small fish starts just above the hull: it can swim down a
        // few cells and is then blocked. Driving it into that wall must fail
        // rather than quietly doing nothing.
        SolutionResult result = SolutionValidator.Validate(TestCorpus.Room(Level), new string('d', 40));

        Assert.IsFalse(result.Solved);
        Assert.IsNotNull(result.Error);
        Assert.IsTrue(result.Steps is > 0 and < 40, $"expected to be blocked partway, stopped at {result.Steps}");
        Assert.AreEqual('d', result.FailedSymbol);
    }

    [TestMethod]
    public void MoveAfterTheFishHasEscaped_IsRejected()
    {
        // Every move symbol belongs to a specific fish, and a fish that has left
        // the room can't be driven (Unit::willMove()), so a solution with
        // anything appended to it must be rejected, not silently ignored.
        string solved = TestCorpus.Solution(Level);
        SolutionResult result = SolutionValidator.Validate(TestCorpus.Room(Level), solved + "u");

        Assert.IsFalse(result.Solved);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual(solved.Length, result.Steps, "the real solution should apply in full first");
    }

    // ------------------------------------------- valid moves, but not a win --

    [TestMethod]
    public void EmptySolution_IsNotSolved()
    {
        SolutionResult result = SolutionValidator.Validate(TestCorpus.Room(Level), "");

        Assert.IsFalse(result.Solved);
        Assert.IsNull(result.Error, "an empty string is not an error, it just doesn't solve anything");
        Assert.AreEqual(0, result.Steps);
    }

    [TestMethod]
    public void TruncatedSolution_AppliesCleanlyButIsNotSolved()
    {
        string solved = TestCorpus.Solution(Level);
        string truncated = solved[..(solved.Length - 1)];

        SolutionResult result = SolutionValidator.Validate(TestCorpus.Room(Level), truncated);

        Assert.IsNull(result.Error, "every move of a prefix is still legal");
        Assert.AreEqual(truncated.Length, result.Steps);
        Assert.IsFalse(result.Solved, "one move short of the end must not count as solved");
    }

    /// <summary>
    /// The strongest form of the above: a level must be solved on the last move
    /// and no earlier. Catches an IsSolved() that fires too eagerly - which would
    /// make a search return truncated "solutions".
    /// </summary>
    [TestMethod]
    [DataRow("airplane")]
    [DataRow("gems")]
    [DataRow("cannons")]
    [DataRow("start")]
    public void SolutionIsNotSolvedBeforeItsLastMove(string level)
    {
        string moves = TestCorpus.Solution(level);
        var room = TestCorpus.Room(level);

        Assert.IsFalse(room.IsSolved(), $"{level} is solved before any move was made");

        for (int i = 0; i < moves.Length; i++)
        {
            Assert.IsTrue(room.ApplyMove(moves[i]), $"{level}: move {i} ('{moves[i]}') was rejected");

            bool last = i == moves.Length - 1;
            Assert.AreEqual(
                last,
                room.IsSolved(),
                last
                    ? $"{level}: not solved after the final move"
                    : $"{level}: reported solved after move {i} of {moves.Length}");
        }
    }

    // ------------------------------------------------- rejection is harmless --

    /// <summary>
    /// An unknown symbol must leave the room exactly as it was, at every point
    /// along a real solution - otherwise a search would corrupt its own state
    /// just by probing a successor that turns out to be illegal.
    /// </summary>
    [TestMethod]
    public void UnknownSymbol_LeavesTheRoomUntouched()
    {
        string moves = TestCorpus.Solution(Level);
        var room = TestCorpus.Room(Level);

        for (int i = 0; i < moves.Length; i++)
        {
            ModelSnapshot[] before = Snapshot(room);
            Assert.IsFalse(room.ApplyMove('?'), $"'?' was accepted as a move at step {i}");
            AssertUnchanged(before, room, $"after a rejected '?' at step {i}");

            Assert.IsTrue(room.ApplyMove(moves[i]), $"move {i} ('{moves[i]}') was rejected");
        }

        Assert.IsTrue(room.IsSolved(), "probing rejected moves broke the solution");
    }

    /// <summary>
    /// The same for a *legal symbol whose move is impossible* - the case a search
    /// hits constantly. Swims the small fish down until the hull blocks it, then
    /// checks that the failed attempt changed nothing and the fish still moves.
    /// </summary>
    [TestMethod]
    public void BlockedMove_LeavesTheRoomUntouched()
    {
        var room = TestCorpus.Room(Level);

        int applied = 0;
        while (true)
        {
            ModelSnapshot[] before = Snapshot(room);
            if (!room.ApplyMove('d'))
            {
                AssertUnchanged(before, room, "after a blocked 'd'");
                break;
            }

            applied++;
            Assert.IsLessThan(50, applied, "expected the small fish to be blocked swimming down");
        }

        Assert.IsGreaterThan(0, applied, "the small fish should manage at least one downward move");
        Assert.IsTrue(room.ApplyMove('u'), "the fish should still be drivable after a blocked move");
    }

    // ---------------------------------------------------------------- helpers --

    /// <summary>
    /// Everything a rejected move must not touch. TouchDir is deliberately
    /// excluded: SetTouched() records it even on a rejected move, which is why
    /// solver/docs/001 requires a search to keep it out of its state key.
    /// </summary>
    private readonly record struct ModelSnapshot(short X, short Y, bool IsLeft, bool IsAlive, bool IsLost, bool IsOut);

    private static ModelSnapshot[] Snapshot(Room room)
    {
        var snapshot = new ModelSnapshot[room.ModelCount];
        for (int i = 0; i < room.ModelCount; i++)
        {
            ref readonly ModelState m = ref room.State(i);
            snapshot[i] = new ModelSnapshot(m.X, m.Y, m.IsLeft, m.IsAlive, m.IsLost, m.IsOut);
        }

        return snapshot;
    }

    private static void AssertUnchanged(ModelSnapshot[] before, Room room, string context)
    {
        for (int i = 0; i < before.Length; i++)
        {
            ref readonly ModelState m = ref room.State(i);
            Assert.AreEqual(
                before[i],
                new ModelSnapshot(m.X, m.Y, m.IsLeft, m.IsAlive, m.IsLost, m.IsOut),
                $"model {i} changed {context}");
        }

        Assert.IsTrue(room.IsFresh, $"the room is not settled {context}");
    }
}
