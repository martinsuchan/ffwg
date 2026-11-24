using System.Globalization;
using System.Text;

using FishFillets.Physics;

using static FishFillets.Cli.SheetWriter;

namespace FishFillets.Cli;

/// <summary>
/// One round of the window optimiser on one level: every window of one size,
/// stride and bound. This is a row of solver/docs/window-results.csv.
/// </summary>
/// <param name="Covered">
/// The longest stretch of moves the round proved has no shortcut anywhere in the
/// solution: window - stride + 1. Set only when every window was searched and
/// none stopped at a limit; null otherwise, or when an old run did not log
/// its limits.
/// </param>
public sealed record WindowRound(
    string Level, int Window, int Stride, string Heuristic,
    int From, int To,
    int Windows, int RuledOut, int Shortcuts, int? Limited, int NotSearched, int? Covered,
    long? NodeLimit, int? WindowSeconds, double? WindowMemoryGb,
    long Nodes, double Seconds, string Note, string Recorded)
{
    public (string, int, int, string) Key => (Level, Window, Stride, Heuristic);
}

/// <summary>
/// solver/docs/window-results.csv and its workbook: what the window optimiser
/// has done on each level, kept apart from results.csv, which records solutions
/// and A* bounds. The CSV is the record and the xlsx is a view of it, as for
/// results. The workbook has a "levels" sheet, how far each level has got, and
/// a "rounds" sheet, every round.
/// </summary>
public static class WindowResults
{
    public static string CsvPath(string docsDir) => Path.Combine(docsDir, "window-results.csv");

    public static string XlsxPath(string docsDir) => Path.Combine(docsDir, "window-results.xlsx");

    private const string Header =
        "level;window;stride;heuristic;from;to;windows;ruledOut;shortcuts;limited;notSearched;covered;" +
        "nodeLimit;windowSeconds;windowMemoryGb;nodes;seconds;note;recorded";

    public static List<WindowRound> Load(string docsDir)
    {
        string path = CsvPath(docsDir);
        var rows = new List<WindowRound>();
        if (!File.Exists(path))
        {
            return rows;
        }

        foreach (string line in File.ReadLines(path).Skip(1))
        {
            // A note written by hand may hold a ';': everything between the
            // fixed columns and the date is the note.
            string[] f = line.Split(';');
            if (f.Length < 19)
            {
                continue;
            }

            if (f.Length > 19)
            {
                f = [.. f[..17], string.Join(",", f[17..^1]), f[^1]];
            }

            rows.Add(new WindowRound(
                f[0], Int(f[1]), Int(f[2]), f[3], Int(f[4]), Int(f[5]),
                Int(f[6]), Int(f[7]), Int(f[8]), NInt(f[9]), Int(f[10]), NInt(f[11]),
                NLong(f[12]), NInt(f[13]), NDouble(f[14]),
                long.Parse(f[15], CultureInfo.InvariantCulture), double.Parse(f[16], CultureInfo.InvariantCulture),
                f[17], f[18]));
        }

        return rows;
    }

    /// <summary>A level's hall-of-fame length and bundled length, 0 if unknown; the levels sheet compares against them.</summary>
    public delegate (int Best, int Bundled) LevelFacts(string level);

    public static LevelFacts FactsOf(Corpus corpus) => level =>
        (corpus.HallOfFame.TryGetValue(level, out var f) ? f.Steps : 0,
         corpus.TryReadSolution(level, out string moves) ? moves.Length : 0);

    /// <summary>Adds or replaces the rounds (by level, window, stride and bound), then rewrites the CSV and the workbook.</summary>
    public static string? Record(Corpus corpus, IEnumerable<WindowRound> rounds) => Record(corpus.DocsDir, FactsOf(corpus), rounds);

    public static string? Record(string docsDir, LevelFacts facts, IEnumerable<WindowRound> rounds)
    {
        List<WindowRound> rows = Load(docsDir);
        foreach (WindowRound round in rounds)
        {
            rows.RemoveAll(r => r.Key == round.Key);
            rows.Add(round);
        }

        return Save(docsDir, facts, rows);
    }

    /// <summary>Rewrites the workbook from the CSV (after the hall of fame or a solution changed); null if there is no CSV.</summary>
    public static string? Rebuild(Corpus corpus) =>
        File.Exists(CsvPath(corpus.DocsDir)) ? Save(corpus.DocsDir, FactsOf(corpus), Load(corpus.DocsDir)) : null;

