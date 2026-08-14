namespace FishFillets.Search;

/// <summary>
/// The visited set: maps a state key to the cheapest cost it has been reached
/// at. Keys are variable-length byte blobs (see <c>Room.WriteStateKey</c>), so
/// they are copied into one growing arena and the table stores offsets - no
/// per-state array allocation, and no GC pressure from millions of small keys.
///
/// Comparison is on the full key, not a hash, so there are no collisions to
/// reason about. That matters more here than raw speed: a false "already seen"
/// would silently drop the very path a search is looking for, and a false match
/// across two different states would produce a spliced solution that doesn't
/// replay - the kind of bug that is expensive to track down later.
/// </summary>
internal sealed class StateSet
{
    private readonly int _keySize;
    private byte[] _keys;
    private int _keyTop;

    private int[] _buckets;   // 1-based index into _entries, 0 = empty
    private Entry[] _entries;
    private int _count;
    private int _mask;

    private struct Entry
    {
        public int HashCode;
        public int KeyOffset;
        public int Cost;
        public int Next;      // 1-based, 0 = end of chain
    }

    public StateSet(int keySize, int initialCapacity = 1 << 12)
    {
        _keySize = keySize;
        int capacity = 1;
        while (capacity < initialCapacity)
        {
            capacity <<= 1;
        }

        _buckets = new int[capacity];
        _entries = new Entry[capacity];
        _mask = capacity - 1;
        _keys = new byte[capacity * keySize];
    }

    public int Count => _count;

    public void Clear()
    {
        Array.Clear(_buckets);
        _count = 0;
        _keyTop = 0;
    }

    /// <summary>
    /// Records <paramref name="key"/> at <paramref name="cost"/> if it is new, or
    /// cheaper than the cost it was previously reached at.
    /// </summary>
    /// <returns>Whether the caller should expand this state.</returns>
    public bool TryImprove(ReadOnlySpan<byte> key, int cost) => TryImprove(key, cost, out _);

    /// <summary>Where this key's bytes live, so a node can reference them instead of holding a second copy.</summary>
    public ReadOnlySpan<byte> KeyAt(int offset) => _keys.AsSpan(offset, _keySize);

    public bool TryImprove(ReadOnlySpan<byte> key, int cost, out int keyOffset)
    {
        keyOffset = -1;
        int hash = Hash(key);
        int bucket = hash & _mask;

        for (int i = _buckets[bucket]; i != 0; i = _entries[i - 1].Next)
        {
            ref Entry entry = ref _entries[i - 1];
            if (entry.HashCode == hash && key.SequenceEqual(_keys.AsSpan(entry.KeyOffset, _keySize)))
            {
                keyOffset = entry.KeyOffset;
                if (cost >= entry.Cost)
                {
                    return false;
                }

                entry.Cost = cost;
                return true;
            }
        }

        if (_count == _entries.Length)
        {
            Grow();
            bucket = hash & _mask;
        }

        if (_keyTop + _keySize > _keys.Length)
        {
            Array.Resize(ref _keys, Math.Max(_keys.Length * 2, _keyTop + _keySize));
        }

        keyOffset = _keyTop;
        key.CopyTo(_keys.AsSpan(_keyTop, _keySize));
        _entries[_count] = new Entry
        {
            HashCode = hash,
            KeyOffset = _keyTop,
            Cost = cost,
            Next = _buckets[bucket],
        };
        _buckets[bucket] = _count + 1;
        _keyTop += _keySize;
        _count++;
        return true;
    }

    private void Grow()
    {
        int capacity = _entries.Length * 2;
        Array.Resize(ref _entries, capacity);
        _buckets = new int[capacity];
        _mask = capacity - 1;

        for (int i = 0; i < _count; i++)
        {
            int bucket = _entries[i].HashCode & _mask;
            _entries[i].Next = _buckets[bucket];
            _buckets[bucket] = i + 1;
        }
    }

    /// <summary>FNV-1a. Cheap, and collisions only cost a comparison - never correctness.</summary>
    private static int Hash(ReadOnlySpan<byte> key)
    {
        uint hash = 2166136261u;
        foreach (byte b in key)
        {
            hash = (hash ^ b) * 16777619u;
        }

        return (int)(hash & 0x7FFFFFFF);
    }
}
