namespace FishFillets.Search;

/// <summary>
/// The open list shared by every A*-style search here: one FIFO bucket per
/// integer priority, always popping from the lowest non-empty bucket.
///
/// <para>Lifted out of <see cref="Solver"/> (solver/docs/013) after the review
/// found the macro and item solvers still on the old single forward sweep (C2).
/// A sweep is only valid while f never decreases along an edge, which holds at
/// weight 1 with a consistent heuristic and nowhere else: above weight 1 every
/// edge that makes progress lowers f by one, so the sweep pushed children into
/// buckets it had already passed and cleared, and lost them - on micro-levels,
/// usually the whole search after the first expansion. Here a push below the
/// cursor simply pulls the cursor back.</para>
///
/// <para>At weight 1 nothing is pushed below the cursor, each bucket drains
/// front to back exactly as the old sweep did, and the expansion order - so every
/// recorded node count - is unchanged.</para>
/// </summary>
internal sealed class BucketQueue
{
    /// <summary>A sanity cap: a priority this large is a bug (an "unreachable" estimate leaking in), not a deep level.</summary>
    private const int MaxPriority = 1 << 24;

    private List<int>[] _buckets = [];

    /// <summary>How far into each bucket the pop cursor has got.</summary>
    private int[] _drained = [];

    /// <summary>The lowest bucket that may still hold anything.</summary>
    private int _current;

    public void Clear()
    {
        foreach (List<int> bucket in _buckets)
        {
            bucket.Clear();
        }

        Array.Clear(_drained);
        _current = 0;
    }

    public void Push(int item, int priority)
    {
        if ((uint)priority >= MaxPriority)
        {
            throw new InvalidOperationException(
                $"open-list priority {priority} is out of range - an unreachable estimate reached the queue");
        }

        if (priority >= _buckets.Length)
        {
            Grow(priority);
        }

        _buckets[priority].Add(item);
        if (priority < _current)
        {
            _current = priority;
        }
    }

    /// <summary>Takes the oldest item of the lowest non-empty bucket.</summary>
    public bool TryPop(out int item, out int priority)
    {
        while (_current < _buckets.Length && _drained[_current] >= _buckets[_current].Count)
        {
            // Nothing will be popped from it again unless something is pushed
            // into it, so give the storage back - on a long search these add up.
            // Resetting the cursor keeps that later push correct.
            _buckets[_current].Clear();
            _buckets[_current].TrimExcess();
            _drained[_current] = 0;
            _current++;
        }

        if (_current >= _buckets.Length)
        {
            item = 0;
            priority = 0;
            return false;
        }

        priority = _current;
        item = _buckets[_current][_drained[_current]++];
        return true;
    }

    /// <summary>
    /// Takes the next item only if it comes from bucket <paramref name="priority"/>
    /// - for gathering a batch that the serial order would expand back to back.
    /// </summary>
    public bool TryPopFrom(int priority, out int item)
    {
        if (_current == priority && priority < _buckets.Length && _drained[priority] < _buckets[priority].Count)
        {
            item = _buckets[priority][_drained[priority]++];
            return true;
        }

        item = 0;
        return false;
    }

    /// <summary>Bytes held by the bucket lists at their current capacity.</summary>
    public long MemoryBytes
    {
        get
        {
            long bytes = 0;
            foreach (List<int> bucket in _buckets)
            {
                bytes += (long)bucket.Capacity * sizeof(int);
            }

            return bytes;
        }
    }

    private void Grow(int priority)
    {
        int size = Math.Max(priority + 1, Math.Max(64, _buckets.Length * 2));
        var grown = new List<int>[size];
        Array.Copy(_buckets, grown, _buckets.Length);
        for (int i = _buckets.Length; i < size; i++)
        {
            grown[i] = [];
        }

        _buckets = grown;
        Array.Resize(ref _drained, size);
    }
}
