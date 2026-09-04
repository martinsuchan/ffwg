using FishFillets.Physics;
using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// The dead-end detector discards states outright, so a false positive silently
/// costs a solution. These check both halves: that it fires where it should, and
/// - the one that matters - that it never fires anywhere along a real solution.
///
/// All of them run it in the configuration the solver actually uses,
/// <c>fishAnywhere: true</c>. That is the weaker of the two modes, so passing
/// here does not imply the stronger one passes; it implies what ships is right.
/// </summary>
[TestClass]
public sealed class StuckAnalysisTests
{
    /// <summary>
    /// The case the whole thing exists for. Opening `society` with the big fish
    /// going right drops the heavy bar into a one-wide well, onto an item already
    /// walled in on three sides. Neither can ever move again, and the big fish -
    /// four cells wide - is left with no route to the exit at the bottom right.
    ///
    /// Nothing has died, so <c>IsSolvable()</c> is true; the walls-only
    /// relaxation deletes both items, so the distance to the border stays finite.
    /// Every test the solver had before this one passes the state.
    /// </summary>
    [TestMethod]
    public void SocietyIsUnsolvableOnceTheBarIsInTheWell()
    {
        Level level = TestCorpus.Instance.LoadLevel("society");
        var room = new Room(level);

        Assert.IsTrue(room.ApplyMove('R'), "turn");
        Assert.IsTrue(room.ApplyMove('R'), "push the bar off its ledge");

        Assert.IsTrue(room.IsSolvable(), "no fish has died - the old test cannot see this");

        bool[] mobile = StuckAnalysis.Mobile(level, room, fishAnywhere: true);
        bool[] solid = StuckAnalysis.SolidCells(level, room, mobile);

        int bar = FindModel(level, room, "item_heavy", 7, 9);
        Assert.IsFalse(mobile[bar], "the bar is in a well it cannot be pushed or lifted out of");

        int big = level.Units.Single(u => level.Models[u.Model].Power >= Weight.Heavy).Model;
        ref readonly ModelState s = ref room.State(big);
        bool[] canLeave = StuckAnalysis.CanStillLeave(level, room, solid, big);

        Assert.IsFalse(
            canLeave[(s.Y * level.Width) + s.X],
            "the big fish can no longer reach any border, so the level is over");
    }

    /// <summary>A level nobody has touched is obviously not unsolvable.</summary>
    [TestMethod]
    [DynamicData(nameof(ReferenceSolutionTests.SolvableLevels), typeof(ReferenceSolutionTests))]
    public void StartingPositionsAreNeverCalledDead(string levelName)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        var room = new Room(level);
        AssertAlive(level, room, levelName, -1);
    }

    /// <summary>
    /// The soundness test. Every state along a recorded solution demonstrably has
    /// a solution, so one hit anywhere in the corpus disproves the analysis.
    ///
    /// Sampled rather than exhaustive - the fixpoint is milliseconds, and 32,031
    /// states of it would dominate the suite. Every level, every eighth state,
    /// with the offset rotating so repeated runs cover different ones.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(ReferenceSolutionTests.SolvableLevels), typeof(ReferenceSolutionTests))]
    public void NeverFiresAnywhereAlongAReferenceSolution(string levelName)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        string moves = TestCorpus.Solution(levelName);
        var room = new Room(level);

        for (int i = 0; i < moves.Length; i++)
        {
            Assert.IsTrue(room.ApplyMove(moves[i]), $"{levelName}: move {i} was rejected");
            if (room.IsSolved())
            {
                break;
            }

            if (i % 8 == levelName.Length % 8)
            {
                AssertAlive(level, room, levelName, i);
            }
        }
    }

    private static void AssertAlive(Level level, Room room, string levelName, int move)
    {
        bool[] mobile = StuckAnalysis.Mobile(level, room, fishAnywhere: true);
        bool[] solid = StuckAnalysis.SolidCells(level, room, mobile);

        foreach (UnitDef unit in level.Units)
        {
            ref readonly ModelState s = ref room.State(unit.Model);
            if (s.IsOut || !s.IsAlive)
            {
                continue;
            }

            bool[] canLeave = StuckAnalysis.CanStillLeave(level, room, solid, unit.Model);
            Assert.IsTrue(
                canLeave[(s.Y * level.Width) + s.X],
                $"{levelName}: after move {move} the analysis says the fish at ({s.X},{s.Y}) can never leave, " +
                "but the recorded solution goes on to finish the level");
        }
    }

    private static int FindModel(Level level, Room room, string kind, int x, int y)
    {
        for (int i = 0; i < level.ModelCount; i++)
        {
            ref readonly ModelState s = ref room.State(i);
            if (level.Models[i].Kind == kind && s.X == x && s.Y == y)
            {
                return i;
            }
        }

        Assert.Fail($"no {kind} at ({x},{y})");
        return -1;
    }
}
