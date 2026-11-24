using static FishFillets.Cli.SheetWriter;

namespace FishFillets.Cli;

/// <summary>
/// Writes the results table as an Excel workbook beside results.csv: typed
/// cells (counts as numbers with thousands separators, seconds with one
/// decimal, the recorded date as a real date), a bold header row that stays
/// frozen while scrolling, an AutoFilter on every column for sorting and
/// filtering, and column widths fitted to the content.
///
/// <para>Official levels only - those on the world map. Hand-simplified copies
/// and custom rooms stay in the CSV, where the solver needs them. Nor does the
/// workbook say how a solution was found: derivedFrom, bestAuthor, method and
/// source are left out. An "our" cell is green where we hold the best known
/// solution, i.e. one at least as short as the hall of fame's, and a "bundled"
/// cell where the game's own solution already is. "bound %" is the
/// proven lower bound as a share of the shortest known solution.</para>
///
/// <para>The CSV stays the record: it is what <see cref="ResultsFile.Load"/>
/// reads back and merges into, and what diffs readably in git. This file is a
/// view of it, regenerated on every write, so editing it changes nothing.</para>
///
/// <para>Hand-written SpreadsheetML (<see cref="SheetWriter"/>) rather than a
/// library: this keeps the CLI free of dependencies and Native-AOT clean, and
/// the same table always produces the same bytes.</para>
/// </summary>
public static class ResultsWorkbook
{
    public static string PathFor(string docsDir) => Path.Combine(docsDir, "results.xlsx");

    private static readonly (string Name, Kind Kind)[] Columns =
    [
        ("name", Kind.Text), ("branch", Kind.Text),
        ("best", Kind.Integer), ("bundled", Kind.Integer), ("our", Kind.Integer),
        ("width", Kind.Integer), ("height", Kind.Integer), ("items", Kind.Integer),
        ("proven", Kind.Text), ("bound", Kind.Integer), ("bound %", Kind.Percent),
        ("expanded", Kind.Integer), ("stored", Kind.Integer), ("seconds", Kind.Decimal),
        ("status", Kind.Text), ("recorded", Kind.Date),
    ];

    /// <summary>The column holding our move count, drawn green where we hold the best.</summary>
    private static readonly int OurColumn = Array.FindIndex(Columns, c => c.Name == "our");

    /// <summary>The column holding the bundled solution's length, drawn green where it is the best.</summary>
    private static readonly int BundledColumn = Array.FindIndex(Columns, c => c.Name == "bundled");

    /// <summary>
    /// Writes the workbook atomically (temp file, then rename). Unlike the CSV
    /// this never throws on a locked file: the workbook is only a view, so a
    /// copy left open in Excel must not cost a search its recorded result.
    /// Returns a warning to print instead, or null.
    /// </summary>
    public static string? TryWrite(string path, IReadOnlyList<ResultsRow> rows) => SheetWriter.TryWrite(path, [Table(rows)]);

    /// <summary>A level of the game itself: on the world map, and not a hand-simplified copy.</summary>
    public static bool IsOfficial(ResultsRow row) => !row.IsDerived && row.Order != int.MaxValue;

    /// <summary>Whether our solution is the best known one - as short as the hall of fame's, or shorter.</summary>
    public static bool HoldsBest(ResultsRow row) =>
        row.Run is { Moves: > 0 } run && row.Best > 0 && run.Moves <= row.Best;

    /// <summary>Whether the bundled solution is already as short as the hall of fame's, or shorter.</summary>
    public static bool BundledIsBest(ResultsRow row) => row.Bundled > 0 && row.Best > 0 && row.Bundled <= row.Best;

    public static void Write(Stream output, IReadOnlyList<ResultsRow> allRows) => SheetWriter.Write(output, [Table(allRows)]);

    private static SheetWriter.Table Table(IReadOnlyList<ResultsRow> allRows)
    {
        List<ResultsRow> rows = allRows.Where(IsOfficial).ToList();
        return new SheetWriter.Table(
            "results",
            Columns,
            rows.Select(Values).ToList(),
            (r, c) => (c == OurColumn && HoldsBest(rows[r])) || (c == BundledColumn && BundledIsBest(rows[r])));
    }

    /// <summary>One row's cell values in column order; null for a blank cell.</summary>
    private static object?[] Values(ResultsRow row)
    {
        LevelResult? r = row.Run;
        return
        [
            row.Name,
            Text(row.Branch),
            Count(row.Best),
            Count(row.Bundled),
            Count(r?.Moves ?? 0),
            Count(row.Width),
            Count(row.Height),
            // Items is a real count where 0 is meaningful, as in the CSV.
            row.Items,
            r is null ? null : r.Proven ? "yes" : "no",
            r is null ? null : Count(r.Bound),
            BoundShare(row),
            r is null ? null : r.Expanded,
            r is null ? null : r.Stored,
            r is null ? null : r.Seconds,
            Text(r?.Status),
            r is null ? null : DateSerial(r.Recorded),
        ];
    }

    /// <summary>
    /// How far the proven lower bound has got towards the shortest known
    /// solution (the hall of fame's or ours, whichever is shorter): 100 % means
    /// that solution is proven optimal. Unlike the bare bound it compares across
    /// levels, so sorting by it shows which unproven levels are closest.
    /// </summary>
    private static object? BoundShare(ResultsRow row)
    {
        if (row.Run is not { Bound: > 0 } run)
        {
            return null;
        }

        int shortest = new[] { row.Best, run.Moves }.Where(m => m > 0).DefaultIfEmpty(0).Min();
        return shortest > 0 ? (double)run.Bound / shortest : null;
    }

    private static string? Text(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Zero means "not applicable" in this table, so it stays blank.</summary>
    private static object? Count(int value) => value == 0 ? null : value;
}
