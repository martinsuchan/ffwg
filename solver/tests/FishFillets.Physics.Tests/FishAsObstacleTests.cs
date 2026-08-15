using FishFillets.Physics;

namespace FishFillets.Physics.Tests;

/// <summary>
/// What happens to a fish that is <b>in the way</b>: something lands on it, or
/// something is pushed into it.
///
/// This decides whether a search has to generate "park here" states at all. If a
/// fish cannot survive being used as a platform or a buffer, parking is only ever
/// about getting out of the way, and the macro expansion can drop a whole class of
/// successors (solver/docs/005).
/// </summary>
[TestClass]
public sealed class FishAsObstacleTests
{
    private const string ClosedRoom = """
        XXXXXXX
        X.....X
        X.....X
        X.....X
        X.....X
        XXXXXXX
        """;

    /// <summary>
    /// A light item is held up by the big fish, which then swims aside, so the
    /// item falls one cell onto the small fish waiting below.
    /// </summary>
    [TestMethod]
    public void LightItemFallingOntoSmallFish_KillsIt()
    {
        var room = Build(
            ClosedRoom,
            Fish("fish_small", 3, 4),                 // 1: waiting on the floor
            Fish("fish_big", 3, 3),                   // 2: holding the item up
            Item("item_light", 3, 2));                // 3

        Assert.IsTrue(room.State(1).IsAlive, "nothing has happened yet");

        Assert.IsTrue(room.ApplyMove('L'), "the big fish should swim aside");
        Assert.AreEqual(3, room.State(3).Y, "the item should have fallen one cell");
        Assert.IsFalse(room.State(1).IsAlive, "even a LIGHT item kills what it lands on");
    }

    /// <summary>The same fall onto the strongest fish in the game.</summary>
    [TestMethod]
    public void LightItemFallingOntoBigFish_KillsItToo()
    {
        var room = Build(
            ClosedRoom,
            Fish("fish_big", 3, 4),                   // 1: waiting on the floor
            Fish("fish_small", 3, 3),                 // 2: holding the item up
            Item("item_light", 3, 2));                // 3

        Assert.IsTrue(room.ApplyMove('l'), "the small fish should swim aside");
        Assert.IsFalse(room.State(1).IsAlive, "a fall kills regardless of power");
    }

    /// <summary>
    /// The contrast that makes the rule clear: the same item, the same fish, but
    /// already at rest when play begins. Weight decides that case, not impact.
    /// </summary>
    [TestMethod]
    public void ItemRestingOnAFish_IsFine()
    {
        var room = Build(
            ClosedRoom,
            Fish("fish_small", 3, 4),
            Item("item_light", 3, 3));

        Assert.IsTrue(room.State(1).IsAlive, "carrying is not the same as being landed on");
        Assert.IsTrue(room.IsFresh);
    }

    /// <summary>
    /// Can one fish shove the other? Both are LIGHT and both have at least LIGHT
    /// power, so the weight table alone says yes.
    /// </summary>
    [TestMethod]
    public void BigFishCannotPushSmallFish()
    {
        var room = Build(
            ClosedRoom,
            Fish("fish_big", 1, 4, isLeft: false),
            Fish("fish_small", 2, 4));

        Assert.IsFalse(room.ApplyMove('R'), "a fish is not cargo");
        Assert.AreEqual(1, room.State(1).X);
        Assert.AreEqual(2, room.State(2).X);
    }

    /// <summary>
    /// And therefore an item pushed into a waiting fish does not shove it along
    /// either - the whole push is simply refused, even with room to spare.
    /// </summary>
    [TestMethod]
    public void ItemPushedIntoAFish_IsRefused()
    {
        var room = Build(
            ClosedRoom,
            Fish("fish_small", 4, 4),                          // 1: waiting, room behind it
            Fish("fish_big", 1, 4, isLeft: false),             // 2: pusher
            Item("item_light", 2, 4));                         // 3

        Assert.IsTrue(room.ApplyMove('R'), "push the item one cell");
        Assert.AreEqual(3, room.State(3).X);

        Assert.IsFalse(room.ApplyMove('R'), "now the item meets the fish, and stops");
        Assert.AreEqual(3, room.State(3).X, "the item did not advance");
        Assert.AreEqual(4, room.State(1).X, "and the fish was not shoved");
        Assert.IsTrue(room.State(1).IsAlive, "a refused push harms nobody");
    }

    /// <summary>
    /// The one way a fish can usefully station itself under something: a
    /// <b>multi-cell</b> item held up by one fish, with the other sliding in
    /// under a different part of its footprint. Support changes hands with no
    /// fall, so nobody is landed on and nobody dies.
    /// </summary>
    [TestMethod]
    public void SecondFishCanTakeOverSupportOfAWideItem_WithoutAFall()
    {
        var room = Build(
            ClosedRoom,
            Fish("fish_big", 3, 3),                            // 1: holding it up
            Fish("fish_small", 1, 3, isLeft: false),           // 2: coming to relieve it
            Wide("item_light", 2, 2, "XXX\n"));                // 3: spans x 2..4

        Assert.AreEqual(2, room.State(3).Y, "the item starts held up");

        Assert.IsTrue(room.ApplyMove('r'), "the small fish slides under the item's left end");
        Assert.AreEqual(2, room.State(2).X);

        // Downwards, because the small fish is now standing where it came from -
        // and a fish cannot push a fish.
        Assert.IsTrue(room.ApplyMove('D'), "the big fish drops out from under the item");
        Assert.AreEqual(4, room.State(1).Y);
        Assert.AreEqual(2, room.State(3).Y, "the item stays up - the small fish now holds it");
        Assert.IsTrue(room.State(1).IsAlive);
        Assert.IsTrue(room.State(2).IsAlive, "no fall happened, so nobody was crushed");
    }

    // ---------------------------------------------------------------- helpers --

    private static ModelJson Wide(string kind, int x, int y, string shape) =>
        new() { Kind = kind, X = x, Y = y, Shape = shape, Goal = "goal_no", IsLeft = true };


    private static ModelJson Fish(string kind, int x, int y, bool isLeft = true, string goal = "goal_no") =>
        new() { Kind = kind, X = x, Y = y, Shape = "X\n", Goal = goal, IsLeft = isLeft };

    private static ModelJson Item(string kind, int x, int y) =>
        new() { Kind = kind, X = x, Y = y, Shape = "X\n", Goal = "goal_no", IsLeft = true };

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
