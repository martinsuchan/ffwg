using System.Collections.Concurrent;

using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// The stuck-item dead-end rule: whether a state has no solution because
/// something that has to leave the room never can again. Shared, thread-safe
/// front end to <see cref="StuckAnalysis"/> for the main solver's serial and
/// parallel loops (docs/020).
///
/// <para><b>What it condemns.</b> Everything <see cref="StuckAnalysis.Mobile"/>
/// proves immobile is scenery from here on, so its cells count as wall exactly,
/// not optimistically. Then a state is dead when</para>
/// <list type="bullet">
/// <item>a fish that has to get out (goal_escape) stands where it can no longer
///   reach any border through what is left - the items that could still move
///   are treated as absent by that reachability, and the ones treated as wall
///   are never going anywhere; or</item>
/// <item>an item that has to be pushed out (goal_out) is itself among the
///   immobile - it will never move again, so it will never leave.</item>
/// </list>
///
/// <para>The classic case is an item dropped down a shaft it can never come back
/// out of. In <c>society</c>, opening with the big fish going right puts the
/// heavy bar into a one-wide well on top of an item that is itself walled in on
/// three sides; neither can move again, and the big fish - four cells wide - is
/// left with no route to the exit. Nothing died and no goal became unreachable
/// in the walls-only relaxation, so every cheaper test passes it and a search
/// without this rule explores the whole dead subtree: 61-63% of everything
/// stored on <c>wc</c> and <c>society</c>.</para>
///
/// <para><b>Why it is cached per item layout.</b> The analysis runs with the
/// fish anywhere (<c>fishAnywhere</c>), so its result depends only on the items
/// and on which fish are still in play - never on where the fish stand. Item
/// layouts are 100-3,000x fewer than states, so one analysis serves hundreds of
/// states, and the verdict is a pure function of its key: computing it twice on
/// two threads gives the same answer, which is what keeps the parallel search
/// identical to the serial one. The fish flags are part of the key because who
/// can push, and how hard, depends on them (review C1).</para>
/// </summary>
internal sealed class StuckDetector
{
    /// <summary>Layouts kept before the cache is dropped wholesale - a few hundred bytes each.</summary>
    private const int CacheLimit = 1 << 18;

    private readonly Level _level;
    private readonly bool[] _walls;
    private readonly int[] _itemSlots;
    private readonly int[] _unitSlots;
    private readonly int[] _unitModel;
    private readonly bool[] _unitMustLeave;
    private readonly int[] _goalOutItems;
    private readonly UnitReach?[] _reach;
    private readonly bool[] _fixedSolid;

    private readonly ConcurrentDictionary<byte[], Verdict> _cache = new(LayoutComparer.Instance);
    private readonly ConcurrentDictionary<byte[], Verdict>.AlternateLookup<ReadOnlySpan<byte>> _lookup;
    private int _cached;
    private long _analyses;
    private long _analysisTicks;

    public StuckDetector(Level level, LevelReduction reduction, int[] mobile)
    {
        _level = level;
        _walls = reduction.Walls;
        _unitModel = [.. level.Units.Select(u => u.Model)];
        _unitSlots = [.. _unitModel.Select(m => Array.IndexOf(mobile, m))];
        _unitMustLeave = [.. _unitModel.Select(m => level.Models[m].Goal is GoalKind.Escape or GoalKind.Out)];

        var fish = _unitModel.ToHashSet();
        _itemSlots = [.. Enumerable.Range(0, mobile.Length).Where(i => !fish.Contains(mobile[i]))];
        _goalOutItems = [.. mobile.Where(m => !fish.Contains(m) && level.Models[m].Goal == GoalKind.Out)];

        _reach = [.. Enumerable.Range(0, _unitModel.Length).Select(u => _unitMustLeave[u] ? UnitReach.Build(level, _walls, _unitModel[u]) : null)];

        // Models that can never move stand where the level put them in every
        // state, so their cells are worked out once, on a fresh room.
        _fixedSolid = StuckAnalysis.FixedSolid(level, new Room(level));

        LayoutSize = (_itemSlots.Length + _unitSlots.Length) * 5;
        _lookup = _cache.GetAlternateLookup<ReadOnlySpan<byte>>();
    }

