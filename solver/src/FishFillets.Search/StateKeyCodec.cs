using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// Packs <c>Room.WriteStateKey</c>'s wide key (5 bytes per mobile model) into
/// the compact, bit-packed form the solver's state table stores, and back.
///
/// <para>The wide key spends two bytes on each coordinate (positions go to
/// -1000 once a model has left) and a flags byte on every model. In a settled
/// state almost none of that varies for a plain item: it is never alive, never
/// turns, is never busy and has no plug capacity, and the only way it leaves is
/// goal_out, which marks it out and lost together. So each slot is:</para>
/// <list type="bullet">
/// <item><b>position</b> - one number: 0 for "left the room", otherwise
///   1 + the anchor's index in a box one shape larger than the room on every
///   side (anchors can sit a little left of or above the room, since shapes
///   may start with blank cells). Just wide enough for that box: 9 bits on
///   society (24x14), 11-12 on the largest rooms, against 16 as two bytes.</item>
/// <item><b>fish, or windoze's output plug</b> - the position plus the wide
///   flags byte's 7 used bits.</item>
/// </list>
/// <para>The slots are written back to back as a bit string. That roughly
/// halves the key again over a byte per coordinate (society: 24 -> 15 bytes),
/// and the key is the largest part of a stored state (review P1).</para>
///
/// <para>The invariants are checked on every encode and violations throw - a
/// silently wrong key would merge distinct states, the one bug a search cannot
/// see from the inside. Encode and Decode keep no state, so the parallel
/// workers share one codec.</para>
/// </summary>
internal sealed class StateKeyCodec
{
    private const int FlagBits = 7;

    private readonly Level _level;
    private readonly int[] _models;
    private readonly bool[] _full;
    private readonly byte[] _itemFlags;   // the one flags byte a plain item has while in the room
    private readonly int[] _xMin;
    private readonly int[] _yMin;
    private readonly int[] _columns;      // width of the position box
    private readonly int[] _rows;
    private readonly int[] _positionBits;
    private readonly int[] _fieldBits;    // position bits, plus the flags for a fish or plug
    private readonly int[][] _classes;    // interchangeable plain items (ItemSymmetry), as slots
    private readonly Slot[] _slots;

    public StateKeyCodec(Level level, int[] mobileModels)
    {
        if (!BitConverter.IsLittleEndian)
        {
            // Field reads the wide key's coordinates as little-endian shorts.
            throw new PlatformNotSupportedException("the compact key codec assumes a little-endian machine");
        }

        _level = level;
        _models = mobileModels;
        int slots = mobileModels.Length;
        _full = new bool[slots];
        _itemFlags = new byte[slots];
        _xMin = new int[slots];
        _yMin = new int[slots];
        _columns = new int[slots];
        _rows = new int[slots];
        _positionBits = new int[slots];
        _fieldBits = new int[slots];

        int bits = 0;
        for (int n = 0; n < slots; n++)
        {
            ModelDef def = level.Models[mobileModels[n]];
            _full[n] = def.IsAlive || def.OutDir != Dir.No;
            _itemFlags[n] = (byte)(def.IsLeft ? 1 : 0);

            _xMin[n] = -def.Shape.W - 1;
            _yMin[n] = -def.Shape.H - 1;
            _columns[n] = level.Width + 1 - _xMin[n] + 1;
            _rows[n] = level.Height + 1 - _yMin[n] + 1;
            long values = ((long)_columns[n] * _rows[n]) + 1;
            _positionBits[n] = 64 - BitOperations.LeadingZeroCount((ulong)(values - 1));
            if (_positionBits[n] > 32)
            {
                throw new InvalidOperationException($"{level.Name}: room too large for the compact key");
            }

            _fieldBits[n] = _positionBits[n] + (_full[n] ? FlagBits : 0);
            bits += _fieldBits[n];
        }

        CompactSize = (bits + 7) / 8;
        _slots = new Slot[slots];
        for (int n = 0; n < slots; n++)
        {
            _slots[n] = new Slot
            {
                XMin = _xMin[n], YMin = _yMin[n], Columns = _columns[n], Rows = _rows[n],
                PositionBits = _positionBits[n], ItemFlags = _itemFlags[n], Full = _full[n],
                Reciprocal = (ulong.MaxValue / (ulong)_columns[n]) + 1,
                FieldBits = _fieldBits[n],
            };
        }
        _classes = ItemSymmetry.Classes(level, mobileModels);
    }

