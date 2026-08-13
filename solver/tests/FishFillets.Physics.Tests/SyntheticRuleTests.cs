using FishFillets.Physics;

namespace FishFillets.Physics.Tests;

/// <summary>
/// Hand-built rooms exercising one rule each. The reference-solution corpus
/// proves the rules compose correctly, but only along paths that work - it never
/// visits a losing state, which is where a search spends most of its time. These
/// pin down the outcomes a search prunes on, in rooms small enough to reason
/// about by hand.
/// </summary>
[TestClass]
public sealed class SyntheticRuleTests
{
    /// <summary>
    /// A 7x6 room, walled all round, with a 5x4 interior (x 1..5, y 1..4) and a
    /// floor at y = 5.
    /// </summary>
    private const string ClosedRoom = """
        XXXXXXX
        X.....X
        X.....X
        X.....X
        X.....X
        XXXXXXX
        """;

    /// <summary>The same room with a gap in the left wall at y = 3.</summary>
    private const string RoomWithExit = """
        XXXXXXX
        X.....X
        X.....X
        ......X
        X.....X
        XXXXXXX
        """;

    [TestMethod]
    public void UnsupportedItem_FallsToTheFloorAtLoad()
    {
        // A room settles before the player gets control, so an item dropped into
        // mid-air by the level file is already resting when play starts.
        var room = Build(ClosedRoom, Item("item_light", 2, 1));

        Assert.AreEqual(4, room.State(1).Y, "the item should have fallen to rest on the floor");
        Assert.IsTrue(room.IsFresh, "the room should be settled after loading");
    }

    [TestMethod]
    public void FishPushesLightItem()
    {
        var room = Build(
            ClosedRoom,
            Fish("fish_small", 1, 4, isLeft: false),
            Item("item_light", 2, 4));

        Assert.IsTrue(room.ApplyMove('r'), "the small fish should be able to push a light item");
        Assert.AreEqual(2, room.State(1).X, "the fish should have advanced");
        Assert.AreEqual(3, room.State(2).X, "the item should have been pushed along");
    }

    [TestMethod]
    public void SmallFishCannotPushHeavyItem()
    {
        // Only the big fish has HEAVY power; this is the rule that makes most
        // levels puzzles at all.
        var room = Build(
            ClosedRoom,
            Fish("fish_small", 1, 4, isLeft: false),
            Item("item_heavy", 2, 4));

        Assert.IsFalse(room.ApplyMove('r'), "the small fish must not move a heavy item");
        Assert.AreEqual(1, room.State(1).X, "a rejected push must not move the fish either");
    }

    [TestMethod]
    public void BigFishPushesHeavyItem()
    {
        var room = Build(
            ClosedRoom,
            Fish("fish_big", 1, 4, isLeft: false),
            Item("item_heavy", 2, 4));

        Assert.IsTrue(room.ApplyMove('R'), "the big fish should be able to push a heavy item");
        Assert.AreEqual(3, room.State(2).X);
    }

    [TestMethod]
    public void MovingAgainstYourFacing_TurnsInsteadOfMoving()
    {
        // Turning costs a move symbol of its own but doesn't move the fish -
        // which is why facing has to be part of a search state (solver/docs/001).
        var room = Build(ClosedRoom, Fish("fish_small", 3, 4, isLeft: true));

        Assert.IsTrue(room.ApplyMove('r'), "turning is always possible");
        Assert.AreEqual(3, room.State(1).X, "the first 'r' should only turn the fish around");
        Assert.IsFalse(room.State(1).IsLeft);

        Assert.IsTrue(room.ApplyMove('r'));
        Assert.AreEqual(4, room.State(1).X, "the second 'r' should actually move it");
    }

    [TestMethod]
    public void HeavyItemRestingOnSmallFish_KillsIt()
    {
        // The stress rule: something too heavy for your power resting on you,
        // with nothing else holding it up. This is the main way a solution goes
        // wrong, and the reason IsSolvable() is a search's primary prune.
        var room = Build(
            ClosedRoom,
            Fish("fish_small", 2, 4, goal: "goal_escape"),
            Item("item_heavy", 2, 3));

        Assert.IsFalse(room.State(1).IsAlive, "the small fish cannot hold a heavy item");
        Assert.IsFalse(room.IsSolvable(), "a dead fish can never satisfy goal_escape");
        Assert.IsFalse(room.IsSolved());
    }

    [TestMethod]
    public void HeavyItemRestingOnBigFish_IsSurvivable()
    {
        // Same room, stronger fish - confirms the test above is measuring power
        // and not just "a heavy item is nearby".
        var room = Build(
            ClosedRoom,
            Fish("fish_big", 2, 4, goal: "goal_escape"),
            Item("item_heavy", 2, 3));

        Assert.IsTrue(room.State(1).IsAlive, "the big fish should hold a heavy item");
        Assert.IsTrue(room.IsSolvable());
    }

    [TestMethod]
    public void FishAtTheBorder_EscapesWithoutBeingDriven()
    {
        // Escaping is automatic once a goal_escape model stands at the edge with
        // a clear way out - it costs no move symbol, which is why a solution
        // string ends on the move that gets the fish there, not on the exit.
        var room = Build(RoomWithExit, Fish("fish_small", 0, 3, goal: "goal_escape"));

        Assert.IsTrue(room.State(1).IsOut, "the fish should have left the room while it settled");
        Assert.IsTrue(room.IsSolved(), "with its only goal satisfied the room is solved");
        Assert.AreEqual(0, room.StepCount, "escaping must not consume a move");
    }

    [TestMethod]
    public void ItemBlockingTheExit_KeepsTheFishIn()
    {
        var room = Build(
            RoomWithExit,
            Fish("fish_small", 1, 3, goal: "goal_escape"),
            Item("item_fixed", 0, 3));

        Assert.IsFalse(room.State(1).IsOut, "the blocked fish should still be in the room");
        Assert.IsFalse(room.IsSolved());
        Assert.IsTrue(room.IsSolvable(), "still alive, so the goal is not lost - just not reached");
    }

    // ---------------------------------------------------------------- helpers --

    private static ModelJson Fish(string kind, int x, int y, bool isLeft = true, string goal = "goal_no") =>
        new() { Kind = kind, X = x, Y = y, Shape = "X\n", Goal = goal, IsLeft = isLeft };

    private static ModelJson Item(string kind, int x, int y) =>
        new() { Kind = kind, X = x, Y = y, Shape = "X\n", Goal = "goal_no", IsLeft = true };

    /// <summary>
    /// Builds a room from a wall shape plus models. The wall is always model 0,
    /// matching how every real level declares it (<c>room = addModel("item_fixed",
    /// 0, 0, ...)</c>), so the models passed here start at index 1.
    /// </summary>
    private static Room Build(string walls, params ModelJson[] models)
    {
        string[] rows = walls.Split('\n');
        var level = new LevelJson
        {
            Name = "synthetic",
            Width = rows.Max(r => r.Length),
            Height = rows.Length,
            Models =
            [
                new ModelJson
                {
                    Kind = "item_fixed",
                    X = 0,
                    Y = 0,
                    Shape = walls + "\n",
                    Goal = "goal_no",
                    IsLeft = true,
                },
                .. models,
            ],
        };

        return new Room(Level.Build(level));
    }
}