    /// <summary>Bytes of an item-layout key (see <see cref="WriteLayout"/>).</summary>
    public int LayoutSize { get; }

    /// <summary>Layouts analysed so far (cache misses), for diagnostics.</summary>
    public long Analyses => Interlocked.Read(ref _analyses);

    /// <summary>Time spent analysing, summed over threads, for diagnostics.</summary>
    public TimeSpan AnalysisTime => System.Diagnostics.Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _analysisTicks));

    /// <summary>Per-thread scratch and a two-entry front for the shared cache.</summary>
    public Probe NewProbe() => new(LayoutSize);

    /// <summary>Drops the cache once it is full. Call only while no other thread is using the detector.</summary>
    public void TrimIfFull()
    {
        if (_cached >= CacheLimit)
        {
            _cache.Clear();
            _cached = 0;
        }
    }

    public void Clear()
    {
        _cache.Clear();
        _cached = 0;
        Interlocked.Exchange(ref _analyses, 0);
        Interlocked.Exchange(ref _analysisTicks, 0);
    }

    /// <summary>
    /// Whether the state in <paramref name="room"/>, whose state key is
    /// <paramref name="key"/>, can no longer be solved because of something stuck.
    /// Thread-safe as long as each thread brings its own room and probe.
    /// </summary>
    public bool NoWayOut(Room room, ReadOnlySpan<byte> key, Probe probe) => Condemns(VerdictFor(room, key, probe), room);

    /// <summary>
    /// The verdict for the layout of the state in <paramref name="room"/>, to be
    /// reused through <see cref="Condemns"/> for states sharing that layout - the
    /// children an expansion produces by fast-path moves (see
    /// <see cref="Room.LastMoveWasFast"/>), which change nothing but the fish.
    /// </summary>
    public Verdict LayoutVerdict(Room room, ReadOnlySpan<byte> key, Probe probe) => VerdictFor(room, key, probe);

    /// <summary>Whether the state in <paramref name="room"/>, with a layout whose verdict this is, is dead.</summary>
    public bool Condemns(Verdict verdict, Room room)
    {
        if (verdict.LayoutDead)
        {
            return true;
        }

        if (verdict.Leave is not { } leave)
        {
            return false;
        }

        int w = _level.Width, h = _level.Height;
        for (int u = 0; u < _unitModel.Length; u++)
        {
            // Only a fish that has to leave can be stranded. On the world-final
            // levels both fish are goal_alive and a walled-in fish is fine (C7).
            if (!_unitMustLeave[u] || leave[u] is not { } reach)
            {
                continue;
            }

            ref readonly ModelState s = ref room.State(_unitModel[u]);
            if (s.IsOut || !s.IsAlive || (uint)s.X >= (uint)w || (uint)s.Y >= (uint)h)
            {
                continue;
            }

            int cell = (s.Y * w) + s.X;
            if ((reach[cell >> 6] & (1UL << (cell & 63))) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private Verdict VerdictFor(Room room, ReadOnlySpan<byte> key, Probe probe)
    {
        // Most moves are a fish swimming, which leaves the items exactly where
        // they were: siblings of one expansion nearly all share their parent's
        // layout, and a push child sits between them. Two entries catch both.
        Span<byte> layout = probe.Scratch;
        WriteLayout(key, layout);
        if (probe.Bytes0 is not null && layout.SequenceEqual(probe.Bytes0))
        {
            return probe.Verdict0!;
        }

        if (probe.Bytes1 is not null && layout.SequenceEqual(probe.Bytes1))
        {
            probe.Swap();
            return probe.Verdict0!;
        }

        if (!_lookup.TryGetValue(layout, out Verdict? verdict))
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            verdict = Analyse(room);
            Interlocked.Add(ref _analysisTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            Interlocked.Increment(ref _analyses);
            if (_cache.TryAdd(layout.ToArray(), verdict))
            {
                Interlocked.Increment(ref _cached);
            }
        }

        probe.Push(layout, verdict);
        return verdict;
    }

    /// <summary>
    /// The layout key: every item's state-key bytes, then five bytes per unit -
    /// its alive / out / lost flags (review C1), and its position only if it is
    /// dead. A living fish's position stays out: the analysis holds wherever it
    /// stands. A dead one is an item to the analysis - it can be shoved, wedge,
    /// hold things up - so its position is part of the layout. The solver never
    /// asks about a state with a dead fish (no such state is solvable), but the
    /// key is exact anyway, so the verdict is a function of it in every state
    /// (docs/020; the fuzzer's stuck-detector check compares on all of them).
    /// </summary>
    public void WriteLayout(ReadOnlySpan<byte> key, Span<byte> destination)
    {
        for (int i = 0; i < _itemSlots.Length; i++)
        {
            key.Slice(_itemSlots[i] * 5, 5).CopyTo(destination.Slice(i * 5, 5));
        }

        int offset = _itemSlots.Length * 5;
        for (int u = 0; u < _unitSlots.Length; u++)
        {
            int slot = _unitSlots[u] * 5;
            Span<byte> unit = destination.Slice(offset + (u * 5), 5);

            // Flag bits of Room.WriteStateKey: 2 alive, 4 out, 8 lost.
            byte flags = (byte)(key[slot + 4] & (2 | 4 | 8));
            if ((flags & 2) == 0)
            {
                key.Slice(slot, 4).CopyTo(unit);
            }
            else
            {
                unit[..4].Clear();
            }

            unit[4] = flags;
        }
    }

    private Verdict Analyse(Room room)
    {
        bool[] mobile = StuckAnalysis.Mobile(_level, room, fishAnywhere: true, _fixedSolid, out bool[] solid);

        foreach (int item in _goalOutItems)
        {
            if (!mobile[item] && !room.State(item).IsOut)
            {
                return Verdict.Dead;
            }
        }


        // A model frozen at the opening position is frozen in every state
        // reachable from it, so the two solid sets can be unioned. They are not
        // otherwise comparable: an item resting on a fish is mobile here and was
        // not at the start.
        var extra = new List<int>();
        for (int c = 0; c < solid.Length; c++)
        {
            if (solid[c] && !_walls[c])
            {
                extra.Add(c);
            }

            solid[c] |= _walls[c];
        }

        if (extra.Count == 0)
        {
            // Nothing stuck beyond the walls the level started with: the fish's
            // reach is the walls-only one, whose dead ends the heuristic already
            // rejects (an infinite relaxed distance). Nothing to add.
            return Verdict.Open;
        }

        var leave = new ulong[]?[_unitModel.Length];
        bool any = false;
        for (int u = 0; u < _unitModel.Length; u++)
        {
            ref readonly ModelState s = ref room.State(_unitModel[u]);
            if (!_unitMustLeave[u] || s.IsOut || !s.IsAlive)
            {
                continue;
            }

            leave[u] = _reach[u]!.CanStillLeave(_level, solid, extra);
            any = true;
        }

        return any ? new Verdict(false, leave) : Verdict.Open;
    }

    /// <summary>
    /// <see cref="StuckAnalysis.CanStillLeave"/> for one fish, with everything
    /// that depends only on the walls worked out once per level: where the
    /// fish's shape fits among the walls, and which of those anchors touch an
    /// edge (only they can be a way out). An analysis then only has to knock
    /// out the anchors that overlap a newly stuck cell, instead of testing the
    /// whole shape at every cell and every cell as an exit - which was most of
    /// the analysis's cost on rooms whose items move often (docs/020).
    /// </summary>
    private sealed class UnitReach
    {
        private int _model;
        private sbyte[] _markX = [];
        private sbyte[] _markY = [];
        private bool[] _fitsWalls = [];
        private int[] _edgeAnchors = [];

        public static UnitReach Build(Level level, bool[] walls, int model)
        {
            int w = level.Width, h = level.Height;
            Shape shape = level.Models[model].Shape;
            var fits = new bool[w * h];
            var edge = new List<int>();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!StuckAnalysis.Fits(level, model, walls, x, y))
                    {
                        continue;
                    }

                    fits[(y * w) + x] = true;
                    bool touches = false;
                    for (int m = 0; m < shape.MarkX.Length; m++)
                    {
                        int cx = x + shape.MarkX[m], cy = y + shape.MarkY[m];
                        touches |= cx == 0 || cy == 0 || cx == w - 1 || cy == h - 1;
                    }

                    if (touches)
                    {
                        edge.Add((y * w) + x);
                    }
                }
            }

            return new UnitReach
            {
                _model = model,
                _markX = shape.MarkX,
                _markY = shape.MarkY,
                _fitsWalls = fits,
                _edgeAnchors = [.. edge],
            };
        }

        /// <summary>Anchors this fish could still leave from, as a bit set; <paramref name="extra"/> are the solid cells that are not walls.</summary>
        public ulong[] CanStillLeave(Level level, bool[] solid, List<int> extra)
        {
            int w = level.Width, h = level.Height;

            // Fits(solid) = fits among the walls and covers no newly stuck cell.
            var blocked = new bool[w * h];
            foreach (int cell in extra)
            {
                int cx = cell % w, cy = cell / w;
                for (int m = 0; m < _markX.Length; m++)
                {
                    int ax = cx - _markX[m], ay = cy - _markY[m];
                    if ((uint)ax < (uint)w && (uint)ay < (uint)h)
                    {
                        blocked[(ay * w) + ax] = true;
                    }
                }
            }

            var seen = new ulong[((w * h) + 63) >> 6];
            var queue = new Queue<int>();
            foreach (int anchor in _edgeAnchors)
            {
                if (!blocked[anchor] && StuckAnalysis.CanLeave(level, _model, solid, anchor % w, anchor / w))
                {
                    seen[anchor >> 6] |= 1UL << (anchor & 63);
                    queue.Enqueue(anchor);
                }
            }

            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                int x = cell % w, y = cell / w;
                if (x > 0) Step(cell - 1);
                if (x < w - 1) Step(cell + 1);
                if (y > 0) Step(cell - w);
                if (y < h - 1) Step(cell + w);
            }

            return seen;

            void Step(int next)
            {
                if ((seen[next >> 6] & (1UL << (next & 63))) != 0 || !_fitsWalls[next] || blocked[next])
                {
                    return;
                }

                seen[next >> 6] |= 1UL << (next & 63);
                queue.Enqueue(next);
            }
        }
    }

    /// <summary>What one item layout implies, whatever the fish do.</summary>
    internal sealed class Verdict(bool layoutDead, ulong[]?[]? leave)
    {
        public static readonly Verdict Open = new(false, null);
        public static readonly Verdict Dead = new(true, null);

        /// <summary>No solution from any state with this layout.</summary>
        public bool LayoutDead { get; } = layoutDead;

        /// <summary>Per unit, the anchors it can still leave from; null where nothing is ruled out.</summary>
        public ulong[]?[]? Leave { get; } = leave;
    }

    public sealed class Probe
    {
        internal Probe(int size)
        {
            Scratch = new byte[size];
        }

        internal byte[] Scratch { get; }

        internal byte[]? Bytes0 { get; private set; }

        internal byte[]? Bytes1 { get; private set; }

        internal Verdict? Verdict0 { get; private set; }

        internal Verdict? Verdict1 { get; private set; }

        internal void Swap()
        {
            (Bytes0, Bytes1) = (Bytes1, Bytes0);
            (Verdict0, Verdict1) = (Verdict1, Verdict0);
        }

        internal void Push(ReadOnlySpan<byte> layout, Verdict verdict)
        {
            // Reuse the evicted entry's buffer rather than allocating.
            byte[] buffer = Bytes1 ?? new byte[layout.Length];
            layout.CopyTo(buffer);
            Bytes1 = Bytes0;
            Verdict1 = Verdict0;
            Bytes0 = buffer;
            Verdict0 = verdict;
        }

        internal void Reset()
        {
            Bytes0 = Bytes1 = null;
            Verdict0 = Verdict1 = null;
        }
    }

    private sealed class LayoutComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        public static readonly LayoutComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => GetHashCode((ReadOnlySpan<byte>)obj);

        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

        public int GetHashCode(ReadOnlySpan<byte> alternate)
        {
            var hash = default(HashCode);
            hash.AddBytes(alternate);
            return hash.ToHashCode();
        }

        public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
    }
}
