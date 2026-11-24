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

        // Pass 1 searches every full window; the last pass only those a splice touched.
        int windows = padded.Length - 8 + 1;
        Assert.IsGreaterThan(1, result.Passes);
        Assert.IsLessThan(windows + (result.Passes - 1) * 40, result.WindowsSearched, $"{result.WindowsSearched} windows searched in {result.Passes} passes");
    }

    /// <summary>
    /// Every window searched is reported once, with a bound that says how far
    /// it got. A shortcut's bound is its length. A window ruled out reaches its
    /// own length. A window stopped by the node limit reports the f it had
    /// reached, which is below the length, and at that limit.
    /// </summary>
    [TestMethod]
    public void EveryWindow_IsReported_WithHowFarItGot()
    {
        Level level = TestCorpus.Instance.LoadLevel("cannons");
        string original = TestCorpus.Solution("cannons");
        string turn = $"{level.Units[0].Left}{level.Units[0].Right}{level.Units[0].Left}{level.Units[0].Right}";
        string padded = original[..10] + turn + original[10..];

        var reports = new System.Collections.Concurrent.ConcurrentBag<WindowReport>();
        OptimizeResult result = new WindowOptimizer(level).Optimize(
            padded, new WindowOptions { Window = 8, Threads = 4, WindowDone = reports.Add });

        Assert.HasCount(result.WindowsSearched, reports);

        // Pass 1: one full-length window per start, the last one ending at the
        // end. No shorter tail windows - each is inside that last one.
        WindowReport[] first = reports.Where(r => r.Pass == 1).OrderBy(r => r.Start).ToArray();
        Assert.HasCount(padded.Length - 8 + 1, first);
        Assert.IsTrue(first.All(r => r.Length == 8), "every window is full length");
        Assert.AreEqual(padded.Length, first[^1].End);
        Assert.AreEqual(result.NodesExplored, reports.Sum(r => r.Nodes));
        Assert.IsTrue(reports.Any(r => r.Outcome == WindowOutcome.Shortcut), "the padding should be found");
        foreach (WindowReport r in reports)
        {
            switch (r.Outcome)
            {
                case WindowOutcome.Shortcut:
                    Assert.IsLessThan(r.Length, r.Bound, $"[{r.Start}..{r.End}]");
                    break;
                case WindowOutcome.NoShortcut:
                    Assert.AreEqual(r.Length, r.Bound, $"[{r.Start}..{r.End}]");
                    break;
                default:
                    Assert.Fail($"[{r.Start}..{r.End}] {r.Outcome} without a node or time limit");
                    break;
            }
        }

        // A budget of one expansion stops every window that needs more.
        var capped = new List<WindowReport>();
        new WindowOptimizer(level).Optimize(
            padded, new WindowOptions { Window = 8, NodeLimit = 1, WindowDone = capped.Add });
        WindowReport[] stopped = capped.Where(r => r.Outcome == WindowOutcome.NodeLimit).ToArray();
        Assert.IsNotEmpty(stopped);
        Assert.IsTrue(stopped.All(r => r.Nodes == 1 && r.Bound < r.Length && r.Stored > 0), "a capped window reports its f, its one node and its table");
    }

    /// <summary>
    /// The spread order takes every window once. Its first ones, one per
    /// thread, sit at 1/5, 2/5, 3/5 and 4/5. Each later one is the middle of the
    /// widest stretch not yet taken, so no stretch left open is ever more
    /// than twice as wide as another.
    /// </summary>
    [TestMethod]
    public void SpreadOrder_SeedsEvenly_ThenSplitsTheWidestGap()
    {
        int[] order = WindowOptimizer.SpreadOrder(100, seeds: 4);
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 100).ToArray(), order);
        CollectionAssert.AreEqual(new[] { 20, 40, 60, 80 }, order[..4]);
        CollectionAssert.AreEqual(new[] { 9, 30, 50, 70, 90 }, order[4..9], "then the middles of the five stretches, widest first, then left to right");

        // After any prefix, the widest open stretch is at most about twice the narrowest.
        for (int taken = 9; taken < 60; taken++)
        {
            int[] points = order[..taken].Append(-1).Append(100).Order().ToArray();
            int[] gaps = points.Zip(points.Skip(1), (a, b) => b - a - 1).ToArray();
            Assert.IsLessThanOrEqualTo((2 * gaps.Min()) + 2, gaps.Max(), $"after {taken}: {string.Join(",", gaps)}");
        }

        CollectionAssert.AreEqual(new[] { 0 }, WindowOptimizer.SpreadOrder(1, 4));
        Assert.IsEmpty(WindowOptimizer.SpreadOrder(0, 4));
    }

    /// <summary>
    /// The per-window limits stop a window as the node limit does: reported
    /// with the bound reached, counted as searched, and the run goes on to the
    /// next window. The order of the windows does not change the result.
    /// </summary>
    [TestMethod]
    public void PerWindowLimits_StopAWindow_NotTheRun()
    {
        Level level = TestCorpus.Instance.LoadLevel("hole");
        string original = TestCorpus.Solution("hole");

        foreach ((WindowOptions limit, WindowOutcome expected) in new[]
        {
            (new WindowOptions { WindowMemoryLimit = 1 }, WindowOutcome.MemoryLimit),
            (new WindowOptions { WindowTimeLimit = TimeSpan.Zero }, WindowOutcome.WindowTimeLimit),
        })
        {
            var reports = new System.Collections.Concurrent.ConcurrentBag<WindowReport>();
            OptimizeResult result = new WindowOptimizer(level).Optimize(
                original, limit with { Window = 18, NodeLimit = long.MaxValue, Threads = 4, WindowDone = reports.Add });

            Assert.IsFalse(result.TimedOut, $"{expected}: the run itself must not stop");
            Assert.HasCount(result.WindowsSearched, reports);
            WindowReport[] cut = reports.Where(r => r.Outcome == expected).ToArray();
            Assert.IsNotEmpty(cut, $"{expected}: some window should need more than that");
            Assert.IsTrue(cut.All(r => r.HitLimit && r.Bound < r.Length && r.Nodes >= 4096), $"{expected}: stopped at the first check, with its bound");
        }

        var sequential = new WindowOptimizer(level).Optimize(original, new WindowOptions { Window = 18, Threads = 4 });
        var spread = new WindowOptimizer(level).Optimize(original, new WindowOptions { Window = 18, Threads = 4, Order = WindowOrder.Spread });
        Assert.AreEqual(sequential.Moves, spread.Moves);
        Assert.AreEqual(sequential.NodesExplored, spread.NodesExplored);
        Assert.AreEqual(sequential.WindowsSearched, spread.WindowsSearched);
    }

    /// <summary>
    /// The memory limit charges a window for the states it stored itself. On
    /// one thread, a window that needed more than the limit leaves a big table
    /// behind; the small windows after it must still run to the end. Charging
    /// the worker's held capacity stopped them all at their first check.
    /// </summary>
    [TestMethod]
    public void TheMemoryLimit_ChargesAWindowOnlyForItsOwnStates()
    {
        // hole at window 20: three windows store over 16,384 states, which grows
        // a worker's table to two chunks (more than this limit), and dozens
        // store 4,096-8,192, which fit in it many times over.
        Level level = TestCorpus.Instance.LoadLevel("hole");
        string original = TestCorpus.Solution("hole");
        const long Limit = 512 * 1024;

        List<WindowReport> Run(long? limit)
        {
            var reports = new List<WindowReport>();
            new WindowOptimizer(level).Optimize(original, new WindowOptions
            {
                Window = 20, NodeLimit = long.MaxValue, Threads = 1, WindowMemoryLimit = limit, WindowDone = reports.Add,
            });
            return reports;
        }

        Dictionary<int, WindowReport> free = Run(null).Where(r => r.Pass == 1).ToDictionary(r => r.Start);
        List<WindowReport> limited = Run(Limit).Where(r => r.Pass == 1).ToList();

        Assert.IsTrue(limited.Any(r => r.Outcome == WindowOutcome.MemoryLimit), "the biggest windows should need more than 512 KB");
        int small = 0;
        foreach (WindowReport r in limited)
        {
            WindowReport unlimited = free[r.Start];
            if (unlimited.Stored * 64L < Limit)
            {
                small++;
                Assert.AreEqual(unlimited.Outcome, r.Outcome, $"[{r.Start}..{r.End}] needs {unlimited.Stored} states but was stopped");
            }
        }

        Assert.IsGreaterThan(400, small);
        Assert.IsTrue(limited.SkipWhile(r => r.Outcome != WindowOutcome.MemoryLimit).Skip(1).Any(r => r.Nodes >= 4096 && r.Outcome == WindowOutcome.NoShortcut),
            "a window big enough to be checked must still finish after a big one");
    }

    /// <summary>
    /// The per-window time limit is charged in the thread's processor time: a
    /// thread that waits (as every thread does while the machine sleeps) uses
    /// none of it, and one that works uses it.
    /// </summary>
    [TestMethod]
    public void TheWindowClock_CountsWork_NotWaiting()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("thread time is read on Windows only");
        }

        long before = ThreadClock.Now();
        Thread.Sleep(400);
        long slept = ThreadClock.Now() - before;
        Assert.IsLessThan(TimeSpan.FromMilliseconds(100).Ticks, slept, $"a sleeping thread used {slept / 10_000} ms");

        // Busy until it has used 100 ms; on a loaded machine that takes longer
        // by the clock, which is the point.
        before = ThreadClock.Now();
        var wall = System.Diagnostics.Stopwatch.StartNew();
        double x = 0;
        while (ThreadClock.Now() - before < TimeSpan.FromMilliseconds(100).Ticks && wall.Elapsed < TimeSpan.FromSeconds(20))
        {
            x += Math.Sqrt(x + 1);
        }

        Assert.IsLessThan(TimeSpan.FromSeconds(20), wall.Elapsed, $"a busy thread's time did not advance ({x})");
    }

    /// <summary>
    /// What a resumed run relies on: a window reported with an id is skipped
    /// when that id is known barren, and the solution found is reported the
    /// moment its window finds it, before the round ends.
    /// </summary>
    [TestMethod]
    public void KnownWindows_AreSkipped_AndAShortcutIsSavedAtOnce()
    {
        Level level = TestCorpus.Instance.LoadLevel("cannons");
        string original = TestCorpus.Solution("cannons");
        string turn = $"{level.Units[0].Left}{level.Units[0].Right}{level.Units[0].Left}{level.Units[0].Right}";
        string padded = original[..10] + turn + original[10..];

        var first = new List<WindowReport>();
        var saved = new List<string>();
        var progress = new List<string>();
        new WindowOptimizer(level).Optimize(padded, new WindowOptions
        {
            Window = 8, WindowDone = first.Add, Improved = saved.Add, Progress = progress.Add,
        });
        Assert.Contains(p => p.Contains("found so far this pass", StringComparison.Ordinal), progress, "the shortcut should be saved before the round ends");
        int found = progress.FindIndex(p => p.Contains("found so far", StringComparison.Ordinal));
        int round = progress.FindIndex(p => p.StartsWith("  pass 1:", StringComparison.Ordinal));
        Assert.IsLessThan(round, found);
        Assert.IsTrue(saved.All(m => SolutionValidator.Validate(new Room(level), m).Solved));

        // A second run that knows every barren window of the first searches only the rest.
        var barren = first.Where(r => r.Outcome == WindowOutcome.NoShortcut).Select(r => r.Id).ToHashSet();
        var second = new List<WindowReport>();
        OptimizeResult again = new WindowOptimizer(level).Optimize(padded, new WindowOptions
        {
            Window = 8, WindowDone = second.Add, IsKnownBarren = barren.Contains,
        });
        Assert.IsFalse(second.Any(r => barren.Contains(r.Id)), "a known window was searched again");
        Assert.AreEqual(original.Length, again.Moves.Length);
        Assert.IsLessThan(first.Count, second.Count);
    }

    /// <summary>
    /// Windows of W + s on a stride of s contain every window of W, so they
    /// find at least what a stride of 1 at W finds, with a quarter of the
    /// windows for s = 4. This is what <c>improve --grow</c> relies on.
    /// </summary>
    [TestMethod]
    [DataRow("hole", 18)]
    [DataRow("linux", 18)]
    public void AStrideOfS_AtWPlusS_FindsWhatEveryWindowOfW_Finds(string name, int window)
    {
        Level level = TestCorpus.Instance.LoadLevel(name);
        string original = TestCorpus.Solution(name);
        OptimizeResult dense = new WindowOptimizer(level).Optimize(original, new WindowOptions { Window = window, Threads = 8 });
        OptimizeResult sparse = new WindowOptimizer(level).Optimize(original, new WindowOptions { Window = window + 4, Stride = 4, Threads = 8 });

        Assert.IsLessThan(original.Length, dense.Moves.Length, $"{name}: the dense run should find something");
        Assert.IsLessThanOrEqualTo(dense.Moves.Length, sparse.Moves.Length, $"{name}: stride 4 at {window + 4} found less than stride 1 at {window}");
        Assert.IsTrue(SolutionValidator.Validate(new Room(level), sparse.Moves).Solved);
    }

    /// <summary>
    /// With a stride, the windows step by it, and the last full window is
    /// searched even off the stride, so the end of the solution is covered.
    /// </summary>
    [TestMethod]
    public void AStride_StillCoversTheEnd()
    {
        Level level = TestCorpus.Instance.LoadLevel("cannons");
        string original = TestCorpus.Solution("cannons");
        var reports = new List<WindowReport>();
        new WindowOptimizer(level).Optimize(original, new WindowOptions { Window = 10, Stride = 7, WindowDone = reports.Add });

        int last = original.Length - 10;
        int[] expected = Enumerable.Range(0, (last / 7) + 1).Select(k => k * 7)
            .Concat(last % 7 != 0 ? [last] : [])
            .ToArray();
        CollectionAssert.AreEqual(expected, reports.Where(r => r.Pass == 1).Select(r => r.Start).OrderBy(x => x).ToArray());
        Assert.IsTrue(reports.All(r => r.Length == 10));
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
