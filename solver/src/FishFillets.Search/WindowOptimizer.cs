using System.Diagnostics;

using FishFillets.Physics;

namespace FishFillets.Search;

public sealed record OptimizeResult(string Moves, int OriginalLength, int Passes, long NodesExplored, int WindowsImproved)
{
    public int Saved => OriginalLength - Moves.Length;

    /// <summary>Windows actually searched; the ones known from an earlier round to hold no shortcut are skipped.</summary>
    public int WindowsSearched { get; init; }

    /// <summary>Stopped by <see cref="WindowOptions.TimeLimit"/> before reaching a fixpoint.</summary>
    public bool TimedOut { get; init; }
}

/// <summary>
/// One rung of a ladder (<see cref="WindowOptimizer.Ladder"/>): the window of
/// <see cref="Window"/> moves from the ladder's start. <see cref="Expanded"/>
/// counts the expansions (the physics). <see cref="Skipped"/> counts the nodes
/// popped but not expanded again, because they were already complete.
/// <see cref="Rescored"/> counts the stored states re-scored for this rung's
/// target. A fresh search has neither of the last two.
/// </summary>
public sealed record LadderRung(
    int Window, WindowOutcome Outcome, int Bound, string? Shortcut, long Expanded, long Skipped, long Rescored, int Stored, double Seconds);

/// <summary>One start of the ladder experiment: the fresh windows and the incremental ladder.</summary>
public sealed record LadderStart(int Start, IReadOnlyList<LadderRung> Fresh, IReadOnlyList<LadderRung> Incremental);

/// <summary>How one window's search ended.</summary>
public enum WindowOutcome
{
    /// <summary>A shorter path was found.</summary>
    Shortcut,

    /// <summary>Proven: no path shorter than the window exists between its two rooms.</summary>
    NoShortcut,

    /// <summary>Gave up at <see cref="WindowOptions.NodeLimit"/>; anything shorter than the reported bound is ruled out.</summary>
    NodeLimit,

    /// <summary>Gave up at <see cref="WindowOptions.WindowMemoryLimit"/>; as for the node limit.</summary>
    MemoryLimit,

    /// <summary>Gave up at <see cref="WindowOptions.WindowTimeLimit"/>; as for the node limit.</summary>
    WindowTimeLimit,

    /// <summary>The whole run stopped (<see cref="WindowOptions.TimeLimit"/>); not counted as searched.</summary>
    Stopped,
}

/// <summary>
/// The lower bound a window's search prunes with: the moves still needed to turn
/// the current room into the window's target room. All are admissible, so they
/// change how much is searched, never what is found.
/// </summary>
public enum WindowHeuristic
{
    /// <summary>None: plain breadth-first search. For tests.</summary>
    None,

    /// <summary>Each fish's straight-line (Manhattan) distance to its target cell, plus a turn; the sum over the fish.</summary>
    Manhattan,

    /// <summary>As <see cref="Manhattan"/>, but measured around the walls (<see cref="RelaxedDistance"/>), facing included.</summary>
    Walls,

    /// <summary>
    /// <see cref="Walls"/>, or the item that is furthest from its target if that
    /// is more. An item moves sideways or up only when a fish move pushes it, one
    /// cell per move, and down for free. So an item that has to go dx across and
    /// dy up needs at least |dx| + dy moves. One push can move several items, so
    /// the items are combined by their maximum, and the fish and items by the
    /// larger of the two.
    /// </summary>
    WallsAndItems,
}

/// <summary>The order a round's windows are searched in. It changes what a stopped run has covered, not the result of a finished one.</summary>
public enum WindowOrder
{
    /// <summary>From the start of the solution to its end.</summary>
    Sequential,

    /// <summary>
    /// Spread out: the first windows, one per thread, start at 1/(n+1), 2/(n+1)...
    /// of the solution. Every later one starts in the middle of the widest stretch
    /// of starts not yet taken. A run stopped at any point has sampled the whole
    /// solution evenly, at a density that rises as it goes.
    /// </summary>
    Spread,
}

/// <summary>
/// One window's search, as reported to <see cref="WindowOptions.WindowDone"/>.
/// <see cref="Bound"/> is how far the search got: no path between the two rooms
/// is shorter than it. It equals <see cref="Length"/> when a shortcut is ruled
/// out, and is the shortcut's length when one is found.
/// </summary>
public sealed record WindowReport(
    int Pass, int Start, int End, WindowOutcome Outcome, int Bound, long Nodes, int Stored, double Seconds, string Id)
{
    public int Length => End - Start;

    /// <summary>Stopped by one of the per-window limits: searched as far as they allowed, not proven.</summary>
    public bool HitLimit => Outcome is WindowOutcome.NodeLimit or WindowOutcome.MemoryLimit or WindowOutcome.WindowTimeLimit;
}

/// <summary>How <see cref="WindowOptimizer.Optimize(string, WindowOptions)"/> searches.</summary>
public sealed record WindowOptions
{
    /// <summary>Window length in moves. Bigger reaches further, and costs exponentially more.</summary>
    public int Window { get; init; } = 16;

    /// <summary>Gap between window starts. 1 is exhaustive.</summary>
    public int Stride { get; init; } = 1;

    /// <summary>Per-window search budget in expansions; exceeding it abandons that window, not the run.</summary>
    public long NodeLimit { get; init; } = 2_000_000;

    /// <summary>Windows searched side by side. The result does not depend on it.</summary>
    public int Threads { get; init; } = 1;

    /// <summary>The order each round's windows are searched in.</summary>
    public WindowOrder Order { get; init; } = WindowOrder.Sequential;

    /// <summary>The lower bound each window's search prunes with.</summary>
    public WindowHeuristic Heuristic { get; init; } = WindowHeuristic.WallsAndItems;

    /// <summary>
    /// Per-window time budget, in processor time the window's thread used
    /// (<see cref="ThreadClock"/>), so a sleeping or throttled machine does not
    /// use it up; a window past it is abandoned, as at the node limit.
    /// </summary>
    public TimeSpan? WindowTimeLimit { get; init; }

    /// <summary>
    /// Per-window memory budget in bytes: the worker's state table and queue.
    /// A window that would grow them past it is abandoned, as at the node limit.
    /// </summary>
    public long? WindowMemoryLimit { get; init; }

    /// <summary>
    /// Windows known to hold nothing within the current limits, from an earlier
    /// run (by <see cref="WindowReport.Id"/>): skipped, like the windows a round
    /// already found barren. This is what lets a stopped run be resumed.
    /// </summary>
    public Func<string, bool>? IsKnownBarren { get; init; }

    /// <summary>Stop after this long, keeping whatever was found. Checked between windows and inside each.</summary>
    public TimeSpan? TimeLimit { get; init; }

