using System.Runtime.CompilerServices;

namespace FishFillets.Physics;

/// <summary>
/// A saved room position. Reusable: a search allocates one per depth level and
/// keeps refilling it, so exploring costs no allocation.
/// </summary>
public sealed class RoomSnapshot
{
    internal ModelState[] Models;
    internal int StepCount;

    public RoomSnapshot(Room room)
    {
        Models = new ModelState[room.SlotCount];
        room.CaptureTo(this);
    }
}

public sealed partial class Room
{
    // Save/restore and the canonical state key - the primitives a search needs
    // on top of the simulation. Two facts make both cheap:
    //
    //  - The grid is DERIVED, never authoritative. Reset() already rebuilds it by
    //    clearing and re-masking every model, so a snapshot only has to hold
    //    ModelState[] (~300 bytes on a typical level) rather than the grid too
    //    (another ~2.5 KB), and restoring re-stamps it.
    //  - A SETTLED state has no in-flight bookkeeping. After SettleAll() the
    //    round pipeline has cleared Dir, Pushing, LastFall and every ReadyTo*
    //    flag, and Weight is back to its declared value (changeGoingOut's
    //    transient Fixed always resolves inside the settle). So the key is just
    //    position, facing and the alive/out flags.

    internal int SlotCount => _models.Length;

    /// <summary>Saves the current position into a reusable snapshot.</summary>
    public void CaptureTo(RoomSnapshot snapshot)
    {
        _models.AsSpan().CopyTo(snapshot.Models);
        snapshot.StepCount = _stepCount;
    }

    public RoomSnapshot Capture() => new(this);

    /// <summary>
    /// Restores a saved position, rebuilding the grid from it. The snapshot must
    /// come from a room built on the same <see cref="Level"/>.
    /// </summary>
    public void RestoreFrom(RoomSnapshot snapshot)
    {
        snapshot.Models.AsSpan().CopyTo(_models);
        _stepCount = snapshot.StepCount;
        _lastAction = Action.No;
        _lastImpact = Weight.None;
        _lastDeadCount = 0;

        _field.AsSpan().Fill(Empty);
        for (int i = 0; i < ModelCount; i++)
        {
            // A model that has left the room sits at (-1000,-1000), where every
            // write falls out of bounds and is dropped - so this needs no
            // special case for it.
            Mask(i);
        }
    }

    // ------------------------------------------------------------ state key --

    /// <summary>Bytes <see cref="WriteStateKey"/> needs. Constant for a level.</summary>
    public int StateKeySize => Level.MutableModels.Length * 5;

    /// <summary>
    /// Writes the canonical key for the current (settled) state. Two rooms with
    /// equal keys are interchangeable: any move string valid from one is valid
    /// from the other and produces the same result. That is what makes it sound
    /// to splice one path segment out for another.
    ///
    /// Only <see cref="Level.MutableModels"/> contribute - a model that is not
    /// alive, cannot be pushed and has no goal to leave by can never change, so
    /// including it would just cost bytes.
    ///
    /// TouchDir is deliberately excluded: <c>SetTouched()</c> records it even on
    /// a REJECTED move, so two otherwise-identical states can differ in it. It is
    /// write-only as far as physics goes (the next round's OccupyNewPos clears
    /// it) and exists only for levels that read it from Lua - see docs/033.
    /// </summary>
    public void WriteStateKey(Span<byte> destination) => WriteStateKey(destination, Level.MutableModels);

    /// <summary>
    /// As <see cref="WriteStateKey(Span{byte})"/>, but over an explicit model
    /// set - a search that has proven more models immobile than the type rules
    /// can (see FishFillets.Search's LevelReduction) passes the smaller set here,
    /// which is what shrinks its state space.
    /// </summary>
    public void WriteStateKey(Span<byte> destination, int[] models)
    {
        int[] mutable = models;
        for (int n = 0; n < mutable.Length; n++)
        {
            ref ModelState m = ref _models[mutable[n]];
            int offset = n * 5;
            // Two bytes each: positions go negative while a model crosses the
            // border, and -1000/-1000 once it has left, so a plain cell index
            // won't do. Everything stays well inside 16 bits.
            destination[offset] = (byte)m.X;
            destination[offset + 1] = (byte)(m.X >> 8);
            destination[offset + 2] = (byte)m.Y;
            destination[offset + 3] = (byte)(m.Y >> 8);
            destination[offset + 4] = (byte)(
                (m.IsLeft ? 1 : 0)
                | (m.IsAlive ? 2 : 0)
                | (m.IsOut ? 4 : 0)
                | (m.IsLost ? 8 : 0)
                | (m.Busy ? 16 : 0)
                // windoze's output plug turns into a normal item as it is used up.
                | ((m.OutCapacity & 3) << 5));
        }
    }