    public static string? Save(string docsDir, LevelFacts facts, List<WindowRound> rows)
    {
        rows.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Level, b.Level);
            if (c == 0) c = a.Window.CompareTo(b.Window);
            if (c == 0) c = a.Stride.CompareTo(b.Stride);
            return c != 0 ? c : string.CompareOrdinal(a.Heuristic, b.Heuristic);
        });

        var csv = new StringBuilder();
        csv.Append(Header).Append('\n');
        foreach (WindowRound r in rows)
        {
            csv.Append(string.Join(';',
                r.Level, I(r.Window), I(r.Stride), r.Heuristic, I(r.From), I(r.To),
                I(r.Windows), I(r.RuledOut), I(r.Shortcuts), N(r.Limited), I(r.NotSearched), N(r.Covered),
                N(r.NodeLimit), N(r.WindowSeconds), r.WindowMemoryGb?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
                r.Nodes.ToString(CultureInfo.InvariantCulture), r.Seconds.ToString("0.0", CultureInfo.InvariantCulture),
                r.Note.Replace(';', ','), r.Recorded)).Append('\n');
        }

        // With a BOM, as results.csv, so Excel opens it as UTF-8.
        File.WriteAllText(CsvPath(docsDir), csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return SheetWriter.TryWrite(XlsxPath(docsDir), [LevelsTable(facts, rows), RoundsTable(rows)]);
    }

    // ----------------------------------------------------------- workbook --

    private static readonly (string Name, Kind Kind)[] LevelColumns =
    [
        ("level", Kind.Text), ("best", Kind.Integer), ("bundled", Kind.Integer), ("now", Kind.Integer),
        ("covered", Kind.Integer), ("complete up to", Kind.Integer), ("largest window", Kind.Integer), ("stride", Kind.Integer),
        ("limited there", Kind.Integer), ("bound", Kind.Text), ("rounds", Kind.Integer),
        ("nodes", Kind.Integer), ("seconds", Kind.Decimal), ("recorded", Kind.Date),
    ];

    /// <summary>
    /// One row per level: how short it now is (the best round, or the solution
    /// in legacy/solution if shorter); "covered", the longest stretch proven to
    /// hold no shortcut; "complete up to", the largest window whose round
    /// searched every window (an old run's node limit may still have cut some);
    /// and the largest window tried, with how many of its windows stopped at a limit.
    /// </summary>
    private static Table LevelsTable(LevelFacts facts, List<WindowRound> rows)
    {
        var levels = rows.GroupBy(r => r.Level, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var values = new List<object?[]>();
        var green = new List<bool>();
        foreach (var g in levels)
        {
            (int best, int bundled) = facts(g.Key);
            int now = bundled > 0 ? Math.Min(bundled, g.Min(r => r.To)) : g.Min(r => r.To);
            int? complete = g.Where(r => r.NotSearched == 0).Select(r => (int?)r.Window).DefaultIfEmpty(null).Max();
            WindowRound largest = g.OrderByDescending(r => r.Window).ThenByDescending(r => r.Recorded, StringComparer.Ordinal).First();
            int? covered = g.Where(r => r.Covered is not null).Select(r => r.Covered).DefaultIfEmpty(null).Max();
            values.Add(
            [
                g.Key, Nz(best), Nz(bundled), now, covered, complete, largest.Window, largest.Stride, largest.Limited,
                largest.Heuristic, g.Count(), g.Sum(r => r.Nodes), Math.Round(g.Sum(r => r.Seconds), 1),
                DateSerial(g.Max(r => r.Recorded)),
            ]);
            green.Add(best > 0 && now <= best);
        }

        int nowColumn = Array.FindIndex(LevelColumns, c => c.Name == "now");
        return new Table("levels", LevelColumns, values, (r, c) => c == nowColumn && green[r]);
    }

    private static readonly (string Name, Kind Kind)[] RoundColumns =
    [
        ("level", Kind.Text), ("window", Kind.Integer), ("stride", Kind.Integer), ("bound", Kind.Text),
        ("from", Kind.Integer), ("to", Kind.Integer), ("windows", Kind.Integer), ("ruled out", Kind.Integer),
        ("shortcuts", Kind.Integer), ("limited", Kind.Integer), ("not searched", Kind.Integer), ("covered", Kind.Integer),
        ("node limit", Kind.Integer), ("window s", Kind.Integer), ("window GB", Kind.Decimal),
        ("nodes", Kind.Integer), ("seconds", Kind.Decimal), ("note", Kind.Text), ("recorded", Kind.Date),
    ];

    private static Table RoundsTable(List<WindowRound> rows)
    {
        var values = rows.Select(r => new object?[]
        {
            r.Level, r.Window, r.Stride, r.Heuristic, r.From, r.To, r.Windows, r.RuledOut, r.Shortcuts, r.Limited,
            r.NotSearched, r.Covered, r.NodeLimit, r.WindowSeconds, r.WindowMemoryGb, r.Nodes, r.Seconds,
            r.Note.Length == 0 ? null : r.Note, DateSerial(r.Recorded),
        }).ToList();

        // Green: the round shortened the solution.
        int toColumn = Array.FindIndex(RoundColumns, c => c.Name == "to");
        return new Table("rounds", RoundColumns, values, (r, c) => c == toColumn && rows[r].To < rows[r].From);
    }

    // -------------------------------------------------------------- text --

    private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    private static int? NInt(string s) => s.Length == 0 ? null : int.Parse(s, CultureInfo.InvariantCulture);

    private static long? NLong(string s) => s.Length == 0 ? null : long.Parse(s, CultureInfo.InvariantCulture);

    private static double? NDouble(string s) => s.Length == 0 ? null : double.Parse(s, CultureInfo.InvariantCulture);

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string N(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string N(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static object? Nz(int v) => v == 0 ? null : v;
}