    /// <summary>
    /// Whether a window may end with identical items swapped (docs/022): the
    /// target is matched on the canonical key, which sorts each class of
    /// interchangeable items. Such a room is the same room to the physics, so
    /// the rest of the solution replays unchanged - and every splice is
    /// replayed and checked regardless.
    /// </summary>
    public bool MergeIdenticalItems { get; init; } = true;

    /// <summary>Skip moves that undo a fast move, as the main solver does (docs/028, window rules in <see cref="WindowOptimizer"/>).</summary>
    public bool SkipReversals { get; init; } = true;

    /// <summary>Called with a line for each round and each shortcut spliced in.</summary>
    public Action<string>? Progress { get; init; }

    /// <summary>
    /// Called with the whole solution each time it gets shorter - to save it as
    /// it goes. That is after every round that spliced something in, and also
    /// in the middle of a round, as soon as a window finds a shortcut: the
    /// shortcuts found so far are spliced into a copy, which is replayed first.
    /// Each solution reported is valid and shorter than the one before.
    /// </summary>
    public Action<string>? Improved { get; init; }

    /// <summary>
    /// Called after each window's search, from the worker that ran it. With
    /// more than one thread the calls are concurrent and in no fixed order.
    /// </summary>
    public Action<WindowReport>? WindowDone { get; init; }
}

/// <summary>
/// Shortens an existing solution by re-solving windows of it. For each window
/// <c>[i, j]</c> along the move string, it searches for a shorter path from the
/// state at step <c>i</c> to the state at step <c>j</c>; anything shorter is
/// spliced in, and the rest of the solution still replays because the two states
/// are identical by the state key.
///
/// <para>Why windows rather than solving outright: the bundled solutions are already
/// near-optimal (a state-revisit sweep over all 32,031 moves finds not one
/// wasted loop), so the remaining slack is local, and a window is a far easier
/// search than a level. A window has a fixed start AND a fixed goal state, which
/// admits a much sharper heuristic than "get both fish out" - see
/// <see cref="WindowSearch.Heuristic"/> - and it is bounded: a path is only
/// interesting if it is shorter than the segment it replaces, so the cost limit
/// is <c>j - i - 1</c>.</para>
///
/// <para>The flip side, and the reason this can't be the whole solver: splicing
/// preserves both endpoints, so it finds local shortcuts and never a globally
/// different route. See solver/docs/003.</para>
///
/// <para><b>Rounds.</b> Each round searches every window of the current solution
/// - in parallel, each worker with its own room and state table - and then
/// splices in the shortcuts found, left to right, skipping any that overlaps
/// one already taken. Non-overlapping splices are independent: each keeps both
/// of its endpoint states, so the states around the others are unchanged. The
/// rounds repeat until one finds nothing. A window's outcome depends only on
/// its start state, its target and its length, so one that found nothing is
/// remembered by those and not searched again in a later round; the windows
/// re-searched are the ones a splice actually changed. The result is the same
/// on any number of threads.</para>
///
/// <para><b>The search inside a window</b> is the main solver's machinery
/// (solver/docs/027): the level reduction's mobile models, the bit-packed key in
/// a <see cref="StateStore"/>, a <see cref="BucketQueue"/>, and the undo restore
/// between the moves of one expansion. Skipping reversals follows docs/028 with
/// one change, because a window's target fixes the fish's facing: after a fast
/// turn, or a fast vertical step, the opposite symbol returns to the
/// predecessor and is not tried. After a fast horizontal step the opposite
/// symbol turns the fish round - a new state, and in the main solver never
/// needed. Here it is, when the turn is the last move of the window: the
/// argument postpones that turn to just before the fish's next horizontal move,
/// or to the end of the path, and only at the end can it come straight after the
/// step. So that turn is simulated, but kept only if it reaches the target.</para>
/// </summary>
public sealed class WindowOptimizer
{
    private readonly Level _level;
    private readonly LevelReduction? _reduction;

    public WindowOptimizer(Level level, LevelReduction? reduction = null)
    {
        _level = level;
        _reduction = reduction;
    }

    /// <summary>
    /// Repeatedly sweeps windows over <paramref name="moves"/> until a whole pass
    /// finds no improvement.
    /// </summary>
    /// <param name="window">Window length in moves. Bigger reaches further, and costs exponentially more.</param>
    /// <param name="stride">Gap between window starts. 1 is exhaustive.</param>
    /// <param name="nodeLimit">Per-window search budget; exceeding it abandons that window, not the run.</param>
    public OptimizeResult Optimize(
        string moves,
        int window,
        int stride = 1,
        long nodeLimit = 2_000_000,
        Action<string>? progress = null) =>
        Optimize(moves, new WindowOptions { Window = window, Stride = stride, NodeLimit = nodeLimit, Progress = progress });

    public OptimizeResult Optimize(string moves, WindowOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Window, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Stride, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Threads, 1);

        var stopwatch = Stopwatch.StartNew();
        LevelReduction reduction = _reduction ?? LevelReduction.Verified(_level, moves);
        var shared = new Shared(_level, reduction, options);
        var replay = new Replayer(shared);
        var contexts = new Stack<WindowSearch>();

        int original = moves.Length;
        long totalNodes = 0;
        int improvedWindows = 0;
        int searchedWindows = 0;
        bool timedOut = false;
        var barren = new HashSet<string>(StringComparer.Ordinal);
        int reportedLength = moves.Length;
        string? reported = null;
        Func<bool>? outOfTime = options.TimeLimit is { } timeLimit ? () => stopwatch.Elapsed > timeLimit : null;

        Trajectory path = replay.Run(moves)
            ?? throw new InvalidOperationException($"{_level.Name}: the input is not a valid move string for this level");

