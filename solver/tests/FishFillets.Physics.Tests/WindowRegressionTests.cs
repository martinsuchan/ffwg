using FishFillets.Physics.Tests.Micro;
using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// The window optimiser after its rework onto the main solver's machinery
/// (solver/docs/029): parallel rounds, the packed state table, reversal
/// skipping with the window's own rule, identical items merged at the target,
/// a time limit and saving as it goes. Each test pins one of those so that a
/// later change cannot quietly lose it.
/// </summary>
[TestClass]
public sealed class WindowRegressionTests
{
    /// <summary>
    /// The shortcuts the optimiser found on the corpus (docs/003), at window
    /// 18: the result is pinned exactly, and the work it took as an upper
    /// bound - a search change that costs more nodes fails here, one that
    /// saves some should lower the number. It must also be the same, node for
    /// node, on several threads.
    /// </summary>
    [TestMethod]
    [DataRow("linux", 656, 652, 573_234L)]
    [DataRow("hole", 465, 463, 952_606L)]
    public void CorpusShortcuts_AreFound_TheSameOnAnyThreadCount(string name, int bundled, int expected, long maxNodes)
    {
        Level level = TestCorpus.Instance.LoadLevel(name);
        string original = TestCorpus.Solution(name);
        Assert.AreEqual(bundled, original.Length, $"{name}: the bundled solution changed");

        var options = new WindowOptions { Window = 18 };
        OptimizeResult serial = new WindowOptimizer(level).Optimize(original, options);
        OptimizeResult parallel = new WindowOptimizer(level).Optimize(original, options with { Threads = 8 });

        Assert.AreEqual(expected, serial.Moves.Length, $"{name}: {serial.Moves.Length} moves");
        Assert.IsTrue(SolutionValidator.Validate(new Room(level), serial.Moves).Solved, $"{name}: the result does not replay");
        Assert.IsLessThanOrEqualTo(maxNodes, serial.NodesExplored, $"{name}: {serial.NodesExplored:N0} nodes, pinned {maxNodes:N0}");

        Assert.AreEqual(serial.Moves, parallel.Moves, $"{name}: threads changed the result");
        Assert.AreEqual(serial.NodesExplored, parallel.NodesExplored, $"{name}: threads changed the work");
        Assert.AreEqual(serial.WindowsSearched, parallel.WindowsSearched, $"{name}: threads changed the windows searched");
    }

    /// <summary>
    /// Skipping reversals keeps the answer and saves work. Measured at the
    /// rework: linux 657,317 -> 573,234 nodes at window 18.
    /// </summary>
    [TestMethod]
    public void SkippingReversals_KeepsTheResult_AndSavesNodes()
    {
        Level level = TestCorpus.Instance.LoadLevel("linux");
        string original = TestCorpus.Solution("linux");
        var options = new WindowOptions { Window = 18, Threads = 4 };

        OptimizeResult skipping = new WindowOptimizer(level).Optimize(original, options);
        OptimizeResult plain = new WindowOptimizer(level).Optimize(original, options with { SkipReversals = false });

        Assert.AreEqual(plain.Moves.Length, skipping.Moves.Length);
        Assert.IsLessThan(plain.NodesExplored * 0.95, skipping.NodesExplored, $"{skipping.NodesExplored:N0} vs {plain.NodesExplored:N0}");
    }

    /// <summary>
    /// The one reversal the main solver skips and a window must not: turning
    /// round straight after a horizontal step, as the window's last move. On
    /// exit-detour the fish faces left in open water; "step left, turn right"
    /// is the only 2-move path to that target, and docs/028's rule alone would
    /// never try the turn.
    /// </summary>
    [TestMethod]
    public void ATurnRightAfterAStep_IsFound_WhenItEndsTheWindow()
    {
        Level level = MicroCorpus.Get("exit-detour").Build();
        var room = new Room(level);
        RoomSnapshot start = room.Capture();
        Assert.IsTrue(room.ApplyMove('l') && room.LastMoveWasFast && !room.LastMoveWasTurn, "l should be a fast step");
        Assert.IsTrue(room.ApplyMove('r') && room.LastMoveWasTurn, "r should be a turn");
        RoomSnapshot target = room.Capture();

        var optimizer = new WindowOptimizer(level);
        Assert.AreEqual("lr", optimizer.Shortcut(start, target, limit: 2));
        Assert.AreEqual("lr", optimizer.Shortcut(start, target, limit: 2, new WindowOptions { SkipReversals = false }));
        Assert.IsNull(optimizer.Shortcut(start, target, limit: 1));
    }

