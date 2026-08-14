using FishFillets.Physics;

namespace FishFillets.Search;

public sealed record OptimizeResult(string Moves, int OriginalLength, int Passes, long NodesExplored, int WindowsImproved)
{
    public int Saved => OriginalLength - Moves.Length;
}

/// <summary>
/// Shortens an existing solution by re-solving windows of it. For each window
/// <c>[i, j]</c> along the move string, it searches for a shorter path from the
/// state at step <c>i</c> to the state at step <c>j</c>; anything shorter is
/// spliced in, and the rest of the solution still replays because the two states
/// are identical by <c>Room.WriteStateKey</c>.
///
/// Why windows rather than solving outright: the bundled solutions are already
/// near-optimal (a state-revisit sweep over all 32,031 moves finds not one
/// wasted loop), so the remaining slack is local, and a window is a far easier
/// search than a level. A window has a fixed start AND a fixed goal state, which
/// admits a much sharper heuristic than "get both fish out" - see
/// <see cref="Heuristic"/> - and it is bounded: a path is only interesting if it
/// is shorter than the segment it replaces, so the cost limit is <c>j - i - 1</c>.
///
/// The flip side, and the reason this can't be the whole solver: splicing
/// preserves both endpoints, so it finds local shortcuts and never a globally
/// different route. See solver/docs/003.
/// </summary>
public sealed class WindowOptimizer
{
    private readonly Level _level;
    private readonly Room _room;
    private readonly int _keySize;

    private readonly char[] _symbols;
    private readonly int[] _fishModels;

    // Pooled room states, so a search allocates nothing per node.
    private readonly List<RoomSnapshot> _slots = [];
    private readonly Stack<int> _freeSlots = new();

    private readonly StateSet _visited;
    private readonly byte[] _key;
    private readonly byte[] _goalKey;

    private Node[] _nodes = new Node[1 << 16];
    private int _nodeCount;

    /// <summary>Open list, bucketed by f = g + h. f never exceeds the cost limit, which is small.</summary>
    private List<int>[] _open = [];

    private struct Node
    {
        public int Parent;
        public int Slot;
        public short G;
        public char Move;
    }

    public WindowOptimizer(Level level)
    {
        _level = level;
        _room = new Room(level);
        _keySize = _room.StateKeySize;
        _key = new byte[_keySize];
        _goalKey = new byte[_keySize];
        _visited = new StateSet(_keySize);

        _symbols = new char[_room.MoveSymbolCount];
        _room.GetMoveSymbols(_symbols, out int count);
        Array.Resize(ref _symbols, count);

        _fishModels = level.Units.Select(u => u.Model).ToArray();
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
        Action<string>? progress = null)
    {
        int original = moves.Length;
        long totalNodes = 0;
        int improvedWindows = 0;
        int passes = 0;

        List<RoomSnapshot> states = ReplayStates(moves);

        for (passes = 1; ; passes++)
        {
            bool improvedThisPass = false;

            for (int i = 0; i + 2 <= moves.Length; i += stride)
            {
                int j = Math.Min(i + window, moves.Length);
                if (j - i < 2)
                {
                    continue;
                }

                string? shorter = Search(states[i], states[j], limit: j - i - 1, nodeLimit, out long nodes);
                totalNodes += nodes;

                if (shorter is null)
                {
                    continue;
                }

                moves = string.Concat(moves.AsSpan(0, i), shorter, moves.AsSpan(j));
                states = ReplayStates(moves);
                improvedThisPass = true;
                improvedWindows++;
                progress?.Invoke(
                    $"  [{i}..{j}] {j - i} -> {shorter.Length} moves ({moves.Length} total)");

                // Re-examine from the same start: a shortcut here often opens another.
                i -= stride;
            }

            if (!improvedThisPass)
            {
                break;
            }
        }

        return new OptimizeResult(moves, original, passes, totalNodes, improvedWindows);
    }

    /// <summary>Snapshots the state after every prefix of the move string.</summary>
    private List<RoomSnapshot> ReplayStates(string moves)
    {
        var states = new List<RoomSnapshot>(moves.Length + 1);
        _room.Reset();
        states.Add(_room.Capture());

        foreach (char symbol in moves)
        {
            if (!_room.ApplyMove(symbol))
            {
                throw new InvalidOperationException(
                    $"{_level.Name}: move {states.Count - 1} ('{symbol}') is not possible - " +
                    "the input is not a valid solution for this level");
            }

            states.Add(_room.Capture());
        }

        return states;
    }

