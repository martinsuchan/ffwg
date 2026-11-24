using System.Diagnostics;
using System.Text;

using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>What <see cref="SolutionSmoother.Smooth"/> did to one solution.</summary>
/// <param name="Moves">The smoothed solution: never longer than the input, and it solves the level.</param>
/// <param name="ReferenceMovesBefore">Moves of the input that a reference segment already accounts for.</param>
/// <param name="ReferenceMovesAfter">The same for the output: moves taken over from the reference.</param>
/// <param name="SharedStatesBefore">States of the input's trace that the reference's trace also passes through.</param>
/// <param name="Accepted">Reorderings accepted in step 2.</param>
public sealed record SmoothResult(
    string Moves,
    int InputLength,
    int SwitchesBefore,
    int SwitchesAfter,
    int ReferenceMovesBefore,
    int ReferenceMovesAfter,
    int SharedStatesBefore,
    int SharedStatesAfter,
    int Accepted,
    double Seconds)
{
    public bool Changed { get; init; }
}

/// <summary>
/// Reorders the moves of a solution without making it longer, so that it reads like
/// one a person played (solver/docs/031):
/// <list type="number">
/// <item>Where the solution and a reference (the game's original) pass through the
/// same two states, and the reference gets from one to the other in no more moves,
/// the reference's moves are used. Those stretches are then locked.</item>
/// <item>In the stretches left, moves are reordered greedily: a run of one fish swaps
/// with the next run of the other, or one move shifts by up to
/// <see cref="MaxShift"/> places. A change is kept only when it makes the trace pass
/// through more of the reference's states, or, with that equal, switches fish less
/// often.</item>
/// </list>
/// Both steps repeat until neither changes anything.
///
/// Sound for the same reason as the window optimiser's splice: two settled states
/// with equal <see cref="Room.WriteStateKey(Span{byte})"/> keys are interchangeable,
/// so a segment from X to Y can stand in for any other from X to Y. The keys here
/// also merge identical items that traded places (<see cref="ItemSymmetry"/>), as
/// the A* search does: the physics cannot tell such states apart either. Each change is
/// checked by replaying only the changed segment from its saved start state and
/// comparing the key at its end. No search: the work is polynomial in the length.
/// </summary>
public sealed class SolutionSmoother
{
    /// <summary>The longest segment a step-2 change rewrites.</summary>
    public const int MaxSegment = 48;

    /// <summary>How far one move may shift in a single step-2 change.</summary>
    public const int MaxShift = 8;

    /// <summary>Step 1 looks this many matched positions ahead for a reference segment.</summary>
    private const int MaxReach = 8;

    /// <summary>A safety cap on step 1 + step 2 rounds (each round strictly improves; a few are typical).</summary>
    private const int MaxRounds = 1000;

    private readonly Room _room;
    private readonly byte[] _keyBuf;

    // Classes of identical items (ItemSymmetry, docs/022), as slots of the state
    // key: their 5-byte fields are sorted within a class, so a state in which two
    // identical items traded places counts as the same state. The physics cannot
    // tell them apart (checked by SymmetrySound), so one stands in for the other.
    private readonly int[][] _classes;
    private readonly byte[] _fieldBuf;

    // The current solution and its trace: _keys[i] / _snaps[i] = the state before
    // move i (so after move i-1); _keys[n] = the final state.
    private char[] _moves = [];
    private string[] _keys = [];
    private RoomSnapshot[] _snaps = [];
    private bool[] _locked = [];

    // The reference trace: key -> every position it occurs at.
    private Dictionary<string, List<int>> _refPositions = new();
    private string _ref = "";

    public SolutionSmoother(Level level)
    {
        _room = new Room(level);
        _keyBuf = new byte[_room.StateKeySize];
        _classes = ItemSymmetry.Classes(level, level.MutableModels);
        _fieldBuf = new byte[_keyBuf.Length];
    }

