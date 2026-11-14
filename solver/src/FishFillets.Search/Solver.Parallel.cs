using System.Diagnostics;
using System.Runtime.ExceptionServices;

using FishFillets.Physics;

namespace FishFillets.Search;

public sealed partial class Solver
{
    // Parallel expansion (review P4). Nearly all of an expansion is physics -
    // restoring a state and applying every move symbol to it - and the physics of
    // one node's successors does not depend on any other node's. So each batch of
    // nodes popped from the same f-bucket is expanded in two phases:
    //
    //  1. in parallel: every worker restores its nodes into its own Room and
    //     simulates their successors, producing for each one its compact key, the
    //     key's hash, the move and the estimate - reading the state store but
    //     never writing it;
    //  2. serially, in batch order: each node's goal test, then its successors
    //     merged into the store and queue exactly as the serial loop would.
    //
    // At weight 1 this reproduces the serial search exactly. Nodes of one bucket
    // are expanded back to back in FIFO order by the serial loop too, and every
    // child they produce lands at the end of a bucket at or above the current
    // one, so popping a whole batch first changes no order - and phase 2's merge
    // order is the serial insertion order. Same expansions, same counts, same
    // answer; only faster. (Above weight 1 a child can land in a lower bucket,
    // which the serial loop would turn to at once; here it waits for the next
    // batch - a different, equally valid order.)

    private const int BatchSize = 2048;

    /// <summary>Contiguous batch slots a worker takes at a time in phase 1.</summary>
    private const int RunLength = 16;

    private sealed class Worker
    {
        public required Room Room { get; init; }

        public required byte[] Key { get; init; }

        public required byte[] Wide { get; init; }

        public required ModelState[] Undo { get; init; }

        public required StuckDetector.Probe Stuck { get; init; }
    }

    private Worker[] _workers = [];

    // Per batch slot: the node, its results.
    private int[] _batch = [];
    private bool[] _batchSolved = [];
    private int[] _batchChildren = [];
    private int[] _batchGenerated = [];

    // Per batch slot x symbol: one generated successor.
    private byte[] _childKeys = [];
    private int[] _childHash = [];
    private byte[] _childSymbol = [];
    private ushort[] _childGuide = [];
    private bool[] _childDead = [];
    private bool[] _childFinished = [];
    private ushort[] _childPlain = [];

