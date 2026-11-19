using System.Globalization;
using System.IO.Compression;
using System.Text;

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
/// solution, i.e. one at least as short as the hall of fame's. "bound %" is the
/// proven lower bound as a share of the shortest known solution.</para>
///
/// <para>The CSV stays the record: it is what <see cref="ResultsFile.Load"/>
/// reads back and merges into, and what diffs readably in git. This file is a
/// view of it, regenerated on every write, so editing it changes nothing.</para>
///
/// <para>Hand-written SpreadsheetML in a zip rather than a library: the format
/// needed here is six small XML parts, and this keeps the CLI free of
/// dependencies and Native-AOT clean. Entry timestamps are fixed, so the same
/// table always produces the same bytes and the committed file only changes
/// when the results do.</para>
/// </summary>
public static class ResultsWorkbook
{
    public static string PathFor(string docsDir) => Path.Combine(docsDir, "results.xlsx");

    private enum Kind
    {
        Text,
        Integer,
        Decimal,
        Date,
        Percent,
    }

    // Style indices into cellXfs in styles.xml below.
    private const int StyleHeader = 1;
    private const int StyleInteger = 2;
    private const int StyleDecimal = 3;
    private const int StyleDate = 4;
    private const int StyleBest = 5;   // integer on green: "our" matches or beats the hall of fame
    private const int StylePercent = 6;

    private static readonly (string Name, Kind Kind)[] Columns =
    [
        ("name", Kind.Text), ("branch", Kind.Text),
        ("best", Kind.Integer), ("bundled", Kind.Integer), ("our", Kind.Integer),
        ("width", Kind.Integer), ("height", Kind.Integer), ("items", Kind.Integer),
        ("proven", Kind.Text), ("bound", Kind.Integer), ("bound %", Kind.Percent),
        ("expanded", Kind.Integer), ("stored", Kind.Integer), ("seconds", Kind.Decimal),
        ("status", Kind.Text), ("recorded", Kind.Date),
    ];

