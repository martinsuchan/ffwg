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
    /// Whether to drop states in which a fish provably can no longer reach any
    /// border, once everything <see cref="StuckAnalysis"/> proves immobile is
    /// counted as wall. Sound - it only ever discards states that genuinely have
    /// no solution - so it costs nothing but time, and what it saves varies a lot
    /// by level (solver/docs/012, docs/014).
    /// </summary>
    public bool DetectStuck { get; init; }
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
public sealed class Solver
{
    private readonly LevelReduction _reduction;
    private readonly Room _room;
    private readonly int[] _mobile;
    private readonly char[] _symbols;
    private readonly byte[] _key;
    private readonly StateSet _visited;

    private readonly ExitHeuristic _heuristic;

    // Everything derived from where the ITEMS are. A fish move leaves those
    // alone - which is nearly every edge - so these are cached on the item half
    // of the state key rather than rebuilt per state. Capped and dropped
    // wholesale when full: an unbounded cache outgrows the state table it exists
    // to help.
    private readonly Dictionary<ItemLayout, LayoutInfo> _layouts = [];
    private readonly int[] _itemSlots;

    // One-entry front for that dictionary: the arrangement most recently looked
    // up, so an unchanged one costs a byte compare instead of an allocation and
    // a hash.
    private readonly byte[] _lastLayoutBytes;
    private LayoutInfo? _lastLayout;

    private int _workPenalty;
    private bool _detectStuck;

    private const int LayoutCacheLimit = 262144;

    /// <summary>What one item arrangement implies, worked out once and reused.</summary>
    private sealed class LayoutInfo
    {
        /// <summary>The guiding estimate, when a toll is in use.</summary>
        public WorkHeuristic? Work;

        /// <summary>
        /// Per unit, the anchors from which it could still get out of the room
        /// once everything provably stuck is treated as wall. Null until asked
        /// for; a unit's entry is null when it has already left.
        /// </summary>
        public bool[]?[]? CanLeave;
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

    private Node[] _nodes = new Node[1 << 16];
    private int _nodeCount;
    private List<int>[] _open = [];

    /// <summary>How far into each bucket the pop cursor has got.</summary>
    private int[] _drained = [];

    private double _weight = 1.0;