    private SolveResult SolveParallel(SolveOptions options, Stopwatch stopwatch)
    {
        int threads = options.Threads;
        PrepareWorkers(threads);

        long expanded = 0, generated = 1;
        int deepestF = 0;
        double nextReport = options.ProgressSeconds;
        int keySize = _codec.CompactSize;

        while (_queue.TryPop(out int first, out int f))
        {
            // Gather a batch from this bucket only, dropping stale entries as the
            // serial loop would on popping them.
            int count = 0;
            int node = first;
            do
            {
                if (Priority(_store.Cost(node), _store.Estimate(node)) == f)
                {
                    _batch[count++] = node;
                }
            }
            while (count < BatchSize && _queue.TryPopFrom(f, out node));

            if (count == 0)
            {
                continue;
            }

            deepestF = Math.Max(deepestF, f);
            RunPhaseOne(threads, count);
            _stuck.TrimIfFull();

            for (int slot = 0; slot < count; slot++)
            {
                int parent = _batch[slot];
                int g = _store.Cost(parent);

                // At weight 1 nothing in this batch can have been improved by an
                // earlier node's children; above it, one can - then the serial loop
                // would have skipped this entry as stale, so do the same.
                if (Priority(g, _store.Estimate(parent)) != f)
                {
                    continue;
                }

                if (_batchSolved[slot])
                {
                    return new SolveResult(
                        Reconstruct(parent), options.Weight <= 1.0, "solved",
                        expanded, generated, _store.Count, ProvenBound(f), stopwatch.Elapsed);
                }

                expanded++;
                generated += _batchGenerated[slot];

                if (g + 1 > ushort.MaxValue)
                {
                    throw new InvalidOperationException("path longer than 65,535 moves - the state store keeps costs in 16 bits");
                }

                int baseIndex = slot * _symbols.Length;
                int parentEstimate = _store.Estimate(parent);
                for (int c = 0; c < _batchChildren[slot]; c++)
                {
                    int o = baseIndex + c;

                    // Phase 1 computed the estimate both with and without the
                    // stuck-item rule, and marked what it condemns; the choice
                    // is made here, in serial order, so that a probation ending
                    // part-way through a batch (see EndStuckProbation) takes
                    // effect at exactly the expansion the serial loop would.
                    int h = _childPlain[o];
                    if (!_childFinished[o])
                    {
                        if (_stuckActive)
                        {
                            if (_childDead[o])
                            {
                                CountStuckPrune(expanded);
                                continue;
                            }

                            if (_childGuide[o] > h)
                            {
                                _stuckRaised++;
                                h = _childGuide[o];
                            }
                        }

                        // Pathmax, as in the serial loop.
                        h = Math.Max(h, parentEstimate - 1);
                    }

                    ushort guide = (ushort)Math.Min(h, ushort.MaxValue);

                    ReadOnlySpan<byte> key = _childKeys.AsSpan(o * keySize, keySize);
                    int child = _store.FindOrAdd(key, _childHash[o], out bool added);
                    if (!added && g + 1 >= _store.Cost(child))
                    {
                        continue;
                    }

                    _store.Parent(child) = parent;
                    _store.Cost(child) = (ushort)(g + 1);
                    _store.Estimate(child) = guide;
                    _store.Move(child) = _childSymbol[o];
                    _queue.Push(child, Priority(g + 1, guide));
                }

                ReportProgress(options, stopwatch, expanded, f, ref nextReport);
                EndStuckProbation(expanded);

                if (expanded >= options.MaxNodes)
                {
                    return Abandoned("node limit reached", expanded, generated, deepestF, stopwatch);
                }
            }

            if (options.TimeLimit is { } limit && stopwatch.Elapsed > limit)
            {
                return Abandoned("time limit reached", expanded, generated, deepestF, stopwatch);
            }
        }

        return new SolveResult(
            null, options.Weight <= 1.0, "exhausted: no solution exists", expanded, generated,
            _store.Count, ProvenBound(deepestF), stopwatch.Elapsed);
    }

    private void PrepareWorkers(int threads)
    {
        if (_workers.Length != threads)
        {
            _workers = new Worker[threads];
            for (int w = 0; w < threads; w++)
            {
                _workers[w] = new Worker
                {
                    Room = new Room(_room.Level),
                    Key = new byte[_key.Length],
                    Wide = new byte[_wide.Length],
                    Undo = new ModelState[_mobile.Length],
                    Stuck = _stuck.NewProbe(),
                };
            }
        }

        if (_batch.Length < BatchSize)
        {
            int children = BatchSize * _symbols.Length;
            _batch = new int[BatchSize];
            _batchSolved = new bool[BatchSize];
            _batchChildren = new int[BatchSize];
            _batchGenerated = new int[BatchSize];
            _childKeys = new byte[children * _codec.CompactSize];
            _childHash = new int[children];
            _childSymbol = new byte[children];
            _childGuide = new ushort[children];
            _childDead = new bool[children];
            _childFinished = new bool[children];
            _childPlain = new ushort[children];
        }
    }

