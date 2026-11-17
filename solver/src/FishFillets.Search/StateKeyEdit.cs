namespace FishFillets.Search;

/// <summary>
/// Edits of <c>Room.WriteStateKey</c>'s wide key (5 bytes per mobile model:
/// x low/high, y low/high, flags with bit 0 = facing left) that do not need a
/// room. Moving a fish in the key is the whole effect of an inert route, which
/// is what lets the decomposed searches build a state without simulating travel.
/// </summary>
internal static class StateKeyEdit
{
    /// <summary>Bytes one model takes in the wide key.</summary>
    public const int SlotSize = 5;

    /// <summary>Puts the model in <paramref name="slot"/> at (x, y) with the given facing; every other bit is kept.</summary>
    public static void SetPlacement(Span<byte> key, int slot, int x, int y, bool isLeft)
    {
        int offset = slot * SlotSize;
        key[offset] = (byte)x;
        key[offset + 1] = (byte)(x >> 8);
        key[offset + 2] = (byte)y;
        key[offset + 3] = (byte)(y >> 8);
        key[offset + 4] = (byte)((key[offset + 4] & ~1) | (isLeft ? 1 : 0));
    }

    /// <summary>Where the model in <paramref name="slot"/> is, as stored in the key.</summary>
    public static (int X, int Y, bool IsLeft) GetPlacement(ReadOnlySpan<byte> key, int slot)
    {
        int offset = slot * SlotSize;
        int x = (short)(key[offset] | (key[offset + 1] << 8));
        int y = (short)(key[offset + 2] | (key[offset + 3] << 8));
        return (x, y, (key[offset + 4] & 1) != 0);
    }
}