    /// <summary>
    /// Two rooms that differ only in which identical block is which are the
    /// same room to the physics, so a solution may pass through the second
    /// where the first was reachable sooner. Merged, a window ending in the
    /// second takes the shorter way to the first and splices it in; kept
    /// apart, the two never match. Built on identical-blocks: breadth first
    /// to the first pair of such rooms, then the shortest finish from there.
    /// </summary>
    [TestMethod]
    public void MergingIdenticalItems_FindsTheShorterWayToASwappedRoom()
    {
        Level level = MicroCorpus.Get("identical-blocks").Build();
        (string toFirst, string toSwapped, RoomSnapshot first) = FindSwappedPair(level, maxDepth: 40);
        string finish = ShortestFinish(level, first, maxDepth: 40);
        string padded = toSwapped + finish;
        Assert.IsTrue(SolutionValidator.Validate(new Room(level), padded).Solved, "the swapped room should finish the same way");

        // The window itself: breadth first proved the swapped room exactly
        // unreachable in fewer moves, so only the merged match finds the way.
        var room = new Room(level);
        RoomSnapshot start = room.Capture();
        foreach (char move in toSwapped)
        {
            room.ApplyMove(move);
        }

        RoomSnapshot swapped = room.Capture();
        var optimizer = new WindowOptimizer(level);
        string? shortcut = optimizer.Shortcut(start, swapped, toSwapped.Length - 1);
        Assert.IsNotNull(shortcut, "merged: no shorter way to the swapped room");
        Assert.AreEqual(toFirst.Length, shortcut.Length);
        Assert.IsNull(optimizer.Shortcut(start, swapped, toSwapped.Length - 1, new WindowOptions { MergeIdenticalItems = false }));

        // And the whole optimiser splices it in.
        OptimizeResult merged = optimizer.Optimize(padded, new WindowOptions { Window = toSwapped.Length });
        Assert.IsTrue(SolutionValidator.Validate(new Room(level), merged.Moves).Solved);
        Assert.IsLessThanOrEqualTo(toFirst.Length + finish.Length, merged.Moves.Length, $"merged: {padded.Length} -> {merged.Moves.Length}");
    }

    /// <summary>
    /// Breadth first over exact states: the first state whose room was already
    /// reached with identical items the other way round. Returns the paths to
    /// both and the earlier one's room.
    /// </summary>
    private static (string ToFirst, string ToSwapped, RoomSnapshot First) FindSwappedPair(Level level, int maxDepth)
    {
        int[] mobile = LevelReduction.Compute(level).MobileModels;
        var codec = new StateKeyCodec(level, mobile) { Canonical = true };
        var room = new Room(level);
        var wide = new byte[mobile.Length * 5];
        var compact = new byte[codec.CompactSize];
        var symbols = new char[room.MoveSymbolCount];
        room.GetMoveSymbols(symbols, out int count);

        (string Exact, string Canonical) Keys()
        {
            room.WriteStateKey(wide, mobile);
            codec.Encode(wide, compact);
            return (Convert.ToHexString(wide), Convert.ToHexString(compact));
        }

        (string startExact, string startCanonical) = Keys();
        var seen = new HashSet<string> { startExact };
        var firstByRoom = new Dictionary<string, (string Exact, string Path, RoomSnapshot State)>
        {
            [startCanonical] = (startExact, "", room.Capture()),
        };
        var frontier = new List<(RoomSnapshot State, string Path)> { (room.Capture(), "") };
        for (int depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<(RoomSnapshot, string)>();
            foreach ((RoomSnapshot state, string path) in frontier)
            {
                for (int s = 0; s < count; s++)
                {
                    room.RestoreFrom(state);
                    if (!room.ApplyMove(symbols[s]) || !room.IsSolvable() || room.IsSolved())
                    {
                        continue;
                    }

                    (string exact, string canonical) = Keys();
                    if (!seen.Add(exact))
                    {
                        continue;
                    }

                    // Strictly sooner: at the same depth there is nothing to shorten.
                    if (firstByRoom.TryGetValue(canonical, out var first))
                    {
                        if (first.Path.Length < path.Length + 1)
                        {
                            Assert.AreNotEqual(first.Exact, exact);
                            return (first.Path, path + symbols[s], first.State);
                        }
                    }
                    else
                    {
                        firstByRoom[canonical] = (exact, path + symbols[s], room.Capture());
                    }

                    next.Add((room.Capture(), path + symbols[s]));
                }
            }

            frontier = next;
        }

        Assert.Fail($"{level.Name}: no two rooms within {maxDepth} moves differ only in which identical item is which");
        return default;
    }

    /// <summary>Breadth first from <paramref name="from"/> to a solved room.</summary>
    private static string ShortestFinish(Level level, RoomSnapshot from, int maxDepth)
    {
        var room = new Room(level);
        var symbols = new char[room.MoveSymbolCount];
        room.GetMoveSymbols(symbols, out int count);
        var key = new byte[room.StateKeySize];
        var seen = new HashSet<string>();
        var frontier = new List<(RoomSnapshot State, string Path)> { (from, "") };
        for (int depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<(RoomSnapshot, string)>();
            foreach ((RoomSnapshot state, string path) in frontier)
            {
                for (int s = 0; s < count; s++)
                {
                    room.RestoreFrom(state);
                    if (!room.ApplyMove(symbols[s]) || !room.IsSolvable())
                    {
                        continue;
                    }

                    if (room.IsSolved())
                    {
                        return path + symbols[s];
                    }

                    room.WriteStateKey(key);
                    if (seen.Add(Convert.ToHexString(key)))
                    {
                        next.Add((room.Capture(), path + symbols[s]));
                    }
                }
            }

            frontier = next;
        }

        Assert.Fail($"{level.Name}: no finish within {maxDepth} moves");
        return "";
    }

