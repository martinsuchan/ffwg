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

    /// <summary>Stop after this long, keeping whatever was found. Checked between windows.</summary>
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

    /// <summary>Called with the whole solution after every round that shortened it - to save it as it goes.</summary>
    public Action<string>? Improved { get; init; }
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
        Func<bool>? outOfTime = options.TimeLimit is { } timeLimit ? () => stopwatch.Elapsed > timeLimit : null;

        Trajectory path = replay.Run(moves)
            ?? throw new InvalidOperationException($"{_level.Name}: the input is not a valid move string for this level");

        int passes;
        for (passes = 1; ; passes++)
        {
            // This round's windows, minus the ones already known to hold nothing.
            var windows = new List<(int I, int J, string Id)>();
            for (int i = 0; i + 2 <= moves.Length; i += options.Stride)
            {
                int j = Math.Min(i + options.Window, moves.Length);
                string id = path.Keys[i] + "|" + path.Keys[j] + "|" + (j - i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!barren.Contains(id))
                {
                    windows.Add((i, j, id));
                }
            }

            var found = new string?[windows.Count];
            var searched = new bool[windows.Count];
            var nodes = new long[windows.Count];
            int next = -1;

            void Work()
            {
                WindowSearch search;
                lock (contexts)
                {
                    search = contexts.Count > 0 ? contexts.Pop() : new WindowSearch(shared);
                }

                try
                {
                    for (int k = Interlocked.Increment(ref next); k < windows.Count; k = Interlocked.Increment(ref next))
                    {
                        if (outOfTime is not null && outOfTime())
                        {
                            return;
                        }

                        (int i, int j, _) = windows[k];
                        found[k] = search.Search(path.States[i], path.States[j], j - i - 1, options.NodeLimit, outOfTime, out nodes[k], out bool stopped);
                        searched[k] = !stopped;
                        if (stopped)
                        {
                            return;
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

            if (splicedNow > 0)
            {
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
    /// One window's search on its own: the shortest path of at most
    /// <paramref name="limit"/> moves from <paramref name="from"/> to
    /// <paramref name="to"/>, or null - for tests that pin the search itself.
    /// </summary>
    internal string? Shortcut(RoomSnapshot from, RoomSnapshot to, int limit, WindowOptions? options = null, long nodeLimit = 2_000_000)
    {
        var shared = new Shared(_level, _reduction ?? LevelReduction.Compute(_level), options ?? new WindowOptions());
        return new WindowSearch(shared).Search(from, to, limit, nodeLimit, out _);
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
            bool[] walls = RelaxedDistance.BuildWallGrid(level);
            ExitDistance = [.. FishModels
                .Select(m => level.Models[m].Goal is GoalKind.Escape or GoalKind.Out
                    ? RelaxedDistance.ToExit(level, m, walls, freeFall: false)
                    : null)];
        }

        public Level Level { get; }

        public LevelReduction Reduction { get; }

        public int[] Mobile { get; }

        /// <summary>Stateless between calls, so the workers share it (see its remarks).</summary>
        public StateKeyCodec Codec { get; }

        public bool SkipReversals { get; }

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
            if (limit < 0)
            {
                return null;
            }

            int[] mobile = _s.Mobile;
            _room.RestoreFrom(to);
            _room.WriteStateKey(_wide, mobile);
            _s.Codec.Encode(_wide, _goal);
            for (int i = 0; i < _s.FishModels.Length; i++)
            {
                ref readonly ModelState m = ref _room.State(_s.FishModels[i]);
                _goalFish[i] = (m.X, m.Y, m.IsLeft, m.IsOut);
            }

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
                    return Reconstruct(node);
                }

                nodes++;
                if (g < limit)
                {
                    Expand(node, g, limit);
                }

                if (nodes >= nodeLimit)
                {
                    return null;
                }

                if (stop is not null && (nodes & 4095) == 0 && stop())
                {
                    stopped = true;
                    return null;
                }
            }

            return null;
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
                if (g + 1 + h > limit)
                {
                    continue;
                }

                _room.WriteStateKey(_wide, mobile);
                _s.Codec.Encode(_wide, _compact);

                // Neither fish can move again, or the turn kept only for the
                // last move: worth keeping only as the target itself.
                if ((s == goalOnly || _room.CannotMove()) && !_compact.AsSpan().SequenceEqual(_goal))
                {
                    continue;
                }

                int child = _store.FindOrAdd(_compact, out bool added);
                if (!added && g + 1 >= _store.Cost(child))
                {
                    continue;
                }

                _store.Parent(child) = node;
                _store.Cost(child) = (ushort)(g + 1);
                _store.Estimate(child) = (ushort)h;
                _store.Move(child) = (byte)(s
                    | (!_room.LastMoveWasFast ? 0 : _room.LastMoveWasTurn ? MadeByTurn : MadeByFishStep));
                _queue.Push(child, g + 1 + h);
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

                    if (isOut || _s.ExitDistance[i] is not { } exit)
                    {
                        return Impossible; // out now but not in the target, or a fish that never leaves
                    }

                    int d = exit.At(x, y, isLeft);
                    if (d >= RelaxedDistance.Unreachable)
                    {
                        return Impossible;
                    }

                    total += d;
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
