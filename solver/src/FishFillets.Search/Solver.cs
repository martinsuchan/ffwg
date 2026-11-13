using System.Diagnostics;

using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>Which redundant-ordering reduction to apply, if any (solver/docs/011).</summary>
public enum PartialOrderMode
{
    /// <summary>No reduction.</summary>
    Off,

    /// <summary>Compare each successor against the move that produced its parent.</summary>
    Pairwise,

    /// <summary>Carry a set of forbidden moves, skipped before the physics runs.</summary>
    SleepSets,
}

public sealed record SolveOptions
{
    /// <summary>
    /// Heuristic weight. 1.0 is plain A* and returns a provably shortest
    /// solution; above 1.0 trades that guarantee for reach, finding longer
    /// solutions on levels A* cannot finish.
    /// </summary>
    public double Weight { get; init; } = 1.0;

    public long MaxNodes { get; init; } = 20_000_000;

    public TimeSpan? TimeLimit { get; init; }

    public Action<string>? Progress { get; init; }

    /// <summary>
    /// How often to call <see cref="Progress"/>, in seconds. Time-based rather
    /// than counted in nodes: a level that expands 2 M/s and one that manages
    /// 20 k/s should both report at a readable pace, and the whole point of a
    /// progress line is to show that a slow search is alive.
    /// </summary>
    public double ProgressSeconds { get; init; } = 1.0;

    /// <summary>
    /// Which redundant-ordering reduction to use. <b>Off by default: both were
    /// measured and neither pays</b> (solver/docs/011). Both are correct - every
    /// known optimum survives either - so the switch exists to keep them
    /// re-measurable rather than because one is recommended.
    /// </summary>
    public PartialOrderMode PartialOrder { get; init; } = PartialOrderMode.Off;

    /// <summary>
    /// Toll charged for routing a fish through cells a movable item occupies -
    /// see <see cref="WorkHeuristic"/>. Zero uses the admissible bound alone and
    /// keeps the shortest-solution guarantee; anything above it gives that up in
    /// exchange for an estimate near the real remaining cost rather than a
    /// fraction of it. Around 5 measured closest over the reference solutions.
    /// </summary>
    public int WorkPenalty { get; init; }

    /// <summary>
    /// Whether to drop states that provably have no solution because something
    /// is stuck for good - a fish that has to leave walled in, or a goal_out item
    /// that can never move again (see <see cref="StuckDetector"/>). Sound: it
    /// only ever discards states that genuinely have no solution, so the answer
    /// and the shortest-solution proof are unaffected. <b>On by default</b> since
    /// docs/020: 61-63% of everything plain A* stores on wc and society is such a
    /// state, and the per-layout cache keeps the cost low everywhere else.
    /// </summary>
    public bool DetectStuck { get; init; } = true;

    /// <summary>
    /// Expansions the stuck-item rule gets to prove itself: if it has not
    /// discarded a single state by then, it is switched off for the rest of the
    /// search. On a level with no dead ends it is pure cost - up to a third more
    /// time on rooms whose items move often (docs/020). The first dead end can
    /// come late (library: expansion 199,356; steel 78,509), hence about a
    /// million. Decided on counts alone, at the same expansion in a serial and a
    /// parallel run, so the two stay identical. Zero keeps the rule on throughout.
    /// </summary>
    public long StuckProbation { get; init; } = 1 << 20;

    /// <summary>
    /// Worker threads for expanding nodes (review P4). Above 1, each batch of
    /// same-f nodes has its successors simulated in parallel and merged in the
    /// order a serial search would - so at weight 1 the expansion order, every
    /// count and the answer are identical to <c>Threads = 1</c>. With
    /// <see cref="PartialOrder"/> or <see cref="WorkPenalty"/>, which keep
    /// single-threaded per-search state, the search runs serially;
    /// <see cref="DetectStuck"/> runs in parallel (its cache is thread-safe).
    /// </summary>
    public int Threads { get; init; } = 1;
}

public sealed record SolveResult(
    string? Moves,
    bool Optimal,
    string Status,
    long Expanded,
    long Generated,
    int StatesStored,
    int DeepestF,
    TimeSpan Elapsed)
{
    public bool Solved => Moves is not null;
}

