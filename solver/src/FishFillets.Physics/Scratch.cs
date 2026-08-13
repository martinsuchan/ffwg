using System.Runtime.CompilerServices;

namespace FishFillets.Physics;

/// <summary>
/// A stack arena for the deduplicated "who resists me" lists the rules ask for
/// constantly. The browser port returns a fresh array from every
/// MarkMask.getResist() call (allocating, plus a Set for dedup) - here every
/// list is a frame pushed onto one preallocated buffer and popped when the
/// caller's scope ends, so the whole simulation runs without allocating.
///
/// Frames nest: the rules recurse (canMoveOthers -&gt; canDir -&gt; canMoveOthers,
/// isOnCond down a stack, whoIsFalling up one) while iterating an outer list, so
/// LIFO discipline is required - always consume a frame inside its `using`.
///
/// Deduplication uses a rolling stamp per model instead of a hash set: O(1) per
/// candidate, no clearing between frames.
/// </summary>
internal sealed class ResistArena
{
    private readonly int[] _buffer;
    private readonly int[] _stamp;
    private int _top;
    private int _generation;

    /// <param name="slots">Number of model slots, including the border.</param>
    /// <param name="maxDepth">
    /// Recursion bound. Support chains are strictly vertical and push chains
    /// strictly horizontal, so no chain can be longer than the room's larger
    /// dimension; the buffer is sized for the worst case of every frame holding
    /// every model at every depth, and overflow therefore means a genuine bug
    /// (a cycle) rather than a big level.
    /// </param>
    public ResistArena(int slots, int maxDepth)
    {
        _stamp = new int[slots];
        _buffer = new int[checked(slots * (maxDepth + 8) + 256)];
    }

    private ReadOnlySpan<int> ItemsFrom(int start) => _buffer.AsSpan(start, _top - start);

    /// <summary>Opens a frame; every <see cref="Push"/> until it is disposed belongs to it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Frame Open()
    {
        if (++_generation == int.MaxValue)
        {
            Array.Clear(_stamp);
            _generation = 1;
        }

        return new Frame(this, _top, _generation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Push(int model, int generation)
    {
        ref int stamp = ref _stamp[model];
        if (stamp == generation)
        {
            return; // already in this frame
        }

        stamp = generation;
        if (_top == _buffer.Length)
        {
            throw new InvalidOperationException(
                "resist arena overflow - a support or push chain recursed deeper than the room can be");
        }

        _buffer[_top++] = model;
    }

    /// <summary>
    /// One deduplicated resist list. Dispose (i.e. let the `using` end) pops it;
    /// the enclosing frame's contents are untouched.
    /// </summary>
    internal readonly ref struct Frame
    {
        private readonly ResistArena _arena;
        private readonly int _generation;

        public readonly int Start;

        internal Frame(ResistArena arena, int start, int generation)
        {
            _arena = arena;
            Start = start;
            _generation = generation;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(int model) => _arena.Push(model, _generation);

        public ReadOnlySpan<int> Items => _arena.ItemsFrom(Start);

        public void Dispose() => _arena._top = Start;
    }
}

/// <summary>
/// A flat, deduplicated result list for the three rules that accumulate across
/// their own recursion while an outer resist frame is still open (getPads,
/// whoIsFalling, whoIsHeavier). Those can't share <see cref="ResistArena"/>,
/// whose LIFO discipline they'd violate; only one is ever live at a time, but
/// each gets its own instance so that stays true by construction.
/// </summary>
internal sealed class ModelCollector
{
    private readonly int[] _items;
    private readonly int[] _stamp;
    private int _count;
    private int _generation;

    public ModelCollector(int slots)
    {
        _items = new int[slots];
        _stamp = new int[slots];
    }

    public int Count => _count;

    public ReadOnlySpan<int> Items => _items.AsSpan(0, _count);

    public void Reset()
    {
        _count = 0;
        if (++_generation == int.MaxValue)
        {
            Array.Clear(_stamp);
            _generation = 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int model)
    {
        ref int stamp = ref _stamp[model];
        if (stamp == _generation)
        {
            return;
        }

        stamp = _generation;
        _items[_count++] = model;
    }
}