        int passes;
        for (passes = 1; ; passes++)
        {
            int pass = passes;
            // This round's windows, minus the ones already known to hold nothing.
            // Only full-length ones: the last ends at the end of the solution.
            // A shorter window at the tail is inside it, and a window's search
            // finds the shortest path between its two rooms, so it would find
            // any shortcut the shorter one could. With a stride, the last full
            // window is added even off the stride, so the tail is still covered.
            var windows = new List<(int I, int J, string Id)>();
            void AddWindow(int i, int j)
            {
                string id = path.Keys[i] + "|" + path.Keys[j] + "|" + (j - i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!barren.Contains(id) && options.IsKnownBarren?.Invoke(id) != true)
                {
                    windows.Add((i, j, id));
                }
            }

            if (moves.Length >= 2)
            {
                int last = Math.Max(0, moves.Length - options.Window);
                for (int i = 0; i <= last; i += options.Stride)
                {
                    AddWindow(i, Math.Min(i + options.Window, moves.Length));
                }

                if (last % options.Stride != 0)
                {
                    AddWindow(last, moves.Length);
                }
            }

            // Windows are listed left to right; this is the order to search them in.
            int[] order = options.Order == WindowOrder.Spread
                ? SpreadOrder(windows.Count, options.Threads)
                : Enumerable.Range(0, windows.Count).ToArray();

            var found = new string?[windows.Count];
            var searched = new bool[windows.Count];
            var nodes = new long[windows.Count];
            int next = -1;
            string baseMoves = moves;
            Trajectory basePath = path;
            var partial = new object();
            Replayer? partialReplay = null;

            // The shortcuts found so far this round, spliced into a copy:
            // reported at once if it replays and is shorter than the last
            // report, so a run stopped mid-round keeps what it found.
            void ReportFoundSoFar()
            {
                if (options.Improved is null)
                {
                    return;
                }

                lock (partial)
                {
                    string candidate = baseMoves;
                    int reach = int.MaxValue;
                    for (int k = windows.Count - 1; k >= 0; k--)
                    {
                        if (Volatile.Read(ref found[k]) is { } shorter && windows[k].J <= reach)
                        {
                            candidate = string.Concat(candidate.AsSpan(0, windows[k].I), shorter, candidate.AsSpan(windows[k].J));
                            reach = windows[k].I;
                        }
                    }

                    if (candidate.Length >= reportedLength)
                    {
                        return;
                    }

                    partialReplay ??= new Replayer(shared);
                    if (partialReplay.Run(candidate) is { } replayed && replayed.EndsLike(basePath))
                    {
                        reportedLength = candidate.Length;
                        reported = candidate;
                        options.Progress?.Invoke($"  found so far this pass: {candidate.Length} moves (saved)");
                        options.Improved(candidate);
                    }
                }
            }

            void Work()
            {
                WindowSearch search;
                lock (contexts)
                {
                    search = contexts.Count > 0 ? contexts.Pop() : new WindowSearch(shared);
                }

                try
                {
                    for (int n = Interlocked.Increment(ref next); n < order.Length; n = Interlocked.Increment(ref next))
                    {
                        if (outOfTime is not null && outOfTime())
                        {
                            return;
                        }

                        int k = order[n];
                        (int i, int j, string id) = windows[k];
                        long began = Stopwatch.GetTimestamp();
                        string? shortcut = search.Search(path.States[i], path.States[j], j - i - 1, options.NodeLimit, outOfTime, out nodes[k], out bool stopped);
                        Volatile.Write(ref found[k], shortcut);
                        searched[k] = !stopped;
                        options.WindowDone?.Invoke(new WindowReport(
                            pass, i, j, search.LastOutcome, search.LastBound, nodes[k], search.LastStored,
                            Stopwatch.GetElapsedTime(began).TotalSeconds, id));
                        if (stopped)
                        {
                            return;
                        }

                        if (shortcut is not null)
                        {
                            ReportFoundSoFar();
                        }
                    }
                }
                finally
                {
                    lock (contexts)
                    {
                        contexts.Push(search);
                    }
                }
            }

            int workers = Math.Min(options.Threads, Math.Max(1, windows.Count));
            if (workers == 1)
            {
                Work();
            }
            else
            {
                var threads = new Thread[workers];
                for (int t = 0; t < workers; t++)
                {
                    threads[t] = new Thread(Work) { IsBackground = true, Name = $"window worker {t}" };
                    threads[t].Start();
                }

                foreach (Thread thread in threads)
                {
                    thread.Join();
                }
            }

            int searchedNow = 0;
            for (int k = 0; k < windows.Count; k++)
            {
                if (searched[k])
                {
                    searchedNow++;
                    totalNodes += nodes[k];
                    if (found[k] is null)
                    {
                        barren.Add(windows[k].Id);
                    }
                }
                else
                {
                    timedOut = true;
                }
            }

            searchedWindows += searchedNow;

            // Left to right, non-overlapping; spliced right to left so the
            // earlier indices stay put. Every splice is replayed before it is
            // kept: with identical items merged, the window ends in a room
            // that matches the target only up to which item is which.
            var chosen = new List<int>();
            int reach = 0;
            for (int k = 0; k < windows.Count; k++)
            {
                if (found[k] is not null && windows[k].I >= reach)
                {
                    chosen.Add(k);
                    reach = windows[k].J;
                }
            }

            int splicedNow = 0;
            for (int c = chosen.Count - 1; c >= 0; c--)
            {
                (int i, int j, string id) = windows[chosen[c]];
                string shorter = found[chosen[c]]!;
                string candidate = string.Concat(moves.AsSpan(0, i), shorter, moves.AsSpan(j));
                if (replay.Run(candidate) is not { } replayed || !replayed.EndsLike(path))
                {
                    barren.Add(id);
                    continue;
                }

                options.Progress?.Invoke($"  [{i}..{j}] {j - i} -> {shorter.Length} moves ({candidate.Length} total)");
                moves = candidate;
                path = replayed;
                splicedNow++;
            }

            improvedWindows += splicedNow;
            options.Progress?.Invoke(
                $"  pass {passes}: {searchedNow:N0} of {windows.Count:N0} new windows searched, {splicedNow} spliced, " +
                $"{moves.Length} moves, {stopwatch.Elapsed.TotalSeconds:F1} s");

            if (splicedNow > 0 && (moves.Length < reportedLength || (moves.Length == reportedLength && moves != reported)))
            {
                reportedLength = moves.Length;
                reported = moves;
                options.Improved?.Invoke(moves);
            }

            if (timedOut || splicedNow == 0)
            {
                break;
            }
        }