    /// <summary>
    /// What a node's own incoming move disturbed, so the expansion can tell
    /// whether the next move is independent of it. <see cref="Mover"/> is
    /// <see cref="NoMover"/> when the move was not "simple" (see
    /// <c>ClassifySimpleMove</c>), which disables the reduction for that node.
    /// </summary>
    private struct Node
    {
        public int Parent;
        public int KeyOffset;
        public int G;
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
    /// The item half of a state key - everything except where the fish are.
    /// Two states sharing one share a <see cref="WorkHeuristic"/>.
    /// </summary>
    private readonly struct ItemLayout : IEquatable<ItemLayout>
    {
        private readonly byte[] _bytes;
        private readonly int _hash;

        public ItemLayout(ReadOnlySpan<byte> key, int[] slots)
        {
            _bytes = new byte[slots.Length * 5];
            for (int i = 0; i < slots.Length; i++)
            {
                key.Slice(slots[i] * 5, 5).CopyTo(_bytes.AsSpan(i * 5));
            }

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
        _visited = new StateSet(_reduction.StateKeySize, 1 << 16);

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
        _lastLayoutBytes = new byte[_itemSlots.Length * 5];

        _unitModel = [.. level.Units.Select(u => u.Model)];
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

    public SolveResult Solve(SolveOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        _weight = options.Weight;
        _mode = options.PartialOrder;
        _workPenalty = options.WorkPenalty;
        _detectStuck = options.DetectStuck;
        _layouts.Clear();
        _lastLayout = null;
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

        if (_detectStuck && NoWayOut())
        {
            return new SolveResult(
                null, false, "unsolvable: a fish is already walled in", 0, 0, 0, 0, stopwatch.Elapsed);
        }

        _visited.TryImprove(_key, 0, out int startKey);
        int startF = Priority(0, Guide(startH));
        EnsureOpen(startF);
        Push(NewNode(-1, startKey, 0, '\0'), startF);

        long expanded = 0, generated = 1;
        int deepestF = 0;
        double nextReport = options.ProgressSeconds;

        // Buckets used to be drained in one forward sweep, which is only valid
        // while f never falls along an edge. At weight 1 it cannot: h is
        // consistent, so f is monotone. Above 1 it can - an edge shifts f by
        // 1 - weight*(drop in h), which is negative as soon as weight exceeds 1,
        // so f drops by one on every move that makes progress. The sweep then
        // wrote children into buckets it had already passed and cleared, losing
        // them silently: `solve society --weight 2` reported "no solution exists"
        // after 350 expansions, on a level with a bundled 110-move solution.
        //
        // So this pops the lowest-f node outright rather than sweeping. Each
        // bucket keeps a drain cursor, and a push below the cursor pulls it back.
        // At weight 1 nothing can push below it, the cursor walks each bucket
        // front to back exactly as the old index loop did, and the search is
        // unchanged - which keeps docs/011's measurements valid.
        int lowestReopened = int.MaxValue;

        int f = 0;
        while (true)
        {
            while (f < _open.Length && _drained[f] >= _open[f].Count)
            {
                // Nothing will be popped from it again unless something is pushed
                // into it, so give the storage back - on a long search these add
                // up. Resetting the cursor keeps that push correct.
                _open[f].Clear();
                _open[f].TrimExcess();
                _drained[f] = 0;
                f++;
            }

            if (f >= _open.Length)
            {
                break;
            }

            {
                int nodeIndex = _open[f][_drained[f]++];
                int g = _nodes[nodeIndex].G;

                // Every state with a smaller f has been expanded, so this is a
                // proven lower bound on the answer - but only at weight 1, where
                // f is a real lower bound on the cost through the node. Above 1
                // it is an inflated priority and means nothing, so it is not
                // reported as a bound (see Abandoned).
                deepestF = Math.Max(deepestF, f);

                _room.RestoreMobile(_visited.KeyAt(_nodes[nodeIndex].KeyOffset), _mobile);

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
                        _visited.Count,
                        ProvenBound(f),
                        stopwatch.Elapsed);
                }

                expanded++;

                byte sleep = _mode == PartialOrderMode.SleepSets ? _nodes[nodeIndex].Sleep : (byte)0;
                byte done = 0;
                bool restoreNeeded = false;
                _room.SaveMobile(_undo, _mobile);

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
                    if (_detectStuck && h > 0 && NoWayOut())
                    {
                        continue;
                    }

                    int guide = Guide(h);
                    if (guide >= WorkHeuristic.Unreachable)
                    {
                        continue;
                    }

                    int childF = Priority(g + 1, guide);

                    var child = new Node { Mover = NoMover, Move = symbol };
                    if (_mode != PartialOrderMode.Off)
                    {
                        ReadOnlySpan<byte> parentKey = _visited.KeyAt(_nodes[nodeIndex].KeyOffset);
                        ClassifySimpleMove(parentKey, _key, symbol, ref child);

                        if (_mode == PartialOrderMode.Pairwise)
                        {
                            if (IsRedundantOrdering(in _nodes[nodeIndex], in child, symbol))
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

                    if (!_visited.TryImprove(_key, g + 1, child.Sleep, out int keyOffset, out byte expandSleep))
                    {
                        continue;
                    }

                    child.Parent = nodeIndex;
                    child.KeyOffset = keyOffset;
                    child.G = g + 1;
                    child.Sleep = expandSleep;

                    EnsureOpen(childF);
                    Push(NewNode(in child), childF);
                    if (childF < f)
                    {
                        lowestReopened = Math.Min(lowestReopened, childF);
                    }
                }

                ReportProgress(options, stopwatch, expanded, f, ref nextReport);

                if (expanded >= options.MaxNodes)
                {
                    return Abandoned("node limit reached", expanded, generated, deepestF, stopwatch);
                }

                if (options.TimeLimit is { } limit && stopwatch.Elapsed > limit)
                {
                    return Abandoned("time limit reached", expanded, generated, deepestF, stopwatch);
                }

                if (lowestReopened < f)
                {
                    f = lowestReopened;
                    lowestReopened = int.MaxValue;
                }
            }
        }

        return new SolveResult(
            null, options.Weight <= 1.0 && options.WorkPenalty <= 0, "exhausted: no solution exists", expanded, generated,
            _visited.Count, ProvenBound(deepestF), stopwatch.Elapsed);
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
        return info.Work.Estimate(_room);
    }

    /// <summary>
    /// Whether some fish that still has to get out no longer can - the state has
    /// no solution and every descendant of it is wasted work.
    ///
    /// <para>Everything <see cref="StuckAnalysis"/> proves immobile is scenery
    /// from here on, so its cells count as wall exactly, not optimistically. If
    /// the fish cannot reach any border through what is left, nothing that
    /// happens next can change that: the items that could still move are already
    /// treated as absent by the reachability, and the ones treated as wall are
    /// never going anywhere.</para>
    ///
    /// <para>The classic case is an item dropped down a shaft it can never come
    /// back out of. In <c>society</c>, opening with the big fish going right puts
    /// the heavy bar into a one-wide well on top of an item that is itself walled
    /// in on three sides; neither can move again, and the big fish - four cells
    /// wide - is left with no route to the exit. Nothing died and no goal became
    /// unreachable in the walls-only relaxation, so every other test in
    /// <see cref="IsDeadEnd"/> passes it and the search explores the whole dead
    /// subtree.</para>
    ///
    /// <para><c>_key</c> must already hold the current state.</para>
    /// </summary>
    private bool NoWayOut()
    {
        LayoutInfo info = LayoutFor();
        if (info.CanLeave is null)
        {
            // fishAnywhere: the result is cached per item arrangement, so it must
            // not depend on where the fish happen to be standing. Measuring their
            // reach from their actual squares made the analysis state-specific,
            // and two states sharing an arrangement then shared an answer that was
            // only correct for whichever of them computed it first.
            bool[] mobile = StuckAnalysis.Mobile(_room.Level, _room, fishAnywhere: true);
            bool[] solid = StuckAnalysis.SolidCells(_room.Level, _room, mobile);

            // A model frozen at the opening position is frozen in every state
            // reachable from it, so the two solid sets can be unioned. They are
            // not otherwise comparable: an item resting on a fish is mobile here
            // and was not at the start.
            bool[] walls = _reduction.Walls;
            for (int c = 0; c < solid.Length; c++)
            {
                solid[c] |= walls[c];
            }

            var canLeave = new bool[]?[_unitModel.Length];
            for (int u = 0; u < _unitModel.Length; u++)
            {
                canLeave[u] = StuckAnalysis.CanStillLeave(_room.Level, _room, solid, _unitModel[u]);
            }

            info.CanLeave = canLeave;
        }

        for (int u = 0; u < _unitModel.Length; u++)
        {
            ref readonly ModelState s = ref _room.State(_unitModel[u]);
            if (s.IsOut || !s.IsAlive || info.CanLeave[u] is not bool[] reach)
            {
                continue;
            }

            if (!reach[(s.Y * _room.Level.Width) + s.X])
            {
                return true;
            }
        }

        return false;
    }

    private LayoutInfo LayoutFor()
    {
        // Most moves are a fish swimming, which leaves the items exactly where
        // they were - so before building a key and hashing it, check against the
        // last one. Siblings of an expansion nearly all share their parent's
        // arrangement, and so does the next expansion more often than not.
        if (_lastLayout is not null && ItemsUnchanged())
        {
            return _lastLayout;
        }

        var layout = new ItemLayout(_key, _itemSlots);
        if (!_layouts.TryGetValue(layout, out LayoutInfo? info))
        {
            if (_layouts.Count >= LayoutCacheLimit)
            {
                _layouts.Clear();
            }

            info = new LayoutInfo();
            _layouts[layout] = info;
        }

        for (int i = 0; i < _itemSlots.Length; i++)
        {
            _key.AsSpan(_itemSlots[i] * 5, 5).CopyTo(_lastLayoutBytes.AsSpan(i * 5));
        }

        _lastLayout = info;
        return info;
    }

    private bool ItemsUnchanged()
    {
        for (int i = 0; i < _itemSlots.Length; i++)
        {
            if (!_key.AsSpan(_itemSlots[i] * 5, 5).SequenceEqual(_lastLayoutBytes.AsSpan(i * 5, 5)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The f the sweep has reached, as a claim about the answer - which it only
    /// is when f is a true lower bound on the cost through a node. A weight above
    /// 1 inflates it and a toll overestimates outright, so in either case it is
    /// not reported, rather than recorded as this level's proven bound.
    /// </summary>
    private int ProvenBound(int deepestF) => _weight <= 1.0 && _workPenalty <= 0 ? deepestF : 0;

    private SolveResult Abandoned(string status, long expanded, long generated, int deepestF, Stopwatch stopwatch) =>
        new(null, false, status, expanded, generated, _visited.Count, ProvenBound(deepestF), stopwatch.Elapsed);

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
            $"  {elapsed,6:F0}s  f={f,-4} expanded {expanded,12:N0}  stored {_visited.Count,12:N0}  " +
            $"{expanded / Math.Max(elapsed, 0.001) / 1000,7:F0}k/s  {GC.GetTotalMemory(false) / (1024.0 * 1024),6:F0} MB");
    }

    /// <summary>
    /// Open-list priority. At weight 1.0 this is plain f = g + h and the first
    /// solution popped is optimal; above 1.0 the heuristic is inflated, which
    /// drives the search at the goal far harder but gives up the guarantee.
    /// </summary>
    private int Priority(int g, int h) =>
        _weight <= 1.0 ? g + h : g + (int)Math.Round(_weight * h);

    private string Reconstruct(int nodeIndex)
    {
        var path = new List<char>();
        for (int i = nodeIndex; _nodes[i].Parent >= 0; i = _nodes[i].Parent)
        {
            path.Add(_nodes[i].Move);
        }

        path.Reverse();
        return new string(path.ToArray());
    }

    private void EnsureOpen(int f)
    {
        if (f < _open.Length)
        {
            return;
        }

        int size = Math.Max(f + 1, Math.Max(64, _open.Length * 2));
        var grown = new List<int>[size];
        Array.Copy(_open, grown, _open.Length);
        for (int i = _open.Length; i < size; i++)
        {
            grown[i] = [];
        }

        _open = grown;
        Array.Resize(ref _drained, size);
    }

    private int NewNode(int parent, int keyOffset, int g, char move) =>
        NewNode(new Node { Parent = parent, KeyOffset = keyOffset, G = g, Move = move, Mover = NoMover });

    private int NewNode(in Node node)
    {
        if (_nodeCount == _nodes.Length)
        {
            Array.Resize(ref _nodes, _nodes.Length * 2);
        }

        _nodes[_nodeCount] = node;
        return _nodeCount++;
    }

    private void Push(int nodeIndex, int f) => _open[f].Add(nodeIndex);
}
