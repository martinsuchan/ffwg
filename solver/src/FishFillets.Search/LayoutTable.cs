using System.Buffers.Binary;
using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// The layout solver's two-level visited set: item layouts first, then per
/// layout the fish configurations it was entered with and the cheapest cost.
///
/// <para><b>Why two levels.</b> Plain A* stores one state per combination of
/// fish positions; on <c>broom</c> that is 5.4 M states over only 1,143 item
/// layouts (solver/docs/023). Everything that depends on the items alone - the
/// item bound, later the stuck verdict and the push catalogue - belongs to the
/// layout and is computed once there (<see cref="Tag"/>), while the fish
/// configurations a layout is entered with are a small dictionary under it.</para>
///
/// <para><b>Level 1 - layouts.</b> The key is the wide state key
/// (<c>Room.WriteStateKey</c> over the mobile models, 5 bytes each) made
/// canonical: identical items (<see cref="ItemSymmetry"/>) sorted within their
/// class, and every fish still in the room reduced to "it is here" - position
/// and facing zeroed, its other flags kept. A fish that is out or lost keeps its
/// bytes, so a fish leaving is a new layout.</para>
///
/// <para><b>Level 2 - entries.</b> (layout id, caller-chosen entry key) with
/// min-cost semantics. The entry key's meaning (which fish is the point, where
/// it stands, the other fish's home...) is the caller's; this table only needs
/// its size.</para>
/// </summary>
internal sealed class LayoutTable
{
    private const byte FacingBit = 1;
    private const byte OutOrLost = 4 | 8;

    private readonly int _wideSize;
    private readonly int[] _fishSlots;
    private readonly int[][] _classes;
    private readonly byte[] _canonical;
    private readonly byte[] _scratch = new byte[StateKeyEdit.SlotSize];

    private readonly StateSet _layouts;
    private readonly StateSet _entries;
    private readonly int _entrySize;
    private readonly byte[] _entryKey;

    private int[] _entriesIn = new int[64];
    private int[] _tags = new int[64];

    public LayoutTable(Level level, int[] mobileModels, int entryKeySize)
    {
        _wideSize = mobileModels.Length * StateKeyEdit.SlotSize;
        if (_wideSize == 0)
        {
            throw new ArgumentException("a layout needs at least one mobile model", nameof(mobileModels));
        }

        var fish = new List<int>();
        for (int n = 0; n < mobileModels.Length; n++)
        {
            if (level.Models[mobileModels[n]].IsAlive)
            {
                fish.Add(n);
            }
        }

        _fishSlots = [.. fish];
        _classes = ItemSymmetry.Classes(level, mobileModels);
        _canonical = new byte[_wideSize];

        _layouts = new StateSet(_wideSize);
        _entrySize = sizeof(int) + entryKeySize;
        _entries = new StateSet(_entrySize);
        _entryKey = new byte[_entrySize];
        _tags.AsSpan().Fill(-1);
    }

    /// <summary>Bytes of a wide key and of <see cref="LayoutKey"/>.</summary>
    public int WideSize => _wideSize;

    public int LayoutCount => _layouts.Count;

    public int EntryCount => _entries.Count;

    /// <summary>
    /// Writes the canonical layout of <paramref name="wideKey"/> - what
    /// <see cref="Intern"/> stores - into <paramref name="destination"/>.
    /// </summary>
    public void Canonicalize(ReadOnlySpan<byte> wideKey, Span<byte> destination)
    {
        if (wideKey.Length != _wideSize)
        {
            throw new ArgumentException($"wide key has {wideKey.Length} bytes, expected {_wideSize}", nameof(wideKey));
        }

        wideKey.CopyTo(destination);

        foreach (int slot in _fishSlots)
        {
            int o = slot * StateKeyEdit.SlotSize;
            if ((destination[o + 4] & OutOrLost) != 0)
            {
                continue; // gone: where and how it left is part of the layout
            }

            destination[o] = 0;
            destination[o + 1] = 0;
            destination[o + 2] = 0;
            destination[o + 3] = 0;
            destination[o + 4] &= unchecked((byte)~FacingBit);
        }

        foreach (int[] members in _classes)
        {
            SortClass(destination, members);
        }
    }