/// <summary>
/// Solves a level outright: A* over settled states, one move symbol per edge.
///
/// What makes it tractable, in order of how much it actually matters - measured,
/// not assumed (solver/docs/011):
///
/// <list type="number">
/// <item><b>The level reduction</b> (<see cref="LevelReduction"/>) removes every
///   model that provably can never move, so the state key carries only what can
///   actually differ. Most rooms are mostly scenery.</item>
/// <item><b>Duplicate detection.</b> 62-80% of everything generated is a state
///   already held at no greater cost - the fish reach the same position by many
///   orderings of the same moves. This is where nearly all the pruning is.</item>
/// <item><b>An admissible heuristic</b> (<see cref="RelaxedDistance"/>) from a
///   walls-only relaxation. Sound because fish swim rather than climb: items can
///   only obstruct them, so deleting the items never lengthens a path.</item>
/// <item><b>Dead-end pruning</b> (<see cref="IsDeadEnd"/>) comes a distant last:
///   0.5-2.1% of everything generated, and two of its three rules never fire at
///   all. It is nearly free, so it stays - but it is not load-bearing.</item>
/// </list>
///
/// The cost of an expansion is almost entirely physics: one
/// <see cref="Room.ApplyMove"/> per symbol plus putting the room back between
/// them, which is why the room is restored by undoing the last move rather than
/// rebuilding it from its key.
///
/// Cost is uniform (one symbol, one step), which is exactly how the game's own
/// pedometer counts, so a weight of 1.0 returns a solution that is shortest by
/// the game's own measure.
/// </summary>
public sealed partial class Solver
{
    private readonly LevelReduction _reduction;
    private readonly Room _room;
    private readonly int[] _mobile;
    private readonly char[] _symbols;
    private readonly byte[] _key;

    // Visited set and nodes in one, keyed on the compact form of _key (review
    // P1) - see StateStore and StateKeyCodec. _wide holds the popped state's
    // wide key: the room is restored from it, and the partial-order reduction
    // reads the parent's key from it.
    private readonly StateStore _store;
    private readonly StateKeyCodec _codec;
    private readonly byte[] _compact;
    private readonly byte[] _wide;

    private readonly ExitHeuristic _heuristic;

    // Everything derived from where the ITEMS are. A fish move leaves those
    // alone - which is nearly every edge - so these are cached on the item half
    // of the state key rather than rebuilt per state. Capped and dropped
    // wholesale when full: an unbounded cache outgrows the state table it exists
    // to help.
    private readonly Dictionary<ItemLayout, LayoutInfo> _layouts = [];
    private readonly int[] _itemSlots;

    // Each unit's key slot: which fish are still in play is part of the layout
    // too (review C1) - see WriteLayout.
    private readonly int[] _unitSlots;

    // One-entry front for that dictionary: the arrangement most recently looked
    // up, so an unchanged one costs a byte compare instead of an allocation and
    // a hash.
    private readonly byte[] _lastLayoutBytes;
    private readonly byte[] _layoutScratch;
    private LayoutInfo? _lastLayout;

    // The stuck-item rule (on by default): a shared, thread-safe per-layout
    // cache, and this thread's front for it.
    private readonly StuckDetector _stuck;
    private readonly StuckDetector.Probe _stuckProbe;

    private int _workPenalty;
    // The stuck-item rule: on for this search, how many states it has
    // discarded, and when it is switched off if that is still none.
    private bool _stuckActive;
    private long _stuckPruned;
    private long _stuckProbation;
    private long _stuckFirstPrune;

    private const int LayoutCacheLimit = 262144;

    /// <summary>What one item arrangement implies, worked out once and reused.</summary>
    private sealed class LayoutInfo
    {
        /// <summary>The guiding estimate, when a toll is in use.</summary>
        public WorkHeuristic? Work;
    }

    // Per-symbol lookups for the partial-order reduction: which unit a symbol
    // drives, and its rank in the canonical order.
    private readonly byte[] _symbolUnit = new byte[128];
    private readonly byte[] _symbolRank = new byte[128];
    private readonly int[] _unitModel;

    // Each unit's shape bounds relative to its anchor. Shape marks are always
    // non-negative, so the minima are the smallest mark and the maxima W-1/H-1 -
    // but a shape may start with blank rows or columns, so they are taken from
    // the marks rather than assumed to be zero.
    private readonly (int MinX, int MinY, int MaxX, int MaxY)[] _unitBounds;
    private readonly byte[] _slotOfModel;
    private PartialOrderMode _mode;
    private readonly Node[] _tried = new Node[16];

    // The parent state, kept as structs so a tried move can be undone instead of
    // rebuilt from its key - see Room.RestoreMobile(ReadOnlySpan<ModelState>, ...).
    private readonly ModelState[] _undo;

    // Partial-order bookkeeping per state, allocated only when that mode is on.
    private Node[] _po = [];
    private readonly BucketQueue _queue = new();

    private double _weight = 1.0;

    /// <summary>
    /// The partial-order reduction's view of a state: what its own incoming
    /// move disturbed, so the expansion can tell whether the next move is
    /// independent of it. <see cref="Mover"/> is <see cref="NoMover"/> when the
    /// move was not "simple" (see <c>ClassifySimpleMove</c>), which disables the
    /// reduction for that node. Parent, cost and key live in the
    /// <see cref="StateStore"/>.
    /// </summary>
    private struct Node
    {
        public char Move;
        public byte Mover;
        public byte X0, Y0, X1, Y1;

