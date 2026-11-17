using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// Plans inert travel for <b>two fish at once</b> in a room whose items stay put.
///
/// <para>Between two item-moving events a fish only travels, and travel is
/// inert: it changes nothing but that fish's own placement (<see cref="InertRouter"/>).
/// Usually each fish can be routed on its own. Sometimes they are in each other's
/// way - a corridor one has to pass the other in, a bay one has to step into - and
/// then the cheapest way to get fish A where it has to be, with fish B ending
/// somewhere acceptable, has both of them moving, interleaved. Neither single-fish
/// route sees it: with B solid A cannot pass, and with B ignored the route walks
/// through B.</para>
///
/// <para><b>The joint search.</b> With the items frozen, the room is fully described
/// by the pair (placement of A, placement of B), and every move costs 1, so a plain
/// BFS over pairs is optimal. A step is legal when <see cref="InertRouter.IsFreeStep"/>
/// says so <i>with the other fish standing where this pair puts it</i>: the room is
/// re-stamped with both fish before their steps are tested, so occupancy, support
/// and crushing are all answered by the engine itself rather than by a model of
/// how two fish interact (a heavy bar shared by both fish is the case that model
/// would get wrong - solver/docs/023).</para>
///
/// <para><b>Re-stamping is cheap.</b> Only the two fish change between pairs, so
/// the room is restored from a two-slot key over just those two models - every
/// other model stays masked where it is, which is exactly the frozen layout.</para>
/// </summary>
internal sealed class SegmentPlanner
{
    /// <summary>Joint visited sets up to this many pairs use a dense stamp array; bigger rooms use a hash set.</summary>
    private const long DenseLimit = 1L << 23;

    private static readonly (int Dx, int Dy)[] Vertical = [(0, -1), (0, 1)];

    private readonly Level _level;
    private readonly int[] _mobile;
    private readonly int[] _slotOfModel;
    private readonly int _width;
    private readonly int _height;
    private readonly int _placementCount;
    private readonly long _denseLimit;

    // One router for the joint search's step tests and encoding, and two for
    // TryCompose, whose two single-fish routes have to be alive at once.
    private readonly InertRouter _router;
    private readonly InertRouter _routerA;
    private readonly InertRouter _routerB;

    private readonly byte[] _entryKey;
    private readonly byte[] _pairKey = new byte[2 * StateKeyEdit.SlotSize];
    private readonly int[] _pairModels = new int[2];
    private readonly int[] _oneModel = new int[1];
    private readonly bool[] _singleAllowed;
    private readonly List<char> _orderOne = [];
    private readonly List<char> _orderTwo = [];

    // BFS nodes, in discovery order - which is also the FIFO queue.
    private int[] _nodeA = new int[1024];
    private int[] _nodeB = new int[1024];
    private int[] _nodeParent = new int[1024];
    private char[] _nodeSymbol = new char[1024];
    private int _nodeCount;

    // Visited pairs: generation-stamped and allocated on first use, so a call
    // never has to clear it.
    private int[]? _stamp;
    private int _generation;
    private HashSet<long>? _seen;

    /// <param name="denseLimit">
    /// Largest number of placement pairs kept in the dense visited array (4 bytes
    /// each, allocated on first use); above it a hash set is used. Only tests pass
    /// it, to run the hash set on a small room.
    /// </param>
    public SegmentPlanner(Level level, LevelReduction reduction, long denseLimit = DenseLimit)
    {
        _level = level;
        _denseLimit = denseLimit;
        _mobile = reduction.MobileModels;
        _width = level.Width;
        _height = level.Height;
        _placementCount = _width * _height * 2;

        _router = new InertRouter(level, reduction, exactSupport: true);
        _routerA = new InertRouter(level, reduction, exactSupport: true);
        _routerB = new InertRouter(level, reduction, exactSupport: true);

        _entryKey = new byte[reduction.StateKeySize];
        _singleAllowed = new bool[_placementCount];

        _slotOfModel = new int[level.Models.Length];
        Array.Fill(_slotOfModel, -1);
        for (int i = 0; i < _mobile.Length; i++)
        {
            _slotOfModel[_mobile[i]] = i;
        }
    }

    /// <summary>Joint states the last <see cref="TryJoint"/> popped - for measuring, not for logic.</summary>
    internal int LastStatesPopped { get; private set; }