        return new OptimizeResult(moves, original, passes, totalNodes, improvedWindows)
        {
            WindowsSearched = searchedWindows,
            TimedOut = timedOut,
        };
    }

    /// <summary>
    /// The <see cref="WindowOrder.Spread"/> order of <paramref name="count"/>
    /// windows: <paramref name="seeds"/> evenly spaced ones first, then always the
    /// middle of the widest run of windows not yet taken (the leftmost one on a tie).
    /// </summary>
    internal static int[] SpreadOrder(int count, int seeds)
    {
        var result = new List<int>(count);
        var taken = new bool[count];
        var points = new List<int> { -1 };
        seeds = Math.Clamp(seeds, 0, count);
        for (int k = 1; k <= seeds; k++)
        {
            int p = (int)((long)k * count / (seeds + 1));
            if (!taken[p])
            {
                taken[p] = true;
                result.Add(p);
                points.Add(p);
            }
        }

        points.Add(count);
        points.Sort();

        // Gaps between taken windows, widest first, then leftmost.
        var gaps = new PriorityQueue<(int Lo, int Hi), (int, int)>();
        for (int g = 0; g + 1 < points.Count; g++)
        {
            gaps.Enqueue((points[g], points[g + 1]), (-(points[g + 1] - points[g]), points[g]));
        }

        while (gaps.TryDequeue(out (int Lo, int Hi) gap, out _))
        {
            if (gap.Hi - gap.Lo < 2)
            {
                continue;
            }

            int mid = gap.Lo + ((gap.Hi - gap.Lo) / 2);
            result.Add(mid);
            gaps.Enqueue((gap.Lo, mid), (-(mid - gap.Lo), gap.Lo));
            gaps.Enqueue((mid, gap.Hi), (-(gap.Hi - mid), mid));
        }

        return result.ToArray();
    }

    /// <summary>
    /// One window's search on its own: the shortest path of at most
    /// <paramref name="limit"/> moves from <paramref name="from"/> to
    /// <paramref name="to"/>, or null - for tests that pin the search itself.
    /// </summary>
    /// <summary>The window bound's estimate of the moves between two rooms, for tests that check it never overestimates.</summary>
    internal Func<RoomSnapshot, RoomSnapshot, int> Estimator(WindowOptions? options = null)
    {
        var shared = new Shared(_level, _reduction ?? LevelReduction.Compute(_level), options ?? new WindowOptions());
        var search = new WindowSearch(shared);
        return search.Estimate;
    }

    internal string? Shortcut(RoomSnapshot from, RoomSnapshot to, int limit, WindowOptions? options = null, long nodeLimit = 2_000_000)
    {
        var shared = new Shared(_level, _reduction ?? LevelReduction.Compute(_level), options ?? new WindowOptions());
        return new WindowSearch(shared).Search(from, to, limit, nodeLimit, out _);
    }

    /// <summary>
    /// The ladder experiment (solver/docs/030). For each start <c>i</c>, the windows
    /// <c>[i, i + W]</c> for W = <paramref name="window"/>, + <paramref name="grow"/>...
    /// up to <paramref name="maxWindow"/>, searched twice:
    /// <list type="bullet">
    /// <item>fresh, one A* per window, as <c>improve --grow</c> does;</item>
    /// <item>incrementally, on one table (<see cref="WindowSearch.Ladder"/>).</item>
    /// </list>
    /// Each stops at its first shortcut or limit. A fresh window's per-window
    /// limits apply to it alone. The ladder's apply to the whole ladder: the
    /// time budget of one start. With <paramref name="storePruned"/>, the ladder also
    /// stores the children a rung leaves out (over its limit, or kept only as a
    /// target), so no node is expanded twice, at the cost of a bigger table.
    /// </summary>
    public IReadOnlyList<LadderStart> Ladder(
        string moves, IReadOnlyList<int> starts, int window, int grow, int maxWindow, WindowOptions options,
        Action<LadderStart>? done = null, bool storePruned = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(grow, 1);
        LevelReduction reduction = _reduction ?? LevelReduction.Verified(_level, moves);
        var shared = new Shared(_level, reduction, options);
        Trajectory path = new Replayer(shared).Run(moves)
            ?? throw new InvalidOperationException($"{_level.Name}: the input is not a valid move string for this level");

        var results = new LadderStart?[starts.Count];
        int next = -1;
        void Work()
        {
            var search = new WindowSearch(shared);
            for (int n = Interlocked.Increment(ref next); n < starts.Count; n = Interlocked.Increment(ref next))
            {
                int i = starts[n];
                var sizes = new List<int>();
                for (int w = window; w <= maxWindow && i + w <= moves.Length; w += grow)
                {
                    sizes.Add(w);
                }

                var fresh = new List<LadderRung>();
                foreach (int w in sizes)
                {
                    long began = ThreadClock.Now();
                    string? shortcut = search.Search(path.States[i], path.States[i + w], w - 1, options.NodeLimit, null, out long nodes, out _);
                    fresh.Add(new LadderRung(w, search.LastOutcome, search.LastBound, shortcut, nodes, 0, 0, search.LastStored,
                        (ThreadClock.Now() - began) / 1e7));
                    if (search.LastOutcome != WindowOutcome.NoShortcut)
                    {
                        break;
                    }
                }

                List<LadderRung> incremental = search.Ladder(
                    path.States[i], [.. sizes.Select(w => path.States[i + w])], [.. sizes.Select(w => w - 1)], options.NodeLimit, storePruned);
                var result = new LadderStart(i, fresh, incremental);
                results[n] = result;
                done?.Invoke(result);
            }
        }

        int workers = Math.Clamp(options.Threads, 1, Math.Max(1, starts.Count));
        var threads = new Thread[workers];
        for (int t = 0; t < workers; t++)
        {
            threads[t] = new Thread(Work) { IsBackground = true, Name = $"ladder worker {t}" };
            threads[t].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        return [.. results.Select(r => r!)];
    }

    // ---------------------------------------------------------------- shared --

    /// <summary>What every worker reads and none writes.</summary>
    private sealed class Shared
    {
        public Shared(Level level, LevelReduction reduction, WindowOptions options)
        {
            Level = level;
            Reduction = reduction;
            Mobile = reduction.MobileModels;
            Codec = new StateKeyCodec(level, Mobile) { Canonical = options.MergeIdenticalItems };
            SkipReversals = options.SkipReversals;
            WindowTicks = options.WindowTimeLimit is { } t ? t.Ticks : long.MaxValue;
            WindowMemoryLimit = options.WindowMemoryLimit ?? long.MaxValue;

            var room = new Room(level);
            Symbols = new char[room.MoveSymbolCount];
            room.GetMoveSymbols(Symbols, out int count);
            Symbols = Symbols[..count];

            // Reversal and the move byte rely on this layout, as in the solver.
            for (int u = 0; u < level.Units.Length; u++)
            {
                UnitDef unit = level.Units[u];
                if (count > MoveSymbol + 1 || Symbols[(4 * u) + 0] != unit.Up || Symbols[(4 * u) + 1] != unit.Down
                    || Symbols[(4 * u) + 2] != unit.Left || Symbols[(4 * u) + 3] != unit.Right)
                {
                    throw new InvalidOperationException($"{level.Name}: unexpected move symbol layout");
                }
            }

            FishModels = [.. level.Units.Select(u => u.Model)];
            Heuristic = options.Heuristic;

            // A fish can also leave through an output plug (windoze's spuntik),
            // anywhere in the room, not only at the border: then no distance to
            // the border bounds its way out.
            FishLeaveAnywhere = level.Models.Any(m => m.OutDir != Dir.No);

            // The proven-immobile items are walls too (as for the main solver's
            // bound); the Manhattan setting keeps the bare walls it always had.
            Walls = Heuristic == WindowHeuristic.Manhattan ? RelaxedDistance.BuildWallGrid(level) : reduction.Walls;
            ExitDistance = [.. FishModels
                .Select(m => level.Models[m].Goal is GoalKind.Escape or GoalKind.Out
                    ? RelaxedDistance.ToExit(level, m, Walls, freeFall: false)
                    : null)];

            // The items, and for each the items whose target it may take: with
            // identical items merged, an item may end where its twin was.
            var fish = new HashSet<int>(FishModels);
            ItemModels = [.. Mobile.Where(m => !fish.Contains(m))];
            var slotOf = new Dictionary<int, int>();
            for (int i = 0; i < ItemModels.Length; i++)
            {
                slotOf[ItemModels[i]] = i;
            }

            ItemShapes = [.. ItemModels.Select(m => level.Models[m].Shape)];
            ItemTwins = [.. Enumerable.Range(0, ItemModels.Length).Select(i => new[] { i })];
            if (options.MergeIdenticalItems)
            {
                foreach (int[] members in ItemSymmetry.Classes(level, Mobile))
                {
                    int[] slots = [.. members.Select(n => slotOf.TryGetValue(Mobile[n], out int slot) ? slot : -1).Where(x => x >= 0)];
                    foreach (int slot in slots)
                    {
                        ItemTwins[slot] = slots;
                    }
                }
            }
        }

        public Level Level { get; }

        public LevelReduction Reduction { get; }

        public int[] Mobile { get; }

        /// <summary>Stateless between calls, so the workers share it (see its remarks).</summary>
        public StateKeyCodec Codec { get; }

        public bool SkipReversals { get; }

        public WindowHeuristic Heuristic { get; }

        /// <summary>The level has an output plug, so a fish's way out has no bound.</summary>
        public bool FishLeaveAnywhere { get; }

        /// <summary>Cells that never change: the bound's relaxed room.</summary>
        public bool[] Walls { get; }

        /// <summary>The mobile models that are not fish.</summary>
        public int[] ItemModels { get; }

        public Shape[] ItemShapes { get; }

        /// <summary>For each item (an index into <see cref="ItemModels"/>), the items whose target it may end on.</summary>
        public int[][] ItemTwins { get; }

        /// <summary>Per-window budget of thread processor time (<see cref="ThreadClock"/>, 100 ns ticks); long.MaxValue for none.</summary>
        public long WindowTicks { get; }

        /// <summary>Per-window memory budget in bytes; long.MaxValue for none.</summary>
        public long WindowMemoryLimit { get; }

        public char[] Symbols { get; }

        public int[] FishModels { get; }

        /// <summary>
        /// Per fish, the walls-only distance to leaving the room (null for a fish
        /// that never leaves) - the bound used when the target has that fish out.
        /// </summary>
        public RelaxedDistance?[] ExitDistance { get; }
    }

    // The move byte, as in the solver: the symbol index, and how it was made.
    private const byte MoveSymbol = 0x3F;
    private const byte MadeByTurn = 0x40;       // a fast turn on the spot
    private const byte MadeByFishStep = 0x80;   // a fast step: only the fish moved

    /// <summary>The states along a move string, and their canonical keys.</summary>
    private sealed class Trajectory(List<RoomSnapshot> states, List<string> keys, bool solved)
    {
        public List<RoomSnapshot> States { get; } = states;

        /// <summary>Canonical compact key of each state, as text - window identities are built from them.</summary>
        public List<string> Keys { get; } = keys;

        public bool Solved { get; } = solved;

        /// <summary>Whether a spliced string ends where this one did: same final state, solved if this was.</summary>
        public bool EndsLike(Trajectory other) => Keys[^1] == other.Keys[^1] && (Solved || !other.Solved);
    }

    private sealed class Replayer(Shared shared)
    {
        private readonly Room _room = new(shared.Level);
        private readonly byte[] _wide = new byte[shared.Reduction.StateKeySize];
        private readonly byte[] _compact = new byte[shared.Codec.CompactSize];

        /// <summary>Snapshots after every prefix of the move string, or null if a move is not possible.</summary>
        public Trajectory? Run(string moves)
        {
            var states = new List<RoomSnapshot>(moves.Length + 1);
            var keys = new List<string>(moves.Length + 1);
            _room.Reset();
            Add();

            foreach (char symbol in moves)
            {
                if (!_room.ApplyMove(symbol))
                {
                    return null;
                }

                Add();
            }

            return new Trajectory(states, keys, _room.IsSolved());

            void Add()
            {
                states.Add(_room.Capture());
                _room.WriteStateKey(_wide, shared.Mobile);
                shared.Codec.Encode(_wide, _compact);
                keys.Add(Convert.ToHexString(_compact));
            }
        }
    }

    // ---------------------------------------------------------------- search --

    /// <summary>One worker's search state: its own room, table and queue, reused from window to window.</summary>
    private sealed class WindowSearch
    {
        private readonly Shared _s;
        private readonly Room _room;
        private readonly StateStore _store;
        private readonly BucketQueue _queue = new();
        private readonly byte[] _wide;
        private readonly byte[] _compact;
        private readonly byte[] _goal;
        private readonly ModelState[] _undo;
        private readonly (int X, int Y, bool IsLeft, bool IsOut)[] _goalFish;
        private readonly RelaxedDistance?[] _fishToGoal;
        private readonly (int X, int Y, bool IsOut)[] _goalItems;
        private readonly Dictionary<(int Fish, int X, int Y, bool IsLeft), RelaxedDistance> _distanceCache = [];

        public WindowSearch(Shared shared)
        {
            _s = shared;
            _room = new Room(shared.Level);
            _store = new StateStore(shared.Codec.CompactSize);
            _wide = new byte[shared.Reduction.StateKeySize];
            _compact = new byte[shared.Codec.CompactSize];
            _goal = new byte[shared.Codec.CompactSize];
            _undo = new ModelState[shared.Mobile.Length];
            _goalFish = new (int, int, bool, bool)[shared.FishModels.Length];
            _fishToGoal = new RelaxedDistance?[shared.FishModels.Length];
            _goalItems = new (int, int, bool)[shared.ItemModels.Length];

            // What one stored state costs this window: the store's fields and
            // key (13 + key bytes), its share of the bucket array (4-8) and of the
            // queue (a 4-byte entry per push, one to two pushes a state).
            _bytesPerState = shared.Codec.CompactSize + 13 + 8 + 8;
        }

        /// <summary>
        /// The per-window memory limit counts the states this window stored, not
        /// what the worker holds: its table keeps the capacity a bigger window
        /// grew it to, and charging that to every later window stopped each of
        /// them at its first check (corals at window 56, 2 GB).
        /// </summary>
        private readonly long _bytesPerState;

        /// <summary>Walls-only distances to a fish's target cell; windows often share a target, so they are kept.</summary>
        private RelaxedDistance DistanceTo(int fish, int x, int y, bool isLeft)
        {
            if (!_distanceCache.TryGetValue((fish, x, y, isLeft), out RelaxedDistance? d))
            {
                if (_distanceCache.Count >= 4096)
                {
                    _distanceCache.Clear();
                }

                d = RelaxedDistance.ToCell(_s.Level, _s.FishModels[fish], _s.Walls, x, y, isLeft);
                _distanceCache[(fish, x, y, isLeft)] = d;
            }

            return d;
        }

        /// <summary>
        /// A* from <paramref name="from"/> to <paramref name="to"/>, admitting only
        /// paths of at most <paramref name="limit"/> moves.
        /// </summary>
        /// <returns>A strictly shorter move string, or null.</returns>
        public string? Search(RoomSnapshot from, RoomSnapshot to, int limit, long nodeLimit, out long nodes)
            => Search(from, to, limit, nodeLimit, null, out nodes, out _);

        /// <summary>
        /// As above, but <paramref name="stop"/> is polled every few thousand
        /// expansions, so a time limit also cuts a single long window short.
        /// A window cut short that way is <paramref name="stopped"/>, not barren.
        /// </summary>
        public string? Search(RoomSnapshot from, RoomSnapshot to, int limit, long nodeLimit, Func<bool>? stop, out long nodes, out bool stopped)
        {
            nodes = 0;
            stopped = false;
            long began = ThreadClock.Now();
            LastStored = 0;
            LastOutcome = WindowOutcome.NoShortcut;
            LastBound = limit + 1;
            if (limit < 0)
            {
                return null;
            }

            int[] mobile = _s.Mobile;
            SetTarget(to);

            _room.RestoreFrom(from);
            int startH = Heuristic();
            if (startH > limit)
            {
                return null; // provably unreachable inside the budget
            }

            _store.Clear();
            _queue.Clear();
            _room.WriteStateKey(_wide, mobile);
            _s.Codec.Encode(_wide, _compact);
            int start = _store.FindOrAdd(_compact, out _);
            _store.Parent(start) = -1;
            _store.Cost(start) = 0;
            _store.Estimate(start) = (ushort)startH;
            _store.Move(start) = 0;
            _queue.Push(start, startH);

            while (_queue.TryPop(out int node, out int f))
            {
                if (f > limit)
                {
                    LastStored = _store.Count;
                    return null;
                }

                int g = _store.Cost(node);
                if (g + _store.Estimate(node) != f)
                {
                    continue; // reached again more cheaply since it was queued
                }

                // Tested on popping, so the shortcut found is the shortest one.
                if (_store.Key(node).SequenceEqual(_goal))
                {
                    LastStored = _store.Count;
                    LastOutcome = WindowOutcome.Shortcut;
                    LastBound = g;
                    return Reconstruct(node);
                }

                nodes++;
                if (g < limit)
                {
                    Expand(node, g, limit);
                }

                if (nodes >= nodeLimit)
                {
                    // Popped in order of f: every path shorter than f is ruled out.
                    LastStored = _store.Count;
                    LastOutcome = WindowOutcome.NodeLimit;
                    LastBound = f;
                    return null;
                }

                if ((nodes & 4095) == 0)
                {
                    WindowOutcome? cut =
                        stop is not null && stop() ? WindowOutcome.Stopped
                        : _s.WindowTicks != long.MaxValue && ThreadClock.Now() - began > _s.WindowTicks ? WindowOutcome.WindowTimeLimit
                        : (long)_store.Count * _bytesPerState > _s.WindowMemoryLimit ? WindowOutcome.MemoryLimit
                        : null;
                    if (cut is { } why)
                    {
                        // As at the node limit, every path shorter than f is ruled out.
                        stopped = why == WindowOutcome.Stopped;
                        LastStored = _store.Count;
                        LastOutcome = why;
                        LastBound = f;
                        return null;
                    }
                }
            }

            LastStored = _store.Count;
            return null;
        }

        /// <summary>Makes <paramref name="to"/> the target: its key, and where each fish and item has to be.</summary>
        private void SetTarget(RoomSnapshot to)
        {
            int[] mobile = _s.Mobile;
            _room.RestoreFrom(to);
            _room.WriteStateKey(_wide, mobile);
            _s.Codec.Encode(_wide, _goal);
            for (int i = 0; i < _s.FishModels.Length; i++)
            {
                ref readonly ModelState m = ref _room.State(_s.FishModels[i]);
                _goalFish[i] = (m.X, m.Y, m.IsLeft, m.IsOut);
                _fishToGoal[i] = _s.Heuristic is WindowHeuristic.Walls or WindowHeuristic.WallsAndItems && !m.IsOut
                    ? DistanceTo(i, m.X, m.Y, m.IsLeft)
                    : null;
            }

            for (int i = 0; i < _s.ItemModels.Length; i++)
            {
                ref readonly ModelState m = ref _room.State(_s.ItemModels[i]);
                _goalItems[i] = (m.X, m.Y, m.IsOut);
            }
        }

        /// <summary>The bound's estimate of the moves from <paramref name="from"/> to <paramref name="to"/>, for tests.</summary>
        internal int Estimate(RoomSnapshot from, RoomSnapshot to)
        {
            SetTarget(to);
            _room.RestoreFrom(from);
            return Heuristic();
        }

        /// <summary>How the last <see cref="Search(RoomSnapshot, RoomSnapshot, int, long, Func{bool}?, out long, out bool)"/> ended.</summary>
        public WindowOutcome LastOutcome { get; private set; }

        /// <summary>No path shorter than this exists, as far as the last search got (see <see cref="WindowReport.Bound"/>).</summary>
        public int LastBound { get; private set; }

        /// <summary>States the last search stored: the memory it needed.</summary>
        public int LastStored { get; private set; }

        // ------------------------------------------------------------ ladder --

        // Per state, for the ladder only: expanded, with every child stored. Such a
        // node's children are all in the table, so after a change of target it need
        // not be expanded again. Chunked like the store; zeroed when a ladder starts.
        private const int FlagShift = 14;
        private const byte OpenFlag = 0;        // not expanded, or expanded with a child left out
        private const byte CompleteFlag = 1;    // expanded, every child stored
        private const byte TargetOnlyFlag = 2;  // stored only as a possible target (_storePruned): never expanded
        private byte[][] _complete = [];
        private bool _trackComplete;
        private bool _storePruned;

        private void SetFlag(int index, byte value)
        {
            int chunk = index >> FlagShift;
            if (chunk >= _complete.Length)
            {
                Array.Resize(ref _complete, Math.Max(chunk + 1, _complete.Length * 2));
            }

            byte[] flags = _complete[chunk] ??= new byte[1 << FlagShift];
            flags[index & ((1 << FlagShift) - 1)] = value;
        }

        private byte Flag(int index)
        {
            int chunk = index >> FlagShift;
            return chunk < _complete.Length && _complete[chunk] is { } flags ? flags[index & ((1 << FlagShift) - 1)] : OpenFlag;
        }

        /// <summary>
        /// One start, a ladder of targets further and further along the solution:
        /// the window <c>[i, i + W]</c>, then <c>[i, i + W + grow]</c>... on ONE state
        /// table. The costs from the start do not depend on the target, so the table
        /// is kept. Each rung re-scores every stored state against the new target
        /// and queues the ones within its limit. A node expanded with every child
        /// stored is not expanded again. A node that had a child left out (over the
        /// old limit, or kept only as the old target) is expanded again, and finds
        /// its stored children at once.
        /// The result of each rung is that of a fresh search: the target test is on
        /// popping, in order of f, with the same admissible bound.
        /// The ladder stops at the first shortcut or limit. Its per-window limits
        /// apply to the whole ladder: time since it began, and the whole table.
        /// </summary>
        public List<LadderRung> Ladder(RoomSnapshot from, IReadOnlyList<RoomSnapshot> targets, IReadOnlyList<int> limits, long nodeLimit, bool storePruned)
        {
            var rungs = new List<LadderRung>(targets.Count);
            int[] mobile = _s.Mobile;
            long began = ThreadClock.Now();
            long ladderNodes = 0;
            _trackComplete = true;
            _storePruned = storePruned;
            try
            {
                _store.Clear();
                _queue.Clear();
                foreach (byte[]? flags in _complete)
                {
                    if (flags is not null)
                    {
                        Array.Clear(flags);
                    }
                }

                _room.RestoreFrom(from);
                _room.WriteStateKey(_wide, mobile);
                _s.Codec.Encode(_wide, _compact);
                int start = _store.FindOrAdd(_compact, out _);
                _store.Parent(start) = -1;
                _store.Cost(start) = 0;
                _store.Move(start) = 0;

                for (int r = 0; r < targets.Count; r++)
                {
                    long rungBegan = ThreadClock.Now();
                    int limit = limits[r];
                    SetTarget(targets[r]);
                    _queue.Clear();

                    // Re-score the table against this target.
                    int count = _store.Count;
                    for (int idx = 0; idx < count; idx++)
                    {
                        _s.Codec.Decode(_store.Key(idx), _wide);
                        _room.RestoreMobile(_wide, mobile);
                        int h = Heuristic();
                        int g = _store.Cost(idx);
                        _store.Estimate(idx) = (ushort)Math.Min(h, ushort.MaxValue);
                        if (g + h <= limit)
                        {
                            _queue.Push(idx, g + h);
                        }
                    }

                    LadderRung rung = RunRung(limit, nodeLimit, began, ref ladderNodes);
                    rungs.Add(rung with
                    {
                        Rescored = count,
                        Seconds = (ThreadClock.Now() - rungBegan) / 1e7,
                    });
                    if (rung.Outcome != WindowOutcome.NoShortcut)
                    {
                        break;
                    }
                }
            }
            finally
            {
                _trackComplete = false;
                _storePruned = false;
            }

            LastStored = _store.Count;
            return rungs;
        }

        /// <summary>The ladder's pop loop for one rung: <see cref="Search(RoomSnapshot, RoomSnapshot, int, long, Func{bool}?, out long, out bool)"/>'s, plus the skip of complete nodes.</summary>
        private LadderRung RunRung(int limit, long nodeLimit, long began, ref long ladderNodes)
        {
            long expanded = 0, skipped = 0;
            LadderRung Done(WindowOutcome outcome, int bound, string? shortcut = null) =>
                new(limit + 1, outcome, bound, shortcut, expanded, skipped, 0, _store.Count, 0);

            while (_queue.TryPop(out int node, out int f))
            {
                if (f > limit)
                {
                    return Done(WindowOutcome.NoShortcut, limit + 1);
                }

                int g = _store.Cost(node);
                if (g + _store.Estimate(node) != f)
                {
                    continue;
                }

                if (_store.Key(node).SequenceEqual(_goal))
                {
                    return Done(WindowOutcome.Shortcut, g, Reconstruct(node));
                }

                byte flag = Flag(node);
                if (flag != OpenFlag)
                {
                    // Complete: its children are stored, and were queued by the re-score.
                    // Target-only: not the target, and never expanded.
                    skipped++;
                    continue;
                }

                expanded++;
                ladderNodes++;
                if (g < limit)
                {
                    Expand(node, g, limit);
                }

                if (ladderNodes >= nodeLimit)
                {
                    return Done(WindowOutcome.NodeLimit, f);
                }

                if ((expanded & 4095) == 0)
                {
                    WindowOutcome? cut =
                        _s.WindowTicks != long.MaxValue && ThreadClock.Now() - began > _s.WindowTicks ? WindowOutcome.WindowTimeLimit
                        : (long)_store.Count * (_bytesPerState + 1) > _s.WindowMemoryLimit ? WindowOutcome.MemoryLimit
                        : null;
                    if (cut is { } why)
                    {
                        return Done(why, f);
                    }
                }
            }

            return Done(WindowOutcome.NoShortcut, limit + 1);
        }


        private void Expand(int node, int g, int limit)
        {
            int[] mobile = _s.Mobile;
            _s.Codec.Decode(_store.Key(node), _wide);
            _room.RestoreMobile(_wide, mobile);
            _room.SaveMobile(_undo, mobile);

            // The reversal rules of the class remarks.
            int skip = -1, goalOnly = -1;
            if (_s.SkipReversals)
            {
                byte made = _store.Move(node);
                int opposite = (made & MoveSymbol) ^ 1;
                if ((made & MadeByTurn) != 0 || ((made & MadeByFishStep) != 0 && (made & 2) == 0))
                {
                    skip = opposite; // up/down are indices 0 and 1 of each fish's four
                }
                else if ((made & MadeByFishStep) != 0)
                {
                    goalOnly = opposite;
                }
            }

            bool restoreNeeded = false;
            bool pruned = false; // a child left out for this target or limit (the ladder re-expands such a node)
            char[] symbols = _s.Symbols;
            for (int s = 0; s < symbols.Length; s++)
            {
                if (s == skip)
                {
                    continue;
                }

                if (restoreNeeded)
                {
                    _room.RestoreMobile(_undo, mobile);
                }

                restoreNeeded = true;
                if (!_room.ApplyMove(symbols[s]))
                {
                    continue;
                }

                _s.Reduction.ThrowIfFrozenMoved(_room);

                // A fish death is irreversible and every fish goal requires alive, so
                // no state past one can ever match a target on a winning path.
                if (!_room.IsSolvable())
                {
                    continue;
                }

                int h = Heuristic();
                bool overLimit = g + 1 + h > limit;
                if (overLimit && !_storePruned)
                {
                    pruned = true;
                    continue;
                }

                _room.WriteStateKey(_wide, mobile);
                _s.Codec.Encode(_wide, _compact);

                // Neither fish can move again, or the turn kept only for the
                // last move: worth keeping only as the target itself.
                bool targetOnly = (s == goalOnly || _room.CannotMove()) && !_compact.AsSpan().SequenceEqual(_goal);
                if (targetOnly && !_storePruned)
                {
                    pruned = true;
                    continue;
                }

                // From here on, with _storePruned (the ladder's second variant), a child
                // over the limit or kept only as a target is stored but not queued: a
                // later rung re-scores it, so this node needs no second expansion.
                int child = _store.FindOrAdd(_compact, out bool added);
                if (!added && g + 1 >= _store.Cost(child))
                {
                    if (_storePruned && !targetOnly && Flag(child) == TargetOnlyFlag)
                    {
                        // A state kept only as a target, now also reached by an ordinary
                        // move: it may be expanded. Every path is a real one, so this only
                        // gives the search routes the target-only rule had spared it.
                        SetFlag(child, OpenFlag);
                        int fc = _store.Cost(child) + _store.Estimate(child);
                        if (fc <= limit)
                        {
                            _queue.Push(child, fc);
                        }
                    }

                    continue;
                }

                if (_trackComplete)
                {
                    // Reached more cheaply: its children's costs (and its own
                    // reversal rule, from the move byte) change, so it is expanded again.
                    SetFlag(child, targetOnly ? TargetOnlyFlag : OpenFlag);
                }

                _store.Parent(child) = node;
                _store.Cost(child) = (ushort)(g + 1);
                _store.Estimate(child) = (ushort)Math.Min(h, ushort.MaxValue);
                _store.Move(child) = (byte)(s
                    | (!_room.LastMoveWasFast ? 0 : _room.LastMoveWasTurn ? MadeByTurn : MadeByFishStep));
                if (!overLimit && !targetOnly)
                {
                    _queue.Push(child, g + 1 + h);
                }
            }

            if (_trackComplete)
            {
                SetFlag(node, pruned ? OpenFlag : CompleteFlag);
            }
        }

        /// <summary>
        /// A lower bound on the moves still needed to reach the target state.
        ///
        /// Every accepted move steps exactly one fish by at most one cell (a turn
        /// steps none), so the summed Manhattan distance of the fish to their target
        /// cells can never overestimate. A turn is added where the fish provably
        /// needs one: to move against its facing, or to end on the target facing when
        /// it has no horizontal travel left to turn it naturally.
        ///
        /// Items are deliberately not counted. A single move can push several at
        /// once, and gravity moves them for free, so their displacement is not
        /// additive with the fish's - counting it would break admissibility and could
        /// make the optimiser miss a genuine shortcut. That leaves the bound weak
        /// when only items differ; the cost limit does the pruning there instead.
        ///
        /// <para>A fish that is out in the target is measured to the nearest exit,
        /// not to its parked out-of-bounds coordinates. Measuring to those made every
        /// window that ends after a fish leaves look ~2000 moves long, so the
        /// optimiser never searched the approach to an exit at all (review C5).</para>
        /// </summary>
        internal int Heuristic()
        {
            if (_s.Heuristic == WindowHeuristic.None)
            {
                return 0;
            }

            int fish = FishBound();
            if (fish >= Impossible || _s.Heuristic != WindowHeuristic.WallsAndItems)
            {
                return fish;
            }

            return Math.Max(fish, ItemBound());
        }

        /// <summary>The item furthest from any target it may take: |dx| + the rise, each a pushed move.</summary>
        private int ItemBound()
        {
            int worst = 0;
            for (int i = 0; i < _s.ItemModels.Length; i++)
            {
                ref readonly ModelState m = ref _room.State(_s.ItemModels[i]);
                int best = Impossible;
                foreach (int t in _s.ItemTwins[i])
                {
                    (int gx, int gy, bool gOut) = _goalItems[t];
                    int d;
                    if (m.IsOut || gOut)
                    {
                        // Gone for good if out now; if it has to leave, no bound is claimed.
                        d = m.IsOut == gOut ? 0 : m.IsOut ? Impossible : 0;
                    }
                    else
                    {
                        // Only the way inside the room costs moves: an item that
                        // touches the border slides out over it for free (going
                        // out), so both ends are clamped to where it is wholly in.
                        int maxX = _s.Level.Width - _s.ItemShapes[i].W, maxY = _s.Level.Height - _s.ItemShapes[i].H;
                        int x0 = Math.Clamp(m.X, 0, Math.Max(0, maxX)), x1 = Math.Clamp(gx, 0, Math.Max(0, maxX));
                        int y0 = Math.Clamp(m.Y, 0, Math.Max(0, maxY)), y1 = Math.Clamp(gy, 0, Math.Max(0, maxY));
                        d = Math.Abs(x1 - x0) + Math.Max(0, y0 - y1);
                    }

                    best = Math.Min(best, d);
                }

                worst = Math.Max(worst, best);
            }

            return worst;
        }

        /// <summary>The fish's moves still needed, summed: each move moves one fish.</summary>
        private int FishBound()
        {
            int total = 0;
            for (int i = 0; i < _s.FishModels.Length; i++)
            {
                (int x, int y, bool isLeft) = _room.Position(_s.FishModels[i]);
                (int gx, int gy, bool gLeft, bool gOut) = _goalFish[i];
                bool isOut = _room.State(_s.FishModels[i]).IsOut;

                if (isOut || gOut)
                {
                    if (isOut && gOut)
                    {
                        continue;
                    }

                    if (isOut)
                    {
                        return Impossible; // out now but not in the target
                    }

                    if (_s.Heuristic != WindowHeuristic.Manhattan && _s.FishLeaveAnywhere)
                    {
                        continue; // it may leave through a plug, anywhere
                    }

                    if (_s.ExitDistance[i] is not { } exit)
                    {
                        return Impossible; // a fish that never leaves
                    }

                    int d = exit.At(x, y, isLeft);
                    if (d >= RelaxedDistance.Unreachable)
                    {
                        return Impossible;
                    }

                    total += d;
                    continue;
                }

                if (_fishToGoal[i] is { } toGoal)
                {
                    // Unreachable only where the shape does not fit the walls-only
                    // room: a fish partly over the border, or a target there.
                    // Moves keep a fish in its walls-only component, so that is the
                    // only case - and it claims nothing.
                    int d = toGoal.At(x, y, isLeft);
                    if (d < RelaxedDistance.Unreachable)
                    {
                        total += d;
                    }

                    continue;
                }

                int dx = gx - x;
                int dy = gy - y;
                total += Math.Abs(dx) + Math.Abs(dy);

                if (dx < 0 && !isLeft) total++;
                else if (dx > 0 && isLeft) total++;
                else if (dx == 0 && isLeft != gLeft) total++;
            }

            return total;
        }

        private string Reconstruct(int node)
        {
            var path = new List<char>();
            for (int i = node; _store.Parent(i) >= 0; i = _store.Parent(i))
            {
                path.Add(_s.Symbols[_store.Move(i) & MoveSymbol]);
            }

            path.Reverse();
            return new string([.. path]);
        }
    }

    /// <summary>A fish in the room in the target but out now: it can never come back.</summary>
    private const int Impossible = int.MaxValue / 4;
}
