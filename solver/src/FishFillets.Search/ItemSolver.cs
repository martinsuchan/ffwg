using System.Diagnostics;

using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// Searches over <b>item moves</b>: a state is only ever recorded when something
/// other than a fish has changed, or a fish has left the room.
///
/// <para>The observation this rests on is that a solution contains very few real
/// events. <c>wc</c>'s optimum is 100 symbols, of which <b>13</b> move an item; the
/// other 87 are fish swimming into position. It has three movable items in a 23x22
/// room, so the space of item configurations is tiny - while the macro search,
/// which also stores where each fish parked, holds 65,000-200,000 states on that
/// level.</para>
///
/// <para><b>Fish positions stay in the key</b>, because walking costs moves here
/// and the price of the next edge depends on where the fish are - Sokoban's trick
/// of normalising the player away needs a free player (docs/005). What changes is
/// that the only fish positions ever stored are "wherever a push left them",
/// instead of every square a fish might usefully wait on.</para>
///
/// <para><b>Getting out of the way stops being a state and becomes part of an
/// edge.</b> That is the point of the design: <see cref="MacroSolver"/> needs a
/// rule for guessing where a fish might usefully park, and every version of that
/// rule measured in docs/008 was either incomplete or ruinously expensive. Here a
/// fish only ever moves because some item needs it to, and a blocking fish is
/// moved aside as part of computing the cost - so there is no parking rule at
/// all.</para>
///
/// <para><b>What it does not promise.</b> When a fish has to clear the way there
/// can be several equally short ways to do it that leave it in different places,
/// and those have different future costs. Every distinct destination among the
/// cheapest is generated, so the choice is A*'s rather than arbitrary - but the
/// number tried is capped (<see cref="ClearanceTries"/>), and beyond that cap the
/// search can miss a cheaper continuation. Answers are always replayed before
/// being reported; they are not proofs.</para>
/// </summary>
public sealed class ItemSolver
{
    /// <summary>
    /// How many resting places to try for a fish that has to clear the way,
    /// cheapest first. Each one costs a route for the fish that wanted to travel.
    /// </summary>
    private const int ClearanceTries = 4;

    /// <summary>Ways the room can be arranged before a fish travels: as it is, or with one other fish moved aside.</summary>
    private struct Variant
    {
        public byte BlockerUnit;        // 0xFF for "nobody moved"
        public int BlockerPlacement;
        public int BlockerCost;
    }

    private readonly Level _level;
    private readonly LevelReduction _reduction;
    private readonly Room _room;
    private readonly ExitHeuristic _heuristic;
    private readonly int[] _mobile;
    private readonly int[] _items;
    private readonly byte[] _key;
    private readonly byte[] _moverKey;
    private readonly byte[] _childKey;
    private readonly List<(int Placement, int Cost)> _destinations = [];
    private readonly StateSet _visited;
    private readonly int[] _keySlotOfModel;

    // Two routers, because both sets of results are live at once: the other
    // fish's own reach (which produces the ways it can step aside) and the
    // traveller's reach under each of those arrangements.
    private readonly InertRouter _reach;
    private readonly InertRouter _blockerReach;

    private readonly Variant[] _variants;
    private int _variantCount;
    private readonly int _placementCount;
    private readonly int[] _routeMark;
    private int _routeGeneration;
    private readonly (int X, int Y)[] _escapes;

    private readonly (int X, int Y)[] _itemsBefore;
    private readonly bool[] _fishGoneBefore;

    private Node[] _nodes = new Node[1 << 16];
    private int _nodeCount;
    private List<int>[] _open = [];
    private double _weight = 1.0;
    private EdgeObserver? _observer;

    /// <summary>Why an edge that the expansion produced did or did not become a state.</summary>
    internal enum EdgeOutcome
    {
        /// <summary>Recorded as a new state, or as a cheaper route to a known one.</summary>
        Stored,

        /// <summary>Nothing but a fish moved, so there is no state to record.</summary>
        NoEvent,

