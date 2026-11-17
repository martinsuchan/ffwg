using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// Searches over <b>item layouts</b>, carrying where an idle fish stands as a
/// deferred field instead of a committed square (<c>ffsolve solve --layouts</c>,
/// solver/docs/024).
///
/// <para><b>Why.</b> Plain A* stores one state per combination of fish
/// positions: on <c>broom</c> 5.44 M states over 1,143 item layouts
/// (solver/docs/023). <see cref="ItemSolver"/> stores only states after an
/// event, but it still has to decide, at every event, where the fish that did
/// not act goes - and its "four nearest squares" are wrong exactly where an
/// optimum sends that fish 4-69 squares down the ranking. Here nothing is
/// decided: the fish that did not act keeps a <b>field</b>, the cost of it
/// standing at each placement, and pays from it only when it next acts.</para>
///
/// <para><b>The model.</b> A node is one entry of a <see cref="LayoutTable"/>
/// layout: which fish acted last (a point, exactly where the event left it),
/// and for every fish a <b>home</b> - a cheapest placement of its field, where
/// the concrete room of the node puts it. A field is never stored per node; it
/// is defined along the chain of parents and recomputed on demand through a
/// <see cref="FieldCache"/>:</para>
/// <list type="bullet">
/// <item>at the root, for the fish that acted, and for a fish an edge
///   committed, a point of cost 0;</item>
/// <item>for any other fish, the parent's field spread by inert travel through
///   the parent's room (<see cref="RelaxDeferred"/>, the other fish solid where
///   it stands) and then masked by the event's footprint
///   (<see cref="LayoutFootprint"/>): the placements where the fish would have
///   been in the event's way are forbidden.</item>
/// </list>
///
/// <para><b>An edge</b> is (fish A, placement p, key s) found geometrically, as
/// in <see cref="ItemSolver"/>, from A's free field (the other fish passed
/// through). The physics is run once with every other fish as far out of A's
/// way as its field allows, preferring placements where it holds nothing up.
/// The outcome's footprint F then says which of its placements would have given
/// the same outcome: all those outside F - the rule <c>MicroChecks.Footprint</c>
/// checks in every reachable micro-level state. The outcome depended on the
/// other fish, and the fish is <b>committed</b> where it stood ("bound"), when
/// its placement is inside F or when something stays up only because it is
/// there (<see cref="NeedsIdleSupport"/>; no footprint marks that, see the
/// micro-level <c>support-bound</c>). A run the other fish may have spoiled -
/// refused, a fish hurt, an arrangement that is not settled - is retried with
/// it at home, then clear of the key's push chain.</para>
///
/// <para><b>Interaction children.</b> A clean generic outcome also gets up to
/// two children per other fish where it stands <i>inside</i> the event and is
/// committed there (<see cref="TryInteractions"/>): the cheapest placement in
/// the moved items' after, swept or under cells that gives a different
/// layout, and the cheapest that gives the same layout with the fish where the
/// generic child forbids it - wc's bowl coming down onto the small fish's back.</para>
///
/// <para><b>Travel is feasible at generation</b>, not just at reconstruction,
/// or an underpriced edge would push a dearer, feasible path to the same entry
/// out of the table. A's price is its free field; if the other fish standing at
/// its home is in the way (the solid field differs), A either goes round it at
/// the solid price, or the other fish moves first - the cheap
/// <see cref="TryStepAside"/>, else <see cref="SegmentPlanner.TryJoint"/> - and
/// is committed to where that plan leaves it. Both edges are kept when the plan
/// is the cheaper.</para>
///
/// <para><b>What it promises.</b> A <b>valid</b> solution, never a shortest
/// one. The cost g is this model's cost. Reconstruction (<see cref="Reconstruct"/>)
/// replays the path concretely - each fish sent to where its field says it
/// was (<see cref="Waypoints"/>), each node's layout checked, the whole string
/// finally run through <see cref="SolutionValidator"/> - and a goal whose path
/// does not replay is dropped and the search goes on. The reported length is
/// the replayed string's, which can exceed g. Fish cooperation that needs the
/// idle fish at an exact square inside an event's footprint - a fish as a
/// bridge, a support handed over, a fish an item comes down around (<c>wc</c>)
/// - is not expressible yet (stage B in the plan).</para>
/// </summary>
public sealed class LayoutSolver
{
    /// <summary>Fields kept in memory. Each is one ushort per placement; a miss is recomputed along the chain of parents.</summary>
    private const int FieldCacheCapacity = 4096;

    /// <summary>Joint states a generation-time joint plan may pop. It is quadratic when the other fish may end anywhere.</summary>
    private const int JointMaxStates = 30_000;

    /// <summary>Joint states a reconstruction segment may pop - rare, and worth more effort than one edge.</summary>
    private const int ComposeMaxStates = 400_000;

    private const byte NoUnit = 0xFF;

    private static readonly (int Dx, int Dy)[] Directions = [(-1, 0), (1, 0), (0, -1), (0, 1)];

    private struct Node
    {
        public int Parent;
        public int Layout;
        public int Entry;
        public int G;
        public byte Actor;          // NoUnit at the root
        public char Symbol;
        public int ActorPlacement;  // where the actor stood when it pressed the key
        public int CellStart;
        public int CellCount;
        public bool Joint;          // the edge was priced by a joint plan from both homes
    }

    private readonly Level _level;
    private readonly LevelReduction _reduction;
    private readonly Room _room;
    private readonly Room _replay;
    private readonly ExitHeuristic _heuristic;
    private readonly int[] _mobile;
    private readonly int[] _items;
    private readonly int _width;
    private readonly int _height;
    private readonly int _placementCount;
    private readonly int _unitCount;
    private readonly int[] _unitModel;
    private readonly int[] _unitSlot;
    private readonly RelaxedDistance?[] _unitExit;
    private readonly (int X, int Y)[] _escapes;

    private readonly LayoutTable _table;
    private readonly InertRouter _router;
    private readonly InertRouter _routeA;
    private readonly InertRouter _routeB;
    private readonly InertRouter _commitRouteA;
    private readonly InertRouter _commitRouteB;
    private readonly int[] _commitReach;
    private int _commitReachGeneration;
    private int _commitNode = -1, _commitA = -1, _commitB = -1, _commitTarget = -1, _commitCostB = -1;
    private readonly SegmentPlanner _planner;
    private readonly LayoutFootprint _footprint;
    private readonly FieldCache _cache;
    private readonly BucketQueue _queue = new();
    private double _weight = 1.0;

    // Nodes, and per node and unit: home (placement, -1 when out) and the
    // placement the edge committed the unit to before the key press (-1: none).
    private Node[] _nodes = new Node[1 << 14];
    private int[] _homes;
    private int[] _commits;
    private int _nodeCount;
    private ushort[] _cellPool = new ushort[1 << 16];
    private int _cellTop;

    // The node whose concrete room _room holds, or -1 when it holds anything else.
    private int _roomNode = -1;

    // Expansion scratch: one field set per unit.
    private readonly ushort[][] _phi;
    private readonly ushort[][] _free;
    private readonly ushort[][] _solid;
    private readonly ushort[][] _masked;
    private readonly List<int>[] _freeList;
    private readonly bool[] _holdsAtHome;
    private readonly bool[] _dependsOn;
    private readonly bool[] _dependsSaved;
    private readonly int[] _run;
    private readonly int[] _childHomes;
    private readonly int[] _childCommits;
    private readonly bool[] _allowed;
    private readonly int[][] _holdMark;      // per unit: == _holdGeneration where it holds something up
    private int _holdGeneration;
    private readonly int[] _candidateMark;
    private int _candidateGeneration;
    private readonly int[] _cellMark;
    private int _cellGeneration;
    private readonly List<(byte Unit, int Placement, int Direction)> _candidates = [];
    private readonly List<int> _chain = [];
    private readonly List<int> _cells = [];
    private readonly List<int> _cellsSaved = [];
    private readonly List<int> _cellsWide = [];
    private readonly List<char> _segment = [];
    private readonly byte[] _genericLayout;
    private readonly int[] _genericRun;
    private readonly List<int> _interactionCells = [];
    private readonly List<(int Model, int X, int Y)> _chainModels = [];
    private readonly HashSet<int> _chainSeen = [];
    private readonly List<long> _interactionCandidates = [];
    private readonly int[] _squareMark;
    private int _squareGeneration;
    private bool _emitted;
    private readonly int[] _backQueue;
    private readonly int[] _backStamp;
    private readonly int[] _backDistance;
    private int _backGeneration;

    private readonly byte[] _nodeKey;
    private readonly byte[] _parentLayout;
    private readonly byte[] _runKey;
    private readonly byte[] _childKey;
    private readonly byte[] _childKeySaved;
    private readonly byte[] _canonical;
    private readonly byte[] _entryKey;
    private readonly (int X, int Y, bool Gone)[] _itemsBefore;
    private readonly bool[] _goneBefore;

