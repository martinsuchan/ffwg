using System.Diagnostics;

using FishFillets.Physics;

namespace FishFillets.Search;

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
/// The three things that make it tractable, in order of how much they matter:
///
/// <list type="number">
/// <item><b>The level reduction</b> (<see cref="LevelReduction"/>) removes every
///   model that provably can never move, so the state key carries only what can
///   actually differ. Most rooms are mostly scenery.</item>
/// <item><b>An admissible heuristic</b> (<see cref="RelaxedDistance"/>) from a
///   walls-only relaxation. Sound because fish swim rather than climb: items can
///   only obstruct them, so deleting the items never lengthens a path.</item>
/// <item><b>Dead-end pruning.</b> Most of the state space is states that can
///   never be finished from - see <see cref="IsDeadEnd"/>.</item>
/// </list>
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

    /// <summary>Relaxed distance-to-exit for each model that has to leave the room.</summary>
    private readonly int[] _exitModels;
    private readonly RelaxedDistance[] _exitDistance;
    private readonly bool[] _exitIsFish;

    private Node[] _nodes = new Node[1 << 16];
    private int _nodeCount;
    private List<int>[] _open = [];
    private double _weight = 1.0;

    private struct Node
    {
        public int Parent;
        public int KeyOffset;
        public int G;
        public char Move;
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

        // Anything with goal_escape or goal_out has to reach the border. Fish
        // must swim every cell; a pushed item gets downward moves free from
        // gravity, so its relaxation is cheaper.
        var models = new List<int>();
        var distances = new List<RelaxedDistance>();
        var isFish = new List<bool>();
        for (int i = 0; i < level.ModelCount; i++)
        {
            GoalKind goal = level.Models[i].Goal;
            if (goal is not (GoalKind.Escape or GoalKind.Out))
            {
                continue;
            }

            bool fish = level.Models[i].IsAlive;
            models.Add(i);
            distances.Add(RelaxedDistance.ToExit(level, i, _reduction.Walls, freeFall: !fish));
            isFish.Add(fish);
        }

        _exitModels = models.ToArray();
        _exitDistance = distances.ToArray();
        _exitIsFish = isFish.ToArray();
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
        int startH = Heuristic();
        if (startH >= RelaxedDistance.Unreachable)
        {
            return new SolveResult(null, false, "unsolvable: a goal is unreachable from the start", 0, 0, 0, 0, stopwatch.Elapsed);
        }

        _visited.TryImprove(_key, 0, out int startKey);
        int startF = Priority(0, startH);
        EnsureOpen(startF);
        Push(NewNode(-1, startKey, 0, '\0'), startF);

        long expanded = 0, generated = 1;
        int deepestF = 0;
        long nextReport = 500_000;

        for (int f = 0; f < _open.Length; f++)
        {
            List<int> bucket = _open[f];

            // A bucket can grow while it is drained (a child with the same f),
            // so this is an index loop, not a foreach.
            for (int b = 0; b < bucket.Count; b++)
            {
                int nodeIndex = bucket[b];
                int g = _nodes[nodeIndex].G;
                deepestF = f;

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
                        options.Weight <= 1.0,
                        "solved",
                        expanded,
                        generated,
                        _visited.Count,
                        f,
                        stopwatch.Elapsed);
                }

                expanded++;

                for (int s = 0; s < _symbols.Length; s++)
                {
                    char symbol = _symbols[s];
                    if (s > 0)
                    {
                        _room.RestoreMobile(_visited.KeyAt(_nodes[nodeIndex].KeyOffset), _mobile);
                    }

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

                    int childF = Priority(g + 1, h);
                    _room.WriteStateKey(_key, _mobile);
                    if (!_visited.TryImprove(_key, g + 1, out int keyOffset))
                    {
                        continue;
                    }

                    EnsureOpen(childF);
                    Push(NewNode(nodeIndex, keyOffset, g + 1, symbol), childF);
                }

                if (expanded >= nextReport)
                {
                    nextReport += 500_000;
                    options.Progress?.Invoke(
                        $"  f={f} expanded={expanded:N0} stored={_visited.Count:N0} " +
                        $"{stopwatch.Elapsed.TotalSeconds:F0}s");
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

            // Drop the drained bucket's backing storage - on a long search these
            // add up, and a bucket is never revisited once passed.
            bucket.Clear();
            bucket.TrimExcess();
        }

        return new SolveResult(
            null, options.Weight <= 1.0, "exhausted: no solution exists", expanded, generated,
            _visited.Count, deepestF, stopwatch.Elapsed);
    }

    private SolveResult Abandoned(string status, long expanded, long generated, int deepestF, Stopwatch stopwatch) =>
        new(null, false, status, expanded, generated, _visited.Count, deepestF, stopwatch.Elapsed);

    /// <summary>
    /// Whether the current state can be discarded outright, and its heuristic if
    /// not. Most of a level's state space is unfinishable, so this is where the
    /// search gets most of its pruning:
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
    /// </summary>
    private bool IsDeadEnd(out int h)
    {
        h = 0;
        if (!_room.IsSolvable() || _room.CannotMove())
        {
            return true;
        }

        h = Heuristic();
        return h >= RelaxedDistance.Unreachable;
    }

    /// <summary>
    /// A lower bound on the moves still needed.
    ///
    /// Fish distances add up: one symbol steps exactly one fish, so no move can
    /// make progress for two of them at once. Items do not add - a single push
    /// can shift a whole chain, and gravity moves them for nothing - so the
    /// bound takes the largest item distance rather than their sum, and the
    /// larger of that and the fish total overall.
    /// </summary>
    private int Heuristic()
    {
        int fishTotal = 0;
        int itemMax = 0;

        for (int i = 0; i < _exitModels.Length; i++)
        {
            ref readonly ModelState m = ref _room.State(_exitModels[i]);
            if (m.IsOut)
            {
                continue;
            }

            int d = _exitDistance[i].At(m.X, m.Y, m.IsLeft);
            if (d >= RelaxedDistance.Unreachable)
            {
                return RelaxedDistance.Unreachable;
            }

            if (_exitIsFish[i])
            {
                fishTotal += d;
            }
            else if (d > itemMax)
            {
                itemMax = d;
            }
        }

        return Math.Max(fishTotal, itemMax);
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
    }

    private int NewNode(int parent, int keyOffset, int g, char move)
    {
        if (_nodeCount == _nodes.Length)
        {
            Array.Resize(ref _nodes, _nodes.Length * 2);
        }

        _nodes[_nodeCount] = new Node { Parent = parent, KeyOffset = keyOffset, G = g, Move = move };
        return _nodeCount++;
    }

    private void Push(int nodeIndex, int f) => _open[f].Add(nodeIndex);
}