    private void RunPhaseOne(int threads, int count)
    {
        // Small batches (the first few f-layers) are not worth waking threads for.
        int workers = Math.Min(threads, Math.Max(1, count / 16));
        if (workers == 1)
        {
            for (int slot = 0; slot < count; slot++)
            {
                ExpandIntoBatch(_workers[0], slot);
            }

            return;
        }

        // Slots are handed out in small contiguous runs rather than interleaved:
        // neighbouring slots are mostly siblings and cousins sharing an item
        // layout, so a run keeps a worker's stuck-verdict front warm (docs/020),
        // while taking runs from a shared counter still balances the load.
        int next = 0;
        try
        {
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, w =>
            {
                Worker worker = _workers[w];
                int start;
                while ((start = Interlocked.Add(ref next, RunLength) - RunLength) < count)
                {
                    int end = Math.Min(start + RunLength, count);
                    for (int slot = start; slot < end; slot++)
                    {
                        ExpandIntoBatch(worker, slot);
                    }
                }
            });
        }
        catch (AggregateException error) when (error.InnerExceptions.Count >= 1)
        {
            // Surface what a serial search would have thrown (ThrowIfFrozenMoved, a
            // broken key invariant), not the wrapper.
            ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw();
        }
    }

    /// <summary>Phase 1 for one batch slot: the serial loop's per-node work, minus every write to shared state.</summary>
    private void ExpandIntoBatch(Worker w, int slot)
    {
        int node = _batch[slot];
        _codec.Decode(_store.Key(node), w.Wide);
        w.Room.RestoreMobile(w.Wide, _mobile);

        _batchChildren[slot] = 0;
        _batchGenerated[slot] = 0;
        _batchSolved[slot] = w.Room.IsSolved();
        if (_batchSolved[slot])
        {
            return;
        }

        int keySize = _codec.CompactSize;
        int baseIndex = slot * _symbols.Length;
        int children = 0, generated = 0;
        bool restoreNeeded = false;
        w.Room.SaveMobile(w.Undo, _mobile);
        // Read once per batch: _stuckActive only changes in phase 2.
        bool stuck = _stuckActive;
        StuckDetector.Verdict? parentVerdict = stuck ? _stuck.LayoutVerdict(w.Room, w.Wide, w.Stuck) : null;

        for (int s = 0; s < _symbols.Length; s++)
        {
            if (restoreNeeded)
            {
                w.Room.RestoreMobile(w.Undo, _mobile);
            }

            restoreNeeded = true;
            if (!w.Room.ApplyMove(_symbols[s]))
            {
                continue;
            }

            _reduction.ThrowIfFrozenMoved(w.Room);
            generated++;

            // IsDeadEnd, on this worker's room: a finished room bypasses it (see Solve).
            int h;
            bool finished = w.Room.IsSolved();
            if (finished)
            {
                h = 0;
            }
            else
            {
                if (!w.Room.IsSolvable() || w.Room.CannotMove())
                {
                    continue;
                }

                h = _heuristic.Estimate(w.Room);
                if (h >= RelaxedDistance.Unreachable)
                {
                    continue;
                }
            }

            w.Room.WriteStateKey(w.Key, _mobile);

            // The stuck-item rule, as the serial loop applies it: after the
            // cheap tests, on a key already written. Its per-layout verdict is a
            // pure function of the layout, so sharing the cache across workers
            // changes no answer - only who computes it first. Marked and kept
            // beside the plain estimate, not applied: phase 2 decides (probation).
            int raised = h;
            if (stuck && h > 0)
            {
                StuckDetector.Verdict verdict = w.Room.LastMoveWasFast
                    ? parentVerdict!
                    : _stuck.LayoutVerdict(w.Room, w.Key, w.Stuck);
                raised = _stuck.Evaluate(verdict, w.Room, h, _stuckDistances);
            }

            int o = baseIndex + children;
            _childDead[o] = raised < 0;
            _childFinished[o] = finished;
            _childPlain[o] = (ushort)Math.Min(h, ushort.MaxValue);
            Span<byte> key = _childKeys.AsSpan(o * keySize, keySize);
            _codec.Encode(w.Key, key);
            _childHash[o] = StateStore.Hash(key);
            _childSymbol[o] = (byte)s;
            _childGuide[o] = (ushort)Math.Clamp(raised, 0, ushort.MaxValue);
            children++;
        }

        _batchChildren[slot] = children;
        _batchGenerated[slot] = generated;
    }
}
