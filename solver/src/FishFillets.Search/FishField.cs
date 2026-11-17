using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// A deferred fish position: for a fish that is not acting, the cost of it
/// standing at each placement instead of one committed square.
///
/// <para>A field is a <c>ushort[]</c> indexed by placement id
/// (<see cref="InertRouter.Placement"/>, <c>width * height * 2</c> long), with
/// <see cref="None"/> meaning unreachable or forbidden. It is advanced through a
/// search by two operations: <see cref="InertRouter.Relax"/> spreads it by inert
/// travel through the room as it now stands, and <see cref="Mask"/> forbids the
/// placements that would have been in the way of an event. Carrying the whole
/// field instead of guessing where the fish steps aside is what removes the
/// item solver's last incompleteness (solver/docs/023).</para>
///
/// <para>Everything here is a linear scan with no allocation: fields are a few
/// thousand entries and are touched once per event.</para>
/// </summary>
internal static class FishField
{
    /// <summary>The fish cannot be at this placement.</summary>
    public const ushort None = ushort.MaxValue;

    /// <summary>Every placement forbidden.</summary>
    public static void Clear(ushort[] field) => field.AsSpan().Fill(None);

    /// <summary>The fish is exactly at <paramref name="placement"/>, at no cost.</summary>
    public static void SetPoint(ushort[] field, int placement)
    {
        Clear(field);
        field[placement] = 0;
    }

    /// <summary>
    /// Forbids every placement where the fish's shape, anchored there, covers
    /// any of <paramref name="cells"/> (cell = <c>y * width + x</c>).
    ///
    /// <para>Worked backwards from the cells: a mark at offset (mx, my) covers
    /// cell (cx, cy) exactly when the anchor is (cx - mx, cy - my). So every
    /// placement to forbid is found in O(cells × marks) without a grid, and an
    /// anchor that falls outside the room is not a placement at all.</para>
    /// </summary>
    public static void Mask(ushort[] field, Shape fishShape, int width, int height, ReadOnlySpan<int> cells)
    {
        sbyte[] markX = fishShape.MarkX, markY = fishShape.MarkY;
        foreach (int cell in cells)
        {
            int cx = cell % width, cy = cell / width;
            for (int m = 0; m < markX.Length; m++)
            {
                int ax = cx - markX[m], ay = cy - markY[m];
                if ((uint)ax >= (uint)width || (uint)ay >= (uint)height)
                {
                    continue;
                }

                int both = ((ay * width) + ax) * 2;
                field[both] = None;
                field[both + 1] = None;
            }
        }
    }

    /// <summary>The cheapest allowed placement; ties go to the lowest placement id.</summary>
    public static bool TryCheapest(ushort[] field, out int placement, out int cost)
    {
        placement = -1;
        cost = None;
        for (int q = 0; q < field.Length; q++)
        {
            if (field[q] < cost)
            {
                cost = field[q];
                placement = q;
            }
        }

        return placement >= 0;
    }

    /// <summary>
    /// The allowed placement farthest from (x, y) by Manhattan distance between
    /// anchors; ties go to the lowest cost, then the lowest placement id.
    ///
    /// <para>Used to run an event's physics with the other fish as far out of the
    /// way as it can be, so that what the event disturbs is measured without that
    /// fish interfering (the generic outcome of a push).</para>
    /// </summary>
    public static bool TryFarthest(ushort[] field, int width, int x, int y, out int placement)
    {
        placement = -1;
        int bestDistance = -1;
        int bestCost = None;

        for (int q = 0; q < field.Length; q++)
        {
            int cost = field[q];
            if (cost == None)
            {
                continue;
            }

            int cell = q >> 1;
            int distance = Math.Abs((cell % width) - x) + Math.Abs((cell / width) - y);
            if (distance > bestDistance || (distance == bestDistance && cost < bestCost))
            {
                bestDistance = distance;
                bestCost = cost;
                placement = q;
            }
        }

        return placement >= 0;
    }