        /// <summary>
        /// Moves that must not be tried from this state, one bit per symbol -
        /// because a sibling of this node already tried them and they are
        /// independent of the move that got here, so the states they lead to are
        /// reached down the sibling's branch at the same cost.
        /// </summary>
        public byte Sleep;
    }

    private const byte NoMover = 0xFF;

    /// <summary>
    /// Everything about a state except where the fish are standing: the items'
    /// key bytes, plus which fish are still alive and in the room (see
    /// <see cref="WriteLayout"/>). Two states sharing one share a
    /// <see cref="WorkHeuristic"/> and a stuck-item verdict.
    /// </summary>
    private readonly struct ItemLayout : IEquatable<ItemLayout>
    {
        private readonly byte[] _bytes;
        private readonly int _hash;

        public ItemLayout(ReadOnlySpan<byte> layout)
        {
            _bytes = layout.ToArray();

            var hash = default(HashCode);
            hash.AddBytes(_bytes);
            _hash = hash.ToHashCode();
        }

        public bool Equals(ItemLayout other) => _bytes.AsSpan().SequenceEqual(other._bytes);

        public override bool Equals(object? obj) => obj is ItemLayout other && Equals(other);

        public override int GetHashCode() => _hash;
    }

    public Solver(Level level, LevelReduction? reduction = null)
    {
        _reduction = reduction ?? LevelReduction.Compute(level);
        _room = new Room(level);
        _mobile = _reduction.MobileModels;
        _key = new byte[_reduction.StateKeySize];
        _codec = new StateKeyCodec(level, _mobile);
        _store = new StateStore(_codec.CompactSize);
        _compact = new byte[_codec.CompactSize];
        _wide = new byte[_reduction.StateKeySize];

        _symbols = new char[_room.MoveSymbolCount];
        _room.GetMoveSymbols(_symbols, out int count);
        Array.Resize(ref _symbols, count);

        _heuristic = new ExitHeuristic(level, _reduction);

        Array.Fill(_symbolUnit, NoMover);
        for (byte u = 0; u < level.Units.Length; u++)
        {
            UnitDef unit = level.Units[u];
            foreach (char c in (ReadOnlySpan<char>)[unit.Up, unit.Down, unit.Left, unit.Right])
            {
                if (c < _symbolUnit.Length)
                {
                    _symbolUnit[c] = u;
                }
            }
        }

        for (byte s = 0; s < _symbols.Length; s++)
        {
            _symbolRank[_symbols[s]] = s;
        }

        var fishModels = level.Units.Select(u => u.Model).ToHashSet();
        var itemSlots = new List<int>();
        for (int i = 0; i < _mobile.Length; i++)
        {
            if (!fishModels.Contains(_mobile[i]))
            {
                itemSlots.Add(i);
            }
        }

        _itemSlots = [.. itemSlots];
        _unitModel = [.. level.Units.Select(u => u.Model)];
        _unitSlots = [.. _unitModel.Select(m => Array.IndexOf(_mobile, m))];
        _stuck = new StuckDetector(level, _reduction, _mobile);
        _stuckProbe = _stuck.NewProbe();
        _lastLayoutBytes = new byte[(_itemSlots.Length * 5) + _unitSlots.Length];
        _layoutScratch = new byte[_lastLayoutBytes.Length];
        _unitBounds = new (int, int, int, int)[level.Units.Length];
        for (int u = 0; u < level.Units.Length; u++)
        {
            Shape shape = level.Models[level.Units[u].Model].Shape;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int m = 0; m < shape.MarkX.Length; m++)
            {
                minX = Math.Min(minX, shape.MarkX[m]);
                maxX = Math.Max(maxX, shape.MarkX[m]);
                minY = Math.Min(minY, shape.MarkY[m]);
                maxY = Math.Max(maxY, shape.MarkY[m]);
            }

            _unitBounds[u] = (minX, minY, maxX, maxY);
        }

        _undo = new ModelState[_mobile.Length];