    /// <summary>
    /// Shortest interleaved inert travel for two fish in a room whose items stay put.
    /// </summary>
    /// <param name="room">
    /// The state at the start of the segment, with both fish at their start
    /// placements. Left exactly as it was on entry (same state key), whatever
    /// the outcome.
    /// </param>
    /// <param name="unitA">Index into <see cref="Level.Units"/> of the fish that must end exactly at <paramref name="toA"/>.</param>
    /// <param name="toA">An <see cref="InertRouter.Placement"/> id.</param>
    /// <param name="unitB">Index into <see cref="Level.Units"/> of the other fish.</param>
    /// <param name="allowedB">
    /// Indexed by placement id: where B may end. B's start may or may not be
    /// allowed.
    /// </param>
    /// <param name="cost">Number of move symbols, or -1 when there is no plan.</param>
    /// <param name="endB">Where B ended, or -1 when there is no plan.</param>
    /// <param name="moves">If non-null, the symbols are appended in order - on success only.</param>
    /// <param name="maxStates">Cap on joint states popped before giving up.</param>
    /// <returns>
    /// False if there is no such travel within <paramref name="maxStates"/>, or
    /// either fish is not standing in the room (dead, gone, or busy fish cannot
    /// travel).
    /// </returns>
    public bool TryJoint(
        Room room, int unitA, int toA, int unitB, bool[] allowedB,
        out int cost, out int endB, List<char>? moves = null, int maxStates = 2_000_000)
    {
        cost = -1;
        endB = -1;
        LastStatesPopped = 0;

        int fishA = _level.Units[unitA].Model;
        int fishB = _level.Units[unitB].Model;
        if (!TryStart(room, fishA, out int startA) || !TryStart(room, fishB, out int startB))
        {
            return false;
        }

        room.WriteStateKey(_entryKey, _mobile);
        try
        {
            return Search(room, unitA, fishA, startA, toA, unitB, fishB, startB, allowedB, maxStates, out cost, out endB, moves);
        }
        finally
        {
            // Popped pairs leave the fish wherever the last one put them.
            room.RestoreMobile(_entryKey, _mobile);
        }
    }

    /// <summary>
    /// Gets A to <paramref name="toA"/> and B to exactly <paramref name="toB"/>
    /// from the room as it stands, for reconstruction.
    ///
    /// <para>The cheap orders come first, because they are what nearly every
    /// segment needs: B's whole route and then A's, or A's whole route and then
    /// B's, each with the single-fish router and the other fish solid where it then
    /// stands. Only when neither order works is the joint search run - with B
    /// allowed to end only at <paramref name="toB"/>.</para>
    /// </summary>
    /// <returns>False if impossible; nothing is appended then. The room is left as it was on entry.</returns>
    public bool TryCompose(
        Room room, int unitA, int toA, int unitB, int toB, List<char> moves, int maxStates = 2_000_000)
    {
        int fishA = _level.Units[unitA].Model;
        int fishB = _level.Units[unitB].Model;
        if (!TryStart(room, fishA, out int startA) || !TryStart(room, fishB, out int startB))
        {
            return false;
        }

        room.WriteStateKey(_entryKey, _mobile);
        try
        {
            _orderOne.Clear();
            bool one = TryOrder(room, unitB, fishB, startB, toB, unitA, fishA, toA, _orderOne);

            room.RestoreMobile(_entryKey, _mobile);
            _orderTwo.Clear();
            bool two = TryOrder(room, unitA, fishA, startA, toA, unitB, fishB, toB, _orderTwo);

            if (one && (!two || _orderOne.Count <= _orderTwo.Count))
            {
                moves.AddRange(_orderOne);
                return true;
            }

            if (two)
            {
                moves.AddRange(_orderTwo);
                return true;
            }
        }
        finally
        {
            room.RestoreMobile(_entryKey, _mobile);
        }

        if ((uint)toB >= (uint)_placementCount)
        {
            return false;
        }

        _singleAllowed[toB] = true;
        try
        {
            return TryJoint(room, unitA, toA, unitB, _singleAllowed, out _, out _, moves, maxStates);
        }
        finally
        {
            _singleAllowed[toB] = false;
        }
    }