        /// <summary>Some goal became unreachable, or a fish died.</summary>
        Unsolvable,

        /// <summary>Neither fish can move again.</summary>
        CannotMove,

        /// <summary>The heuristic says a goal can no longer be reached.</summary>
        HeuristicDead,

        /// <summary>Already known at this cost or cheaper.</summary>
        NotImproved,
    }

    /// <summary>Called for every edge the expansion produces, whatever became of it.</summary>
    internal delegate void EdgeObserver(ReadOnlySpan<byte> childKey, int cost, EdgeOutcome outcome);

    /// <summary>
    /// Runs one expansion of <paramref name="stateKey"/> and reports every edge it
    /// produces. This exists so a probe can ask "does the expansion generate this
    /// particular successor, and if not, what stopped it" against the real code
    /// path rather than a re-implementation of it. Use a fresh solver per call:
    /// the seeded state is the only thing in the visited set.
    /// </summary>
    internal void ProbeSuccessors(ReadOnlySpan<byte> stateKey, EdgeObserver observer)
    {
        _room.Reset();
        _room.RestoreMobile(stateKey, _mobile);
        _visited.TryImprove(stateKey, 0, out int keyOffset);

        int node = NewNode(-1, keyOffset, 0, 0xFF, 0, 0, 0, '\0');
        long generated = 0;

        _observer = observer;
        try
        {
            ExpandItemMoves(node, 0, ref generated);
        }
        finally
        {
            _observer = null;
        }
    }

    private void Observe(EdgeOutcome outcome, int cost)
    {
        if (_observer is null)
        {
            return;
        }

        _room.WriteStateKey(_childKey, _mobile);
        _observer(_childKey, cost, outcome);
    }

    private struct Node
    {
        public int Parent;
        public int KeyOffset;
        public int G;
        public byte MoverUnit;
        public int MoverPlacement;
        public byte ActorUnit;
        public int ActorPlacement;
        public char Action;
    }

    public ItemSolver(Level level, LevelReduction? reduction = null)
    {
        _level = level;
        _reduction = reduction ?? LevelReduction.Compute(level);
        _room = new Room(level);
        _heuristic = new ExitHeuristic(level, _reduction);
        _mobile = _reduction.MobileModels;
        _key = new byte[_reduction.StateKeySize];
        _moverKey = new byte[_reduction.StateKeySize];
        _childKey = new byte[_reduction.StateKeySize];
        _visited = new StateSet(_reduction.StateKeySize, 1 << 16);

        _reach = new InertRouter(level, _reduction);
        _blockerReach = new InertRouter(level, _reduction);

        _keySlotOfModel = new int[level.Models.Length];
        Array.Fill(_keySlotOfModel, -1);
        for (int i = 0; i < _mobile.Length; i++)
        {
            _keySlotOfModel[_mobile[i]] = i;
        }

        // The models whose movement makes a state worth recording: everything
        // mobile that is not a fish.
        _items = [.. _mobile.Where(m => !level.Models[m].IsAlive)];
        _itemsBefore = new (int, int)[_items.Length];
        _fishGoneBefore = new bool[level.Units.Length];

        _placementCount = level.Width * level.Height * 2;

        // One arrangement where nobody moved, plus a capped number of ways each
        // other fish could step aside.
        _variants = new Variant[1 + (ClearanceTries * Math.Max(1, level.Units.Length - 1))];
        _routeMark = new int[_placementCount];
        _escapes = FindEscapes(level, _reduction);
    }

    public LevelReduction Reduction => _reduction;