    public LayoutSolver(Level level, LevelReduction? reduction = null)
    {
        _level = level;
        _reduction = reduction ?? LevelReduction.Compute(level);
        _room = new Room(level);
        _replay = new Room(level);
        _heuristic = new ExitHeuristic(level, _reduction);
        _mobile = _reduction.MobileModels;
        _items = [.. _mobile.Where(m => !level.Models[m].IsAlive)];
        _width = level.Width;
        _height = level.Height;
        _placementCount = _width * _height * 2;
        _unitCount = level.Units.Length;

        _unitModel = new int[_unitCount];
        _unitSlot = new int[_unitCount];
        _unitExit = new RelaxedDistance?[_unitCount];
        for (int u = 0; u < _unitCount; u++)
        {
            _unitModel[u] = level.Units[u].Model;
            _unitSlot[u] = Array.IndexOf(_mobile, _unitModel[u]);
            _unitExit[u] = _heuristic.FishExit(_unitModel[u]);
        }

        _table = new LayoutTable(level, _mobile, EntryKeySize);
        _router = new InertRouter(level, _reduction, exactSupport: true);
        _routeA = new InertRouter(level, _reduction, exactSupport: true);
        _routeB = new InertRouter(level, _reduction, exactSupport: true);
        _commitRouteA = new InertRouter(level, _reduction, exactSupport: true);
        _commitRouteB = new InertRouter(level, _reduction, exactSupport: true);
        _commitReach = new int[_placementCount];
        _planner = new SegmentPlanner(level, _reduction);
        _footprint = new LayoutFootprint(level);
        _cache = new FieldCache(FieldCacheCapacity, _placementCount);
        _escapes = FindEscapes(level, _reduction);

        _homes = new int[_nodes.Length * _unitCount];
        _commits = new int[_nodes.Length * _unitCount];

        _phi = NewFields();
        _free = NewFields();
        _solid = NewFields();
        _masked = NewFields();
        _freeList = new List<int>[_unitCount];
        for (int u = 0; u < _unitCount; u++)
        {
            _freeList[u] = [];
        }

        _holdsAtHome = new bool[_unitCount];
        _dependsOn = new bool[_unitCount];
        _dependsSaved = new bool[_unitCount];
        _run = new int[_unitCount];
        _childHomes = new int[_unitCount];
        _childCommits = new int[_unitCount];
        _allowed = new bool[_placementCount];
        _holdMark = new int[_unitCount][];
        for (int u = 0; u < _unitCount; u++)
        {
            _holdMark[u] = new int[_placementCount];
        }
        _candidateMark = new int[_placementCount * 4];
        _cellMark = new int[_width * _height];
        _backQueue = new int[_placementCount];
        _squareMark = new int[_width * _height];
        _backStamp = new int[_placementCount];
        _backDistance = new int[_placementCount];

        _nodeKey = new byte[_reduction.StateKeySize];
        _parentLayout = new byte[_reduction.StateKeySize];
        _runKey = new byte[_reduction.StateKeySize];
        _childKey = new byte[_reduction.StateKeySize];
        _childKeySaved = new byte[_reduction.StateKeySize];
        _genericLayout = new byte[_reduction.StateKeySize];
        _genericRun = new int[_unitCount];
        _canonical = new byte[_reduction.StateKeySize];
        _entryKey = new byte[EntryKeySize];
        _itemsBefore = new (int, int, bool)[_items.Length];
        _goneBefore = new bool[_unitCount];

        ushort[][] NewFields()
        {
            var fields = new ushort[_unitCount][];
            for (int u = 0; u < _unitCount; u++)
            {
                fields[u] = new ushort[_placementCount];
            }

            return fields;
        }
    }

    public LevelReduction Reduction => _reduction;

    /// <summary>Distinct item layouts stored.</summary>
    public int Layouts => _table.LayoutCount;

    /// <summary>Distinct (layout, point fish, homes) entries stored.</summary>
    public int Entries => _table.EntryCount;

    /// <summary>Two-fish travel plans run at edge generation, because the other fish was in A's way or committed elsewhere.</summary>
    public long JointCalls { get; private set; }

    /// <summary>Of those, how many found nothing (the edge was dropped).</summary>
    public long JointFailures { get; private set; }

    /// <summary>Of the plans for a free fish, how many the cheap "step off A's way" plan settled without a joint search.</summary>
    public long StepAsides { get; private set; }

    /// <summary>Edges whose outcome depended on where the other fish stood, which was then committed.</summary>
    public long BoundEdges { get; private set; }

    /// <summary>Goals popped whose path could not be replayed concretely; the search went on.</summary>
    public long ReconstructionRejects { get; private set; }

    /// <summary>Replayed strings that the validator then refused - always a bug.</summary>
    public long ReplayFailures { get; private set; }

    /// <summary>Key presses simulated at edge generation (interaction probes included).</summary>
    public long PhysicsCalls { get; private set; }

    /// <summary>Children where the idle fish stands inside the event and changes it (<see cref="TryInteractions"/>).</summary>
    public long InteractionEdges { get; private set; }

    /// <summary>Key presses spent looking for those.</summary>
    public long InteractionPhysics { get; private set; }

    /// <summary>Diagnostics: interaction probes that gave a different layout / the same one, and of the kept ones, how many became children.</summary>
    internal long InteractionDifferentFound, InteractionSameFound, InteractionDifferentEmitted, InteractionSameEmitted;

    /// <summary>Queue entries skipped because their entry had since been reached more cheaply.</summary>
    public long StaleSkips { get; private set; }

    /// <summary>Candidates retried with the other fish at home, because the first run was spoiled by where it stood.</summary>
    public long Retries { get; private set; }

    /// <summary>Why the last reconstruction failed, for diagnostics.</summary>
    public string? LastRejectReason { get; private set; }

    private int EntryKeySize => 1 + (2 * _unitCount);

    /// <summary>Why the last <see cref="Simulate"/> failed - for <see cref="Trace"/>.</summary>
    private string _simWhy = "";

    /// <summary>Whether the last <see cref="Simulate"/> failed because the key was refused.</summary>
    private bool _lastRefused;

    /// <summary>Diagnostics: called with (node, unit, placement, key, why) for every candidate that does not become an entry.</summary>
    internal Action<int, int, int, char, string>? Trace { get; set; }

    /// <summary>Diagnostics: the layout and homes of a node.</summary>
    internal (int Layout, int[] Homes, int G) NodeInfo(int node) =>
        (_nodes[node].Layout, _homes.AsSpan(node * _unitCount, _unitCount).ToArray(), _nodes[node].G);

    /// <summary>
    /// Diagnostics: expands one state - the fish exactly where it says, as at
    /// the root - and returns the canonical layout of every child stored, with
    /// its cost. Use a fresh solver per call. Lets a probe ask whether the real
    /// expansion produces a given successor (as <c>ItemExpressibilityTests</c>
    /// does for <see cref="ItemSolver"/>); <see cref="Trace"/> says why not.
    /// </summary>
    internal List<(byte[] Layout, int Cost)> ProbeSuccessors(ReadOnlySpan<byte> wideKey)
    {
        _room.Reset();
        _room.RestoreMobile(wideKey, _mobile);
        _roomNode = -1;
        wideKey.CopyTo(_childKey);
        int layout = _table.Intern(_childKey, out _);
        for (int u = 0; u < _unitCount; u++)
        {
            _childHomes[u] = InRoom(_room, _unitModel[u], out int placement) ? placement : -1;
            _childCommits[u] = -1;
        }

        WriteEntryKey(NoUnit, _childHomes);
        _table.TryImprove(layout, _entryKey, 0, out int entry, out _);
        _cells.Clear();
        int root = NewNode(-1, layout, entry, 0, NoUnit, '\0', -1, _cells, _childHomes, _childCommits);
        long generated = 0;
        Expand(root, 0, ref generated);

        var children = new List<(byte[], int)>();
        for (int n = root + 1; n < _nodeCount; n++)
        {
            children.Add((_table.LayoutKey(_nodes[n].Layout).ToArray(), _nodes[n].G));
        }

        return children;
    }

