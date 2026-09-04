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

        /// <summary>
        /// The sleep set this state was last expanded under - moves that were
        /// deliberately not explored from it (see <c>Solver</c>'s sleep sets).
        /// Zero means "everything was explored", which is what every caller that
        /// does not use the reduction passes.
        /// </summary>
        public byte Sleep;
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

    public bool TryImprove(ReadOnlySpan<byte> key, int cost, out int keyOffset) =>
        TryImprove(key, cost, 0, out keyOffset, out _);

    /// <summary>
    /// As <see cref="TryImprove(ReadOnlySpan{byte}, int, out int)"/>, but aware of
    /// sleep sets.
    ///
    /// <para>A state expanded under a sleep set is only <b>partially</b> expanded:
    /// the sleeping moves were never tried. So reaching it again by another path
    /// that permits more - a sleep set missing some of the bits the stored one had
    /// - is not a duplicate, even at the same cost, and has to be expanded again
    /// or those successors are lost. Without this, sleep sets and a transposition
    /// table quietly drop parts of the search space.</para>
    ///
    /// <para>The stored sleep set is intersected on every accepted arrival, so it
    /// only ever shrinks. That bounds the re-expansions at one per bit.</para>
    /// </summary>
    /// <param name="expandedSleep">
    /// The sleep set the caller should actually expand under - the intersection of
    /// everything seen for this state so far.
    /// </param>
    public bool TryImprove(
        ReadOnlySpan<byte> key, int cost, byte sleep, out int keyOffset, out byte expandedSleep)
    {
        keyOffset = -1;
        expandedSleep = sleep;
        int hash = Hash(key);
        int bucket = hash & _mask;

        for (int i = _buckets[bucket]; i != 0; i = _entries[i - 1].Next)
        {
            ref Entry entry = ref _entries[i - 1];
            if (entry.HashCode == hash && key.SequenceEqual(_keys.AsSpan(entry.KeyOffset, _keySize)))
            {
                keyOffset = entry.KeyOffset;
                byte merged = (byte)(entry.Sleep & sleep);
                expandedSleep = merged;

                if (cost < entry.Cost)
                {
                    entry.Cost = cost;
                    entry.Sleep = merged;
                    return true;
                }

                // Same cost but this path forbids less: the moves the stored
                // expansion skipped still have to be tried.
                if (cost == entry.Cost && merged != entry.Sleep)
                {
                    entry.Sleep = merged;
                    return true;
                }

                return false;
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
            Sleep = sleep,
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
