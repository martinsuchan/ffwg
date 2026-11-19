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
    private const int MaxPriority = 1 << 24;

    // Each bucket is a FIFO of fixed 4 KB blocks rather than a List<int>: a
    // list doubles by copying, so a bucket of tens of millions of entries
    // briefly needed its old and new array at once (1.5x its size on average,
    // 3x at the copy) and left the old one to the GC. Blocks are recycled
    // through a pool as buckets drain, so a long search allocates almost
    // nothing after its first few buckets.
    private const int BlockShift = 10;
    private const int BlockSize = 1 << BlockShift;

    private Bucket[] _buckets = [];
    private readonly Stack<int[]> _pool = new();
    private int _blocks;
    private int _current;

    private struct Bucket
    {
        public List<int[]?>? Blocks;
        public int Head;   // entries already popped
        public int Count;  // entries pushed
    }

    public void Clear()
    {
        for (int i = 0; i < _buckets.Length; i++)
        {
            Release(ref _buckets[i]);
        }

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

        ref Bucket bucket = ref _buckets[priority];
        List<int[]?> blocks = bucket.Blocks ??= [];
        int slot = bucket.Count & (BlockSize - 1);
        if (slot == 0)
        {
            blocks.Add(Rent());
        }

        blocks[blocks.Count - 1]![slot] = item;
        bucket.Count++;
        if (priority < _current)
        {
            _current = priority;
        }
    }

    public bool TryPop(out int item, out int priority)
    {
        while (_current < _buckets.Length && _buckets[_current].Head >= _buckets[_current].Count)
        {
            // Nothing will be popped from it again unless something is pushed
            // into it, so give the storage back - on a long search these add up.
            // Resetting the cursor keeps that later push correct.
            Release(ref _buckets[_current]);
            _current++;
        }

        if (_current >= _buckets.Length)
        {
            item = 0;
            priority = 0;
            return false;
        }

        priority = _current;
        item = Take(ref _buckets[_current]);
        return true;
    }

    public bool TryPopFrom(int priority, out int item)
    {
        if (_current == priority && priority < _buckets.Length && _buckets[priority].Head < _buckets[priority].Count)
        {
            item = Take(ref _buckets[priority]);
            return true;
        }

        item = 0;
        return false;
    }

    public long MemoryBytes => ((long)_blocks * BlockSize * sizeof(int)) + ((long)_buckets.Length * 16);

    private int Take(ref Bucket bucket)
    {
        // Entry i lives in block i >> BlockShift; a block goes back to the pool
        // the moment its last entry is taken, leaving a null behind so later
        // indices stay put.
        List<int[]?> blocks = bucket.Blocks!;
        int index = bucket.Head >> BlockShift;
        int slot = bucket.Head & (BlockSize - 1);
        int[] block = blocks[index]!;
        int item = block[slot];
        bucket.Head++;
        if (slot == BlockSize - 1)
        {
            _pool.Push(block);
            blocks[index] = null;
        }

        return item;
    }

    private void Release(ref Bucket bucket)
    {
        if (bucket.Blocks is { } blocks)
        {
            foreach (int[]? block in blocks)
            {
                if (block is not null)
                {
                    _pool.Push(block);
                }
            }

            blocks.Clear();
        }

        bucket.Head = 0;
        bucket.Count = 0;
    }

    private int[] Rent()
    {
        if (_pool.TryPop(out int[]? block))
        {
            return block;
        }

        _blocks++;
        return new int[BlockSize];
    }

    private void Grow(int priority)
    {
        int size = Math.Max(priority + 1, Math.Max(64, _buckets.Length * 2));
        Array.Resize(ref _buckets, size);
    }
}