    /// <summary>
    /// min over q of <c>field[q] + exit(q)</c>: the cheapest way for this fish to
    /// get out, counting what it costs to be at q in the first place.
    /// <see cref="RelaxedDistance.Unreachable"/> when no allowed placement has a way
    /// out. A lower bound on the fish's share of the remaining moves as long as
    /// the field itself is (a masked parent field is: masking only forbids).
    /// </summary>
    public static int MinPlus(ushort[] field, int width, RelaxedDistance exit)
    {
        int best = RelaxedDistance.Unreachable;
        for (int q = 0; q < field.Length; q++)
        {
            int cost = field[q];
            if (cost == None || cost >= best)
            {
                continue;
            }

            int cell = q >> 1;
            int distance = exit.At(cell % width, cell / width, (q & 1) == 0);
            if (distance >= RelaxedDistance.Unreachable)
            {
                continue;
            }

            if (cost + distance < best)
            {
                best = cost + distance;
            }
        }

        return best;
    }

    /// <summary>How many placements are allowed.</summary>
    public static int Count(ushort[] field)
    {
        int count = 0;
        foreach (ushort cost in field)
        {
            if (cost != None)
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>
/// The most recently used fields, keyed by search node. A field is cheap to
/// recompute along the chain of parents but too large to keep for every node,
/// so the solver keeps a fixed number of them and re-derives the rest on a miss.
///
/// <para>Arrays are pooled: capacity × length ushorts are allocated once, as
/// slots are first used, and an evicted slot's array is handed to the next key.
/// Recency is a doubly linked list over the slots, so every operation is O(1)
/// apart from the dictionary lookup.</para>
/// </summary>
internal sealed class FieldCache
{
    private readonly int _length;
    private readonly ushort[][] _arrays;
    private readonly int[] _keys;
    private readonly int[] _prev;   // toward more recent, -1 at the head
    private readonly int[] _next;   // toward less recent, -1 at the tail
    private readonly Dictionary<int, int> _slotOf;
    private int _used;
    private int _head = -1;         // most recently used
    private int _tail = -1;         // least recently used

    public FieldCache(int capacity, int fieldLength)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "a cache needs at least one slot");
        }

        _length = fieldLength;
        _arrays = new ushort[capacity][];
        _keys = new int[capacity];
        _prev = new int[capacity];
        _next = new int[capacity];
        _slotOf = new Dictionary<int, int>(capacity);
    }

    public int Capacity => _arrays.Length;

    public int Count => _slotOf.Count;

    /// <summary>The field cached under <paramref name="key"/>, which becomes the most recently used.</summary>
    public bool TryGet(int key, out ushort[] field)
    {
        if (_slotOf.TryGetValue(key, out int slot))
        {
            Touch(slot);
            field = _arrays[slot];
            return true;
        }

        field = null!;
        return false;
    }

    /// <summary>
    /// An array registered under <paramref name="key"/>, evicting the least
    /// recently used key when the cache is full. Its contents are undefined - it
    /// is usually some other node's old field - so the caller fills it. A key
    /// already present gets its own array back.
    /// </summary>
    public ushort[] Rent(int key)
    {
        if (_slotOf.TryGetValue(key, out int slot))
        {
            Touch(slot);
            return _arrays[slot];
        }

        if (_used < _arrays.Length)
        {
            slot = _used++;
            _arrays[slot] ??= new ushort[_length];   // kept across Clear
        }
        else
        {
            slot = _tail;
            _slotOf.Remove(_keys[slot]);
            Unlink(slot);
        }

        _keys[slot] = key;
        _slotOf[key] = slot;
        PushFront(slot);
        return _arrays[slot];
    }

    /// <summary>Forgets every key. The arrays stay allocated and are handed out again.</summary>
    public void Clear()
    {
        _slotOf.Clear();
        _head = _tail = -1;
        _used = 0;
    }

    private void Touch(int slot)
    {
        if (slot == _head)
        {
            return;
        }

        Unlink(slot);
        PushFront(slot);
    }

    private void Unlink(int slot)
    {
        int prev = _prev[slot], next = _next[slot];
        if (prev >= 0)
        {
            _next[prev] = next;
        }
        else
        {
            _head = next;
        }

        if (next >= 0)
        {
            _prev[next] = prev;
        }
        else
        {
            _tail = prev;
        }
    }

    private void PushFront(int slot)
    {
        _prev[slot] = -1;
        _next[slot] = _head;
        if (_head >= 0)
        {
            _prev[_head] = slot;
        }

        _head = slot;
        if (_tail < 0)
        {
            _tail = slot;
        }
    }
}