    /// <summary>
    /// A* from <paramref name="from"/> to <paramref name="to"/>, admitting only
    /// paths of at most <paramref name="limit"/> moves.
    /// </summary>
    /// <returns>A strictly shorter move string, or null.</returns>
    private string? Search(RoomSnapshot from, RoomSnapshot to, int limit, long nodeLimit, out long nodes)
    {
        nodes = 0;
        if (limit < 1)
        {
            return null;
        }

        _room.RestoreFrom(to);
        _room.WriteStateKey(_goalKey);
        (int X, int Y, bool IsLeft)[] goalFish = _fishModels.Select(_room.Position).ToArray();

        _room.RestoreFrom(from);
        _room.WriteStateKey(_key);
        int startH = Heuristic(goalFish);
        if (startH > limit)
        {
            return null; // provably unreachable inside the budget
        }

        ResetSearch(limit);
        _visited.TryImprove(_key, 0);
        Push(NewNode(parent: -1, slot: TakeSlot(from), g: 0, move: '\0'), startH);

        for (int f = 0; f <= limit; f++)
        {
            List<int> bucket = _open[f];
            // Bucket f can grow while it is being drained (a move whose child has
            // the same f), so this is an index loop, not a foreach.
            for (int b = 0; b < bucket.Count; b++)
            {
                int nodeIndex = bucket[b];
                ref Node node = ref _nodes[nodeIndex];
                int slot = node.Slot;
                if (slot < 0)
                {
                    continue; // already expanded via a cheaper route
                }

                int g = node.G;
                nodes++;

                _room.RestoreFrom(_slots[slot]);
                _room.WriteStateKey(_key);
                if (_key.AsSpan().SequenceEqual(_goalKey))
                {
                    return Reconstruct(nodeIndex);
                }

                if (g < limit && nodes < nodeLimit)
                {
                    ExpandInto(nodeIndex, slot, g, goalFish, limit);
                }

                ReleaseSlot(slot);
                _nodes[nodeIndex].Slot = -1;

                if (nodes >= nodeLimit)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private void ExpandInto(int parent, int parentSlot, int g, (int X, int Y, bool IsLeft)[] goalFish, int limit)
    {
        foreach (char symbol in _symbols)
        {
            _room.RestoreFrom(_slots[parentSlot]);
            if (!_room.ApplyMove(symbol))
            {
                continue;
            }

            // A fish death is irreversible and every fish goal requires alive, so
            // no state past one can ever match a target on a winning path.
            if (!_room.IsSolvable())
            {
                continue;
            }

            int h = Heuristic(goalFish);
            int f = g + 1 + h;
            if (f > limit)
            {
                continue;
            }

            _room.WriteStateKey(_key);
            if (!_visited.TryImprove(_key, g + 1))
            {
                continue;
            }

            int slot = TakeSlot(null);
            _room.CaptureTo(_slots[slot]);
            Push(NewNode(parent, slot, (short)(g + 1), symbol), f);
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
    /// </summary>
    private int Heuristic((int X, int Y, bool IsLeft)[] goalFish)
    {
        int total = 0;
        for (int i = 0; i < _fishModels.Length; i++)
        {
            (int x, int y, bool isLeft) = _room.Position(_fishModels[i]);
            (int gx, int gy, bool gLeft) = goalFish[i];

            int dx = gx - x;
            int dy = gy - y;
            total += Math.Abs(dx) + Math.Abs(dy);

            if (dx < 0 && !isLeft) total++;
            else if (dx > 0 && isLeft) total++;
            else if (dx == 0 && isLeft != gLeft) total++;
        }

        return total;
    }

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

    // ------------------------------------------------------------ bookkeeping --

    private void ResetSearch(int limit)
    {
        _visited.Clear();
        _nodeCount = 0;

        if (_open.Length < limit + 1)
        {
            _open = new List<int>[limit + 1];
            for (int i = 0; i <= limit; i++)
            {
                _open[i] = [];
            }
        }

        for (int i = 0; i <= limit; i++)
        {
            _open[i].Clear();
        }

        // Slots outlive a single search - only the free list needs rebuilding.
        _freeSlots.Clear();
        for (int i = 0; i < _slots.Count; i++)
        {
            _freeSlots.Push(i);
        }
    }

    private int NewNode(int parent, int slot, short g, char move)
    {
        if (_nodeCount == _nodes.Length)
        {
            Array.Resize(ref _nodes, _nodes.Length * 2);
        }

        _nodes[_nodeCount] = new Node { Parent = parent, Slot = slot, G = g, Move = move };
        return _nodeCount++;
    }

    private void Push(int nodeIndex, int f) => _open[f].Add(nodeIndex);

    /// <summary>Takes a pooled state slot, optionally filled from a snapshot.</summary>
    private int TakeSlot(RoomSnapshot? fill)
    {
        int slot;
        if (_freeSlots.Count > 0)
        {
            slot = _freeSlots.Pop();
        }
        else
        {
            _slots.Add(new RoomSnapshot(_room));
            slot = _slots.Count - 1;
        }

        if (fill is not null)
        {
            _room.RestoreFrom(fill);
            _room.CaptureTo(_slots[slot]);
        }

        return slot;
    }

    private void ReleaseSlot(int slot) => _freeSlots.Push(slot);
}
