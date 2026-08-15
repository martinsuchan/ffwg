using System.Diagnostics;

using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// Solves a level by searching over <b>decisions</b> rather than key presses.
///
/// <para>Almost every symbol in a real solution is a fish swimming through open
/// water. The decisions are the pushes, the moments a fish swims out from under
/// something, and where it parks. This search expands a state into
/// <i>macro-moves</i>: an inert route (travel, costed but not branched on)
/// followed by one thing that actually changes the world. On `start` that turns
/// branching 8 at depth 54 into roughly branching 20 at depth 12.</para>
///
/// <para><b>Cost is still counted in symbols</b>, so the answer is directly
/// comparable with the plain <see cref="Solver"/> and with the game's own
/// pedometer. The heuristic is unchanged and still admissible per symbol, so
/// A*'s ordering argument survives: an edge of length k drops h by at most k.</para>
///
/// <para><b>What it does not promise.</b> Plain A* proves no shorter solution
/// exists. This proves no shorter solution exists <i>that the decomposition can
/// express</i> - every inert run has to end at a placement
/// <see cref="InertRouter.IsParking"/> recognises, or at an action. That set is
/// argued rather than proven complete, which is why <see cref="Solver"/> stays as
/// the oracle: the eight levels it has proven optimal are the regression test, and
/// if this returns 56 for `start` the parking set is too narrow.</para>
/// </summary>
public sealed class MacroSolver
{
    private readonly Level _level;
    private readonly LevelReduction _reduction;
    private readonly Room _room;
    private readonly InertRouter _router;
    private readonly ExitHeuristic _heuristic;
    private readonly int[] _mobile;
    private readonly byte[] _key;
    private readonly byte[] _routeKey;
    private readonly StateSet _visited;

    private readonly int[] _keySlotOfModel;
    private int[] _placements = new int[1024];
    private readonly int[] _placementCosts;

    private Node[] _nodes = new Node[1 << 16];
    private int _nodeCount;
    private List<int>[] _open = [];
    private double _weight = 1.0;

    /// <summary>
    /// A macro-move: which fish travelled, where it ended up, and what it did
    /// there ('\0' for a park). The route itself is not stored - it is a shortest
    /// inert path, so it can be recomputed from the parent state when a solution
    /// is finally written out, which keeps nodes small.
    /// </summary>
    private struct Node
    {
        public int Parent;
        public int KeyOffset;
        public int G;
        public byte Fish;
        public int Placement;
        public char Action;
    }

    public MacroSolver(Level level, LevelReduction? reduction = null)
    {
        _level = level;
        _reduction = reduction ?? LevelReduction.Compute(level);
        _room = new Room(level);
        _router = new InertRouter(level, _reduction);
        _heuristic = new ExitHeuristic(level, _reduction);
        _mobile = _reduction.MobileModels;
        _key = new byte[_reduction.StateKeySize];
        _routeKey = new byte[_reduction.StateKeySize];
        _visited = new StateSet(_reduction.StateKeySize, 1 << 16);

        _keySlotOfModel = new int[level.Models.Length];
        Array.Fill(_keySlotOfModel, -1);
        for (int i = 0; i < _mobile.Length; i++)
        {
            _keySlotOfModel[_mobile[i]] = i;
        }

        _placementCosts = new int[level.Width * level.Height * 2];
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
            return new SolveResult(null, false, "unsolvable: a goal is unreachable from the start", 0, 0, 0, 0, stopwatch.Elapsed);
        }

        _visited.TryImprove(_key, 0, out int startKey);
        int startF = Priority(0, startH);
        EnsureOpen(startF);
        Push(NewNode(-1, startKey, 0, 0, 0, '\0'), startF);

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

                // Goal on pop, exactly as in Solver - see its comment for why
                // testing on generation is not equivalent.
                if (_room.IsSolved())
                {
                    return new SolveResult(
                        Reconstruct(nodeIndex), options.Weight <= 1.0, "solved",
                        expanded, generated, _visited.Count, f, stopwatch.Elapsed);
                }

                expanded++;
                ExpandMacros(nodeIndex, g, ref generated);