        _slotOfModel = new byte[level.Models.Length];
        Array.Fill(_slotOfModel, NoMover);
        for (int i = 0; i < _mobile.Length; i++)
        {
            _slotOfModel[_mobile[i]] = (byte)i;
        }
    }

    public LevelReduction Reduction => _reduction;

    /// <summary>
    /// Bytes held by the search's own structures - visited set, node array and
    /// open lists - at their current capacity. Excludes the per-layout caches
    /// (<c>--work</c>/<c>--stuck</c>). Read after <see cref="Solve"/> for a
    /// deterministic, GC-independent memory figure (<c>ffsolve bench --suite</c>).
    /// </summary>
    public long MemoryBytes
    {
        get
        {
            return _store.MemoryBytes
                   + ((long)_po.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<Node>())
                   + _queue.MemoryBytes;
        }
    }

    public SolveResult Solve(SolveOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        _weight = options.Weight;
        _mode = options.PartialOrder;
        _workPenalty = options.WorkPenalty;
        _stuckActive = options.DetectStuck;
        _stuckPruned = 0;
        _stuckFirstPrune = 0;
        _stuckProbation = options.StuckProbation;
        _layouts.Clear();
        _lastLayout = null;
        _stuck.Clear();
        _stuckProbe.Reset();
        _store.Clear();
        _queue.Clear();
        _room.Reset();

        if (_room.IsSolved())
        {
            return new SolveResult("", true, "already solved", 0, 0, 0, 0, stopwatch.Elapsed);
        }

        _room.WriteStateKey(_key, _mobile);
        int startH = _heuristic.Estimate(_room);
        if (startH >= RelaxedDistance.Unreachable)
        {
            return new SolveResult(null, false, "unsolvable: a goal is unreachable from the start", 0, 0, 0, 0, stopwatch.Elapsed);
        }

        if (_stuckActive && NoWayOut())
        {
            return new SolveResult(
                null, false, "unsolvable: a fish is already walled in", 0, 0, 0, 0, stopwatch.Elapsed);
        }

        int startGuide = Guide(startH);
        if (startGuide >= WorkHeuristic.Unreachable)
        {
            return new SolveResult(null, false, "unsolvable: the guiding estimate finds no way out", 0, 0, 0, 0, stopwatch.Elapsed);
        }

        _codec.Encode(_key, _compact);
        int startIndex = _store.FindOrAdd(_compact, out _);
        _store.Parent(startIndex) = -1;
        _store.Cost(startIndex) = 0;
        _store.Estimate(startIndex) = (ushort)Math.Min(startGuide, ushort.MaxValue);
        if (_mode != PartialOrderMode.Off)
        {
            EnsurePo(startIndex);
            _po[startIndex] = new Node { Mover = NoMover, Move = '\0' };
        }

        _queue.Push(startIndex, Priority(0, _store.Estimate(startIndex)));

        if (options.Threads > 1 && _mode == PartialOrderMode.Off && _workPenalty <= 0)
        {
            return SolveParallel(options, stopwatch);
        }

        long expanded = 0, generated = 1;
        int deepestF = 0;
        double nextReport = options.ProgressSeconds;

        // Lowest f first. A push below the current bucket - possible above
        // weight 1, or with the toll - pulls the cursor back (BucketQueue).
        while (_queue.TryPop(out int nodeIndex, out int f))
        {
            {
                int g = _store.Cost(nodeIndex);

                // A state reached again more cheaply is updated in place and
                // queued again; the older, dearer queue entry is now stale.
                if (Priority(g, _store.Estimate(nodeIndex)) != f)
                {
                    continue;
                }

                // Every state with a smaller f has been expanded, so this is a
                // proven lower bound on the answer - but only at weight 1, where
                // f is a real lower bound on the cost through the node. Above 1
                // it is an inflated priority and means nothing, so it is not
                // reported as a bound (see Abandoned).
                deepestF = Math.Max(deepestF, f);

                _codec.Decode(_store.Key(nodeIndex), _wide);
                _room.RestoreMobile(_wide, _mobile);

                // The goal is tested when a state is POPPED, not when it is
                // generated, and that is what makes "shortest" true rather than
                // merely likely.
                //
                // Testing on generation looks equivalent and is not. A state can
                // have h == 0 without being solved - both fish sitting where the
                // walls-only relaxation says they could leave, with a real item
                // actually blocking the way. Such a state lands in bucket g, and
                // expanding it generates a finished room at cost g + 1, possibly
                // before the genuinely optimal finish is generated from another
                // node in that same bucket. Popping in f order cannot do that:
                // a finished room has h == 0, so its f is exactly its move count,
                // and buckets are drained lowest first.
                if (_room.IsSolved())
                {
                    return new SolveResult(
                        Reconstruct(nodeIndex),
                        options.Weight <= 1.0 && options.WorkPenalty <= 0,
                        "solved",
                        expanded,
                        generated,
                        _store.Count,
                        ProvenBound(f),
                        stopwatch.Elapsed);
                }

                expanded++;

                byte sleep = _mode == PartialOrderMode.SleepSets ? _po[nodeIndex].Sleep : (byte)0;
                byte done = 0;
                bool restoreNeeded = false;
                _room.SaveMobile(_undo, _mobile);

                // The parent's stuck verdict, for every child that only moved a
                // fish: same items, same layout, same verdict - so those skip
                // the layout key entirely and cost one bit test.
                StuckDetector.Verdict? parentVerdict = _stuckActive ? _stuck.LayoutVerdict(_room, _wide, _stuckProbe) : null;

                for (int s = 0; s < _symbols.Length; s++)
                {
                    // The whole point of the sleep set: a sleeping move is skipped
                    // here, before the physics runs. That is the 93% of a
                    // expansion's cost that the docs/011 reduction still paid.
                    if ((sleep & (1 << s)) != 0)
                    {
                        continue;
                    }

                    char symbol = _symbols[s];
                    if (restoreNeeded)
                    {
                        // Undo the previous move rather than rebuilding this state
                        // from its key: a move touches one or two models, a rebuild
                        // re-derives all of them (docs/011).
                        _room.RestoreMobile(_undo, _mobile);
                    }

                    restoreNeeded = true;

                    if (!_room.ApplyMove(symbol))
                    {
                        continue;
                    }

                    _reduction.ThrowIfFrozenMoved(_room);

                    generated++;

                    int h;
                    if (_room.IsSolved())
                    {
                        // Queue it like any other state - it will be recognised
                        // when it is popped. It needs no more moves, so h is 0 and
                        // it lands in bucket g + 1, which is never a bucket that
                        // has already been drained (h is consistent, so the parent
                        // can be at most one ahead of it).
                        //
                        // It also has to bypass IsDeadEnd: a finished room has
                        // both fish out, which makes CannotMove() true, so the
                        // dead-end rules would otherwise throw the answer away.
                        h = 0;
                    }
                    else if (IsDeadEnd(out h))
                    {
                        continue;
                    }

                    _room.WriteStateKey(_key, _mobile);

                    // After the key is written, because it is cached on it - and
                    // after IsDeadEnd, which is orders of magnitude cheaper and
                    // catches its own cases first.
                    if (_stuckActive && h > 0
                        && (_room.LastMoveWasFast ? _stuck.Condemns(parentVerdict!, _room) : NoWayOut()))
                    {
                        CountStuckPrune(expanded);
                        continue;
                    }

                    int guide = Guide(h);
                    if (guide >= WorkHeuristic.Unreachable)
                    {
                        continue;
                    }

                    guide = Math.Min(guide, ushort.MaxValue);
                    int childF = Priority(g + 1, guide);

                    var child = new Node { Mover = NoMover, Move = symbol };
                    if (_mode != PartialOrderMode.Off)
                    {
                        ClassifySimpleMove(_wide, _key, symbol, ref child);

                        if (_mode == PartialOrderMode.Pairwise)
                        {
                            if (IsRedundantOrdering(in _po[nodeIndex], in child, symbol))
                            {
                                continue;
                            }
                        }
                        else
                        {
                            child.Sleep = SleepSetFor(in child, done);
                            RecordTried(s, in child);
                            done |= (byte)(1 << s);
                        }
                    }

                    if (g + 1 > ushort.MaxValue)
                    {
                        throw new InvalidOperationException("path longer than 65,535 moves - the state store keeps costs in 16 bits");
                    }

                    _codec.Encode(_key, _compact);
                    int childIndex = _store.FindOrAdd(_compact, out bool added);
                    if (!added)
                    {
                        int known = _store.Cost(childIndex);
                        if (g + 1 > known)
                        {
                            continue;
                        }

                        if (g + 1 == known)
                        {
                            // Same cost: only worth expanding again if this path
                            // forbids less than the stored expansion did - the
                            // sleep-set rule StateSet.TryImprove used to apply.
                            if (_mode != PartialOrderMode.SleepSets)
                            {
                                continue;
                            }

                            byte merged = (byte)(_po[childIndex].Sleep & child.Sleep);
                            if (merged == _po[childIndex].Sleep)
                            {
                                continue;
                            }

                            child.Sleep = merged;
                        }
                        else if (_mode == PartialOrderMode.SleepSets)
                        {
                            child.Sleep = (byte)(_po[childIndex].Sleep & child.Sleep);
                        }
                    }

                    _store.Parent(childIndex) = nodeIndex;
                    _store.Cost(childIndex) = (ushort)(g + 1);
                    _store.Estimate(childIndex) = (ushort)guide;
                    _store.Move(childIndex) = (byte)s;
                    if (_mode != PartialOrderMode.Off)
                    {
                        EnsurePo(childIndex);
                        _po[childIndex] = child;
                    }

                    _queue.Push(childIndex, childF);
                }

                ReportProgress(options, stopwatch, expanded, f, ref nextReport);
                _stuck.TrimIfFull();
                EndStuckProbation(expanded);

                if (expanded >= options.MaxNodes)
                {
                    return Abandoned("node limit reached", expanded, generated, deepestF, stopwatch);
                }

                if (options.TimeLimit is { } limit && stopwatch.Elapsed > limit)
                {
                    return Abandoned("time limit reached", expanded, generated, deepestF, stopwatch);
                }
            }
        }

        return new SolveResult(
            null, options.Weight <= 1.0 && options.WorkPenalty <= 0, "exhausted: no solution exists", expanded, generated,
            _store.Count, ProvenBound(deepestF), stopwatch.Elapsed);
    }

    /// <summary>
    /// The estimate the open list is ordered by: the admissible one, unless a
    /// toll is in use. <c>_key</c> must already hold the current state.
    /// </summary>
    private int Guide(int admissible)
    {
        if (_workPenalty <= 0 || admissible <= 0)
        {
            return admissible;
        }

        LayoutInfo info = LayoutFor();
        info.Work ??= WorkHeuristic.Build(_room.Level, _room, _reduction.Walls, _workPenalty);

        // Never below the admissible bound: the toll only measures fish that
        // have to leave, so on a goal_out level (fish goal_alive) it is zero and
        // would otherwise blind the search (review C4).
        return Math.Max(admissible, info.Work.Estimate(_room));
    }

    /// <summary>States the stuck-item rule discarded in the last <see cref="Solve"/>.</summary>
    public long StuckPruned => _stuckPruned;

    /// <summary>Whether the stuck-item rule was still on when the last <see cref="Solve"/> ended (see <see cref="SolveOptions.StuckProbation"/>).</summary>
    public bool StuckActive => _stuckActive;

    /// <summary>The expansion during which the stuck-item rule first discarded a state in the last <see cref="Solve"/>, or 0.</summary>
    public long StuckFirstPruneAt => _stuckFirstPrune;

    private void CountStuckPrune(long expanded)
    {
        if (_stuckPruned++ == 0)
        {
            _stuckFirstPrune = expanded;
        }
    }

    /// <summary>Switches the stuck-item rule off once its probation ends with nothing discarded. Call after each expansion.</summary>
    private void EndStuckProbation(long expanded)
    {
        if (_stuckActive && expanded == _stuckProbation && _stuckPruned == 0)
        {
            _stuckActive = false;
        }
    }

    /// <summary>
    /// Whether the current state has no solution because something that has to
    /// leave the room never can again - see <see cref="StuckDetector"/>.
    /// <c>_key</c> must already hold the current state.
    /// </summary>
    private bool NoWayOut() => _stuck.NoWayOut(_room, _key, _stuckProbe);

    private LayoutInfo LayoutFor()
    {
        // Most moves are a fish swimming, which leaves the items exactly where
        // they were - so before building a key and hashing it, check against the
        // last one. Siblings of an expansion nearly all share their parent's
        // arrangement, and so does the next expansion more often than not.
        WriteLayout(_layoutScratch);
        if (_lastLayout is not null && _layoutScratch.AsSpan().SequenceEqual(_lastLayoutBytes))
        {
            return _lastLayout;
        }

        var layout = new ItemLayout(_layoutScratch);
        if (!_layouts.TryGetValue(layout, out LayoutInfo? info))
        {
            if (_layouts.Count >= LayoutCacheLimit)
            {
                _layouts.Clear();
            }

            info = new LayoutInfo();
            _layouts[layout] = info;
        }

        _layoutScratch.CopyTo(_lastLayoutBytes, 0);
        _lastLayout = info;
        return info;
    }

    /// <summary>
    /// The cache key for what <see cref="LayoutInfo"/> holds: every item's key
    /// bytes, then one byte per unit with its alive / out / lost flags.
    ///
    /// <para>The fish flags are what review C1 found missing. The stuck-item
    /// analysis depends on which fish are still in play - who can push, and how
    /// hard (<c>StuckAnalysis.StrongestFish</c>) - so a layout first analysed
    /// after the big fish left (heavy items then count as wall) handed its
    /// verdict to every later state with the same items and the big fish still
    /// there, condemning them. Fish positions stay out: the analysis is run
    /// with the fish anywhere, so it holds wherever they stand.</para>
    /// </summary>
    private void WriteLayout(Span<byte> destination)
    {
        for (int i = 0; i < _itemSlots.Length; i++)
        {
            _key.AsSpan(_itemSlots[i] * 5, 5).CopyTo(destination.Slice(i * 5, 5));
        }

        int offset = _itemSlots.Length * 5;
        for (int u = 0; u < _unitSlots.Length; u++)
        {
            // Flag bits of Room.WriteStateKey: 2 alive, 4 out, 8 lost.
            destination[offset + u] = (byte)(_key[(_unitSlots[u] * 5) + 4] & (2 | 4 | 8));
        }
    }

    /// <summary>
    /// The f the sweep has reached, as a claim about the answer - which it only
    /// is when f is a true lower bound on the cost through a node. A weight above
    /// 1 inflates it and a toll overestimates outright, so in either case it is
    /// not reported, rather than recorded as this level's proven bound.
    /// </summary>
    private int ProvenBound(int deepestF) => _weight <= 1.0 && _workPenalty <= 0 ? deepestF : 0;

    private SolveResult Abandoned(string status, long expanded, long generated, int deepestF, Stopwatch stopwatch) =>
        new(null, false, status, expanded, generated, _store.Count, ProvenBound(deepestF), stopwatch.Elapsed);

    /// <summary>
    /// Classifies the move just applied as <b>simple</b>, and if so reports the
    /// cells it disturbed.
    ///
    /// <para>A move is simple when the only thing in the whole room that changed
    /// is the fish that made it, and that fish is still alive and in the room.
    /// This is deliberately narrow: it means nothing was pushed, nothing fell,
    /// nothing died and nobody left, so the move's entire effect on the world is
    /// "that fish is somewhere else now". It is also the overwhelmingly common
    /// case, because most of a solution is a fish swimming through open water -
    /// which is exactly the redundancy this reduction is aimed at.</para>
    ///
    /// <para>Detected by diffing the parent's state key against the child's,
    /// which costs a few bytes' comparison and needs no cooperation from the
    /// physics.</para>
    /// </summary>
    private bool ClassifySimpleMove(
        ReadOnlySpan<byte> parentKey, ReadOnlySpan<byte> childKey, char symbol, ref Node node)
    {
        byte unit = _symbolUnit[symbol];
        if (unit == NoMover)
        {
            return false;
        }

        int movedSlot = _slotOfModel[_unitModel[unit]];
        for (int n = 0; n < _mobile.Length; n++)
        {
            if (n == movedSlot)
            {
                continue;
            }

            int offset = n * 5;
            if (!parentKey.Slice(offset, 5).SequenceEqual(childKey.Slice(offset, 5)))
            {
                return false; // something else moved: a push, a fall, a death
            }
        }

        // The fish's own flags must be untouched too - only position and facing
        // may differ. A fish that died or left changes the world far beyond its
        // own cells.
        int slot = movedSlot * 5;
        if ((parentKey[slot + 4] & ~1) != (childKey[slot + 4] & ~1))
        {
            return false;
        }

        int fromX = (short)(parentKey[slot] | (parentKey[slot + 1] << 8));
        int fromY = (short)(parentKey[slot + 2] | (parentKey[slot + 3] << 8));
        int toX = (short)(childKey[slot] | (childKey[slot + 1] << 8));
        int toY = (short)(childKey[slot + 2] | (childKey[slot + 3] << 8));

        if (fromX < 0 || fromY < 0 || toX < 0 || toY < 0)
        {
            return false; // crossing the border - not a plain swim
        }

        // The cells the fish occupied before and after, as one box.
        (int minX, int minY, int maxX, int maxY) = _unitBounds[unit];
        int x0 = Math.Min(fromX, toX) + minX, x1 = Math.Max(fromX, toX) + maxX;
        int y0 = Math.Min(fromY, toY) + minY, y1 = Math.Max(fromY, toY) + maxY;

        if (x0 < 0 || y0 < 0 || x1 > 255 || y1 > 255)
        {
            return false;
        }

        node.Mover = unit;
        node.X0 = (byte)x0;
        node.Y0 = (byte)y0;
        node.X1 = (byte)x1;
        node.Y1 = (byte)y1;
        return true;
    }

    /// <summary>
    /// The sleep set for a freshly generated child: every move already explored
    /// from the same parent that is independent of the move which produced this
    /// child.
    ///
    /// <para><b>Why those moves can be skipped.</b> Say the parent explored
    /// <c>a</c> first, reaching X, and then <c>b</c>, reaching this child. If
    /// <c>a</c> and <c>b</c> are independent they commute, so the state
    /// <c>child + a</c> is the same as <c>X + b</c> - and it is reached down X's
    /// branch, at the same cost, because <c>b</c> was not yet explored when X was
    /// generated and so is not in X's sleep set. Trying <c>a</c> here as well only
    /// re-derives it.</para>
    ///
    /// <para>Only moves that were actually explored count. A move the parent
    /// refused never produced an X for the argument to lean on, and one the
    /// dead-end rules dropped has no branch to reach anything down - both are left
    /// out of <paramref name="done"/> by the caller, which errs towards exploring
    /// more.</para>
    /// </summary>
    private byte SleepSetFor(in Node child, byte done)
    {
        if (child.Mover == NoMover)
        {
            return 0; // this move disturbed more than one fish: nothing may sleep under it
        }

        byte sleep = 0;
        for (int t = 0; t < _symbols.Length; t++)
        {
            if ((done & (1 << t)) == 0)
            {
                continue;
            }

            ref Node prev = ref _tried[t];
            if (prev.Mover == NoMover || prev.Mover == child.Mover)
            {
                continue;
            }

            bool disjoint = child.X1 < prev.X0 || child.X0 > prev.X1
                            || child.Y1 < prev.Y0 || child.Y0 > prev.Y1;
            if (disjoint)
            {
                sleep |= (byte)(1 << t);
            }
        }

        return sleep;
    }

    private void RecordTried(int symbolIndex, in Node child) => _tried[symbolIndex] = child;

    /// <summary>
    /// The pairwise rule: whether this successor is a redundant ordering of two
    /// independent moves, judged against the move that produced its parent.
    ///
    /// <para>Both moves are simple, so each one's entire effect is "one fish is
    /// somewhere else", and their occupied-cell boxes are disjoint - so neither
    /// changes any cell the other reads or writes, both orders are applicable, and
    /// both end in the same state at the same cost. Only the ordering matching the
    /// canonical symbol rank is kept.</para>
    ///
    /// <para>It cannot prune away every optimal solution: while some adjacent pair
    /// in a solution would be suppressed, swap it - legal, same length, same final
    /// state, and it strictly reduces rank inversions, which cannot go below zero.
    /// The rewriting terminates at an equally short surviving solution. Only
    /// <i>independent</i> pairs are ever swapped, so successive moves of the same
    /// fish keep their order.</para>
    ///
    /// <para>Unlike the sleep sets, this runs <b>after</b> the move is simulated,
    /// which is why it saves so little - see solver/docs/011.</para>
    /// </summary>
    private bool IsRedundantOrdering(in Node parent, in Node child, char symbol)
    {
        if (parent.Mover == NoMover || child.Mover == NoMover || parent.Mover == child.Mover)
        {
            return false;
        }

        if (_symbolRank[symbol] >= _symbolRank[parent.Move])
        {
            return false;
        }

        return child.X1 < parent.X0 || child.X0 > parent.X1
               || child.Y1 < parent.Y0 || child.Y0 > parent.Y1;
    }

    /// <summary>
    /// Whether the current state can be discarded outright, and its heuristic if
    /// not.
    ///
    /// <list type="bullet">
    /// <item><b>A goal has failed for good.</b> <c>IsSolvable()</c> - a fish
    ///   died, and both fish goals require alive.</item>
    /// <item><b>Nothing can move any more.</b> <c>CannotMove()</c> with the level
    ///   unsolved: every fish is dead or already out, so nothing further can
    ///   happen.</item>
    /// <item><b>Something that must leave the room no longer can.</b> The
    ///   relaxed distance is infinite - and that relaxation has every movable
    ///   item deleted, so if there is no way out even then, there is genuinely
    ///   none. This is the "an item got dropped somewhere it can never be
    ///   recovered from" case: a fish sealed into a pocket, or a goal_out item
    ///   pushed into a well it can never come out of.</item>
    /// </list>
    ///
    /// <para><b>These earn far less than they look like they should.</b> Measured
    /// over nine levels (solver/docs/011), <c>CannotMove()</c> and the unreachable
    /// test each fired <b>zero</b> times, and <c>IsSolvable()</c> caught 0.5-2.1%
    /// of everything generated; the duplicate check does 62-80%. They cost
    /// single-digit nanoseconds, so they stay - but this is not where the search
    /// gets its pruning, whatever the class comment used to claim.</para>
    /// </summary>
    private bool IsDeadEnd(out int h)
    {
        h = 0;
        if (!_room.IsSolvable() || _room.CannotMove())
        {
            return true;
        }

        h = _heuristic.Estimate(_room);
        return h >= RelaxedDistance.Unreachable;
    }

    /// <summary>
    /// Emits a progress line if enough time has passed.
    ///
    /// The clock is only read every few thousand expansions - a timestamp costs
    /// a system call, and at over a million expansions a second, checking on
    /// every one of them is a measurable tax for something the user reads a few
    /// times a minute.
    /// </summary>
    private void ReportProgress(
        SolveOptions options, Stopwatch stopwatch, long expanded, int f, ref double nextReport)
    {
        if (options.Progress is null || (expanded & 0xFFF) != 0)
        {
            return;
        }

        double elapsed = stopwatch.Elapsed.TotalSeconds;
        if (elapsed < nextReport)
        {
            return;
        }

        nextReport = elapsed + options.ProgressSeconds;
        options.Progress(
            $"  {elapsed,6:F0}s  f={f,-4} expanded {expanded,12:N0}  stored {_store.Count,12:N0}  " +
            $"{expanded / Math.Max(elapsed, 0.001) / 1000,7:F0}k/s  {GC.GetTotalMemory(false) / (1024.0 * 1024),6:F0} MB");
    }

    /// <summary>
    /// Open-list priority. At weight 1.0 this is plain f = g + h and the first
    /// solution popped is optimal; above 1.0 the heuristic is inflated, which
    /// drives the search at the goal far harder but gives up the guarantee.
    /// </summary>
    private int Priority(int g, int h) =>
        _weight <= 1.0 ? g + h : g + (int)Math.Round(_weight * h);

    private string Reconstruct(int index)
    {
        var path = new List<char>();
        for (int i = index; _store.Parent(i) >= 0; i = _store.Parent(i))
        {
            path.Add(_symbols[_store.Move(i)]);
        }

        path.Reverse();
        return new string(path.ToArray());
    }

    private void EnsurePo(int index)
    {
        if (index >= _po.Length)
        {
            Array.Resize(ref _po, Math.Max(index + 1, Math.Max(1024, _po.Length * 2)));
        }
    }
}
