namespace FishFillets.Cli;

/// <summary>
/// <c>improve --grow-per-start</c> (solver/docs/030): with <c>--grow</c>, a window
/// that hits a per-window limit stops only its own start, not the whole run.
/// <list type="bullet">
/// <item>The next sizes skip every window that starts in that room. A window of
///   the same start and a greater length would need still more, so it would hit
///   the limit too.</item>
/// <item>The other starts go on growing.</item>
/// </list>
/// A start is known by its room, the first part of the window id
/// (<c>startKey|endKey|length</c>), so it stays blocked when a splice before it
/// moves it along the solution. Thread-safe: the workers report concurrently.
/// </summary>
public sealed class GrowPolicy
{
    private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blockedAtSize = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>The room a window starts in: the first part of its id.</summary>
    public static string StartOf(string id)
    {
        int bar = id.IndexOf('|', StringComparison.Ordinal);
        return bar < 0 ? id : id[..bar];
    }

    /// <summary>Starts blocked so far.</summary>
    public int Blocked
    {
        get
        {
            lock (_gate)
            {
                return _blocked.Count;
            }
        }
    }

    /// <summary>Windows of the current size skipped because their start is blocked.</summary>
    public int BlockedAtSize
    {
        get
        {
            lock (_gate)
            {
                return _blockedAtSize.Count;
            }
        }
    }

    /// <summary>A window from this start hit a per-window limit: the start grows no further.</summary>
    public void Limited(string id)
    {
        lock (_gate)
        {
            _blocked.Add(StartOf(id));
        }
    }

    /// <summary>
    /// Whether to skip this window because its start is blocked. A skipped window
    /// is counted for <see cref="BlockedAtSize"/>.
    /// </summary>
    public bool Skips(string id)
    {
        lock (_gate)
        {
            if (!_blocked.Contains(StartOf(id)))
            {
                return false;
            }

            _blockedAtSize.Add(id);
            return true;
        }
    }

    /// <summary>A new size begins: its skipped windows are counted afresh.</summary>
    public void NextSize()
    {
        lock (_gate)
        {
            _blockedAtSize.Clear();
        }
    }

    /// <summary>
    /// The windows a round of <paramref name="size"/> lists over a solution of
    /// <paramref name="length"/> moves, on <paramref name="stride"/>: the full-length
    /// ones, plus the last one off the stride (as <c>WindowOptimizer</c> lists them).
    /// </summary>
    public static int WindowCount(int length, int size, int stride)
    {
        if (length < 2)
        {
            return 0;
        }

        int last = Math.Max(0, length - size);
        return (last / stride) + 1 + (last % stride != 0 ? 1 : 0);
    }
}