    public int CompactSize { get; }

    /// <summary>
    /// Whether <see cref="Encode"/> sorts the positions within each class of
    /// interchangeable items, so that states differing only in which identical
    /// item stands where get the same key (docs/022). Decoding such a key hands
    /// the positions out in sorted order - a room with identities swapped, which
    /// is the same room to the physics.
    /// </summary>
    public bool Canonical { get; set; }

    /// <summary>Whether this level has any interchangeable items at all.</summary>
    public bool HasSymmetry => _classes.Length > 0;

    public int WideSize => _models.Length * 5;

    public void Encode(ReadOnlySpan<byte> wide, Span<byte> compact)
    {
        int slots = _models.Length;
        if (wide.Length < slots * 5 || compact.Length < CompactSize)
        {
            throw new ArgumentException("key buffer too small");
        }

        // Packed into a scratch copy with 8 bytes of slack, so every field can be
        // stored as one unaligned 64-bit write with no loop: a per-byte loop
        // whose trip count depends on the data mispredicted on most slots.
        Span<byte> packed = stackalloc byte[CompactSize + 8];
        ref byte output = ref MemoryMarshal.GetReference(packed);
        ulong buffer = 0;
        int pending = 0, c = 0;
        if (Canonical && _classes.Length > 0)
        {
            // The classes are sorted on the numbers, so collect them first.
            Span<ulong> fields = slots <= 256 ? stackalloc ulong[slots] : new ulong[slots];
            for (int n = 0; n < slots; n++)
            {
                fields[n] = Field(wide, n);
            }

            Canonicalize(fields);
            for (int n = 0; n < slots; n++)
            {
                Put(ref output, ref buffer, ref pending, ref c, fields[n], _slots[n].FieldBits);
            }
        }
        else
        {
            for (int n = 0; n < slots; n++)
            {
                Put(ref output, ref buffer, ref pending, ref c, Field(wide, n), _slots[n].FieldBits);
            }
        }

        packed[..CompactSize].CopyTo(compact);
    }

    /// <summary>What <see cref="Field"/> needs about a slot, in one place: one bounds check and one cache line instead of eight arrays.</summary>
    private struct Slot
    {
        public int XMin;
        public int YMin;
        public int Columns;
        public int Rows;
        public int PositionBits;
        public byte ItemFlags;
        public bool Full;
        public ulong Reciprocal;   // ulong.MaxValue / Columns + 1
        public int FieldBits;
    }