    /// <summary>A time limit already passed: nothing searched, the input back unchanged, marked as cut short.</summary>
    [TestMethod]
    public void ATimeLimit_StopsAndKeepsAValidSolution()
    {
        Level level = TestCorpus.Instance.LoadLevel("cannons");
        string original = TestCorpus.Solution("cannons");
        OptimizeResult result = new WindowOptimizer(level).Optimize(
            original, new WindowOptions { Window = 10, Threads = 4, TimeLimit = TimeSpan.Zero });

        Assert.IsTrue(result.TimedOut);
        Assert.AreEqual(original, result.Moves);
        Assert.AreEqual(0, result.WindowsSearched);
    }

    /// <summary>
    /// The time limit also stops a window already being searched. Checked only
    /// between windows, one big window on corals ran 2.6 h past a 40-minute
    /// limit. A window cut short is not counted as searched, so not as barren.
    /// </summary>
    [TestMethod]
    public void ATimeLimit_CutsALongWindowShort()
    {
        Level level = TestCorpus.Instance.LoadLevel("corals");
        string original = TestCorpus.Solution("corals");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        OptimizeResult result = new WindowOptimizer(level).Optimize(
            original,
            new WindowOptions { Window = 60, NodeLimit = long.MaxValue, Threads = 2, TimeLimit = TimeSpan.FromMilliseconds(300) });

        Assert.IsTrue(result.TimedOut);
        Assert.AreEqual(original, result.Moves);
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
    }

    /// <summary>
    /// Every shortened solution is handed out as it is found, each one valid
    /// and shorter than the last, ending with the result. And a later round
    /// searches only the windows a splice changed: the rest are known.
    /// </summary>
    [TestMethod]
    public void Improvements_AreReportedAsFound_AndKnownWindowsAreNotSearchedAgain()
    {
        Level level = TestCorpus.Instance.LoadLevel("cannons");
        string original = TestCorpus.Solution("cannons");
        string turn = $"{level.Units[0].Left}{level.Units[0].Right}{level.Units[0].Left}{level.Units[0].Right}";
        string padded = original[..10] + turn + original[10..];
        Assert.IsTrue(SolutionValidator.Validate(new Room(level), padded).Solved, "turning on the spot twice should be a no-op");

        var reported = new List<string>();
        OptimizeResult result = new WindowOptimizer(level).Optimize(
            padded, new WindowOptions { Window = 8, Improved = reported.Add });

        Assert.AreEqual(original.Length, result.Moves.Length);
        Assert.IsNotEmpty(reported);
        Assert.AreEqual(result.Moves, reported[^1]);
        for (int i = 0; i < reported.Count; i++)
        {
            Assert.IsTrue(SolutionValidator.Validate(new Room(level), reported[i]).Solved, $"report {i} does not replay");
            Assert.IsLessThan(i == 0 ? padded.Length : reported[i - 1].Length, reported[i].Length);
        }

        // Pass 1 searches every window; the last pass only those a splice touched.
        int windows = padded.Length - 1;
        Assert.IsGreaterThan(1, result.Passes);
        Assert.IsLessThan(windows + (result.Passes - 1) * 40, result.WindowsSearched, $"{result.WindowsSearched} windows searched in {result.Passes} passes");
    }

    /// <summary>
    /// Clearing a store that holds few states in a large table zeroes only
    /// their chains; either way, nothing stored before may be found after.
    /// </summary>
    [TestMethod]
    public void StateStore_Clear_ForgetsEverything_FullOrSparse()
    {
        var store = new StateStore(keySize: 4, initialBuckets: 16);
        byte[] Key(int i) => BitConverter.GetBytes(i);

        for (int i = 0; i < 100_000; i++)
        {
            store.FindOrAdd(Key(i), out _);
        }

        store.Clear(); // full: the table is about as big as the count
        Assert.AreEqual(0, store.Count);

        foreach (int size in new[] { 3, 50, 1_000 })
        {
            for (int i = 0; i < size; i++)
            {
                store.FindOrAdd(Key(i * 7919), out bool added);
                Assert.IsTrue(added, $"key {i} survived a clear");
            }

            store.Clear(); // sparse: a few states in a table grown for 100 k
            Assert.AreEqual(0, store.Count);
        }

        for (int i = 0; i < 100_000; i++)
        {
            store.FindOrAdd(Key(i), out bool added);
            Assert.IsTrue(added, $"key {i} survived a clear");
        }
    }
}