                if (options.Progress is not null && (expanded & 0xFFF) == 0)
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
            null, options.Weight <= 1.0, "exhausted: no solution exists", expanded, generated,
            _visited.Count, deepestF, stopwatch.Elapsed);
    }

    private void ExpandMacros(int nodeIndex, int g, ref long generated)
    {
        int parentKeyOffset = _nodes[nodeIndex].KeyOffset;

        for (byte u = 0; u < _level.Units.Length; u++)
        {
            UnitDef unit = _level.Units[u];
            int fish = unit.Model;

            _room.RestoreMobile(_visited.KeyAt(parentKeyOffset), _mobile);
            ref readonly ModelState state = ref _room.State(fish);
            if (!state.IsAlive || state.IsLost || state.Busy)
            {
                continue;
            }

            _router.Route(_room, fish);

            // Snapshot the reached placements: evaluating them restores the room
            // repeatedly, and the router's buffers are reused by the next fish.
            int reachedCount = _router.Reached.Count;
            if (_placements.Length < reachedCount)
            {
                Array.Resize(ref _placements, Math.Max(reachedCount, _placements.Length * 2));
            }

            for (int i = 0; i < reachedCount; i++)
            {
                _placements[i] = _router.Reached[i];
                _placementCosts[_placements[i]] = _router.CostOf(_placements[i]);
            }

            for (int i = 0; i < reachedCount; i++)
            {
                int placement = _placements[i];
                (int px, int py, bool pLeft) = _router.Decode(placement);
                int routeCost = _placementCosts[placement];

                // The route is inert, so the state it reaches is just the parent
                // with this fish moved - no simulation needed to build it. Put the
                // room into that state once; everything below reads or restores it.
                BuildRouteKey(parentKeyOffset, fish, px, py, pLeft);
                _room.RestoreMobile(_routeKey, _mobile);

                // Parking: worth a state of its own even with nothing done.
                if (routeCost > 0 && _router.IsParking(_room, fish, px, py))
                {
                    generated++;
                    int parkH = _heuristic.Estimate(_room);
                    if (parkH < RelaxedDistance.Unreachable)
                    {
                        AddChild(nodeIndex, g + routeCost, u, placement, '\0', _routeKey, parkH);
                    }
                }

                // Actions: everything that is not travel. Horizontal ones only
                // from the matching facing - against your facing the key press is
                // a turn, which the router already walked.
                TryAction(nodeIndex, g + routeCost, u, placement, unit.Up, 0, -1, px, py, fish, ref generated);
                TryAction(nodeIndex, g + routeCost, u, placement, unit.Down, 0, 1, px, py, fish, ref generated);
                TryAction(
                    nodeIndex, g + routeCost, u, placement,
                    pLeft ? unit.Left : unit.Right, pLeft ? -1 : 1, 0, px, py, fish, ref generated);
            }
        }
    }

    private void TryAction(
        int nodeIndex, int cost, byte unitIndex, int placement, char symbol,
        int dx, int dy, int px, int py, int fish, ref long generated)
    {
        // The room has to be in the routed state before either test: IsFreeStep
        // reads the grid, and a previous action here will have left the room
        // somewhere else entirely.
        _room.RestoreMobile(_routeKey, _mobile);

        // Travel is already covered by the router; only act where acting means
        // something.
        if (_router.IsFreeStep(_room, fish, px, py, px + dx, py + dy))
        {
            return;
        }

        if (!_room.ApplyMove(symbol))
        {
            return;
        }

        generated++;

        int h;
        if (_room.IsSolved())
        {
            h = 0;
        }
        else if (!_room.IsSolvable() || _room.CannotMove())
        {
            return;
        }
        else
        {
            h = _heuristic.Estimate(_room);
            if (h >= RelaxedDistance.Unreachable)
            {
                return;
            }
        }

        _room.WriteStateKey(_key, _mobile);
        AddChild(nodeIndex, cost + 1, unitIndex, placement, symbol, _key, h);
    }

    private void AddChild(int nodeIndex, int cost, byte unitIndex, int placement, char action, byte[] key, int h)
    {
        if (!_visited.TryImprove(key, cost, out int keyOffset))
        {
            return;
        }

        int f = Priority(cost, h);
        EnsureOpen(f);
        Push(NewNode(nodeIndex, keyOffset, cost, unitIndex, placement, action), f);
    }

    /// <summary>
    /// The parent's key with one fish moved - which is the whole effect of an
    /// inert route.
    /// </summary>
    private void BuildRouteKey(int parentKeyOffset, int fish, int x, int y, bool isLeft)
    {
        _visited.KeyAt(parentKeyOffset).CopyTo(_routeKey);

        int offset = _keySlotOfModel[fish] * 5;
        _routeKey[offset] = (byte)x;
        _routeKey[offset + 1] = (byte)(x >> 8);
        _routeKey[offset + 2] = (byte)y;
        _routeKey[offset + 3] = (byte)(y >> 8);
        _routeKey[offset + 4] = (byte)((_routeKey[offset + 4] & ~1) | (isLeft ? 1 : 0));
    }

    /// <summary>
    /// Writes the move string out. Each node records only which fish went where,
    /// so the routes are recomputed here by re-running the router from each
    /// parent state - a handful of BFS runs along the solution path, rather than
    /// storing every route for millions of nodes.
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
            _room.RestoreMobile(_visited.KeyAt(_nodes[node.Parent].KeyOffset), _mobile);
            _router.Route(_room, _level.Units[node.Fish].Model);
            _router.WriteRoute(node.Placement, _level.Units[node.Fish], moves);

            if (node.Action != '\0')
            {
                moves.Add(node.Action);
            }
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

    private int NewNode(int parent, int keyOffset, int g, byte fish, int placement, char action)
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
            Fish = fish,
            Placement = placement,
            Action = action,
        };
        return _nodeCount++;
    }

    private void Push(int nodeIndex, int f) => _open[f].Add(nodeIndex);
}