    /// <summary>One slot's number: the position, with a fish's or plug's flags above it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong Field(ReadOnlySpan<byte> wide, int n)
    {
        ref readonly Slot slot = ref _slots[n];
        // WideSize bytes are guaranteed by the caller's key buffer; n < slots.
        ref byte record = ref Unsafe.Add(ref MemoryMarshal.GetReference(wide), n * 5);
        int x = Unsafe.ReadUnaligned<short>(ref record);
        int y = Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref record, 2));
        byte flags = Unsafe.Add(ref record, 4);
        bool gone = x == -1000 && y == -1000;

        uint position = 0;
        if (!gone)
        {
            int dx = x - slot.XMin, dy = y - slot.YMin;
            if ((uint)dx >= (uint)slot.Columns || (uint)dy >= (uint)slot.Rows)
            {
                ThrowOutOfRange(n, x, y);
            }

            position = (uint)((dy * slot.Columns) + dx + 1);
        }

        if (slot.Full)
        {
            if (flags >= 1 << FlagBits)
            {
                ThrowBadFlags(n, flags, -1);
            }

            return position | ((ulong)flags << slot.PositionBits);
        }

        // In the room: exactly the declared facing and nothing else.
        // Gone: out and lost on top of it (goal_out is the only way).
        byte expected = (byte)(slot.ItemFlags | (gone ? 4 | 8 : 0));
        if (flags != expected)
        {
            ThrowBadFlags(n, flags, expected);
        }

        return position;
    }

    /// <summary>
    /// Appends a field. On entry fewer than 8 bits are pending and a field is
    /// at most 39 bits, so the 64-bit buffer cannot overflow; whole bytes are
    /// then committed and the rest (still written, as zero-padded high bits)
    /// carries over.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Put(ref byte output, ref ulong buffer, ref int pending, ref int c, ulong field, int bits)
    {
        buffer |= field << pending;
        pending += bits;
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, c), buffer);
        int whole = pending >> 3;
        c += whole;
        buffer >>= whole << 3;
        pending &= 7;
    }

    /// <summary>
    /// Sorts each class's positions (gone, 0, first) in place. Members of a
    /// class share a shape and so a position box, so equal numbers mean equal
    /// places. A child differs from its already-canonical parent by a move or
    /// two, so a class is almost always still sorted: one pass checks, and only
    /// an unsorted class is insertion-sorted - linear on nearly sorted input.
    /// </summary>
    private void Canonicalize(Span<ulong> positions)
    {
        foreach (int[] members in _classes)
        {
            ulong previous = positions[members[0]];
            int i = 1;
            for (; i < members.Length; i++)
            {
                ulong v = positions[members[i]];
                if (v < previous)
                {
                    break;
                }

                previous = v;
            }

            // members[i] is the first out of order; insert it and the rest.
            for (; i < members.Length; i++)
            {
                ulong v = positions[members[i]];
                int j = i - 1;
                while (j >= 0 && positions[members[j]] > v)
                {
                    positions[members[j + 1]] = positions[members[j]];
                    j--;
                }

                positions[members[j + 1]] = v;
            }
        }
    }

    public void Decode(ReadOnlySpan<byte> compact, Span<byte> wide)
    {
        int slots = _models.Length;
        if (wide.Length < slots * 5 || compact.Length < CompactSize)
        {
            throw new ArgumentException("key buffer too small");
        }

        // A copy with 8 bytes of slack, so each field is one unaligned 64-bit
        // load and a shift, with no data-dependent loop.
        Span<byte> packed = stackalloc byte[CompactSize + 8];
        compact[..CompactSize].CopyTo(packed);
        ref byte source = ref MemoryMarshal.GetReference(packed);
        ref byte target = ref MemoryMarshal.GetReference(wide);
        int bit = 0;
        for (int n = 0; n < slots; n++)
        {
            ref readonly Slot slot = ref _slots[n];
            ulong field = (Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, bit >> 3)) >> (bit & 7))
                          & ((1UL << slot.FieldBits) - 1);
            bit += slot.FieldBits;

            uint position = (uint)(field & ((1UL << slot.PositionBits) - 1));
            bool gone = position == 0;
            int x = -1000, y = -1000;
            if (!gone)
            {
                uint cell = position - 1;
                // cell / Columns as a multiply (Lemire's exact 32-bit fastdiv):
                // a hardware divide per slot was half of Decode's time.
                int dy = (int)Math.BigMul(slot.Reciprocal, cell, out _);
                x = (int)cell - (dy * slot.Columns) + slot.XMin;
                y = dy + slot.YMin;
            }

            ref byte record = ref Unsafe.Add(ref target, n * 5);
            Unsafe.WriteUnaligned(ref record, (short)x);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref record, 2), (short)y);
            Unsafe.Add(ref record, 4) = slot.Full
                ? (byte)(field >> slot.PositionBits)
                : (byte)(slot.ItemFlags | (gone ? 4 | 8 : 0));
        }
    }

    // Kept out of line: an interpolated message in the hot method costs more
    // than the check it reports on.
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowOutOfRange(int slot, int x, int y) =>
        throw Broken(slot, $"position ({x},{y}) outside the compact range");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowBadFlags(int slot, byte flags, int expected) =>
        throw Broken(slot, expected < 0
            ? $"flags 0x{flags:X2} use more than {FlagBits} bits"
            : $"flags 0x{flags:X2}, expected 0x{expected:X2}");

    private InvalidOperationException Broken(int slot, string what) => new(
        $"{_level.Name}: model {_models[slot]} ({_level.Models[_models[slot]].Kind}) breaks a compact-key invariant: {what}");
}