    /// <summary>
    /// Rebuilds the room from a state key alone, without a snapshot.
    ///
    /// This works because for a <b>settled, still-solvable</b> state the key is
    /// not a digest - it is the whole state. Everything else is either constant
    /// (shape, power, goal), derived (Weight follows IsLost and the plug
    /// capacity; the grid is re-stamped) or provably zero: the round pipeline
    /// clears Dir/Pushing/LastFall/ReadyTo* on settling, no model is ever left
    /// half-way through the border once fresh, and DeathRoundsLeft is only ever
    /// non-zero for a corpse - which makes the level unsolvable, so a search has
    /// already discarded it.
    ///
    /// Storing 5 bytes per mutable model instead of a full ModelState array is
    /// what lets a search hold tens of millions of states. Do not call it for a
    /// mid-settle or unsolvable state.
    /// </summary>
    public void RestoreFromKey(ReadOnlySpan<byte> key) => RestoreFromKey(key, Level.MutableModels);

    public void RestoreFromKey(ReadOnlySpan<byte> key, int[] models)
    {
        for (int i = 0; i < _models.Length; i++)
        {
            ResetModelToDeclared(i);
        }

        for (int n = 0; n < models.Length; n++)
        {
            DecodeModel(models[n], key, n);
        }

        _lastAction = Action.No;
        _lastImpact = Weight.None;
        _lastDeadCount = 0;

        _field.AsSpan().Fill(Empty);
        for (int i = 0; i < ModelCount; i++)
        {
            Mask(i);
        }
    }

    /// <summary>
    /// The hot path version: rewrites only <paramref name="models"/>, leaving
    /// every other model masked where it already is.
    ///
    /// A search restores a state up to nine times per node expansion (once to
    /// read it, once per move symbol tried), so the cost of a restore sets the
    /// search's speed. <see cref="RestoreFromKey(ReadOnlySpan{byte}, int[])"/>
    /// re-fills the whole grid and re-stamps every model - including each level's
    /// room shape, which can be a thousand cells and never moves. This touches
    /// only the handful of cells that can differ.
    ///
    /// Requires the room to already hold a state of the same level with the same
    /// frozen models in place - which is true throughout a search, since it never
    /// leaves them.
    /// </summary>
    public void RestoreMobile(ReadOnlySpan<byte> key, int[] models)
    {
        foreach (int i in models)
        {
            Unmask(i);
        }

        for (int n = 0; n < models.Length; n++)
        {
            int index = models[n];
            ResetModelToDeclared(index);
            DecodeModel(index, key, n);
        }

        foreach (int i in models)
        {
            Mask(i);
        }

        _lastAction = Action.No;
        _lastImpact = Weight.None;
        _lastDeadCount = 0;
    }

    private void ResetModelToDeclared(int index)
    {
        ModelDef def = Level.Models[index];
        _models[index] = new ModelState
        {
            X = def.X,
            Y = def.Y,
            Weight = def.Weight,
            IsAlive = def.IsAlive,
            IsLeft = def.IsLeft,
            OutDir = def.OutDir,
            OutCapacity = def.OutCapacity,
            Dir = Dir.No,
            TouchDir = Dir.No,
        };
    }

    private void DecodeModel(int index, ReadOnlySpan<byte> key, int slot)
    {
        ModelDef def = Level.Models[index];
        ref ModelState m = ref _models[index];
        int offset = slot * 5;

        m.X = (short)(key[offset] | (key[offset + 1] << 8));
        m.Y = (short)(key[offset + 2] | (key[offset + 3] << 8));

        byte flags = key[offset + 4];
        m.IsLeft = (flags & 1) != 0;
        m.IsAlive = (flags & 2) != 0;
        m.IsOut = (flags & 4) != 0;
        m.IsLost = (flags & 8) != 0;
        m.Busy = (flags & 16) != 0;

        int capacity = (flags >> 5) & 3;
        m.OutCapacity = (sbyte)(capacity == 3 ? -1 : capacity);

        if (def.OutDir != Dir.No)
        {
            // A spent plug stops being an output and becomes a light item.
            bool live = m.OutCapacity > 0;
            m.OutDir = live ? def.OutDir : Dir.No;
            m.Weight = live ? def.Weight : Weight.Light;
        }
        else
        {
            m.Weight = m.IsLost ? Weight.None : def.Weight;
        }
    }

    /// <summary>Position of a model, for a search's heuristics.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int X, int Y, bool IsLeft) Position(int model)
    {
        ref ModelState m = ref _models[model];
        return (m.X, m.Y, m.IsLeft);
    }
}
