using System.Globalization;

using FishFillets.Search;

namespace FishFillets.Cli;

/// <summary>
/// One line of an <c>improve --state</c> file: a window searched, and the limits
/// it was searched under. A window is known by its id (the two rooms and its
/// length), so it stays known when a splice elsewhere moves it along the
/// solution, and a resumed run skips it.
/// </summary>
internal sealed record KnownWindow(
    string Id, string Outcome, int Bound, int Length, long Nodes, long NodeLimit, int WindowSeconds, long WindowBytes, string Heuristic)
{
    public const string Header = "id;outcome;bound;length;nodes;nodeLimit;windowSeconds;windowBytes;heuristic";

    /// <summary>
    /// Whether nothing is left to find in this window under the current limits:
    /// it was ruled out, or it stopped at a limit no smaller than the current one.
    /// A bigger limit searches it again, and so does a different bound: one that
    /// prunes more may get further on the same budget.
    /// </summary>
    public bool Covers(long nodeLimit, int windowSeconds, long windowBytes, string heuristic) => Outcome switch
    {
        "none" => true,
        _ when Heuristic != heuristic => false,
        "node-limit" => NodeLimit >= nodeLimit,
        "memory-limit" => WindowBytes >= windowBytes,
        "window-time" => windowSeconds > 0 && WindowSeconds >= windowSeconds,
        _ => false,
    };

    public static string Name(WindowOutcome outcome) => outcome switch
    {
        WindowOutcome.Shortcut => "shortcut",
        WindowOutcome.NoShortcut => "none",
        WindowOutcome.NodeLimit => "node-limit",
        WindowOutcome.MemoryLimit => "memory-limit",
        WindowOutcome.WindowTimeLimit => "window-time",
        _ => "stopped",
    };

    // No limit is written as -1.
    public string ToLine() => string.Join(';',
        Id, Outcome, Bound.ToString(CultureInfo.InvariantCulture), Length.ToString(CultureInfo.InvariantCulture),
        Nodes.ToString(CultureInfo.InvariantCulture),
        (NodeLimit == long.MaxValue ? -1 : NodeLimit).ToString(CultureInfo.InvariantCulture),
        (WindowSeconds <= 0 ? -1 : WindowSeconds).ToString(CultureInfo.InvariantCulture),
        (WindowBytes == long.MaxValue ? -1 : WindowBytes).ToString(CultureInfo.InvariantCulture),
        Heuristic);

    public static KnownWindow? Parse(string line)
    {
        string[] f = line.Split(';');
        // The first state files had no heuristic column: they were all Manhattan.
        if (f.Length is not (8 or 9)
            || !int.TryParse(f[2], CultureInfo.InvariantCulture, out int bound)
            || !int.TryParse(f[3], CultureInfo.InvariantCulture, out int length)
            || !long.TryParse(f[4], CultureInfo.InvariantCulture, out long nodes)
            || !long.TryParse(f[5], CultureInfo.InvariantCulture, out long nodeLimit)
            || !int.TryParse(f[6], CultureInfo.InvariantCulture, out int seconds)
            || !long.TryParse(f[7], CultureInfo.InvariantCulture, out long bytes))
        {
            return null;
        }

        return new KnownWindow(
            f[0], f[1], bound, length, nodes,
            nodeLimit < 0 ? long.MaxValue : nodeLimit,
            seconds < 0 ? 0 : seconds,
            bytes < 0 ? long.MaxValue : bytes,
            f.Length == 9 ? f[8] : "manhattan");
    }
}
