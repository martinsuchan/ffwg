using FishFillets.Physics.Tests.Micro;
using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// The layout solver's two-level table (<see cref="LayoutTable"/>): which states
/// share a layout, that a layout plus fish placements is enough to rebuild the
/// room, and the min-cost semantics of the entries under a layout.
/// </summary>
[TestClass]
public sealed class LayoutTableTests
{
    /// <summary>
    /// Along the reference solution: consecutive states share a layout exactly
    /// when no item moved and no fish left, and moving a fish anywhere else in
    /// the key never changes the layout.
    /// </summary>
    [TestMethod]
    [DataRow("start")]
    [DataRow("wc")]
    public void Layout_IgnoresFishPositions_AndSeesItemsAndExits(string levelName)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        int[] mobile = LevelReduction.Compute(level).MobileModels;
        var table = new LayoutTable(level, mobile, entryKeySize: 4);
        bool[] isFish = [.. mobile.Select(m => level.Models[m].IsAlive)];
        int size = mobile.Length * StateKeyEdit.SlotSize;

        var room = new Room(level);
        byte[] key = new byte[size];
        byte[] previous = new byte[size];
        room.WriteStateKey(previous, mobile);
        int previousId = table.Intern(previous, out bool added);
        Assert.IsTrue(added);
        Assert.AreEqual(0, previousId);

        int itemMoves = 0, exits = 0;
        foreach (char move in TestCorpus.Solution(levelName))
        {
            Assert.IsTrue(room.ApplyMove(move));
            room.WriteStateKey(key, mobile);
            int id = table.Intern(key, out _);

            bool itemMoved = false, fishLeft = false;
            for (int n = 0; n < mobile.Length; n++)
            {
                int o = n * StateKeyEdit.SlotSize;
                if (isFish[n])
                {
                    fishLeft |= (key[o + 4] & 4) != (previous[o + 4] & 4);
                }
                else
                {
                    itemMoved |= !key.AsSpan(o, StateKeyEdit.SlotSize).SequenceEqual(previous.AsSpan(o, StateKeyEdit.SlotSize));
                }
            }

            if (itemMoved || fishLeft)
            {
                Assert.AreNotEqual(previousId, id, $"{levelName}: '{move}' changed the layout but kept its id");
                itemMoves += itemMoved ? 1 : 0;
                exits += fishLeft ? 1 : 0;
            }
            else
            {
                Assert.AreEqual(previousId, id, $"{levelName}: '{move}' only moved a fish but changed the layout");
            }

            // Any other fish placement: still the same layout.
            for (int n = 0; n < mobile.Length; n++)
            {
                if (isFish[n] && (key[(n * StateKeyEdit.SlotSize) + 4] & (4 | 8)) == 0)
                {
                    byte[] moved = (byte[])key.Clone();
                    StateKeyEdit.SetPlacement(moved, n, 1, 1, true);
                    Assert.AreEqual(id, table.Intern(moved, out bool movedAdded));
                    Assert.IsFalse(movedAdded);
                }
            }

            key.CopyTo(previous, 0);
            previousId = id;
        }