    /// <summary>
    /// Writes the workbook atomically (temp file, then rename). Unlike the CSV
    /// this never throws on a locked file: the workbook is only a view, so a
    /// copy left open in Excel must not cost a search its recorded result.
    /// Returns a warning to print instead, or null.
    /// </summary>
    public static string? TryWrite(string path, IReadOnlyList<ResultsRow> rows)
    {
        string temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write))
            {
                Write(stream, rows);
            }

            File.Move(temporary, path, overwrite: true);
            return null;
        }
        catch (IOException)
        {
            return $"could not update {path} - it is probably open in Excel; the new workbook is at {temporary}";
        }
        catch (UnauthorizedAccessException)
        {
            return $"could not update {path} - access denied";
        }
    }

    /// <summary>The column holding our move count, which <see cref="StyleBest"/> may colour.</summary>
    private static readonly int OurColumn = Array.FindIndex(Columns, c => c.Name == "our");

    /// <summary>A level of the game itself: on the world map, and not a hand-simplified copy.</summary>
    public static bool IsOfficial(ResultsRow row) => !row.IsDerived && row.Order != int.MaxValue;

    /// <summary>Whether our solution is the best known one - as short as the hall of fame's, or shorter.</summary>
    public static bool HoldsBest(ResultsRow row) =>
        row.Run is { Moves: > 0 } run && row.Best > 0 && run.Moves <= row.Best;

    public static void Write(Stream output, IReadOnlyList<ResultsRow> allRows)
    {
        List<ResultsRow> rows = allRows.Where(IsOfficial).ToList();
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, "[Content_Types].xml", ContentTypes);
        Add(zip, "_rels/.rels", RootRels);
        Add(zip, "xl/workbook.xml", Workbook(rows.Count));
        Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels);
        Add(zip, "xl/styles.xml", Styles);
        Add(zip, "xl/worksheets/sheet1.xml", Sheet(rows));
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using Stream stream = entry.Open();
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        stream.Write(bytes);
    }

    // ------------------------------------------------------------- sheet --

    private static string Sheet(IReadOnlyList<ResultsRow> rows)
    {
        var values = new List<object?[]>(rows.Count);
        var best = new List<bool>(rows.Count);
        foreach (ResultsRow row in rows)
        {
            values.Add(Values(row));
            best.Add(HoldsBest(row));
        }

        string lastCell = $"{ColumnName(Columns.Length - 1)}{rows.Count + 1}";
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
        xml.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
        xml.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        xml.Append($"<dimension ref=\"A1:{lastCell}\"/>");

        // Header row frozen: the pane splits below row 1.
        xml.Append("<sheetViews><sheetView tabSelected=\"1\" workbookViewId=\"0\">");
        xml.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
        xml.Append("<selection pane=\"bottomLeft\" activeCell=\"A2\" sqref=\"A2\"/>");
        xml.Append("</sheetView></sheetViews>");
        xml.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");

        xml.Append("<cols>");
        for (int c = 0; c < Columns.Length; c++)
        {
            double width = FitWidth(c, values);
            xml.Append(CultureInfo.InvariantCulture, $"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{width:0.##}\" customWidth=\"1\"/>");
        }

        xml.Append("</cols><sheetData>");

        xml.Append("<row r=\"1\">");
        for (int c = 0; c < Columns.Length; c++)
        {
            AppendText(xml, $"{ColumnName(c)}1", Columns[c].Name, StyleHeader);
        }

        xml.Append("</row>");

        for (int r = 0; r < values.Count; r++)
        {
            int rowNumber = r + 2;
            xml.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowNumber}\">");
            for (int c = 0; c < Columns.Length; c++)
            {
                object? value = values[r][c];
                if (value is null)
                {
                    continue; // blank cell - "not applicable" reads as empty, as in the CSV
                }

                string cell = $"{ColumnName(c)}{rowNumber}";
                switch (Columns[c].Kind)
                {
                    case Kind.Text:
                        AppendText(xml, cell, (string)value, 0);
                        break;
                    case Kind.Integer:
                        AppendNumber(xml, cell, Convert.ToString(value, CultureInfo.InvariantCulture)!,
                            c == OurColumn && best[r] ? StyleBest : StyleInteger);
                        break;
                    case Kind.Decimal:
                        AppendNumber(xml, cell, ((double)value).ToString("0.0##", CultureInfo.InvariantCulture), StyleDecimal);
                        break;
                    case Kind.Date:
                        AppendNumber(xml, cell, ((double)value).ToString(CultureInfo.InvariantCulture), StyleDate);
                        break;
                    case Kind.Percent:
                        AppendNumber(xml, cell, ((double)value).ToString("0.######", CultureInfo.InvariantCulture), StylePercent);
                        break;
                }
            }

            xml.Append("</row>");
        }

        xml.Append("</sheetData>");
        xml.Append($"<autoFilter ref=\"A1:{lastCell}\"/>");
        xml.Append("<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/>");
        xml.Append("</worksheet>");
        return xml.ToString();
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

    /// <summary>An ISO date as an Excel date serial (days since 1899-12-30), or null.</summary>
    private static object? DateSerial(string recorded) =>
        DateTime.TryParseExact(recorded, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
            ? (date - new DateTime(1899, 12, 30)).TotalDays
            : null;

    private static double FitWidth(int column, List<object?[]> values)
    {
        // Header text plus room for the AutoFilter button; numbers measured as
        // displayed, with their thousands separators.
        int longest = Columns[column].Name.Length + 3;
        foreach (object?[] row in values)
        {
            int length = row[column] switch
            {
                null => 0,
                string s => s.Length,
                int or long => Convert.ToInt64(row[column], CultureInfo.InvariantCulture).ToString("#,##0", CultureInfo.InvariantCulture).Length,
                double d when Columns[column].Kind == Kind.Date => 10,
                double d when Columns[column].Kind == Kind.Percent => d.ToString("0.0%", CultureInfo.InvariantCulture).Length,
                double d => d.ToString("#,##0.0", CultureInfo.InvariantCulture).Length,
                _ => 0,
            };
            longest = Math.Max(longest, length);
        }

        return Math.Min(longest + 2, 60);
    }

    private static void AppendText(StringBuilder xml, string cell, string text, int style)
    {
        xml.Append($"<c r=\"{cell}\" t=\"inlineStr\"");
        if (style != 0)
        {
            xml.Append(CultureInfo.InvariantCulture, $" s=\"{style}\"");
        }

        xml.Append("><is><t xml:space=\"preserve\">").Append(Escape(text)).Append("</t></is></c>");
    }

    private static void AppendNumber(StringBuilder xml, string cell, string number, int style) =>
        xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{cell}\" s=\"{style}\"><v>{number}</v></c>");

    private static string ColumnName(int index)
    {
        string name = "";
        for (int i = index + 1; i > 0; i = (i - 1) / 26)
        {
            name = (char)('A' + ((i - 1) % 26)) + name;
        }

        return name;
    }

    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            switch (ch)
            {
                case '&': escaped.Append("&amp;"); break;
                case '<': escaped.Append("&lt;"); break;
                case '>': escaped.Append("&gt;"); break;
                case '"': escaped.Append("&quot;"); break;
                default:
                    // XML 1.0 forbids most control characters outright.
                    if (ch >= 0x20 || ch is '\t' or '\n' or '\r')
                    {
                        escaped.Append(ch);
                    }

                    break;
            }
        }

        return escaped.ToString();
    }

    // ------------------------------------------------------- fixed parts --

    private static string Workbook(int rowCount) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<bookViews><workbookView/></bookViews>" +
        "<sheets><sheet name=\"results\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
        // Excel keeps an AutoFilter's range in this hidden name; without it the
        // filter still works, but Excel rewrites the file on first save.
        "<definedNames><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">" +
        $"results!$A$1:${ColumnName(Columns.Length - 1)}${rowCount + 1}</definedName></definedNames>" +
        "</workbook>";

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
        "</Types>";

    private const string RootRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private const string WorkbookRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    // cellXfs: 0 default, 1 header (bold, blue-grey fill), 2 integer "#,##0" (built-in 3),
    // 3 decimal "#,##0.0", 4 date "yyyy-mm-dd", 5 integer on green (Excel's "good" fill), 6 percent "0.0%". Number formats only fix the shape;
    // Excel shows the separators of the viewer's locale (a space and a comma in Czech).
    private const string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<numFmts count=\"3\"><numFmt numFmtId=\"164\" formatCode=\"#,##0.0\"/><numFmt numFmtId=\"165\" formatCode=\"yyyy\\-mm\\-dd\"/><numFmt numFmtId=\"166\" formatCode=\"0.0%\"/></numFmts>" +
        "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
        "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font></fonts>" +
        "<fills count=\"4\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9E1F2\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFC6EFCE\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"7\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
        "<xf numFmtId=\"3\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"3\" fontId=\"0\" fillId=\"3\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFill=\"1\"/>" +
        "<xf numFmtId=\"166\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";
}
