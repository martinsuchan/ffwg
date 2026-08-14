using FishFillets.Physics;

namespace FishFillets.Physics.Tests;

/// <summary>
/// Snapshot/restore and the canonical state key - the primitives every search
/// stands on. Two states with the same key are claimed to be interchangeable,
/// and splicing one path segment out for another is only sound if that claim
/// holds, so these check it directly rather than through a search.
/// </summary>
[TestClass]
public sealed class StateLayerTests
{
    private const string Level = "airplane";

    [TestMethod]
    public void RestoreFrom_ReproducesTheStateExactly()
    {
        string moves = TestCorpus.Solution(Level);
        var room = TestCorpus.Room(Level);

        // Somewhere in the middle, where items have been pushed and have fallen.
        for (int i = 0; i < moves.Length / 2; i++)
        {
            Assert.IsTrue(room.ApplyMove(moves[i]));
        }

        RoomSnapshot mid = room.Capture();
        byte[] midKey = KeyOf(room);

        // Wander off, then come back.
        for (int i = moves.Length / 2; i < (moves.Length / 2) + 20; i++)
        {
            room.ApplyMove(moves[i]);
        }

        room.RestoreFrom(mid);
        CollectionAssert.AreEqual(midKey, KeyOf(room), "restoring did not reproduce the state key");

        // The real test of a restore is that the future still works: the rest of
        // the solution must replay from here and still solve the level.
        for (int i = moves.Length / 2; i < moves.Length; i++)
        {
            Assert.IsTrue(room.ApplyMove(moves[i]), $"move {i} failed after a restore");
        }

        Assert.IsTrue(room.IsSolved(), "the level should still solve after restoring mid-solution");
    }

    [TestMethod]
    public void RestoreFrom_RebuildsTheGrid()
    {
        // The grid is derived, not stored in a snapshot, so a restore has to
        // re-stamp it - otherwise moves would pass through stale occupancy.
        string moves = TestCorpus.Solution(Level);
        var room = TestCorpus.Room(Level);
        RoomSnapshot start = room.Capture();

        for (int i = 0; i < 60; i++)
        {
            room.ApplyMove(moves[i]);
        }

        room.RestoreFrom(start);

        var fresh = TestCorpus.Room(Level);
        for (int y = 0; y < fresh.Level.Height; y++)
        {
            for (int x = 0; x < fresh.Level.Width; x++)
            {
                Assert.AreEqual(fresh.GetModel(x, y), room.GetModel(x, y), $"grid differs at ({x},{y})");
            }
        }
    }

    [TestMethod]
    public void StateKey_DistinguishesEveryStateAlongASolution()
    {
        // Also re-establishes the docs/003 finding at test time: a reference
        // solution never revisits a state, so all 236 keys must be distinct. A
        // key too coarse to tell two real states apart would show up here.
        string moves = TestCorpus.Solution(Level);
        var room = TestCorpus.Room(Level);

        var seen = new HashSet<string> { Convert.ToHexString(KeyOf(room)) };
        foreach (char symbol in moves)
        {
            Assert.IsTrue(room.ApplyMove(symbol));
            Assert.IsTrue(
                seen.Add(Convert.ToHexString(KeyOf(room))),
                "two different states along the solution produced the same key");
        }

        Assert.AreEqual(moves.Length + 1, seen.Count);
    }

    [TestMethod]
    public void StateKey_IsUnchangedByARejectedMove()
    {
        // The documented caveat, pinned down: SetTouched() writes TouchDir even
        // when a move is refused, so the key must not include it. If it did,
        // merely probing a successor would change the key of the state you are
        // standing in - and a search would never find its way back.
        var room = TestCorpus.Room(Level);
        byte[] before = KeyOf(room);

        Assert.IsFalse(room.ApplyMove('?'), "expected an unknown symbol to be refused");
        CollectionAssert.AreEqual(before, KeyOf(room), "an unknown symbol changed the state key");

        // Now the case that actually records TouchDir: a legal symbol whose move
        // is blocked. Swim down until the hull refuses, comparing across the
        // refusal itself.
        int applied = 0;
        while (true)
        {
            byte[] beforeMove = KeyOf(room);
            if (!room.ApplyMove('d'))
            {
                CollectionAssert.AreEqual(beforeMove, KeyOf(room), "a blocked move changed the state key");
                break;
            }

            applied++;
            Assert.IsLessThan(50, applied, "expected the fish to be blocked swimming down");
        }

        Assert.IsGreaterThan(0, applied);
    }

    [TestMethod]
    public void MutableModels_ExcludeTheRoomShape()
    {
        // Model 0 is the room's wall shape in every level: not alive, FIXED, no
        // goal - so it can never change and has no business in a state key.
        Level level = TestCorpus.Instance.LoadLevel(Level);

        CollectionAssert.DoesNotContain(level.MutableModels, 0, "the room shape should not be in the state key");
        Assert.AreEqual(level.ModelCount - 1, level.MutableModels.Length);
    }

    [TestMethod]
    public void MutableModels_KeepAnOutputPlug()
    {
        // windoze's spuntik starts FIXED but turns into a normal LIGHT item as
        // fish go out through it, so "FIXED means immovable" is not enough on its
        // own to drop a model from the key.
        Level level = TestCorpus.Instance.LoadLevel("windoze");

        int plug = Array.FindIndex(level.Models, m => m.OutDir != Dir.No);
        Assert.IsGreaterThan(-1, plug, "windoze should declare an output_* plug");
        CollectionAssert.Contains(level.MutableModels, plug, "the output plug must be in the state key");
    }

    private static byte[] KeyOf(Room room)
    {
        var key = new byte[room.StateKeySize];
        room.WriteStateKey(key);
        return key;
    }
}