    /// <summary>Fish switches: places where two consecutive moves belong to different fish.</summary>
    public static int Switches(ReadOnlySpan<char> moves)
    {
        int n = 0;
        for (int i = 1; i < moves.Length; i++)
        {
            if (char.IsUpper(moves[i]) != char.IsUpper(moves[i - 1]))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// Smooths <paramref name="moves"/>, which must solve the level. With a
    /// <paramref name="reference"/> (a solution of the same level, usually the
    /// game's original), step 1 takes over its moves where it can; without one only
    /// step 2 runs, against fish switches alone.
    /// </summary>
    public SmoothResult Smooth(string moves, string? reference = null)
    {
        var sw = Stopwatch.StartNew();
        if (!Solves(moves))
        {
            throw new ArgumentException("the solution to smooth does not solve the level", nameof(moves));
        }

        _ref = reference ?? "";
        _refPositions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        if (reference is not null)
        {
            if (!Solves(reference))
            {
                throw new ArgumentException("the reference does not solve the level", nameof(reference));
            }

            string[] refKeys = Trace(reference);
            for (int j = 0; j < refKeys.Length; j++)
            {
                if (!_refPositions.TryGetValue(refKeys[j], out List<int>? list))
                {
                    _refPositions[refKeys[j]] = list = [];
                }

                list.Add(j);
            }
        }

        Load(moves);
        int switchesBefore = Switches(_moves);
        int sharedBefore = SharedStates();
        int refBefore = reference is null ? 0 : AlignWithReference(apply: false);

        int accepted = 0;
        for (int round = 0; round < MaxRounds; round++)
        {
            bool changed = false;
            if (reference is not null)
            {
                string before = new(_moves);
                AlignWithReference(apply: true);
                changed |= before != new string(_moves);
            }

            int n = Reorder();
            accepted += n;
            if (!changed && n == 0)
            {
                break;
            }
        }

        string result = new(_moves);
        if (!Solves(result))
        {
            // Every change was checked locally, so this would be a bug, not bad input.
            throw new InvalidOperationException("the smoothed solution no longer solves the level");
        }

        return new SmoothResult(
            result,
            moves.Length,
            switchesBefore,
            Switches(_moves),
            refBefore,
            _locked.Count(l => l),
            sharedBefore,
            SharedStates(),
            accepted,
            sw.Elapsed.TotalSeconds)
        { Changed = result != moves };
    }

    // ------------------------------------------------------------ the trace --

    private bool Solves(string moves) => SolutionValidator.Validate(_room, moves) is { Solved: true };

    private string Key()
    {
        _room.WriteStateKey(_keyBuf);
        foreach (int[] members in _classes)
        {
            Canonicalize(members);
        }

        return Encoding.Latin1.GetString(_keyBuf);
    }

    /// <summary>Sorts the 5-byte key fields of one class of identical items into its slots.</summary>
    private void Canonicalize(int[] members)
    {
        const int F = 5;
        Span<byte> key = _keyBuf;
        // Insertion sort: classes are small, and usually already in order.
        for (int i = 1; i < members.Length; i++)
        {
            key.Slice(members[i] * F, F).CopyTo(_fieldBuf);
            ReadOnlySpan<byte> v = _fieldBuf.AsSpan(0, F);
            int j = i - 1;
            while (j >= 0 && key.Slice(members[j] * F, F).SequenceCompareTo(v) > 0)
            {
                key.Slice(members[j] * F, F).CopyTo(key.Slice(members[j + 1] * F, F));
                j--;
            }

            v.CopyTo(key.Slice(members[j + 1] * F, F));
        }
    }

    private string[] Trace(string moves)
    {
        var keys = new string[moves.Length + 1];
        _room.Reset();
        keys[0] = Key();
        for (int i = 0; i < moves.Length; i++)
        {
            if (!_room.ApplyMove(moves[i]))
            {
                throw new InvalidOperationException($"move {i} ('{moves[i]}') is not possible");
            }

            keys[i + 1] = Key();
        }

        return keys;
    }

    private void Load(string moves)
    {
        _moves = moves.ToCharArray();
        _keys = new string[moves.Length + 1];
        _snaps = new RoomSnapshot[moves.Length + 1];
        _locked = new bool[moves.Length];
        _room.Reset();
        _keys[0] = Key();
        _snaps[0] = _room.Capture();
        for (int i = 0; i < moves.Length; i++)
        {
            if (!_room.ApplyMove(moves[i]))
            {
                throw new InvalidOperationException($"move {i} ('{moves[i]}') is not possible");
            }

            _keys[i + 1] = Key();
            _snaps[i + 1] = _room.Capture();
        }
    }

    private bool IsShared(string key) => _refPositions.ContainsKey(key);

    private int SharedStates()
    {
        int n = 0;
        foreach (string k in _keys)
        {
            if (IsShared(k))
            {
                n++;
            }
        }

        return n;
    }

    // --------------------------------------------- step 1: the reference --

    private readonly record struct Cost(int Length, int Reference, int Changed)
    {
        /// <summary>Shorter first, then more reference moves, then fewer moves changed.</summary>
        public bool BetterThan(Cost o) =>
            Length != o.Length ? Length < o.Length
            : Reference != o.Reference ? Reference > o.Reference
            : Changed < o.Changed;
    }

    /// <summary>
    /// DP over the positions of the current solution: the best way to reach each one
    /// is either the solution's own move from the position before, or a reference
    /// segment from an earlier position whose state the reference also passes
    /// through, no longer than the stretch it replaces. With
    /// <paramref name="apply"/>, rewrites the solution along the best path and locks
    /// the reference segments; returns the number of reference moves on it.
    /// </summary>
    private int AlignWithReference(bool apply)
    {
        int n = _moves.Length;
        var best = new Cost?[n + 1];
        var from = new int[n + 1];       // previous position on the best path
        var refFrom = new int[n + 1];    // reference start, or -1 for the solution's own move
        var refTo = new int[n + 1];
        best[0] = new Cost(0, 0, 0);

        // Positions that the reference passes through, in order: only these can
        // start or end a reference segment.
        var matched = new List<int>();
        for (int i = 0; i <= n; i++)
        {
            if (_refPositions.ContainsKey(_keys[i]))
            {
                matched.Add(i);
            }
        }

        for (int i = 0; i <= n; i++)
        {
            if (best[i] is not Cost c)
            {
                continue;
            }

            if (i < n)
            {
                Relax(i + 1, new Cost(c.Length + 1, c.Reference, c.Changed), i, -1, -1);
            }

            if (!_refPositions.TryGetValue(_keys[i], out List<int>? starts))
            {
                continue;
            }

            // Only the next few matched positions: a longer reference segment is the
            // same as a chain of shorter ones through the matches in between, unless
            // a state repeats - a handful covers that and keeps this O(n).
            int k0 = matched.BinarySearch(i);
            for (int k = k0 + 1; k < matched.Count && k <= k0 + MaxReach; k++)
            {
                int i2 = matched[k];
                foreach (int j1 in starts)
                {
                    foreach (int j2 in _refPositions[_keys[i2]])
                    {
                        int len = j2 - j1;
                        if (len <= 0 || len > i2 - i)
                        {
                            continue;
                        }

                        int changed = len < i2 - i ? len : Hamming(i, j1, len);
                        Relax(i2, new Cost(c.Length + len, c.Reference + len, c.Changed + changed), i, j1, j2);
                    }
                }
            }
        }

        if (best[n] is not Cost final)
        {
            throw new InvalidOperationException("no path through the solution's own positions");
        }

        if (!apply)
        {
            return final.Reference;
        }

        // Walk the best path back and rebuild the solution front to back.
        var pieces = new List<(string Moves, bool Locked)>();
        for (int i = n; i > 0; i = from[i])
        {
            pieces.Add(refFrom[i] < 0
                ? (_moves[from[i]].ToString(), false)
                : (_ref[refFrom[i]..refTo[i]], true));
        }

        pieces.Reverse();
        var sb = new StringBuilder(n);
        var locked = new List<bool>(n);
        foreach ((string m, bool l) in pieces)
        {
            sb.Append(m);
            for (int t = 0; t < m.Length; t++)
            {
                locked.Add(l);
            }
        }

        string rebuilt = sb.ToString();
        if (rebuilt != new string(_moves))
        {
            Load(rebuilt);
        }

        _locked = locked.ToArray();
        return final.Reference;

        void Relax(int to, Cost cost, int prev, int j1, int j2)
        {
            if (best[to] is Cost old && !cost.BetterThan(old))
            {
                return;
            }

            best[to] = cost;
            from[to] = prev;
            refFrom[to] = j1;
            refTo[to] = j2;
        }

        int Hamming(int i, int j, int len)
        {
            int d = 0;
            for (int t = 0; t < len; t++)
            {
                if (_moves[i + t] != _ref[j + t])
                {
                    d++;
                }
            }

            return d;
        }
    }

    // ------------------------------------------------- step 2: reordering --

    /// <summary>
    /// Greedy passes over the unlocked stretches until one changes nothing; returns
    /// the number of changes kept.
    /// </summary>
    private int Reorder()
    {
        int accepted = 0;
        char[] seg = new char[MaxSegment];
        bool any;
        do
        {
            any = false;
            int p = 0;
            while (p < _moves.Length)
            {
                if (_locked[p])
                {
                    p++;
                    continue;
                }

                int spanEnd = p;
                while (spanEnd < _moves.Length && !_locked[spanEnd])
                {
                    spanEnd++;
                }

                if (TryAt(p, spanEnd, seg))
                {
                    accepted++;
                    any = true;
                    // A segment starting a little earlier may improve now: look again
                    // from as far back as a segment ending here could start.
                    p = Math.Max(0, p - MaxSegment);
                    continue;
                }

                p++;
            }
        }
        while (any);

        return accepted;
    }

    /// <summary>Tries the step-2 changes whose segment starts at <paramref name="a"/>, in a fixed order; keeps the first that improves.</summary>
    private bool TryAt(int a, int spanEnd, char[] seg)
    {
        // 1. Swap the run starting at a (or its tail, at a span's start) with the
        //    next run, of the other fish.
        {
            int r1 = RunEnd(a, spanEnd);
            if (r1 < spanEnd)
            {
                int r2 = RunEnd(r1, spanEnd);
                int b = r2;
                if (b - a <= MaxSegment)
                {
                    int len = 0;
                    for (int t = r1; t < r2; t++)
                    {
                        seg[len++] = _moves[t];
                    }

                    for (int t = a; t < r1; t++)
                    {
                        seg[len++] = _moves[t];
                    }

                    if (TryReplace(a, b, seg.AsSpan(0, len)))
                    {
                        return true;
                    }
                }
            }
        }

        // 2. Shift one move by 1..MaxShift places: the move at a+k forward to a,
        //    or the move at a back to a+k. k = 1 is a plain swap of two moves.
        for (int k = 1; k <= MaxShift && a + k < spanEnd; k++)
        {
            int b = a + k + 1;
            {
                seg[0] = _moves[a + k];
                for (int t = 0; t < k; t++)
                {
                    seg[t + 1] = _moves[a + t];
                }

                if (TryReplace(a, b, seg.AsSpan(0, k + 1)))
                {
                    return true;
                }

                if (k > 1)
                {
                    for (int t = 0; t < k; t++)
                    {
                        seg[t] = _moves[a + 1 + t];
                    }

                    seg[k] = _moves[a];
                    if (TryReplace(a, b, seg.AsSpan(0, k + 1)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private int RunEnd(int start, int spanEnd)
    {
        bool upper = char.IsUpper(_moves[start]);
        int e = start + 1;
        while (e < spanEnd && char.IsUpper(_moves[e]) == upper)
        {
            e++;
        }

        return e;
    }

    private readonly string[] _segKeys = new string[MaxSegment + 1];

    /// <summary>
    /// Replaces moves [a, b) with <paramref name="seg"/> (same length) if every move
    /// is possible from the saved state at a, the state at b comes out the same, and
    /// the objective improves. Updates the trace on success.
    /// </summary>
    private bool TryReplace(int a, int b, ReadOnlySpan<char> seg)
    {
        if (seg.SequenceEqual(_moves.AsSpan(a, b - a)))
        {
            return false;
        }

        // Objective delta first (cheap on fish switches; shared states need the replay).
        int lo = Math.Max(0, a - 1), hi = Math.Min(_moves.Length, b + 1);
        int oldSwitches = Switches(_moves.AsSpan(lo, hi - lo));
        Span<char> probe = stackalloc char[hi - lo];
        _moves.AsSpan(lo, hi - lo).CopyTo(probe);
        seg.CopyTo(probe[(a - lo)..]);
        int newSwitches = Switches(probe);

        bool useShared = _refPositions.Count > 0;
        if (!useShared && newSwitches >= oldSwitches)
        {
            return false;
        }

        _room.RestoreFrom(_snaps[a]);
        int len = seg.Length;
        for (int t = 0; t < len; t++)
        {
            if (!_room.ApplyMove(seg[t]))
            {
                return false;
            }

            _segKeys[t + 1] = Key();
        }

        if (_segKeys[len] != _keys[b])
        {
            return false;
        }

        if (useShared)
        {
            int oldShared = 0, newShared = 0;
            for (int t = 1; t < len; t++)
            {
                if (IsShared(_keys[a + t]))
                {
                    oldShared++;
                }

                if (IsShared(_segKeys[t]))
                {
                    newShared++;
                }
            }

            if (newShared < oldShared || (newShared == oldShared && newSwitches >= oldSwitches))
            {
                return false;
            }
        }

        // Accept: rewrite the moves and the trace inside [a, b]; past b nothing changed.
        seg.CopyTo(_moves.AsSpan(a));
        _room.RestoreFrom(_snaps[a]);
        for (int t = 0; t < len - 1; t++)
        {
            _room.ApplyMove(seg[t]);
            _keys[a + t + 1] = _segKeys[t + 1];
            _room.CaptureTo(_snaps[a + t + 1]);
        }

        return true;
    }
}