    /// <summary>
    /// The first fish's whole route, then the second's with the first standing at
    /// its target. Leaves the room wherever it got to; the caller restores it.
    /// </summary>
    private bool TryOrder(
        Room room, int firstUnit, int firstFish, int firstStart, int firstTo,
        int secondUnit, int secondFish, int secondTo, List<char> destination)
    {
        if (firstTo != firstStart)
        {
            if (room.State(firstFish).Busy)
            {
                return false; // the joint search does not move it either
            }

            _routerA.Route(room, firstFish);

            // CostOf and the predecessors hold stale data for placements the
            // last route did not reach, so reachability is membership.
            if (!Contains(_routerA.Reached, firstTo))
            {
                return false;
            }

            _routerA.WriteRoute(firstTo, _level.Units[firstUnit], destination);

            (int x, int y, bool isLeft) = _routerA.Decode(firstTo);
            MoveFish(room, firstFish, x, y, isLeft);
        }

        (int sx, int sy, bool sLeft) = room.Position(secondFish);
        if (_routerB.Placement(sx, sy, sLeft) == secondTo)
        {
            return true;
        }

        if (room.State(secondFish).Busy)
        {
            return false;
        }

        _routerB.Route(room, secondFish);
        if (!Contains(_routerB.Reached, secondTo))
        {
            return false;
        }

        _routerB.WriteRoute(secondTo, _level.Units[secondUnit], destination);
        return true;
    }

    private bool Search(
        Room room, int unitA, int fishA, int startA, int toA, int unitB, int fishB, int startB, bool[] allowedB,
        int maxStates, out int cost, out int endB, List<char>? moves)
    {
        cost = -1;
        endB = -1;

        UnitDef defA = _level.Units[unitA];
        UnitDef defB = _level.Units[unitB];
        bool movesA = !room.State(fishA).Busy;
        bool movesB = !room.State(fishB).Busy;

        // The two-slot key the popped pairs are stamped from: both fish's flags
        // as they are, positions rewritten per pair.
        int slotA = _slotOfModel[fishA], slotB = _slotOfModel[fishB];
        _entryKey.AsSpan(slotA * StateKeyEdit.SlotSize, StateKeyEdit.SlotSize).CopyTo(_pairKey);
        _entryKey.AsSpan(slotB * StateKeyEdit.SlotSize, StateKeyEdit.SlotSize)
            .CopyTo(_pairKey.AsSpan(StateKeyEdit.SlotSize));
        _pairModels[0] = fishA;
        _pairModels[1] = fishB;

        BeginVisited();
        _nodeCount = 0;
        Visit(startA, startB, -1, '\0');

        int head = 0;
        while (head < _nodeCount)
        {
            if (LastStatesPopped >= maxStates)
            {
                return false;
            }

            int node = head++;
            LastStatesPopped++;
            int pa = _nodeA[node], pb = _nodeB[node];

            // Goal on pop: every edge costs 1, so the first goal popped is a
            // cheapest one, and a start that already is a goal costs 0.
            if (pa == toA && allowedB[pb])
            {
                cost = WritePath(node, moves);
                endB = pb;
                return true;
            }

            (int ax, int ay, bool aLeft) = _router.Decode(pa);
            (int bx, int by, bool bLeft) = _router.Decode(pb);

            StateKeyEdit.SetPlacement(_pairKey, 0, ax, ay, aLeft);
            StateKeyEdit.SetPlacement(_pairKey, 1, bx, by, bLeft);
            room.RestoreMobile(_pairKey, _pairModels);

            if (movesA)
            {
                Expand(room, node, fishA, defA, ax, ay, aLeft, pb, isA: true);
            }

            if (movesB)
            {
                Expand(room, node, fishB, defB, bx, by, bLeft, pa, isA: false);
            }
        }

        return false;
    }

