using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FishFillets.Search;

/// <summary>
/// The solver's visited set and node store in one: each distinct state is one
/// index, holding its key, its best known cost and how it was reached.
///
/// <para>Replaces <see cref="StateSet"/> plus a separate node array for the main
/// solver (review P1). Per state that was a 20-byte table entry, a 20-byte node,
/// 4 bytes of bucket and the key, every array doubled by copying - so on top of
/// ~130 bytes a state, each doubling briefly needed the old and the new array at
/// once. On long runs those copies are where searches died: every run in
/// results.csv that hit its time limit near 2^24 or 2^25 stored states had
/// spent most of its budget stalled in a resize (society: 24.8 M expansions in
/// both a 360 s and a 900 s run).</para>
///
/// <para>Here:</para>
/// <list type="bullet">
/// <item>one index serves as the node too - no key offset (it is
///   <c>index * keySize</c>), no stored hash (recomputed from the key when the
///   buckets grow), cost and estimate in 16 bits each, the move as a symbol
///   index: 13 bytes plus the key plus ~4-8 of buckets;</item>
/// <item>storage is <b>chunked</b>: growing allocates one more fixed-size chunk
///   and copies nothing. Only the bucket array (4 bytes a slot) is reallocated
///   and rehashed when it doubles.</item>
/// </list>
/// <para>Comparison is on the full key, as before: a false "already seen" would
/// silently drop a path, so collisions only ever cost a comparison.</para>
/// </summary>
internal sealed class StateStore
{
    private const int Shift = 14;                 // 16,384 states per chunk
    private const int ChunkSize = 1 << Shift;
    private const int Mask = ChunkSize - 1;

    private readonly int _keySize;

    private int[][] _next = [];       // 1-based chain link into the same index space, 0 = end
    private int[][] _parent = [];
    private ushort[][] _cost = [];
    private ushort[][] _estimate = [];
    private byte[][] _move = [];
    private byte[][] _keys = [];

    private int[] _buckets;           // 1-based index of the first state in each chain, 0 = empty
    private int _mask;
    private int _count;

    public StateStore(int keySize, int initialBuckets = 1 << 12)
    {
        _keySize = keySize;
        _buckets = new int[initialBuckets];
        _mask = initialBuckets - 1;
    }

    public int Count => _count;

    public void Clear()
    {
        // The window optimiser clears one store per window, thousands of times,
        // and most windows store a handful of states in a bucket array grown by
        // an earlier, bigger one - so a nearly empty table zeroes just the
        // chains it used instead of the whole array.
        if (_count < _buckets.Length >> 4)
        {
            for (int i = 0; i < _count; i++)
            {
                _buckets[Hash(KeySpan(i)) & _mask] = 0;
            }
        }
        else
        {
            Array.Clear(_buckets);
        }

        _count = 0;
    }

    /// <summary>
    /// The index of <paramref name="key"/>, adding it if it is new (its fields
    /// are then the caller's to set).
    /// </summary>
    public int FindOrAdd(ReadOnlySpan<byte> key, out bool added) => FindOrAdd(key, Hash(key), out added);

    /// <summary>As <see cref="FindOrAdd(ReadOnlySpan{byte}, out bool)"/>, with the key's <see cref="Hash"/> already computed (in parallel, by the solver's workers).</summary>
    public int FindOrAdd(ReadOnlySpan<byte> key, int hash, out bool added)
    {
        if (key.Length != _keySize)
        {
            throw new ArgumentException("key has the wrong size");
        }

        ref byte probe = ref MemoryMarshal.GetReference(key);
        for (int i = _buckets[hash & _mask]; i != 0; i = Next(i - 1))
        {
            if (KeyEquals(ref probe, ref KeyRef(i - 1)))
            {
                added = false;
                return i - 1;
            }
        }

        if (_count >= _buckets.Length)
        {
            GrowBuckets();
        }

        int index = _count;
        if ((index >> Shift) >= _keys.Length)
        {
            AddChunk();
        }

        key.CopyTo(KeySpan(index));
        ref int slot = ref _buckets[hash & _mask];
        _next[index >> Shift][index & Mask] = slot;
        slot = index + 1;
        _count++;

        added = true;
        return index;
    }

