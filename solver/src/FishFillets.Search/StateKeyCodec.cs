using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// Packs <c>Room.WriteStateKey</c>'s wide key (5 bytes per mobile model) into
/// the compact form the solver's state table stores, and back.
///
/// <para>The wide key spends two bytes on each coordinate (positions go to
/// -1000 once a model has left) and a flags byte on every model. In a settled
/// state almost none of that varies for a plain item: it is never alive, never
/// turns, is never busy and has no plug capacity, and the only way it leaves is
/// goal_out, which marks it out and lost together. So:</para>
/// <list type="bullet">
/// <item><b>plain item</b> - 2 bytes: x and y, each biased by
///   <see cref="Bias"/> into a byte; (0xFF, 0xFF) for "left the room".</item>
/// <item><b>fish, or windoze's output plug</b> - 3 bytes: the same position
///   bytes plus the wide flags byte unchanged.</item>
/// </list>
/// <para>On a typical level that halves the key (start: 30 -> 14 bytes), and
/// the key is the largest part of a stored state (review P1).</para>
///
/// <para>The invariants are checked on every encode and violations throw - a
/// silently wrong key would merge distinct states, the one bug a search cannot
/// see from the inside.</para>
/// </summary>
internal sealed class StateKeyCodec
{
    /// <summary>Added to a coordinate so it fits a byte. Anchors can sit a little left of or above the room (shapes may start with blank cells); rooms are under 64 wide.</summary>
    public const int Bias = 64;

    private const byte Gone = 0xFF;

    private readonly Level _level;
    private readonly int[] _models;
    private readonly bool[] _full;
    private readonly byte[] _itemFlags;   // the one flags byte a plain item has while in the room

    public StateKeyCodec(Level level, int[] mobileModels)
    {
        _level = level;
        _models = mobileModels;
        _full = new bool[mobileModels.Length];
        _itemFlags = new byte[mobileModels.Length];

        int size = 0;
        for (int n = 0; n < mobileModels.Length; n++)
        {
            ModelDef def = level.Models[mobileModels[n]];
            _full[n] = def.IsAlive || def.OutDir != Dir.No;
            _itemFlags[n] = (byte)(def.IsLeft ? 1 : 0);
            size += _full[n] ? 3 : 2;
        }

        CompactSize = size;
    }

    public int CompactSize { get; }

    public int WideSize => _models.Length * 5;

    public void Encode(ReadOnlySpan<byte> wide, Span<byte> compact)
    {
        int c = 0;
        for (int n = 0; n < _models.Length; n++)
        {
            int o = n * 5;
            int x = (short)(wide[o] | (wide[o + 1] << 8));
            int y = (short)(wide[o + 2] | (wide[o + 3] << 8));
            byte flags = wide[o + 4];

            if (x == -1000 && y == -1000)
            {
                compact[c] = Gone;
                compact[c + 1] = Gone;
            }
            else
            {
                int bx = x + Bias, by = y + Bias;
                if ((uint)bx >= Gone || (uint)by >= Gone)
                {
                    throw Broken(n, $"position ({x},{y}) outside the compact range");
                }

                compact[c] = (byte)bx;
                compact[c + 1] = (byte)by;
            }

            if (_full[n])
            {
                compact[c + 2] = flags;
                c += 3;
            }
            else
            {
                // In the room: exactly the declared facing and nothing else.
                // Gone: out and lost on top of it (goal_out is the only way).
                bool gone = compact[c] == Gone;
                byte expected = (byte)(_itemFlags[n] | (gone ? 4 | 8 : 0));
                if (flags != expected)
                {
                    throw Broken(n, $"flags 0x{flags:X2}, expected 0x{expected:X2}");
                }

                c += 2;
            }
        }
    }

    public void Decode(ReadOnlySpan<byte> compact, Span<byte> wide)
    {
        int c = 0;
        for (int n = 0; n < _models.Length; n++)
        {
            int o = n * 5;
            bool gone = compact[c] == Gone;
            int x = gone ? -1000 : compact[c] - Bias;
            int y = gone ? -1000 : compact[c + 1] - Bias;
            wide[o] = (byte)x;
            wide[o + 1] = (byte)(x >> 8);
            wide[o + 2] = (byte)y;
            wide[o + 3] = (byte)(y >> 8);

            if (_full[n])
            {
                wide[o + 4] = compact[c + 2];
                c += 3;
            }
            else
            {
                wide[o + 4] = (byte)(_itemFlags[n] | (gone ? 4 | 8 : 0));
                c += 2;
            }
        }
    }

    private InvalidOperationException Broken(int slot, string what) => new(
        $"{_level.Name}: model {_models[slot]} ({_level.Models[_models[slot]].Kind}) breaks a compact-key invariant: {what}");
}