        Assert.IsGreaterThan(0, itemMoves);
        Assert.AreEqual(level.Units.Length, exits, "every fish leaves once");
        Assert.IsLessThan(TestCorpus.Solution(levelName).Length, table.LayoutCount);
    }

    /// <summary>identical-blocks: swapping two identical blocks is the same layout; moving one is not.</summary>
    [TestMethod]
    public void Layout_IdenticalItemsSwapped_InternToTheSameId()
    {
        Level level = MicroCorpus.Get("identical-blocks").Build();
        int[] mobile = LevelReduction.Compute(level).MobileModels;
        int[][] classes = ItemSymmetry.Classes(level, mobile);
        Assert.HasCount(1, classes);
        Assert.IsGreaterThanOrEqualTo(2, classes[0].Length);

        var table = new LayoutTable(level, mobile, entryKeySize: 1);
        byte[] key = new byte[mobile.Length * StateKeyEdit.SlotSize];
        new Room(level).WriteStateKey(key, mobile);
        int id = table.Intern(key, out _);

        const int size = StateKeyEdit.SlotSize;
        int a = classes[0][0], b = classes[0][^1];
        byte[] swapped = (byte[])key.Clone();
        key.AsSpan(a * size, size).CopyTo(swapped.AsSpan(b * size, size));
        key.AsSpan(b * size, size).CopyTo(swapped.AsSpan(a * size, size));
        Assert.IsFalse(key.AsSpan().SequenceEqual(swapped));
        Assert.AreEqual(id, table.Intern(swapped, out bool added));
        Assert.IsFalse(added);

        byte[] shifted = (byte[])key.Clone();
        (int x, int y, bool isLeft) = StateKeyEdit.GetPlacement(key, a);
        StateKeyEdit.SetPlacement(shifted, a, x, y + 1, isLeft);
        Assert.AreNotEqual(id, table.Intern(shifted, out added));
        Assert.IsTrue(added);
        Assert.AreEqual(2, table.LayoutCount);
    }

    /// <summary>
    /// LayoutKey + each in-room fish's placement rebuilds the room: the very
    /// same key without identical items (start, along its solution), and a room
    /// with the same canonical layout when there are (identical-blocks, every
    /// state of its graph).
    /// </summary>
    [TestMethod]
    public void LayoutKey_WithFishPlacements_RestoresTheRoom()
    {
        Level level = TestCorpus.Instance.LoadLevel("start");
        int[] mobile = LevelReduction.Compute(level).MobileModels;
        Assert.IsEmpty(ItemSymmetry.Classes(level, mobile), "start is meant to be the case without identical items");

        var room = new Room(level);
        var states = new List<RoomSnapshot> { room.Capture() };
        foreach (char move in TestCorpus.Solution("start"))
        {
            room.ApplyMove(move);
            states.Add(room.Capture());
        }

        CheckRestore(level, mobile, states, exact: true);

        Level blocks = MicroCorpus.Get("identical-blocks").Build();
        StateGraph graph = ReferenceBfs.For(MicroCorpus.Get("identical-blocks"));
        var solvable = new List<RoomSnapshot>();
        for (int i = 0; i < graph.Count; i++)
        {
            if (graph.Solvable[i])
            {
                solvable.Add(graph.States[i]);
            }
        }

        CheckRestore(blocks, LevelReduction.Compute(blocks).MobileModels, solvable, exact: false);
    }

    private static void CheckRestore(Level level, int[] mobile, List<RoomSnapshot> states, bool exact)
    {
        var table = new LayoutTable(level, mobile, entryKeySize: 1);
        var room = new Room(level);
        var restored = new Room(level);
        int size = mobile.Length * StateKeyEdit.SlotSize;
        byte[] original = new byte[size];
        byte[] rebuilt = new byte[size];
        byte[] back = new byte[size];
        byte[] canonicalOriginal = new byte[size];
        byte[] canonicalBack = new byte[size];

        foreach (RoomSnapshot state in states)
        {
            room.RestoreFrom(state);
            room.WriteStateKey(original, mobile);
            int id = table.Intern(original, out _);

            table.LayoutKey(id).CopyTo(rebuilt);
            for (int n = 0; n < mobile.Length; n++)
            {
                ref readonly ModelState m = ref room.State(mobile[n]);
                if (level.Models[mobile[n]].IsAlive && !m.IsOut && !m.IsLost)
                {
                    StateKeyEdit.SetPlacement(rebuilt, n, m.X, m.Y, m.IsLeft);
                }
            }

            restored.RestoreMobile(rebuilt, mobile);
            restored.WriteStateKey(back, mobile);

            if (exact)
            {
                Assert.IsTrue(original.AsSpan().SequenceEqual(rebuilt), "layout key + fish placements is not the original key");
                Assert.IsTrue(original.AsSpan().SequenceEqual(back), "the restored room's key differs from the original");
            }
            else
            {
                table.Canonicalize(original, canonicalOriginal);
                table.Canonicalize(back, canonicalBack);
                Assert.IsTrue(canonicalOriginal.AsSpan().SequenceEqual(canonicalBack), "the restored room has another layout");
                Assert.AreEqual(id, table.Intern(back, out bool added));
                Assert.IsFalse(added);
            }
        }
    }

    [TestMethod]
    public void TryImprove_KeepsTheCheapestCostPerEntry()
    {
        Level level = MicroCorpus.Get("identical-blocks").Build();
        int[] mobile = LevelReduction.Compute(level).MobileModels;
        var table = new LayoutTable(level, mobile, entryKeySize: 2);

        byte[] key = new byte[mobile.Length * StateKeyEdit.SlotSize];
        new Room(level).WriteStateKey(key, mobile);
        int first = table.Intern(key, out _);
        (int x, int y, bool isLeft) = StateKeyEdit.GetPlacement(key, ItemSymmetry.Classes(level, mobile)[0][0]);
        StateKeyEdit.SetPlacement(key, ItemSymmetry.Classes(level, mobile)[0][0], x, y + 1, isLeft);
        int second = table.Intern(key, out _);

        Assert.AreEqual(-1, table.Tag(first));
        table.Tag(first) = 42;
        Assert.AreEqual(42, table.Tag(first));
        Assert.AreEqual(-1, table.Tag(second));

        Assert.IsTrue(table.TryImprove(first, [1, 2], 10, out int e1, out bool added));
        Assert.IsTrue(added);
        Assert.AreEqual(10, table.BestCost(e1));

        Assert.IsFalse(table.TryImprove(first, [1, 2], 10, out int again, out added), "same cost is no improvement");
        Assert.IsFalse(added);
        Assert.AreEqual(e1, again);

        Assert.IsFalse(table.TryImprove(first, [1, 2], 12, out again, out added), "dearer is no improvement");
        Assert.AreEqual(10, table.BestCost(e1));

        Assert.IsTrue(table.TryImprove(first, [1, 2], 7, out again, out added), "cheaper is");
        Assert.IsFalse(added);
        Assert.AreEqual(e1, again);
        Assert.AreEqual(7, table.BestCost(e1));

        // The same entry key under another layout is another entry.
        Assert.IsTrue(table.TryImprove(second, [1, 2], 3, out int e2, out added));
        Assert.IsTrue(added);
        Assert.AreNotEqual(e1, e2);
        Assert.IsTrue(table.TryImprove(first, [2, 1], 5, out int e3, out added));
        Assert.IsTrue(added);

        Assert.AreEqual(3, table.EntryCount);
        Assert.AreEqual(2, table.EntriesIn(first));
        Assert.AreEqual(1, table.EntriesIn(second));
        Assert.AreEqual(first, table.LayoutOf(e3));
        Assert.AreEqual(second, table.LayoutOf(e2));
        Assert.IsTrue(table.EntryKey(e3).SequenceEqual(new byte[] { 2, 1 }));
        Assert.AreEqual(7, table.BestCost(e1));
        Assert.AreEqual(3, table.BestCost(e2));
    }

    /// <summary>Tags and entry counts survive the tables growing past their initial size.</summary>
    [TestMethod]
    public void Tags_SurviveGrowth()
    {
        Level level = MicroCorpus.Get("identical-blocks").Build();
        int[] mobile = LevelReduction.Compute(level).MobileModels;
        var table = new LayoutTable(level, mobile, entryKeySize: 1);
        byte[] key = new byte[mobile.Length * StateKeyEdit.SlotSize];
        new Room(level).WriteStateKey(key, mobile);

        // A non-fish slot to vary.
        int slot = Array.FindIndex(mobile, m => !level.Models[m].IsAlive);
        for (int i = 0; i < 300; i++)
        {
            StateKeyEdit.SetPlacement(key, slot, i % 20, i / 20, false);
            int id = table.Intern(key, out bool added);
            Assert.IsTrue(added);
            Assert.AreEqual(i, id);
            Assert.AreEqual(-1, table.Tag(id));
            table.Tag(id) = i * 2;
            table.TryImprove(id, [(byte)i], i, out _, out _);
        }

        for (int i = 0; i < 300; i++)
        {
            Assert.AreEqual(i * 2, table.Tag(i));
            Assert.AreEqual(1, table.EntriesIn(i));
        }
    }
}