    /// <summary>
    /// One fish's moves from a popped pair, with the room stamped as that pair:
    /// the turn, then up, down and forward - the same moves
    /// <see cref="InertRouter.Route"/> considers.
    /// </summary>
    private void Expand(Room room, int node, int fish, UnitDef unit, int x, int y, bool isLeft, int other, bool isA)
    {
        // Turning moves the fish nowhere, so nothing it holds up can fall. The
        // symbol is the key for the new facing.
        Push(node, _router.Placement(x, y, !isLeft), other, isLeft ? unit.Right : unit.Left, isA);

        foreach ((int dx, int dy) in Vertical)
        {
            if (_router.IsFreeStep(room, fish, x, y, x, y + dy))
            {
                Push(node, _router.Placement(x, y + dy, isLeft), other, dy < 0 ? unit.Up : unit.Down, isA);
            }
        }

        // Horizontally only forward: against the facing the key is the turn above.
        int fx = isLeft ? x - 1 : x + 1;
        if (_router.IsFreeStep(room, fish, x, y, fx, y))
        {
            Push(node, _router.Placement(fx, y, isLeft), other, isLeft ? unit.Left : unit.Right, isA);
        }
    }

    private void Push(int parent, int moved, int other, char symbol, bool isA)
    {
        if (isA)
        {
            Visit(moved, other, parent, symbol);
        }
        else
        {
            Visit(other, moved, parent, symbol);
        }
    }

    private void Visit(int pa, int pb, int parent, char symbol)
    {
        if (!MarkVisited(((long)pa * _placementCount) + pb))
        {
            return;
        }

        if (_nodeCount == _nodeA.Length)
        {
            int size = _nodeA.Length * 2;
            Array.Resize(ref _nodeA, size);
            Array.Resize(ref _nodeB, size);
            Array.Resize(ref _nodeParent, size);
            Array.Resize(ref _nodeSymbol, size);
        }

        _nodeA[_nodeCount] = pa;
        _nodeB[_nodeCount] = pb;
        _nodeParent[_nodeCount] = parent;
        _nodeSymbol[_nodeCount] = symbol;
        _nodeCount++;
    }

    /// <summary>Appends the symbols leading to <paramref name="node"/> and returns how many there are.</summary>
    private int WritePath(int node, List<char>? moves)
    {
        int length = 0;
        for (int n = node; _nodeParent[n] >= 0; n = _nodeParent[n])
        {
            length++;
        }

        if (moves is null)
        {
            return length;
        }

        int start = moves.Count;
        for (int n = node; _nodeParent[n] >= 0; n = _nodeParent[n])
        {
            moves.Add(_nodeSymbol[n]);
        }

        moves.Reverse(start, length);
        return length;
    }

    private void BeginVisited()
    {
        long pairs = (long)_placementCount * _placementCount;
        if (pairs <= _denseLimit)
        {
            _stamp ??= new int[pairs];
            if (++_generation == int.MaxValue)
            {
                Array.Clear(_stamp);
                _generation = 1;
            }
        }
        else
        {
            _seen ??= [];
            _seen.Clear();
        }
    }

    /// <summary>False if the pair was already seen this call.</summary>
    private bool MarkVisited(long pair)
    {
        if (_stamp is not null)
        {
            if (_stamp[pair] == _generation)
            {
                return false;
            }

            _stamp[pair] = _generation;
            return true;
        }

        return _seen!.Add(pair);
    }

    /// <summary>The fish's placement, if it is standing in the room at all.</summary>
    private bool TryStart(Room room, int fish, out int placement)
    {
        placement = -1;
        ref readonly ModelState state = ref room.State(fish);
        if (!state.IsAlive || state.IsOut || state.IsLost || _slotOfModel[fish] < 0)
        {
            return false;
        }

        (int x, int y, bool isLeft) = room.Position(fish);
        if ((uint)x >= (uint)_width || (uint)y >= (uint)_height)
        {
            return false; // a departed fish sits far off the grid
        }

        placement = _router.Placement(x, y, isLeft);
        return true;
    }

    /// <summary>Stamps one fish at a new placement - the whole effect of its inert route.</summary>
    private void MoveFish(Room room, int fish, int x, int y, bool isLeft)
    {
        int slot = _slotOfModel[fish];
        _entryKey.AsSpan(slot * StateKeyEdit.SlotSize, StateKeyEdit.SlotSize).CopyTo(_pairKey);
        StateKeyEdit.SetPlacement(_pairKey, 0, x, y, isLeft);
        _oneModel[0] = fish;
        room.RestoreMobile(_pairKey.AsSpan(0, StateKeyEdit.SlotSize), _oneModel);
    }

    private static bool Contains(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return true;
            }
        }

        return false;
    }
}