    public ReadOnlySpan<byte> Key(int index) => KeySpan(index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref int Parent(int index) => ref _parent[index >> Shift][index & Mask];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref ushort Cost(int index) => ref _cost[index >> Shift][index & Mask];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref ushort Estimate(int index) => ref _estimate[index >> Shift][index & Mask];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte Move(int index) => ref _move[index >> Shift][index & Mask];

    /// <summary>Bytes held at current capacity, growth headroom included.</summary>
    public long MemoryBytes =>
        ((long)_buckets.Length * sizeof(int))
        + ((long)_keys.Length * ChunkSize * ((sizeof(int) * 2) + (sizeof(ushort) * 2) + sizeof(byte) + _keySize));

    // The accessors below skip the array bounds checks: every index they see is
    // below _count, whose chunk exists and holds a full ChunkSize entries.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Next(int index) =>
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_next), index >> Shift)), index & Mask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref byte KeyRef(int index) =>
        ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_keys), index >> Shift)), (index & Mask) * _keySize);

    /// <summary>
    /// Key comparison. Up to 16 bytes - most levels - it is two overlapping
    /// 8-byte loads per side instead of a call into SequenceEqual.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool KeyEquals(ref byte a, ref byte b)
    {
        int size = _keySize;
        if (size >= 8 && size <= 16)
        {
            return Unsafe.ReadUnaligned<ulong>(ref a) == Unsafe.ReadUnaligned<ulong>(ref b)
                   && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, size - 8)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, size - 8));
        }

        return MemoryMarshal.CreateReadOnlySpan(ref a, size).SequenceEqual(MemoryMarshal.CreateReadOnlySpan(ref b, size));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> KeySpan(int index) => _keys[index >> Shift].AsSpan((index & Mask) * _keySize, _keySize);

    private void AddChunk()
    {
        int chunks = _keys.Length + 1;
        Array.Resize(ref _next, chunks);
        Array.Resize(ref _parent, chunks);
        Array.Resize(ref _cost, chunks);
        Array.Resize(ref _estimate, chunks);
        Array.Resize(ref _move, chunks);
        Array.Resize(ref _keys, chunks);

        int c = chunks - 1;
        _next[c] = new int[ChunkSize];
        _parent[c] = new int[ChunkSize];
        _cost[c] = new ushort[ChunkSize];
        _estimate[c] = new ushort[ChunkSize];
        _move[c] = new byte[ChunkSize];
        _keys[c] = new byte[ChunkSize * _keySize];
    }

    private void GrowBuckets()
    {
        _buckets = new int[_buckets.Length * 2];
        _mask = _buckets.Length - 1;

        for (int i = 0; i < _count; i++)
        {
            ref int slot = ref _buckets[Hash(KeySpan(i)) & _mask];
            _next[i >> Shift][i & Mask] = slot;
            slot = i + 1;
        }
    }

    /// <summary>
    /// Eight bytes at a time, finished with MurmurHash3's 64-bit avalanche so
    /// the low bits the buckets use depend on every input bit. Collisions only
    /// cost a comparison - never correctness. (FNV-1a, which this replaced,
    /// took a multiply per byte.)
    /// </summary>
    public static int Hash(ReadOnlySpan<byte> key)
    {
        ref byte start = ref MemoryMarshal.GetReference(key);
        int length = key.Length;
        ulong hash = 0x9E3779B97F4A7C15UL ^ (ulong)length;
        int i = 0;
        for (; i + 8 <= length; i += 8)
        {
            ulong word = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, i));
            hash = BitOperations.RotateLeft(hash ^ (word * 0x87C37B91114253D5UL), 31) * 0x4CF5AD432745937FUL;
        }

        if (i < length)
        {
            ulong tail = 0;
            for (int shift = 0; i < length; i++, shift += 8)
            {
                tail |= (ulong)Unsafe.Add(ref start, i) << shift;
            }

            hash ^= tail * 0x87C37B91114253D5UL;
        }

        hash ^= hash >> 33;
        hash *= 0xFF51AFD7ED558CCDUL;
        hash ^= hash >> 33;
        hash *= 0xC4CEB9FE1A85EC53UL;
        hash ^= hash >> 33;
        return (int)(hash & 0x7FFFFFFF);
    }
}