    public SolveResult Solve(SolveOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        _weight = options.Weight;
        _room.Reset();

        if (_room.IsSolved())
        {
            return new SolveResult("", true, "already solved", 0, 0, 0, 0, stopwatch.Elapsed);
        }

        _room.WriteStateKey(_key, _mobile);
        int startH = _heuristic.Estimate(_room);
        if (startH >= RelaxedDistance.Unreachable)
        {
            return new SolveResult(
                null, false, "unsolvable: a goal is unreachable from the start", 0, 0, 0, 0, stopwatch.Elapsed);
        }

        _visited.TryImprove(_key, 0, out int startKey);
        int startF = Priority(0, startH);
        EnsureOpen(startF);
        Push(NewNode(-1, startKey, 0, 0xFF, 0, 0, 0, '\0'), startF);

        long expanded = 0, generated = 1;
        int deepestF = 0;
        double nextReport = options.ProgressSeconds;

        for (int f = 0; f < _open.Length; f++)
        {
            List<int> bucket = _open[f];
            for (int b = 0; b < bucket.Count; b++)
            {
                int nodeIndex = bucket[b];
                int g = _nodes[nodeIndex].G;
                deepestF = f;

                _room.RestoreMobile(_visited.KeyAt(_nodes[nodeIndex].KeyOffset), _mobile);

                // Goal on pop, as in Solver - a state can have h == 0 without
                // being solved, so testing on generation can return a finish that
                // is one move too long.
                if (_room.IsSolved())
                {
                    return new SolveResult(
                        Reconstruct(nodeIndex), false, "solved",
                        expanded, generated, _visited.Count, f, stopwatch.Elapsed);
                }

                expanded++;
                ExpandItemMoves(nodeIndex, g, ref generated);

                if (options.Progress is not null && (expanded & 0xFF) == 0)
                {
                    double elapsed = stopwatch.Elapsed.TotalSeconds;
                    if (elapsed >= nextReport)
                    {
                        nextReport = elapsed + options.ProgressSeconds;
                        options.Progress(
                            $"  {elapsed,6:F0}s  f={f,-4} expanded {expanded,12:N0}  " +
                            $"stored {_visited.Count,12:N0}");
                    }
                }

                if (expanded >= options.MaxNodes)
                {
                    return Abandoned("node limit reached", expanded, generated, deepestF, stopwatch);
                }

                if (options.TimeLimit is { } limit && stopwatch.Elapsed > limit)
                {
                    return Abandoned("time limit reached", expanded, generated, deepestF, stopwatch);
                }
            }

            bucket.Clear();
            bucket.TrimExcess();
        }

        return new SolveResult(
            null, false, "exhausted: no item move leads anywhere new", expanded, generated,
            _visited.Count, deepestF, stopwatch.Elapsed);
    }

    /// <summary>
    /// left, right, up - the three ways a fish can push. Down is never a push: an
    /// item below a fish in a settled room is supported, or it would already have
    /// fallen. Downward movement is a carry or a drop instead.
    /// </summary>
    private static readonly (int Dx, int Dy)[] PushDirections = [(-1, 0), (1, 0), (0, -1)];

    private static readonly (int Dx, int Dy)[] AllDirections = [(-1, 0), (1, 0), (0, -1), (0, 1)];

    private void ExpandItemMoves(int nodeIndex, int g, ref long generated)
    {
        int parentKeyOffset = _nodes[nodeIndex].KeyOffset;
        _room.RestoreMobile(_visited.KeyAt(parentKeyOffset), _mobile);

        for (int i = 0; i < _items.Length; i++)
        {
            (int x, int y, _) = _room.Position(_items[i]);
            _itemsBefore[i] = (x, y);
        }

        for (int i = 0; i < _level.Units.Length; i++)
        {
            _fishGoneBefore[i] = HasGone(_level.Units[i].Model);
        }

        for (byte u = 0; u < _level.Units.Length; u++)
        {
            int fish = _level.Units[u].Model;

            _room.RestoreMobile(_visited.KeyAt(parentKeyOffset), _mobile);
            ref readonly ModelState state = ref _room.State(fish);
            if (!state.IsAlive || state.IsLost || state.Busy)
            {
                continue;
            }

            BuildArrangements(parentKeyOffset, fish);

            for (int a = 0; a < _variantCount; a++)
            {
                RestoreArrangement(parentKeyOffset, _variants[a]);
                _reach.Route(_room, fish);

                _routeGeneration++;
                foreach (int placement in _reach.Reached)
                {
                    _routeMark[placement] = _routeGeneration;
                }

                ExpandUnder(nodeIndex, g, parentKeyOffset, u, fish, a, ref generated);
            }
        }
    }