    public SolveResult Solve(SolveOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        _weight = options.Weight;
        _room.Reset();
        _roomNode = -1;

        if (_room.IsSolved())
        {
            return new SolveResult("", false, "already solved", 0, 0, 0, 0, stopwatch.Elapsed);
        }

        int startH = _heuristic.Estimate(_room);
        if (startH >= RelaxedDistance.Unreachable)
        {
            return new SolveResult(
                null, false, "unsolvable: a goal is unreachable from the start", 0, 0, 0, 0, stopwatch.Elapsed);
        }

        _room.WriteStateKey(_childKey, _mobile);
        int layout = _table.Intern(_childKey, out _);
        _table.Tag(layout) = _heuristic.ItemBound(_room);

        for (int u = 0; u < _unitCount; u++)
        {
            _childHomes[u] = InRoom(_room, _unitModel[u], out int placement) ? placement : -1;
            _childCommits[u] = -1;
        }

        WriteEntryKey(NoUnit, _childHomes);
        _table.TryImprove(layout, _entryKey, 0, out int entry, out _);
        _cells.Clear();
        int root = NewNode(-1, layout, entry, 0, NoUnit, '\0', -1, _cells, _childHomes, _childCommits);
        _queue.Push(root, Priority(0, startH));

        long expanded = 0, generated = 1;
        int deepestF = 0;
        double nextReport = options.ProgressSeconds;

        while (_queue.TryPop(out int nodeIndex, out int f))
        {
            int g = _nodes[nodeIndex].G;
            if (g > _table.BestCost(_nodes[nodeIndex].Entry))
            {
                StaleSkips++;
                continue;
            }

            deepestF = Math.Max(deepestF, f);
            RestoreNode(nodeIndex);

            // Goal on pop, as everywhere else. A goal whose path cannot be
            // replayed is not the end: the search goes on past it.
            if (_room.IsSolved())
            {
                string? moves = Reconstruct(nodeIndex);
                if (moves is not null)
                {
                    return new SolveResult(
                        moves, false, "solved", expanded, generated, _table.EntryCount, f, stopwatch.Elapsed);
                }

                continue;
            }

            expanded++;
            Expand(nodeIndex, g, ref generated);

            if (options.Progress is not null && (expanded & 0x3F) == 0)
            {
                double elapsed = stopwatch.Elapsed.TotalSeconds;
                if (elapsed >= nextReport)
                {
                    nextReport = elapsed + options.ProgressSeconds;
                    options.Progress(
                        $"  {elapsed,6:F0}s  f={f,-4} expanded {expanded,10:N0}  " +
                        $"layouts {_table.LayoutCount,8:N0}  entries {_table.EntryCount,10:N0}  " +
                        $"plans {JointCalls:N0}/{JointFailures:N0} failed  bound {BoundEdges:N0}");
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

        return new SolveResult(
            null, false, "exhausted: the layout search found nothing new", expanded, generated,
            _table.EntryCount, deepestF, stopwatch.Elapsed);
    }

    // ------------------------------------------------------------- expansion --

    private void Expand(int nodeIndex, int g, ref long generated)
    {
        int nodeBase = nodeIndex * _unitCount;
        int inRoom = 0;
        for (int u = 0; u < _unitCount; u++)
        {
            if (_homes[nodeBase + u] >= 0)
            {
                inRoom++;
            }
        }

        // Fields first - computing them may restore other nodes' rooms.
        for (byte u = 0; u < _unitCount; u++)
        {
            _freeList[u].Clear();
            if (_homes[nodeBase + u] < 0)
            {
                continue;
            }

            Phi(nodeIndex, u, _phi[u]);

            _phi[u].AsSpan().CopyTo(_free[u]);
            RelaxFree(nodeIndex, u, _free[u]);

            _phi[u].AsSpan().CopyTo(_solid[u]);
            if (!IsBusy(nodeIndex, u))
            {
                RestoreNode(nodeIndex);
                _router.Relax(_room, _unitModel[u], _solid[u]);
            }

            _solid[u].AsSpan().CopyTo(_cache.Rent(FieldKey(nodeIndex, u, relaxed: true)));

            ushort[] free = _free[u];
            List<int> list = _freeList[u];
            for (int q = 0; q < free.Length; q++)
            {
                if (free[q] != FishField.None)
                {
                    list.Add(q);
                }
            }
        }

        RestoreNode(nodeIndex);
        _room.WriteStateKey(_nodeKey, _mobile);
        _table.LayoutKey(_nodes[nodeIndex].Layout).CopyTo(_parentLayout);

        for (int i = 0; i < _items.Length; i++)
        {
            ref readonly ModelState s = ref _room.State(_items[i]);
            _itemsBefore[i] = (s.X, s.Y, s.IsOut || s.IsLost);
        }

        // Where each fish would be holding something up, read while the room
        // is the node's. The other fish is not "something": fish never fall.
        // Used for drops and downward pushes, for keeping the idle fish's run
        // placement off anything it might be supporting, and for the
        // settledness test of an arrangement.
        _holdGeneration++;
        for (int u = 0; u < _unitCount; u++)
        {
            _goneBefore[u] = _homes[nodeBase + u] < 0;
            int home = _homes[nodeBase + u];
            _holdsAtHome[u] = false;
            if (home < 0)
            {
                continue;
            }

            int ignore = inRoom == 2 ? OtherFish(nodeIndex, u) : -1;
            int[] marks = _holdMark[u];
            foreach (int placement in _freeList[u])
            {
                (int hx, int hy, _) = _router.Decode(placement);
                if (_router.IsHoldingSomethingUp(_room, _unitModel[u], hx, hy, ignore))
                {
                    marks[placement] = _holdGeneration;
                }
            }

            (int x, int y, _) = _router.Decode(home);
            _holdsAtHome[u] = _router.IsHoldingSomethingUp(_room, _unitModel[u], x, y, ignore);
        }

        for (byte a = 0; a < _unitCount; a++)
        {
            if (_homes[nodeBase + a] < 0 || IsBusy(nodeIndex, a))
            {
                continue;
            }

            CollectCandidates(nodeIndex, a, inRoom);
            foreach ((byte unit, int placement, int direction) in _candidates)
            {
                TryEdge(nodeIndex, g, unit, placement, direction, inRoom, ref generated);
            }
        }
    }

    /// <summary>
    /// Every (placement, key) of one fish worth simulating from this node: the
    /// pushes, drops and exits <see cref="ItemSolver.ExpandUnder"/> finds, taken
    /// from wherever the fish's free field reaches.
    /// </summary>
    private void CollectCandidates(int nodeIndex, byte a, int inRoom)
    {
        _candidates.Clear();
        _candidateGeneration++;
        RestoreNode(nodeIndex);

        int fish = _unitModel[a];
        ushort[] free = _free[a];
        Shape fishShape = _level.Models[fish].Shape;
        int[] holds = _holdMark[a];

        for (int n = 0; n < _items.Length; n++)
        {
            (int ix, int iy, bool gone) = _itemsBefore[n];
            if (gone)
            {
                continue;
            }

            int item = _items[n];
            Shape itemShape = _level.Models[item].Shape;

            for (int d = 0; d < 4; d++)
            {
                (int dx, int dy) = Directions[d];
                if (!CanPossiblyMove(item, ix, iy, dx, dy))
                {
                    continue;
                }

                for (int c = 0; c < itemShape.MarkX.Length; c++)
                {
                    for (int m = 0; m < fishShape.MarkX.Length; m++)
                    {
                        int ax = ix + itemShape.MarkX[c] - dx - fishShape.MarkX[m];
                        int ay = iy + itemShape.MarkY[c] - dy - fishShape.MarkY[m];

                        // Sideways only facing that way: against the facing the
                        // key is a turn, which the field has already priced.
                        if (dx != 0)
                        {
                            AddCandidate(a, ax, ay, dx < 0, d, free);
                            continue;
                        }

                        // Down only works on something the fish holds up itself.
                        if (dy > 0 && !IsHoldingAt(holds, ax, ay))
                        {
                            continue;
                        }

                        AddCandidate(a, ax, ay, true, d, free);
                        AddCandidate(a, ax, ay, false, d, free);
                    }
                }
            }
        }

        // Drops: from every placement where the fish holds something up, moving
        // away down or forward. Lifting is a push upwards and is found above.
        foreach (int placement in _freeList[a])
        {
            if (holds[placement] != _holdGeneration)
            {
                continue;
            }

            (int hx, int hy, bool hLeft) = _router.Decode(placement);
            AddCandidate(a, hx, hy, hLeft, 3, free);
            AddCandidate(a, hx, hy, hLeft, hLeft ? 0 : 1, free);
        }

        if (_level.Models[fish].Goal is GoalKind.Escape or GoalKind.Out)
        {
            foreach ((int ex, int ey) in _escapes)
            {
                for (int d = 0; d < 4; d++)
                {
                    (int dx, int dy) = Directions[d];
                    int ax = ex - dx, ay = ey - dy;
                    if (dx != 0)
                    {
                        AddCandidate(a, ax, ay, dx < 0, d, free);
                    }
                    else
                    {
                        AddCandidate(a, ax, ay, true, d, free);
                        AddCandidate(a, ax, ay, false, d, free);
                    }
                }
            }
        }
    }

    private void AddCandidate(byte a, int x, int y, bool isLeft, int direction, ushort[] free)
    {
        if ((uint)x >= (uint)_width || (uint)y >= (uint)_height)
        {
            return;
        }

        int placement = _router.Placement(x, y, isLeft);
        if (free[placement] == FishField.None)
        {
            return;
        }

        int mark = (placement * 4) + direction;
        if (_candidateMark[mark] == _candidateGeneration)
        {
            return;
        }

        _candidateMark[mark] = _candidateGeneration;
        _candidates.Add((a, placement, direction));
    }

    private bool IsHoldingAt(int[] holds, int x, int y)
    {
        if ((uint)x >= (uint)_width || (uint)y >= (uint)_height)
        {
            return false;
        }

        // What is overhead does not depend on facing.
        return holds[_router.Placement(x, y, true)] == _holdGeneration
            || holds[_router.Placement(x, y, false)] == _holdGeneration;
    }

    /// <summary>The outcome of one simulated key press, kept so a retry can be compared against it.</summary>
    private struct Outcome
    {
        public bool Solved;
        public int ItemBound;
        public int ActorAfter;   // placement, -1 when it left
    }

    /// <summary>
    /// Simulates one candidate and, if it is an event the model can express,
    /// stores the child entry. The steps are numbered as in the plan.
    /// </summary>
    private void TryEdge(int nodeIndex, int g, byte a, int p, int direction, int inRoom, ref long generated)
    {
        int nodeBase = nodeIndex * _unitCount;
        (int dx, int dy) = Directions[direction];
        UnitDef unit = _level.Units[a];
        char symbol = dx < 0 ? unit.Left : dx > 0 ? unit.Right : dy < 0 ? unit.Up : unit.Down;
        (int px, int py, bool pLeft) = _router.Decode(p);
        int fishA = _unitModel[a];

        // 1. Every other fish out of A's way, as far as its free field allows.
        _cellGeneration++;
        MarkShape(_level.Models[fishA].Shape, px, py, dilate: true);
        for (int v = 0; v < _unitCount; v++)
        {
            _run[v] = -1;
            if (v == a || _homes[nodeBase + v] < 0)
            {
                continue;
            }

            _run[v] = FarthestClear(v, px, py, _homes[nodeBase + v]);

            // A placement where the fish holds something up gets it committed,
            // and a committed fish is exact - the margin round A is for the
            // deferred kind. So when it holds something at home too and home
            // only touches that margin, it stays home rather than paying for a
            // step it does not need (cannons: the big fish beside the small
            // one lifting, 41 against 39).
            int stay = _homes[nodeBase + v];
            if (_run[v] >= 0 && _run[v] != stay && HoldsAt(v, _run[v]) && HoldsAt(v, stay)
                && !OverlapsActor(v, stay, fishA, px, py))
            {
                _run[v] = stay;
            }

            if (_run[v] < 0)
            {
                int home = _homes[nodeBase + v];
                if (OverlapsActor(v, home, fishA, px, py))
                {
                    Trace?.Invoke(nodeIndex, a, p, symbol, "the other fish cannot get out of the way");
                    return;
                }

                _run[v] = home;
            }
        }

        if (!Simulate(nodeIndex, a, p, symbol, out Outcome outcome, out bool spoiled))
        {
            // A run the other fish may have spoiled (refused, or it was hurt)
            // gets one more try with it at home, and then one with it clear of
            // where this key's push chain would move (wc: the big fish stands
            // in the bowl's way both farthest off and at home).
            // A refusal counts even with every fish at home: home can be in the
            // way too.
            bool refused = _lastRefused;
            bool happened = spoiled && RetryAtHome(nodeIndex, a, px, py, fishA)
                && Simulate(nodeIndex, a, p, symbol, out outcome, out _);
            if (!happened && (spoiled || refused) && RetryClearOfPush(nodeIndex, a, px, py, dx, dy))
            {
                happened = Simulate(nodeIndex, a, p, symbol, out outcome, out _);
            }

            if (!happened)
            {
                Trace?.Invoke(nodeIndex, a, p, symbol, _simWhy);
                return;
            }
        }

        generated++;

        // 3. The footprint, and 4. what it says about each other fish.
        _footprint.Compute(_room, _items, _itemsBefore, fishA, px, py, dx, dy, _cells);
        int boundUnit = FirstBound(nodeBase, a);
        if (boundUnit >= 0)
        {
            // The fish stood in the event's way. Before committing it there, see
            // whether the event also happens with it standing clear of that
            // footprint: then the outcome does not depend on it after all.
            _room.WriteStateKey(_childKeySaved, _mobile);
            _cellsSaved.Clear();
            _cellsSaved.AddRange(_cells);
            Outcome saved = outcome;
            int savedRun = _run[boundUnit];
            _dependsOn.AsSpan().CopyTo(_dependsSaved);

            _cellGeneration++;
            MarkShape(_level.Models[fishA].Shape, px, py, dilate: true);
            foreach (int cell in _cells)
            {
                _cellMark[cell] = _cellGeneration;
            }

            int clear = FarthestClear(boundUnit, px, py, _homes[nodeBase + boundUnit]);
            bool rescued = false;
            if (clear >= 0)
            {
                _run[boundUnit] = clear;
                Retries++;
                if (Simulate(nodeIndex, a, p, symbol, out Outcome second, out _))
                {
                    _footprint.Compute(_room, _items, _itemsBefore, fishA, px, py, dx, dy, _cells);
                    if (FirstBound(nodeBase, a) < 0)
                    {
                        outcome = second;
                        rescued = true;
                    }
                }
            }

            if (!rescued)
            {
                _run[boundUnit] = savedRun;
                outcome = saved;
                _dependsSaved.AsSpan().CopyTo(_dependsOn);
                _cells.Clear();
                _cells.AddRange(_cellsSaved);
                _childKeySaved.AsSpan().CopyTo(_childKey);
            }
            else
            {
                _room.WriteStateKey(_childKey, _mobile);
            }
        }
        else
        {
            _room.WriteStateKey(_childKey, _mobile);
        }

        // A clean generic outcome - no other fish it depended on - is where an
        // idle fish standing inside the event could make a different one.
        bool clean = true;
        for (int v = 0; v < _unitCount; v++)
        {
            clean &= !_dependsOn[v] || v == a || _homes[nodeBase + v] < 0;
        }

        if (clean)
        {
            _table.Canonicalize(_childKey, _genericLayout);
            _interactionCells.Clear();
            _interactionCells.AddRange(_footprint.InteractionCells);
            _interactionCells.AddRange(_footprint.UnderCells);
            _run.AsSpan().CopyTo(_genericRun);
        }

        PriceAndEmit(nodeIndex, g, a, p, symbol, inRoom, outcome);

        if (clean && _interactionCells.Count > 0)
        {
            TryInteractions(nodeIndex, g, a, p, symbol, dx, dy, inRoom);
        }
    }

    /// <summary>
    /// The interaction children of a clean generic outcome: for each other fish
    /// v, the placements whose cells meet what the moved items covered after
    /// the event or swept through on the way (<see cref="LayoutFootprint.InteractionCells"/>)
    /// or the cells right under where they came to rest (<see cref="LayoutFootprint.UnderCells"/>),
    /// cheapest first by v's free field, one facing per square. Each is run
    /// (v untouched, the arrangement settled, the room still solvable, or it is
    /// skipped), and two kinds become a child with v committed there: the first
    /// that gives a <i>different</i> layout, and the first that gives the
    /// <i>same</i> layout - same items, but v standing inside the event, where
    /// the generic child forbids it. Then the next fish.
    ///
    /// <para>Why those cells: measured over every clean generic outcome on wc,
    /// cannons, noground and broom, every different outcome that could still
    /// be finished came from a placement meeting the after or swept cells
    /// (2.4-6.8 squares an event, against 20-28 touching the old four-way
    /// footprint), and no event had more than one distinct alternative;
    /// <c>MicroChecks.Footprint</c> pins that. The same-layout kind was not in
    /// the plan: wc's optimum needs it - the bowl comes down onto the small
    /// fish's back and also rests on walls, so the items end exactly as without
    /// the fish, and only the under cells contain that square.</para>
    /// </summary>
    private void TryInteractions(int nodeIndex, int g, byte a, int p, char symbol, int dx, int dy, int inRoom)
    {
        int nodeBase = nodeIndex * _unitCount;
        int fishA = _unitModel[a];
        (int px, int py, _) = _router.Decode(p);

        for (int v = 0; v < _unitCount; v++)
        {
            if (v == a || _homes[nodeBase + v] < 0 || IsBusy(nodeIndex, v))
            {
                continue;
            }

            // Squares whose shape meets an interaction cell, priced from the
            // free field (the travel plan below makes the price feasible).
            Shape shape = _level.Models[_unitModel[v]].Shape;
            ushort[] free = _free[v];
            _squareGeneration++;
            _interactionCandidates.Clear();
            foreach (int cell in _interactionCells)
            {
                int cx = cell % _width, cy = cell / _width;
                for (int m = 0; m < shape.MarkX.Length; m++)
                {
                    int ax = cx - shape.MarkX[m], ay = cy - shape.MarkY[m];
                    if ((uint)ax >= (uint)_width || (uint)ay >= (uint)_height)
                    {
                        continue;
                    }

                    int square = (ay * _width) + ax;
                    if (_squareMark[square] == _squareGeneration)
                    {
                        continue;
                    }

                    _squareMark[square] = _squareGeneration;
                    int left = _router.Placement(ax, ay, true), right = _router.Placement(ax, ay, false);
                    int q = free[left] <= free[right] ? left : right;
                    if (free[q] == FishField.None || OverlapsActor(v, q, fishA, px, py))
                    {
                        continue;
                    }

                    _interactionCandidates.Add(((long)free[q] << 32) | (uint)q);
                }
            }

            _interactionCandidates.Sort();
            bool foundDifferent = false, foundSame = false;
            foreach (long candidate in _interactionCandidates)
            {
                if (foundDifferent && foundSame)
                {
                    break;
                }

                int q = (int)(candidate & 0xFFFFFFFF);
                _genericRun.AsSpan().CopyTo(_run);
                _run[v] = q;

                long pressed = PhysicsCalls;
                bool happened = Simulate(nodeIndex, a, p, symbol, out Outcome outcome, out _);
                InteractionPhysics += PhysicsCalls - pressed;
                if (!happened)
                {
                    continue;
                }

                // Either a different layout, or the same one with v standing
                // where the generic child forbids it - inside the event, as on
                // wc, where the bowl comes down around the small fish and the
                // layout is the generic one. One child of each kind per fish.
                _room.WriteStateKey(_childKey, _mobile);
                _table.Canonicalize(_childKey, _canonical);
                bool same = _canonical.AsSpan().SequenceEqual(_genericLayout);
                if (same) { InteractionSameFound++; } else { InteractionDifferentFound++; }
                if (same ? foundSame : foundDifferent)
                {
                    continue;
                }

                if (same)
                {
                    foundSame = true;
                }
                else
                {
                    foundDifferent = true;
                }

                // v is committed where it stood, any further fish judged
                // against this outcome's own footprint.
                _footprint.Compute(_room, _items, _itemsBefore, fishA, px, py, dx, dy, _cells);
                FirstBound(nodeBase, a);
                _dependsOn[v] = true;
                _emitted = false;
                PriceAndEmit(nodeIndex, g, a, p, symbol, inRoom, outcome);
                if (_emitted)
                {
                    InteractionEdges++;
                    if (same) { InteractionSameEmitted++; } else { InteractionDifferentEmitted++; }
                }
            }
        }
    }

    /// <summary>
    /// 4-6 for one outcome: what it says about each other fish (committed where
    /// <see cref="_dependsOn"/>, else a masked field), A's travel price and
    /// feasibility, then the child(ren). The outcome is in <see cref="_childKey"/>,
    /// <see cref="_cells"/> and <paramref name="outcome"/>; the room may be
    /// restored to the node's from here on.
    /// </summary>
    private void PriceAndEmit(int nodeIndex, int g, byte a, int p, char symbol, int inRoom, Outcome outcome)
    {
        int nodeBase = nodeIndex * _unitCount;
        int cost = g + _free[a][p] + 1;
        int boundCount = 0;
        for (int v = 0; v < _unitCount; v++)
        {
            _childCommits[v] = -1;
            _childHomes[v] = -1;
            if (v == a || _homes[nodeBase + v] < 0)
            {
                continue;
            }

            // The child's field of a deferred fish is the solid one masked
            // (RelaxDeferred) - the same array Phi rebuilds later, so its home,
            // its estimate and any plan's end are all taken from it.
            ushort[] masked = _masked[v];
            _solid[v].AsSpan().CopyTo(masked);
            FishField.Mask(masked, _level.Models[_unitModel[v]].Shape, _width, _height, CollectionsMarshal.AsSpan(_cells));

            int run = _run[v];
            int home = _homes[nodeBase + v];

            // The run placement came from the free field. If nothing of the
            // solid one survives the mask, the fish is committed where it ran.
            if (_dependsOn[v] || (masked[home] == FishField.None && !FishField.TryCheapest(masked, out _, out _)))
            {
                _childCommits[v] = run;
                _childHomes[v] = run;
                cost += _free[v][run];
                boundCount++;
                continue;
            }

            if (masked[home] != FishField.None)
            {
                _childHomes[v] = home;
            }
            else
            {
                FishField.TryCheapest(masked, out int cheapest, out _);
                _childHomes[v] = cheapest;
            }
        }

        // 5. A's travel has to be feasible with the other fish where it is.
        int solid = _solid[a][p];
        int free = _free[a][p];
        _childHomes[a] = outcome.ActorAfter;
        _childCommits[a] = -1;
        if (boundCount > 0)
        {
            BoundEdges++;
        }

        if (inRoom != 2)
        {
            // One fish, or more than two (where nothing is ignored, so the two
            // fields agree): the solid field is the price.
            if (solid == FishField.None)
            {
                Trace?.Invoke(nodeIndex, a, p, symbol, "unreachable with the others where they are");
                return;
            }

            EmitChild(nodeIndex, a, p, symbol, cost + solid - free, jointEdge: false, outcome);
            return;
        }

        int b = OtherUnit(nodeIndex, a);
        int homeA = _homes[nodeBase + a], homeB = _homes[nodeBase + b];
        bool bound = _childCommits[b] >= 0;

        // Committed where it already stands, or free and not in A's way: A goes
        // round it, as the solid field already says - no plan needed. (The
        // planner's single-fish orders route with exact support, which asks
        // the engine at every step under an item: 3 ms a call on noground.)
        if (solid != FishField.None && (bound ? _childCommits[b] == homeB : solid == free))
        {
            EmitChild(nodeIndex, a, p, symbol, cost + solid - free, jointEdge: false, outcome);
            return;
        }

        // Otherwise the other fish has to move for A. A committed fish has one
        // place to be and goes there first (TryCommittedFirst); a free one
        // usually only has to step off A's way (TryStepAside), and the joint
        // search - quadratic when B may end anywhere - is its last resort.
        JointCalls++;
        bool planned;
        int joint, endB;
        if (bound)
        {
            // B first, then A - the planner's other order and its joint
            // fallback were measured on all six levels and bought nothing but
            // time (noground: 18,000 fallbacks, 37 s of 60, same answers
            // without them), so a committed fish that cannot go first drops
            // the edge.
            endB = _childCommits[b];
            planned = TryCommittedFirst(nodeIndex, a, p, b, endB, out joint);
        }
        else
        {
            planned = TryStepAside(nodeIndex, a, p, b, out joint, out endB);
            if (planned)
            {
                StepAsides++;
            }
            else
            {
                ushort[] masked = _masked[b];
                for (int q = 0; q < masked.Length; q++)
                {
                    _allowed[q] = masked[q] != FishField.None;
                }

                RestoreNode(nodeIndex);
                planned = _planner.TryJoint(_room, a, p, b, _allowed, out joint, out endB, null, JointMaxStates);
            }
        }

        if (!planned)
        {
            JointFailures++;
        }

        // A free fish also leaves the edge where A goes round it at the solid
        // price, B still deferred - dearer than the plan, but B keeps its field.
        int around = !bound && solid != FishField.None ? cost + solid - free : int.MaxValue;
        int plannedCost = planned ? g + _phi[a][homeA] + _phi[b][homeB] + joint + 1 : int.MaxValue;
        if (plannedCost < around)
        {
            int genericHome = _childHomes[b];
            _childCommits[b] = endB;
            _childHomes[b] = endB;
            EmitChild(nodeIndex, a, p, symbol, plannedCost, jointEdge: true, outcome);
            _childCommits[b] = -1;
            _childHomes[b] = genericHome;
        }

        if (around != int.MaxValue)
        {
            EmitChild(nodeIndex, a, p, symbol, around, jointEdge: false, outcome);
        }
        else if (!planned)
        {
            Trace?.Invoke(nodeIndex, a, p, symbol, "no travel plan");
        }
    }

    /// <summary>
    /// 6. Stores the child of an edge whose other fish are described by
    /// <see cref="_childHomes"/>, <see cref="_childCommits"/> and (for the
    /// deferred ones) <see cref="_masked"/>, with the outcome in
    /// <see cref="_childKey"/> and <see cref="_cells"/>. The estimate combines
    /// as <see cref="ExitHeuristic.Estimate"/> does: fish terms add, the item
    /// bound is a max.
    /// </summary>
    private void EmitChild(int nodeIndex, byte a, int p, char symbol, int cost, bool jointEdge, Outcome outcome)
    {
        int h = 0;
        if (!outcome.Solved)
        {
            long fishSum = 0;
            for (int v = 0; v < _unitCount; v++)
            {
                RelaxedDistance? exit = _unitExit[v];
                if (exit is null || _childHomes[v] < 0)
                {
                    continue;
                }

                int term;
                if (v == a || _childCommits[v] >= 0)
                {
                    (int x, int y, bool left) = _router.Decode(_childHomes[v]);
                    term = exit.At(x, y, left);
                }
                else
                {
                    term = FishField.MinPlus(_masked[v], _width, exit);
                }

                if (term >= RelaxedDistance.Unreachable)
                {
                    Trace?.Invoke(nodeIndex, a, p, symbol, "a fish can no longer get out");
                    return;
                }

                fishSum += term;
            }

            if (outcome.ItemBound >= RelaxedDistance.Unreachable)
            {
                Trace?.Invoke(nodeIndex, a, p, symbol, "an item can no longer get out");
                return;
            }

            h = (int)Math.Max(fishSum, outcome.ItemBound);
        }

        int layout = _table.Intern(_childKey, out bool added);
        if (added)
        {
            _table.Tag(layout) = outcome.ItemBound;
        }

        WriteEntryKey(a, _childHomes);
        if (!_table.TryImprove(layout, _entryKey, cost, out int entry, out _))
        {
            Trace?.Invoke(nodeIndex, a, p, symbol, "not cheaper than an entry already stored");
            return;
        }

        int child = NewNode(nodeIndex, layout, entry, cost, a, symbol, p, _cells, _childHomes, _childCommits);
        _nodes[child].Joint = jointEdge;
        _queue.Push(child, Priority(cost, h));
        _emitted = true;
    }

    /// <summary>
    /// 1-2: builds the run arrangement (A at p, everyone else at <see cref="_run"/>),
    /// presses the key and tests for an event. On success the room holds the
    /// outcome.
    /// </summary>
    /// <param name="spoiled">On failure: whether another fish may have caused it (worth a retry).</param>
    private bool Simulate(int nodeIndex, byte a, int p, char symbol, out Outcome outcome, out bool spoiled)
    {
        outcome = default;
        spoiled = false;
        _lastRefused = false;
        int nodeBase = nodeIndex * _unitCount;
        (int px, int py, bool pLeft) = _router.Decode(p);
        int fishA = _unitModel[a];

        _parentLayout.AsSpan().CopyTo(_runKey);
        bool anyMoved = p != _homes[nodeBase + a];
        bool anyHolding = anyMoved && _holdsAtHome[a];
        int movedCount = anyMoved ? 1 : 0;
        StateKeyEdit.SetPlacement(_runKey, _unitSlot[a], px, py, pLeft);
        for (int v = 0; v < _unitCount; v++)
        {
            if (v == a || _homes[nodeBase + v] < 0)
            {
                continue;
            }

            (int x, int y, bool left) = _router.Decode(_run[v]);
            StateKeyEdit.SetPlacement(_runKey, _unitSlot[v], x, y, left);
            if (_run[v] != _homes[nodeBase + v])
            {
                movedCount++;
                anyHolding |= _holdsAtHome[v];
            }
        }

        _room.RestoreMobile(_runKey, _mobile);
        _roomNode = -1;

        // Each fish's placement is reachable with the others at home, but two
        // fish that shared the support of one item can both have left it: then
        // the arrangement itself is not settled and the "event" would be an
        // artifact of building it.
        if (movedCount >= 2 && anyHolding && _room.WouldDisturbWithFishAt(fishA, px, py))
        {
            spoiled = true;
            _simWhy = "unsettled";
            return false;
        }

        PhysicsCalls++;
        if (!_room.ApplyMove(symbol))
        {
            // Any fish moved off its home may have blocked it - not only one
            // ahead of A: a pushed item can reach back past the pusher (on
            // noground the big fish lifts a frame whose bottom is 8 rows below
            // it), so there is no cheap geometric test.
            spoiled = movedCount > (anyMoved ? 1 : 0);
            _simWhy = "refused";
            _lastRefused = true;
            return false;
        }

        _reduction.ThrowIfFrozenMoved(_room);

        if (!SomethingHappened())
        {
            _simWhy = "no event";
            return false;
        }

        // The other fish must come through untouched: one that was hit (or let
        // out) was in the way, and is worth one try at home.
        for (int v = 0; v < _unitCount; v++)
        {
            if (v == a || _homes[nodeBase + v] < 0)
            {
                continue;
            }

            if (!InRoom(_room, _unitModel[v], out int now) || now != _run[v])
            {
                spoiled = true;
                _simWhy = "other fish hit";
                return false;
            }
        }

        bool solved = _room.IsSolved();
        if (!solved && (!_room.IsSolvable() || _room.CannotMove()))
        {
            // A fish died. That can be the idle fish's doing - by not standing
            // where it held something up - so this is worth a try at home too.
            spoiled = movedCount > (anyMoved ? 1 : 0);
            _simWhy = "unsolvable";
            return false;
        }

        outcome.Solved = solved;
        outcome.ItemBound = _heuristic.ItemBound(_room);
        outcome.ActorAfter = InRoom(_room, fishA, out int after) ? after : -1;
        return true;
    }

    /// <summary>
    /// The sequential plan for a fish in A's way: B swims (A solid at home) to
    /// the nearest allowed placement clear of every cell A's route from home
    /// passes through with B ignored, then A swims to p with B solid there.
    /// Not always the cheapest interleaving, but two routes instead of a joint
    /// search, and feasible by construction. Leaves the room as the node's.
    /// </summary>
    private bool TryStepAside(int nodeIndex, byte a, int p, int b, out int cost, out int endB)
    {
        cost = -1;
        endB = -1;
        int fishA = _unitModel[a], fishB = _unitModel[b];
        RestoreNode(nodeIndex);

        _routeA.Route(_room, fishA, fishB);
        if (!Reached(_routeA, p))
        {
            return false;
        }

        _cellGeneration++;
        Shape shapeA = _level.Models[fishA].Shape;
        for (int q = p; q >= 0; q = _routeA.PredecessorOf(q))
        {
            (int x, int y, _) = _routeA.Decode(q);
            MarkShape(shapeA, x, y, dilate: false);
        }

        _routeB.Route(_room, fishB);
        Shape shapeB = _level.Models[fishB].Shape;
        ushort[] masked = _masked[b];
        int target = -1;
        foreach (int q in _routeB.Reached)
        {
            (int x, int y, _) = _routeB.Decode(q);
            if (masked[q] != FishField.None && !CoversMarked(shapeB, x, y))
            {
                target = q;   // reached in BFS order: the first is the cheapest
                break;
            }
        }

        if (target < 0)
        {
            return false;
        }

        int costB = _routeB.CostOf(target);
        (int bx, int by, bool bLeft) = _routeB.Decode(target);
        _nodeKey.AsSpan().CopyTo(_runKey);
        StateKeyEdit.SetPlacement(_runKey, _unitSlot[b], bx, by, bLeft);
        _room.RestoreMobile(_runKey, _mobile);
        _roomNode = -1;

        _routeA.Route(_room, fishA);
        bool ok = Reached(_routeA, p);
        if (ok)
        {
            cost = costB + _routeA.CostOf(p);
            endB = target;
        }

        RestoreNode(nodeIndex);
        return ok;
    }

    /// <summary>
    /// The plan for a fish committed away from its home, B first: B swims there
    /// (A solid at home), then A to p with B solid there. The two routes depend
    /// only on (node, A, B, B's target), and one expansion asks for the same
    /// target over and over - every candidate of A against a B committed to its
    /// run placement - so they are kept for the last such quadruple and only
    /// A's cost at p is read per call. Without that, re-planning this with
    /// <see cref="SegmentPlanner.TryCompose"/> was 42 of 60 seconds on noground.
    /// False when B-first does not work.
    /// </summary>
    private bool TryCommittedFirst(int nodeIndex, byte a, int p, int b, int target, out int cost)
    {
        cost = -1;
        if (_commitNode != nodeIndex || _commitA != a || _commitB != b || _commitTarget != target)
        {
            _commitNode = nodeIndex;
            _commitA = a;
            _commitB = b;
            _commitTarget = target;
            _commitCostB = -1;
            _commitReachGeneration++;

            RestoreNode(nodeIndex);
            _commitRouteB.Route(_room, _unitModel[b]);
            if (Reached(_commitRouteB, target))
            {
                _commitCostB = _commitRouteB.CostOf(target);
                (int bx, int by, bool bLeft) = _commitRouteB.Decode(target);
                _nodeKey.AsSpan().CopyTo(_runKey);
                StateKeyEdit.SetPlacement(_runKey, _unitSlot[b], bx, by, bLeft);
                _room.RestoreMobile(_runKey, _mobile);
                _roomNode = -1;

                _commitRouteA.Route(_room, _unitModel[a]);
                foreach (int q in _commitRouteA.Reached)
                {
                    _commitReach[q] = _commitReachGeneration;
                }

                RestoreNode(nodeIndex);
            }
        }

        if (_commitCostB < 0 || _commitReach[p] != _commitReachGeneration)
        {
            return false;
        }

        cost = _commitCostB + _commitRouteA.CostOf(p);
        return true;
    }

    private static bool Reached(InertRouter router, int placement)
    {
        IReadOnlyList<int> reached = router.Reached;
        for (int i = 0; i < reached.Count; i++)
        {
            if (reached[i] == placement)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The third try: every other fish at the farthest placement of its free
    /// field (as in the first run) that also keeps clear of where the push
    /// chain of this key would move - A and everything in front of it, shifted
    /// one step. False if that changes nothing. (Shifting every item instead
    /// was too broad: on wc it ruled out the one square that works.)
    /// </summary>
    private bool RetryClearOfPush(int nodeIndex, byte a, int px, int py, int dx, int dy)
    {
        int nodeBase = nodeIndex * _unitCount;
        int fishA = _unitModel[a];
        RestoreNode(nodeIndex);
        _cellGeneration++;
        MarkShape(_level.Models[fishA].Shape, px, py, dilate: true);

        // The push chain, read off the node's room the way the engine collects
        // resistance: whatever (other than a fish) the pusher's shape would
        // move into, then whatever that would move into, and so on. Each
        // member's shifted cells are where it would go.
        _chainModels.Clear();
        _chainSeen.Clear();
        _chainModels.Add((fishA, px, py));
        for (int i = 0; i < _chainModels.Count; i++)
        {
            (int model, int mx, int my) = _chainModels[i];
            Shape shape = _level.Models[model].Shape;
            MarkShape(shape, mx + dx, my + dy, dilate: false);
            for (int m = 0; m < shape.MarkX.Length; m++)
            {
                int other = _room.GetModel(mx + shape.MarkX[m] + dx, my + shape.MarkY[m] + dy);
                if (other == Room.Empty || other == model || other >= _level.ModelCount
                    || _level.Models[other].IsAlive || _reduction.IsFrozen(other) || !_chainSeen.Add(other))
                {
                    continue;
                }

                (int ox, int oy, _) = _room.Position(other);
                _chainModels.Add((other, ox, oy));
            }
        }

        bool changed = false;
        for (int v = 0; v < _unitCount; v++)
        {
            if (v == a || _homes[nodeBase + v] < 0)
            {
                continue;
            }

            int clear = FarthestClear(v, px, py, _homes[nodeBase + v]);
            if (clear >= 0 && clear != _run[v])
            {
                _run[v] = clear;
                changed = true;
            }
        }

        if (changed)
        {
            Retries++;
        }

        return changed;
    }

    /// <summary>Puts every other fish back at its home for a second run, if that is a different arrangement at all.</summary>
    private bool RetryAtHome(int nodeIndex, byte a, int px, int py, int fishA)
    {
        int nodeBase = nodeIndex * _unitCount;
        bool changed = false;
        for (int v = 0; v < _unitCount; v++)
        {
            if (v == a || _homes[nodeBase + v] < 0)
            {
                continue;
            }

            int home = _homes[nodeBase + v];
            if (OverlapsActor(v, home, fishA, px, py))
            {
                return false;
            }

            changed |= _run[v] != home;
            _run[v] = home;
        }

        if (changed)
        {
            Retries++;
        }

        return changed;
    }

    /// <summary>
    /// The first other fish the outcome may have depended on, or -1: its run
    /// placement is covered by the footprint in <see cref="_cells"/>, or it was
    /// holding something up there. The second is not covered by any footprint
    /// - an item that needs the idle fish to stay up does not move, so nothing
    /// marks it - and it is real: on <c>submarine</c> the big fish is what keeps
    /// a pipe off the small one while the small one pushes elsewhere
    /// (micro-level <c>support-bound</c>).
    /// </summary>
    private int FirstBound(int nodeBase, byte a)
    {
        _cellGeneration++;
        foreach (int cell in _cells)
        {
            _cellMark[cell] = _cellGeneration;
        }

        int first = -1;
        int fishA = _unitModel[a];
        for (int v = 0; v < _unitCount; v++)
        {
            _dependsOn[v] = false;
            if (v == a || _homes[nodeBase + v] < 0)
            {
                continue;
            }

            (int x, int y, _) = _router.Decode(_run[v]);
            Shape shape = _level.Models[_unitModel[v]].Shape;
            if (CoversMarked(shape, x, y) || NeedsIdleSupport(v, x, y, fishA))
            {
                _dependsOn[v] = true;
                if (first < 0)
                {
                    first = v;
                }
            }
        }

        return first;
    }

    /// <summary>
    /// Whether, in the outcome the room holds, something stays up only because
    /// unit v stands at (x, y): coarse first (anything mobile on it at all),
    /// then exactly - the engine asked whether the room would still be settled
    /// with v gone. "Gone" is v moved off the grid, where it covers no cell:
    /// nothing rests on it there and the border it would see above itself
    /// counts as wall, so no load is put on it either.
    /// </summary>
    private bool NeedsIdleSupport(int v, int x, int y, int fishA) =>
        LayoutFootprint.NeedsIdleSupport(_room, _router, _unitModel[v], x, y, fishA);

    /// <summary>
    /// The free-field placement of unit v farthest (Manhattan, anchors) from
    /// (px, py) whose cells avoid everything marked in <see cref="_cellMark"/>
    /// this generation; ties go to the cheaper, then the lower id - the order
    /// <see cref="FishField.TryFarthest"/> uses.
    /// </summary>
    private int FarthestClear(int v, int px, int py, int home)
    {
        Shape shape = _level.Models[_unitModel[v]].Shape;
        ushort[] free = _free[v];

        // Two tiers. A placement where the fish holds nothing up gives an
        // outcome that holds wherever the fish waits outside the footprint, so
        // the farthest of those is taken. Where the fish holds something up,
        // the outcome may need it there and it gets committed - so then it is
        // best left where it already is, or moved as little as possible.
        int best = -1, bestDistance = -1, bestCost = int.MaxValue;
        int held = -1, heldCost = int.MaxValue;
        foreach (int q in _freeList[v])
        {
            (int x, int y, _) = _router.Decode(q);
            if (CoversMarked(shape, x, y))
            {
                continue;
            }

            if (HoldsAt(v, q))
            {
                int rank = q == home ? -1 : free[q];
                if (rank < heldCost)
                {
                    held = q;
                    heldCost = rank;
                }

                continue;
            }

            int distance = Math.Abs(x - px) + Math.Abs(y - py);
            if (distance > bestDistance || (distance == bestDistance && free[q] < bestCost))
            {
                best = q;
                bestDistance = distance;
                bestCost = free[q];
            }
        }

        return best >= 0 ? best : held;
    }

    private bool HoldsAt(int unit, int placement) => _holdMark[unit][placement] == _holdGeneration;

    private void MarkShape(Shape shape, int x, int y, bool dilate)
    {
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            int cx = x + shape.MarkX[m], cy = y + shape.MarkY[m];
            Mark(cx, cy);
            if (dilate)
            {
                Mark(cx - 1, cy);
                Mark(cx + 1, cy);
                Mark(cx, cy - 1);
                Mark(cx, cy + 1);
            }
        }

        void Mark(int cx, int cy)
        {
            if ((uint)cx < (uint)_width && (uint)cy < (uint)_height)
            {
                _cellMark[(cy * _width) + cx] = _cellGeneration;
            }
        }
    }

    private bool CoversMarked(Shape shape, int x, int y)
    {
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            int cx = x + shape.MarkX[m], cy = y + shape.MarkY[m];
            if ((uint)cx < (uint)_width && (uint)cy < (uint)_height && _cellMark[(cy * _width) + cx] == _cellGeneration)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether unit v standing at <paramref name="placement"/> shares a cell with the actor at (px, py).</summary>
    private bool OverlapsActor(int v, int placement, int fishA, int px, int py)
    {
        Shape a = _level.Models[fishA].Shape;
        Shape b = _level.Models[_unitModel[v]].Shape;
        (int bx, int by, _) = _router.Decode(placement);
        for (int i = 0; i < a.MarkX.Length; i++)
        {
            int ax = px + a.MarkX[i], ay = py + a.MarkY[i];
            for (int j = 0; j < b.MarkX.Length; j++)
            {
                if (bx + b.MarkX[j] == ax && by + b.MarkY[j] == ay)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>An item moved relative to the node, or a fish left (compared against the node, not tested outright).</summary>
    private bool SomethingHappened()
    {
        for (int i = 0; i < _items.Length; i++)
        {
            ref readonly ModelState s = ref _room.State(_items[i]);
            if ((s.X, s.Y, s.IsOut || s.IsLost) != _itemsBefore[i])
            {
                return true;
            }
        }

        for (int u = 0; u < _unitCount; u++)
        {
            ref readonly ModelState s = ref _room.State(_unitModel[u]);
            if ((s.IsOut || s.IsLost) != _goneBefore[u])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The "with both fish removed" test of <see cref="ItemSolver"/>: only wall or a frozen model rules a push out.</summary>
    private bool CanPossiblyMove(int item, int ix, int iy, int dx, int dy)
    {
        Shape shape = _level.Models[item].Shape;
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            int cx = ix + shape.MarkX[m] + dx, cy = iy + shape.MarkY[m] + dy;
            if ((uint)cx >= (uint)_width || (uint)cy >= (uint)_height)
            {
                continue;
            }

            if (_reduction.Walls[(cy * _width) + cx])
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

    // ---------------------------------------------------------------- fields --

    /// <summary>
    /// φ(node, unit) into <paramref name="destination"/>: walks up the parents to
    /// the nearest point or cached field, then back down - relax in the parent's
    /// room (<see cref="RelaxDeferred"/>), mask with the child's footprint -
    /// caching every step.
    /// </summary>
    private void Phi(int node, int unit, ushort[] destination)
    {
        _chain.Clear();
        int n = node;
        bool relaxed = false;   // whether destination holds Φ(n) rather than φ(n)
        while (true)
        {
            if (IsPoint(n, unit))
            {
                FishField.SetPoint(destination, _homes[(n * _unitCount) + unit]);
                break;
            }

            if (_cache.TryGet(FieldKey(n, unit, relaxed: false), out ushort[] cached))
            {
                cached.AsSpan().CopyTo(destination);
                break;
            }

            _chain.Add(n);
            int parent = _nodes[n].Parent;
            if (_cache.TryGet(FieldKey(parent, unit, relaxed: true), out cached))
            {
                cached.AsSpan().CopyTo(destination);
                n = parent;
                relaxed = true;
                break;
            }

            n = parent;
        }

        Shape shape = _level.Models[_unitModel[unit]].Shape;
        for (int i = _chain.Count - 1; i >= 0; i--)
        {
            int child = _chain[i];
            if (!relaxed)
            {
                RelaxDeferred(n, unit, destination);
                destination.AsSpan().CopyTo(_cache.Rent(FieldKey(n, unit, relaxed: true)));
            }

            FishField.Mask(destination, shape, _width, _height, Cells(child));
            destination.AsSpan().CopyTo(_cache.Rent(FieldKey(child, unit, relaxed: false)));
            n = child;
            relaxed = false;
        }
    }

    /// <summary>
    /// φ → Φ in the node's room: the deferred fish's inert travel with every
    /// other fish <b>solid</b> at its home - the field a child inherits.
    ///
    /// <para>The plan relaxed this "free" (the other fish passed through). That
    /// priced a deferred fish's travel through a layout as though the other fish
    /// were not there, and nothing re-checks those legs before reconstruction:
    /// on <c>noground</c> the replayed string came out at 52 for a g of 44, the
    /// gap being the big fish swimming round the small one in a layout where the
    /// field had let it through. Solid, the same search replays at 44 (the
    /// optimum), and no level measured lost anything (docs/024). The free field
    /// is still what an acting fish's candidates come from (<see cref="RelaxFree"/>),
    /// with the gap closed by a travel plan at the edge.</para>
    /// </summary>
    private void RelaxDeferred(int node, int unit, ushort[] field)
    {
        if (!IsBusy(node, unit))
        {
            RestoreNode(node);
            _router.Relax(_room, _unitModel[unit], field);
        }
    }

    /// <summary>
    /// φ → the acting fish's free field in the node's room: inert travel with
    /// the other fish passed through as though absent (it still holds things up
    /// where its home is). With more than two fish in the room nothing is
    /// ignored.
    /// </summary>
    private void RelaxFree(int node, int unit, ushort[] field)
    {
        if (IsBusy(node, unit))
        {
            return;
        }

        int inRoom = 0;
        int other = -1;
        for (int u = 0; u < _unitCount; u++)
        {
            if (_homes[(node * _unitCount) + u] >= 0)
            {
                inRoom++;
                if (u != unit)
                {
                    other = _unitModel[u];
                }
            }
        }

        RestoreNode(node);
        _router.Relax(_room, _unitModel[unit], field, inRoom == 2 ? other : -1);
    }

    private bool IsPoint(int node, int unit) =>
        _nodes[node].Parent < 0 || _nodes[node].Actor == unit || _commits[(node * _unitCount) + unit] >= 0;

    private bool IsBusy(int node, int unit)
    {
        ReadOnlySpan<byte> layout = _table.LayoutKey(_nodes[node].Layout);
        return (layout[(_unitSlot[unit] * StateKeyEdit.SlotSize) + 4] & 16) != 0;
    }

    private int FieldKey(int node, int unit, bool relaxed) => (((node * _unitCount) + unit) * 2) + (relaxed ? 1 : 0);

    private ReadOnlySpan<int> Cells(int node)
    {
        // Footprints are pooled as ushort; widen into a list of their own.
        _cellsWide.Clear();
        int start = _nodes[node].CellStart;
        for (int i = 0; i < _nodes[node].CellCount; i++)
        {
            _cellsWide.Add(_cellPool[start + i]);
        }

        return CollectionsMarshal.AsSpan(_cellsWide);
    }

    private int OtherFish(int node, int unit)
    {
        int other = OtherUnit(node, unit);
        return other < 0 ? -1 : _unitModel[other];
    }

    private int OtherUnit(int node, int unit)
    {
        for (int u = 0; u < _unitCount; u++)
        {
            if (u != unit && _homes[(node * _unitCount) + u] >= 0)
            {
                return u;
            }
        }

        return -1;
    }

    /// <summary>Puts the node's concrete room into <see cref="_room"/>: its layout with every fish at home.</summary>
    private void RestoreNode(int node)
    {
        if (_roomNode == node)
        {
            return;
        }

        _table.LayoutKey(_nodes[node].Layout).CopyTo(_nodeKey);
        for (int u = 0; u < _unitCount; u++)
        {
            int home = _homes[(node * _unitCount) + u];
            if (home >= 0)
            {
                (int x, int y, bool left) = _router.Decode(home);
                StateKeyEdit.SetPlacement(_nodeKey, _unitSlot[u], x, y, left);
            }
        }

        _room.RestoreMobile(_nodeKey, _mobile);
        _roomNode = node;
    }

    // -------------------------------------------------------- reconstruction --

    /// <summary>
    /// Replays the path concretely from the level's start: per edge, the other
    /// fish to wherever the edge needs it (committed, or forced out of the
    /// footprint) and the actor to its placement, then the key - each checked
    /// against the node it should arrive at. Null if any segment cannot be
    /// built; the caller goes on searching.
    /// </summary>
    private string? Reconstruct(int goal)
    {
        _chain.Clear();
        for (int n = goal; n >= 0; n = _nodes[n].Parent)
        {
            _chain.Add(n);
        }

        _chain.Reverse();
        int[] chain = [.. _chain];   // Phi below reuses _chain
        int[] waypoints = Waypoints(chain);

        Room room = _replay;
        room.Reset();
        var moves = new List<char>();

        for (int i = 1; i < chain.Length; i++)
        {
            string? failure = ReplayEdge(room, chain[i], waypoints.AsSpan(i * _unitCount, _unitCount), moves);
            if (failure is not null)
            {
                ReconstructionRejects++;
                LastRejectReason = $"edge {i}/{chain.Length - 1}: {failure}";
                return null;
            }
        }

        string solution = new([.. moves]);
        if (!SolutionValidator.Validate(new Room(_level), solution).Solved)
        {
            ReplayFailures++;
            ReconstructionRejects++;
            LastRejectReason = "the replayed string does not solve the level";
            return null;
        }

        return solution;
    }

    /// <summary>
    /// Where each fish stood at each node of the path, by the fields' own
    /// account: walking back from the goal, every placement a fish was at (or
    /// acted from) is traced to the cheapest placement of the parent's field it
    /// can be reached from by inert travel in the parent's room - the travel the
    /// field priced. Replaying to these instead of "wherever the fish happens to
    /// be" is what makes the concrete string follow g: a fish that the field
    /// let cross an earlier layout before an item closed the way does cross it
    /// then. Indexed [chain position * units + unit]; -1 where the fish is out.
    /// </summary>
    private int[] Waypoints(int[] chain)
    {
        var waypoints = new int[chain.Length * _unitCount];
        for (int v = 0; v < _unitCount; v++)
        {
            int target = _homes[(chain[^1] * _unitCount) + v];
            for (int i = chain.Length - 1; i >= 0; i--)
            {
                int node = chain[i];
                int home = _homes[(node * _unitCount) + v];
                if (home < 0)
                {
                    waypoints[(i * _unitCount) + v] = -1;
                    target = -1;
                    continue;
                }

                if (target < 0)
                {
                    target = home;
                }

                waypoints[(i * _unitCount) + v] = target;
                if (i == 0)
                {
                    break;
                }

                // Where in the parent's relaxed field this placement came from.
                int parent = chain[i - 1];
                Node edge = _nodes[node];
                bool acted = edge.Actor == v;
                int commit = _commits[(node * _unitCount) + v];
                if (edge.Joint && (acted || commit >= 0))
                {
                    // A joint plan starts both fish at their homes.
                    target = _homes[(parent * _unitCount) + v];
                    continue;
                }

                int from = acted ? edge.ActorPlacement : commit >= 0 ? commit : target;
                target = Backtrack(parent, v, from);
            }
        }

        return waypoints;
    }

    /// <summary>
    /// The placement w of the node's field minimising field[w] + (inert distance
    /// from w to <paramref name="target"/> in the node's room, the other fish
    /// passed through as in <see cref="RelaxFree"/>) - found by a breadth-first
    /// search backwards from the target. The node's home if nothing reaches it.
    /// </summary>
    private int Backtrack(int node, int unit, int target)
    {
        int home = _homes[(node * _unitCount) + unit];
        ushort[] phi = _masked[unit];   // free during reconstruction
        Phi(node, unit, phi);
        if (IsBusy(node, unit))
        {
            return phi[target] != FishField.None ? target : home;
        }

        int inRoom = 0, other = -1;
        for (int u = 0; u < _unitCount; u++)
        {
            if (_homes[(node * _unitCount) + u] >= 0)
            {
                inRoom++;
                if (u != unit)
                {
                    other = _unitModel[u];
                }
            }
        }

        int ignore = -1;
        int fish = _unitModel[unit];
        RestoreNode(node);

        _backGeneration++;
        int head = 0, tail = 0;
        _backQueue[tail++] = target;
        _backStamp[target] = _backGeneration;
        _backDistance[target] = 0;

        int best = -1, bestCost = int.MaxValue;
        while (head < tail)
        {
            int x = _backQueue[head++];
            int d = _backDistance[x];
            if (d >= bestCost)
            {
                break; // every later pop is at least this far, and a field is never negative
            }

            if (phi[x] != FishField.None && phi[x] + d < bestCost)
            {
                best = x;
                bestCost = phi[x] + d;
            }

            (int cx, int cy, bool left) = _router.Decode(x);

            // Predecessors: a turn in place (always inert), a vertical step that
            // keeps facing, and a step forward in this facing.
            BackVisit(_router.Placement(cx, cy, !left), d + 1, ref tail);
            BackStep(fish, cx, cy + 1, cx, cy, left, d, ignore, ref tail);
            BackStep(fish, cx, cy - 1, cx, cy, left, d, ignore, ref tail);
            BackStep(fish, left ? cx + 1 : cx - 1, cy, cx, cy, left, d, ignore, ref tail);
        }

        return best >= 0 ? best : home;
    }

    private void BackStep(int fish, int fx, int fy, int tx, int ty, bool left, int d, int ignore, ref int tail)
    {
        if ((uint)fx >= (uint)_width || (uint)fy >= (uint)_height)
        {
            return;
        }

        int from = _router.Placement(fx, fy, left);
        if (_backStamp[from] == _backGeneration || !_router.IsFreeStep(_room, fish, fx, fy, tx, ty, ignore))
        {
            return;
        }

        BackVisit(from, d + 1, ref tail);
    }

    private void BackVisit(int placement, int d, ref int tail)
    {
        if (_backStamp[placement] == _backGeneration)
        {
            return;
        }

        _backStamp[placement] = _backGeneration;
        _backDistance[placement] = d;
        _backQueue[tail++] = placement;
    }

    private string? ReplayEdge(Room room, int node, ReadOnlySpan<int> waypoints, List<char> moves)
    {
        Node edge = _nodes[node];
        int a = edge.Actor;
        int fishA = _unitModel[a];
        int nodeBase = node * _unitCount;
        int p = edge.ActorPlacement;
        (int px, int py, bool pLeft) = _router.Decode(p);

        // The footprint, to tell whether a fish may stay where it stands.
        _cellGeneration++;
        int start = edge.CellStart;
        for (int i = 0; i < edge.CellCount; i++)
        {
            _cellMark[_cellPool[start + i]] = _cellGeneration;
        }

        // Each other fish ends the segment at its waypoint; failing that, where
        // the edge committed it, or else where it stands (if clear of the
        // footprint) or the child's home - distinct ones only, in that order.
        var others = new List<(int Unit, int[] Targets)>();
        for (int v = 0; v < _unitCount; v++)
        {
            if (v == a || !InRoom(room, _unitModel[v], out int now))
            {
                continue;
            }

            int commit = _commits[nodeBase + v];
            int home = _homes[nodeBase + v];
            (int x, int y, _) = _router.Decode(now);
            bool clear = !CoversMarked(_level.Models[_unitModel[v]].Shape, x, y);
            var targets = new List<int>(4);
            if (commit >= 0)
            {
                targets.Add(commit);
            }
            else
            {
                foreach (int t in (ReadOnlySpan<int>)[waypoints[v], clear ? now : -1, home])
                {
                    if (t >= 0 && !targets.Contains(t))
                    {
                        targets.Add(t);
                    }
                }
            }

            others.Add((v, [.. targets]));
        }

        _segment.Clear();
        if (!InRoom(room, fishA, out _))
        {
            return "the actor is not in the room";
        }

        if (others.Count <= 1)
        {
            bool ok;
            if (others.Count == 0)
            {
                ok = RouteAlone(room, a, p, _segment);
            }
            else
            {
                (int b, int[] targets) = others[0];
                ok = false;
                foreach (int target in targets)
                {
                    _segment.Clear();
                    if (_planner.TryCompose(room, a, p, b, target, _segment, ComposeMaxStates))
                    {
                        ok = true;
                        break;
                    }
                }
            }

            if (!ok)
            {
                return $"no travel for {_level.Models[fishA].Kind} to ({px},{py}){(pLeft ? "<" : ">")}";
            }

            if (!Apply(room, _segment, 0))
            {
                return "a travel move was refused";
            }
        }
        else
        {
            // More than two fish: each one that has to move goes on its own,
            // then the actor.
            foreach ((int v, int[] targets) in others)
            {
                InRoom(room, _unitModel[v], out int now);
                int target = targets.Length > 0 ? targets[0] : now;
                if (now == target)
                {
                    continue;
                }

                int before = _segment.Count;
                if (!RouteAlone(room, v, target, _segment) || !Apply(room, _segment, before))
                {
                    return "a third fish cannot get out of the way";
                }
            }

            int mark = _segment.Count;
            if (!RouteAlone(room, a, p, _segment) || !Apply(room, _segment, mark))
            {
                return "the actor cannot get there past the others";
            }
        }

        moves.AddRange(_segment);
        if (!InRoom(room, fishA, out int at) || at != p)
        {
            return "the actor is not where the edge needs it";
        }

        if (!room.ApplyMove(edge.Symbol))
        {
            return $"key '{edge.Symbol}' was refused";
        }

        moves.Add(edge.Symbol);

        room.WriteStateKey(_childKey, _mobile);
        _table.Canonicalize(_childKey, _canonical);
        if (!_canonical.AsSpan().SequenceEqual(_table.LayoutKey(edge.Layout)))
        {
            return $"key '{edge.Symbol}' gave a different layout";
        }

        int expected = _homes[nodeBase + a];
        int actual = InRoom(room, fishA, out int afterA) ? afterA : -1;
        if (expected != actual)
        {
            return "the actor ended somewhere else";
        }

        return null;
    }

    private bool RouteAlone(Room room, int unit, int target, List<char> destination)
    {
        _router.Route(room, _unitModel[unit]);
        foreach (int q in _router.Reached)
        {
            if (q == target)
            {
                _router.WriteRoute(target, _level.Units[unit], destination);
                return true;
            }
        }

        return false;
    }

    private static bool Apply(Room room, List<char> symbols, int from)
    {
        for (int i = from; i < symbols.Count; i++)
        {
            if (!room.ApplyMove(symbols[i]))
            {
                return false;
            }
        }

        return true;
    }

    // --------------------------------------------------------------- helpers --

    /// <summary>The fish's placement, if it is standing in the room.</summary>
    private bool InRoom(Room room, int fish, out int placement)
    {
        placement = -1;
        ref readonly ModelState s = ref room.State(fish);
        if (!s.IsAlive || s.IsOut || s.IsLost || (uint)s.X >= (uint)_width || (uint)s.Y >= (uint)_height)
        {
            return false;
        }

        placement = _router.Placement(s.X, s.Y, s.IsLeft);
        return true;
    }

    private void WriteEntryKey(byte pointUnit, int[] homes)
    {
        _entryKey[0] = pointUnit;
        for (int u = 0; u < _unitCount; u++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_entryKey.AsSpan(1 + (2 * u)), homes[u] < 0 ? ushort.MaxValue : (ushort)homes[u]);
        }
    }

    /// <summary>Squares a fish would be carried out of the room from - as <see cref="ItemSolver"/> finds them.</summary>
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

    private SolveResult Abandoned(string status, long expanded, long generated, int deepestF, Stopwatch stopwatch) =>
        new(null, false, status, expanded, generated, _table.EntryCount, deepestF, stopwatch.Elapsed);

    private int Priority(int g, int h) =>
        _weight <= 1.0 ? g + h : g + (int)Math.Round(_weight * h);

    private int NewNode(
        int parent, int layout, int entry, int g, byte actor, char symbol, int actorPlacement,
        List<int> cells, int[] homes, int[] commits)
    {
        if (_nodeCount == _nodes.Length)
        {
            Array.Resize(ref _nodes, _nodes.Length * 2);
            Array.Resize(ref _homes, _nodes.Length * _unitCount);
            Array.Resize(ref _commits, _nodes.Length * _unitCount);
        }

        if (_cellTop + cells.Count > _cellPool.Length)
        {
            Array.Resize(ref _cellPool, Math.Max(_cellPool.Length * 2, _cellTop + cells.Count));
        }

        int cellStart = _cellTop;
        foreach (int cell in cells)
        {
            _cellPool[_cellTop++] = (ushort)cell;
        }

        _nodes[_nodeCount] = new Node
        {
            Parent = parent,
            Layout = layout,
            Entry = entry,
            G = g,
            Actor = actor,
            Symbol = symbol,
            ActorPlacement = actorPlacement,
            CellStart = cellStart,
            CellCount = cells.Count,
        };

        int nodeBase = _nodeCount * _unitCount;
        for (int u = 0; u < _unitCount; u++)
        {
            _homes[nodeBase + u] = homes[u];
            _commits[nodeBase + u] = commits[u];
        }

        return _nodeCount++;
    }
}
