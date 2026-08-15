using System.Text;

using FishFillets.Physics;
using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// Locates completeness leaks in the macro decomposition instead of guessing at
/// them.
///
/// <para>A macro edge is "travel, then one thing that changes the world". So a
/// known-optimal solution is expressible only if every point where one fish stops
/// swimming and the other takes over is a placement <c>IsParking</c> recognises -
/// otherwise the search has no state there and can never continue from it.</para>
///
/// <para>Replaying a real optimum and checking exactly those handover points says
/// which rule is missing, and where on the board.</para>
/// </summary>
[TestClass]
public sealed class MacroExpressibilityTests
{
    /// <summary>
    /// <b>These counts are a snapshot of a known incompleteness, not a goal.</b>
    /// A handover that is not a parking place does not by itself lose the level -
    /// the search only has to find <i>some</i> solution of the same length, not
    /// this one, and both <c>start</c> and <c>submarine</c> still reach their
    /// optimum with one such point each. The numbers are pinned so that a change
    /// to the parking rule has to state what it did to them.
    /// </summary>
    [TestMethod]
    [DataRow("wc", 13, 5)]
    [DataRow("start", 1, 1)]
    [DataRow("submarine", 2, 1)]
    public void HandoverPointsThatAreNotParkingPlaces(string levelName, int expectedHandovers, int expectedGaps)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        string moves = TestCorpus.Solution(levelName);
        LevelReduction reduction = LevelReduction.Verified(level, moves);
        var router = new InertRouter(level, reduction);
        var room = new Room(level);

        var gaps = new StringBuilder();
        int handovers = 0, gapCount = 0;

        for (int i = 0; i < moves.Length; i++)
        {
            int unit = UnitOf(level, moves[i]);
            Assert.IsGreaterThanOrEqualTo(0, unit, $"unknown symbol '{moves[i]}' at {i}");

            Assert.IsTrue(room.ApplyMove(moves[i]), $"{levelName}: move {i} ('{moves[i]}') failed to replay");

            // A handover is where this fish stops and the other one starts. The
            // last move of the solution is not one - nothing follows it.
            int next = i + 1 < moves.Length ? UnitOf(level, moves[i + 1]) : unit;
            if (next == unit)
            {
                continue;
            }

            int fish = level.Units[unit].Model;
            (int x, int y, _) = room.Position(fish);
            if (room.State(fish).IsOut || !room.State(fish).IsAlive)
            {
                continue; // it left or died; there is nothing to park
            }

            handovers++;
            if (!router.IsParking(room, fish, x, y))
            {
                gapCount++;
                gaps.Append($"\n  move {i,4} ('{moves[i]}')  {level.Models[fish].Kind} stops at ({x},{y})");
                gaps.Append(Surroundings(room, level, fish, x, y));
            }
        }

        Assert.AreEqual(expectedHandovers, handovers, $"{levelName}: number of handover points");
        Assert.AreEqual(
            expectedGaps,
            gapCount,
            $"{levelName}: handovers the parking rule does not recognise:{gaps}\n");
    }

    /// <summary>What is next to the fish, which is what a parking rule has to key on.</summary>
    private static string Surroundings(Room room, Level level, int fish, int x, int y)
    {
        var text = new StringBuilder("  [");
        (string label, int dx, int dy)[] directions =
            [("above", 0, -1), ("below", 0, 1), ("left", -1, 0), ("right", 1, 0)];

        foreach ((string label, int dx, int dy) in directions)
        {
            int other = room.GetModel(x + dx, y + dy);
            string what = other == Room.Empty ? "-"
                : other >= level.ModelCount ? "border"
                : level.Models[other].Kind;
            text.Append($"{label}:{what} ");
        }

        return text.Append(']').ToString();
    }

    private static int UnitOf(Level level, char symbol)
    {
        for (int u = 0; u < level.Units.Length; u++)
        {
            UnitDef unit = level.Units[u];
            if (symbol == unit.Up || symbol == unit.Down || symbol == unit.Left || symbol == unit.Right)
            {
                return u;
            }
        }

        return -1;
    }
}