    /// <summary>
    /// The dense id (from 0) of the canonical layout of a wide state key - see
    /// <see cref="Canonicalize"/>: identical items that swapped places, and fish
    /// standing anywhere in the room, intern to the same id.
    /// </summary>
    public int Intern(ReadOnlySpan<byte> wideKey, out bool added)
    {
        Canonicalize(wideKey, _canonical);

        int before = _layouts.Count;
        _layouts.TryImprove(_canonical, 0, out int offset);
        int id = offset / _wideSize;
        added = _layouts.Count != before;

        if (added && id >= _tags.Length)
        {
            int size = _tags.Length * 2;
            int old = _tags.Length;
            Array.Resize(ref _tags, size);
            _tags.AsSpan(old).Fill(-1);
            Array.Resize(ref _entriesIn, size);
        }

        return id;
    }

    /// <summary>
    /// The canonical wide key of a layout, fish in the room at (0, 0) facing
    /// right with their other flags intact. Writing each such fish's placement
    /// with <see cref="StateKeyEdit.SetPlacement"/> gives a key a room can be
    /// restored from (<c>Room.RestoreMobile</c>). The span points into the table
    /// and stays valid until the next <see cref="Intern"/>.
    /// </summary>
    public ReadOnlySpan<byte> LayoutKey(int layoutId) => _layouts.KeyAt(layoutId * _wideSize);

    /// <summary>
    /// Records entry (<paramref name="layoutId"/>, <paramref name="entryKey"/>) at
    /// cost <paramref name="g"/> if it is new or cheaper than before.
    /// </summary>
    /// <param name="entryId">A stable dense id for the (layout, entry) pair, from 0.</param>
    /// <param name="added">Whether the pair was never seen before.</param>
    /// <returns>Whether the caller should expand it: new, or reached more cheaply.</returns>
    public bool TryImprove(int layoutId, ReadOnlySpan<byte> entryKey, int g, out int entryId, out bool added)
    {
        if (entryKey.Length != _entrySize - sizeof(int))
        {
            throw new ArgumentException(
                $"entry key has {entryKey.Length} bytes, expected {_entrySize - sizeof(int)}", nameof(entryKey));
        }

        if ((uint)layoutId >= (uint)_layouts.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(layoutId));
        }

        BinaryPrimitives.WriteInt32LittleEndian(_entryKey, layoutId);
        entryKey.CopyTo(_entryKey.AsSpan(sizeof(int)));

        int before = _entries.Count;
        bool improved = _entries.TryImprove(_entryKey, g, out int offset);
        entryId = offset / _entrySize;
        added = _entries.Count != before;

        if (added)
        {
            _entriesIn[layoutId]++;
        }

        return improved;
    }

    /// <summary>The cheapest cost an entry has been reached at.</summary>
    public int BestCost(int entryId) => _entries.CostAt(entryId * _entrySize);

    /// <summary>The layout an entry belongs to.</summary>
    public int LayoutOf(int entryId) =>
        BinaryPrimitives.ReadInt32LittleEndian(_entries.KeyAt(entryId * _entrySize));

    /// <summary>The caller's entry key of an entry (without the layout id).</summary>
    public ReadOnlySpan<byte> EntryKey(int entryId) => _entries.KeyAt(entryId * _entrySize)[sizeof(int)..];

    /// <summary>How many distinct entries this layout has.</summary>
    public int EntriesIn(int layoutId) => _entriesIn[layoutId];

    /// <summary>
    /// One int of the solver's own per layout (for example the cached item
    /// bound), -1 until set. The reference points into a growable array: do not
    /// hold it across an <see cref="Intern"/>, or a write through it is lost.
    /// </summary>
    public ref int Tag(int layoutId)
    {
        if ((uint)layoutId >= (uint)_layouts.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(layoutId));
        }

        return ref _tags[layoutId];
    }

    /// <summary>
    /// Sorts one class's 5-byte slots by plain byte order. Any fixed total order
    /// makes the key canonical; classes are short and a layout differs from its
    /// parent's by a move or two, so insertion sort is linear in practice.
    /// </summary>
    private void SortClass(Span<byte> key, int[] members)
    {
        const int size = StateKeyEdit.SlotSize;
        for (int i = 1; i < members.Length; i++)
        {
            key.Slice(members[i] * size, size).CopyTo(_scratch);
            int j = i - 1;
            while (j >= 0 && key.Slice(members[j] * size, size).SequenceCompareTo(_scratch) > 0)
            {
                key.Slice(members[j] * size, size).CopyTo(key.Slice(members[j + 1] * size, size));
                j--;
            }

            _scratch.CopyTo(key.Slice(members[j + 1] * size, size));
        }
    }
}