    /// <summary>Every item move this fish can make with the room arranged this way.</summary>
    private void ExpandUnder(
        int nodeIndex, int g, int parentKeyOffset, byte unitIndex, int fish, int arrangement, ref long generated)
    {
        UnitDef unit = _level.Units[unitIndex];
        Shape fishShape = _level.Models[fish].Shape;

        foreach (int item in _items)
        {
            (int ix, int iy, _) = _room.Position(item);
            Shape itemShape = _level.Models[item].Shape;

            foreach ((int dx, int dy) in PushDirections)
            {
                if (!CanPossiblyMove(item, ix, iy, dx, dy))
                {
                    continue;
                }

                char symbol = dx < 0 ? unit.Left : dx > 0 ? unit.Right : unit.Up;

                // Where the fish has to stand to make that push: one of its own
                // cells against one of the item's, on the far side of the push.
                for (int c = 0; c < itemShape.MarkX.Length; c++)
                {
                    for (int m = 0; m < fishShape.MarkX.Length; m++)
                    {
                        int ax = ix + itemShape.MarkX[c] - dx - fishShape.MarkX[m];
                        int ay = iy + itemShape.MarkY[c] - dy - fishShape.MarkY[m];

                        // Horizontal pushes only happen facing that way; against
                        // your facing the key press is a turn, which the route has
                        // already paid for.
                        if (dx != 0)
                        {
                            TryEdge(nodeIndex, g, parentKeyOffset, unitIndex, fish, arrangement, ax, ay, dx < 0, symbol, ref generated);
                        }
                        else
                        {
                            TryEdge(nodeIndex, g, parentKeyOffset, unitIndex, fish, arrangement, ax, ay, true, symbol, ref generated);
                            TryEdge(nodeIndex, g, parentKeyOffset, unitIndex, fish, arrangement, ax, ay, false, symbol, ref generated);
                        }
                    }
                }
            }
        }

        // Carries and drops: a fish with something resting on it sends that thing
        // somewhere by moving at all. Its own square is the only one that works,
        // so there is nothing to route to.
        (int fx, int fy, bool fLeft) = _room.Position(fish);
        if (_reach.IsHoldingSomethingUp(_room, fish, fx, fy))
        {
            TryEdge(nodeIndex, g, parentKeyOffset, unitIndex, fish, arrangement, fx, fy, fLeft, unit.Down, ref generated);
            TryEdge(
                nodeIndex, g, parentKeyOffset, unitIndex, fish, arrangement, fx, fy, fLeft,
                fLeft ? unit.Left : unit.Right, ref generated);
        }

        // Leaving the room. Where a fish can escape from depends only on the
        // walls, so the spots are enumerated once for the level; a fish reaches
        // the square beside one and steps in.
        if (_level.Models[fish].Goal is GoalKind.Escape or GoalKind.Out)
        {
            foreach ((int ex, int ey) in _escapes)
            {
                foreach ((int dx, int dy) in AllDirections)
                {
                    char symbol = dx < 0 ? unit.Left : dx > 0 ? unit.Right : dy < 0 ? unit.Up : unit.Down;
                    int ax = ex - dx, ay = ey - dy;

                    if (dx != 0)
                    {
                        TryEdge(nodeIndex, g, parentKeyOffset, unitIndex, fish, arrangement, ax, ay, dx < 0, symbol, ref generated);
                    }
                    else
                    {
                        TryEdge(nodeIndex, g, parentKeyOffset, unitIndex, fish, arrangement, ax, ay, true, symbol, ref generated);
                        TryEdge(nodeIndex, g, parentKeyOffset, unitIndex, fish, arrangement, ax, ay, false, symbol, ref generated);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The cheap "with both fish removed" test: could this item go that way at
    /// all, ignoring who is standing where?
    ///
    /// <para>Only permanent obstruction counts - wall, or a model the reduction
    /// has proven immobile. A fish in the way is not a reason to give up, it is a
    /// reason to move it, and that is what the arrangements are for.</para>
    /// </summary>
    private bool CanPossiblyMove(int item, int ix, int iy, int dx, int dy)
    {
        Shape shape = _level.Models[item].Shape;

        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            int cx = ix + shape.MarkX[m] + dx, cy = iy + shape.MarkY[m] + dy;
            if ((uint)cx >= (uint)_level.Width || (uint)cy >= (uint)_level.Height)
            {
                continue; // leaving the room is the engine's business, not a wall
            }

            if (_reduction.Walls[(cy * _level.Width) + cx])
            {
                return false;
            }

            int other = _room.GetModel(cx, cy);
            if (other == Room.Empty || other == item)
            {
                continue;
            }

            if (other >= _level.ModelCount || _reduction.IsFrozen(other))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Routes the fish to one placement and presses one key, keeping the result
    /// if the world changed.
    /// </summary>
    private void TryEdge(
        int nodeIndex, int g, int parentKeyOffset, byte unitIndex, int fish, int arrangement,
        int x, int y, bool isLeft, char symbol, ref long generated)
    {
        if ((uint)x >= (uint)_level.Width || (uint)y >= (uint)_level.Height)
        {
            return;
        }

        int placement = _reach.Placement(x, y, isLeft);
        if (_routeMark[placement] != _routeGeneration)
        {
            return; // the fish cannot get there under this arrangement
        }

        Variant variant = _variants[arrangement];
        int cost = g + variant.BlockerCost + _reach.CostOf(placement);

        BuildTravelKey(parentKeyOffset, variant, placement, fish);
        _room.RestoreMobile(_key, _mobile);

        if (!_room.ApplyMove(symbol))
        {
            return;
        }

        generated++;

        if (!SomethingHappened())
        {
            Observe(EdgeOutcome.NoEvent, cost + 1);
            return;
        }

        int h;
        if (_room.IsSolved())
        {
            h = 0;
        }
        else if (!_room.IsSolvable())
        {
            Observe(EdgeOutcome.Unsolvable, cost + 1);
            return;
        }
        else if (_room.CannotMove())
        {
            Observe(EdgeOutcome.CannotMove, cost + 1);
            return;
        }
        else
        {
            h = _heuristic.Estimate(_room);
            if (h >= RelaxedDistance.Unreachable)
            {
                Observe(EdgeOutcome.HeuristicDead, cost + 1);
                return;
            }
        }

        _room.WriteStateKey(_childKey, _mobile);
        if (!_visited.TryImprove(_childKey, cost + 1, out int keyOffset))
        {
            Observe(EdgeOutcome.NotImproved, cost + 1);
            return;
        }

        Observe(EdgeOutcome.Stored, cost + 1);

        int f = Priority(cost + 1, h);
        EnsureOpen(f);
        Push(
            NewNode(
                nodeIndex, keyOffset, cost + 1, variant.BlockerUnit, variant.BlockerPlacement,
                unitIndex, placement, symbol),
            f);
    }

    /// <summary>
    /// The ways the room can be arranged before this fish travels: as it stands,
    /// or with one other fish moved somewhere else.
    ///
    /// <para>Destinations are deduplicated <b>by square, not by placement</b>. A
    /// turn is a distinct placement costing 1 in the same square, and since the
    /// next edge re-derives facing from wherever the fish ended up at the same
    /// price, keeping both spends half the budget on nothing.</para>
    /// </summary>
    private void BuildArrangements(int parentKeyOffset, int fish)
    {
        _variantCount = 0;
        _variants[_variantCount++] = new Variant { BlockerUnit = 0xFF, BlockerPlacement = 0, BlockerCost = 0 };

        for (byte o = 0; o < _level.Units.Length && _variantCount < _variants.Length; o++)
        {
            int blocker = _level.Units[o].Model;
            if (blocker == fish)
            {
                continue;
            }

            _room.RestoreMobile(_visited.KeyAt(parentKeyOffset), _mobile);
            ref readonly ModelState blockerState = ref _room.State(blocker);
            if (!blockerState.IsAlive || blockerState.IsLost || blockerState.Busy)
            {
                continue;
            }

            _blockerReach.Route(_room, blocker);

            _destinations.Clear();
            _routeGeneration++;
            foreach (int placement in _blockerReach.Reached)
            {
                int cost = _blockerReach.CostOf(placement);
                if (cost == 0)
                {
                    continue;
                }

                (int bx, int by, _) = _blockerReach.Decode(placement);
                int cell = (by * _level.Width) + bx;
                if (_routeMark[cell] == _routeGeneration)
                {
                    continue;
                }

                _routeMark[cell] = _routeGeneration;
                _destinations.Add((placement, cost));
            }

            _destinations.Sort(static (a, b) => a.Cost.CompareTo(b.Cost));

            int tried = 0;
            foreach ((int placement, int cost) in _destinations)
            {
                if (tried++ >= ClearanceTries || _variantCount == _variants.Length)
                {
                    break;
                }

                _variants[_variantCount++] = new Variant
                {
                    BlockerUnit = o,
                    BlockerPlacement = placement,
                    BlockerCost = cost,
                };
            }
        }
    }

    private void RestoreArrangement(int parentKeyOffset, Variant variant)
    {
        ReadOnlySpan<byte> parent = _visited.KeyAt(parentKeyOffset);
        if (variant.BlockerUnit == 0xFF)
        {
            _room.RestoreMobile(parent, _mobile);
            return;
        }

        (int bx, int by, bool bLeft) = _blockerReach.Decode(variant.BlockerPlacement);
        WriteMoved(parent, _moverKey, _level.Units[variant.BlockerUnit].Model, bx, by, bLeft);
        _room.RestoreMobile(_moverKey, _mobile);
    }

    /// <summary>
    /// Squares a fish would be carried out of the room from. These depend only on
    /// the walls, so they are found once per level.
    /// </summary>
    private static (int X, int Y)[] FindEscapes(Level level, LevelReduction reduction)
    {
        var router = new InertRouter(level, reduction);
        var room = new Room(level);
        var spots = new List<(int, int)>();

        foreach (UnitDef unit in level.Units)
        {
            if (level.Models[unit.Model].Goal is not (GoalKind.Escape or GoalKind.Out))
            {
                continue;
            }

            for (int y = 0; y < level.Height; y++)
            {
                for (int x = 0; x < level.Width; x++)
                {
                    if (router.MightEscapeFrom(room, unit.Model, x, y) && !spots.Contains((x, y)))
                    {
                        spots.Add((x, y));
                    }
                }
            }
        }

        return [.. spots];
    }

    /// <summary>
    /// Whether this move changed the world rather than merely where a fish is:
    /// an item moved, or a fish left the room.
    /// </summary>
    private bool SomethingHappened()
    {
        for (int i = 0; i < _items.Length; i++)
        {
            (int x, int y, _) = _room.Position(_items[i]);
            if (x != _itemsBefore[i].X || y != _itemsBefore[i].Y)
            {
                return true;
            }
        }

        // A fish leaving is an event too - and it has to be compared against the
        // parent, not tested outright, or every move after the first escape
        // counts as one. IsOut is the escape; IsLost is removal, which follows
        // it and also covers a corpse being cleared away.
        for (int i = 0; i < _level.Units.Length; i++)
        {
            if (HasGone(_level.Units[i].Model) != _fishGoneBefore[i])
            {
                return true;
            }
        }

        return false;
    }

    private bool HasGone(int model)
    {
        ref readonly ModelState state = ref _room.State(model);
        return state.IsOut || state.IsLost;
    }

    /// <summary>
    /// The parent's key with the blocker moved aside (if it had to be) and the
    /// travelling fish at its destination - the entire effect of inert travel.
    /// </summary>
    private void BuildTravelKey(int parentKeyOffset, Variant variant, int placement, int fish)
    {
        ReadOnlySpan<byte> parent = _visited.KeyAt(parentKeyOffset);

        if (variant.BlockerUnit != 0xFF)
        {
            (int bx, int by, bool bLeft) = _blockerReach.Decode(variant.BlockerPlacement);
            WriteMoved(parent, _key, _level.Units[variant.BlockerUnit].Model, bx, by, bLeft);
            parent = _key;
        }

        (int x, int y, bool isLeft) = _reach.Decode(placement);
        WriteMoved(parent, _key, fish, x, y, isLeft);
    }

    private void WriteMoved(ReadOnlySpan<byte> source, byte[] destination, int model, int x, int y, bool isLeft)
    {
        source.CopyTo(destination);

        int offset = _keySlotOfModel[model] * 5;
        destination[offset] = (byte)x;
        destination[offset + 1] = (byte)(x >> 8);
        destination[offset + 2] = (byte)y;
        destination[offset + 3] = (byte)(y >> 8);
        destination[offset + 4] = (byte)((destination[offset + 4] & ~1) | (isLeft ? 1 : 0));
    }

    /// <summary>
    /// Writes the move string out, recomputing each edge's routes from its parent
    /// state - a handful of BFS runs along the solution path, rather than storing
    /// every route for every node.
    /// </summary>
    private string Reconstruct(int nodeIndex)
    {
        var chain = new List<int>();
        for (int i = nodeIndex; _nodes[i].Parent >= 0; i = _nodes[i].Parent)
        {
            chain.Add(i);
        }

        chain.Reverse();

        var moves = new List<char>();
        foreach (int i in chain)
        {
            Node node = _nodes[i];
            ReadOnlySpan<byte> parent = _visited.KeyAt(_nodes[node.Parent].KeyOffset);
            _room.RestoreMobile(parent, _mobile);

            if (node.MoverUnit != 0xFF)
            {
                int blocker = _level.Units[node.MoverUnit].Model;
                _blockerReach.Route(_room, blocker);
                _blockerReach.WriteRoute(node.MoverPlacement, _level.Units[node.MoverUnit], moves);

                (int bx, int by, bool bLeft) = _blockerReach.Decode(node.MoverPlacement);
                WriteMoved(parent, _moverKey, blocker, bx, by, bLeft);
                _room.RestoreMobile(_moverKey, _mobile);
            }

            int actor = _level.Units[node.ActorUnit].Model;
            _reach.Route(_room, actor);
            _reach.WriteRoute(node.ActorPlacement, _level.Units[node.ActorUnit], moves);
            moves.Add(node.Action);
        }

        return new string(moves.ToArray());
    }

    private SolveResult Abandoned(string status, long expanded, long generated, int deepestF, Stopwatch stopwatch) =>
        new(null, false, status, expanded, generated, _visited.Count, deepestF, stopwatch.Elapsed);

    private int Priority(int g, int h) =>
        _weight <= 1.0 ? g + h : g + (int)Math.Round(_weight * h);

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
    }

    private int NewNode(
        int parent, int keyOffset, int g, byte moverUnit, int moverPlacement,
        byte actorUnit, int actorPlacement, char action)
    {
        if (_nodeCount == _nodes.Length)
        {
            Array.Resize(ref _nodes, _nodes.Length * 2);
        }

        _nodes[_nodeCount] = new Node
        {
            Parent = parent,
            KeyOffset = keyOffset,
            G = g,
            MoverUnit = moverUnit,
            MoverPlacement = moverPlacement,
            ActorUnit = actorUnit,
            ActorPlacement = actorPlacement,
            Action = action,
        };
        return _nodeCount++;
    }

    private void Push(int nodeIndex, int f) => _open[f].Add(nodeIndex);
}
