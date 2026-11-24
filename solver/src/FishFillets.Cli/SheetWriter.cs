using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace FishFillets.Cli;

/// <summary>
/// A minimal .xlsx writer for the solver's tables (results.xlsx,
/// window-results.xlsx): typed cells, a frozen bold header, an AutoFilter, and
/// the same bytes for the same table, so a committed copy only changes when its
/// data does. One worksheet per <see cref="Table"/>, in order.
/// </summary>
internal static class SheetWriter
{
    public enum Kind
    {
        Text,
        Integer,
        Decimal,
        Date,
        Percent,
    }

    // Style indices into cellXfs in styles.xml below.
    public const int StyleText = 0;
    public const int StyleHeader = 1;
    public const int StyleInteger = 2;
    public const int StyleDecimal = 3;
    public const int StyleDate = 4;
    public const int StyleGreen = 5;   // integer on green (Excel's "good" fill)
    public const int StylePercent = 6;

    /// <summary>
    /// One worksheet. <paramref name="Green"/> picks the integer cells drawn on
    /// green, by row and column index; null for none.
    /// </summary>
    public sealed record Table(string Name, (string Name, Kind Kind)[] Columns, IReadOnlyList<object?[]> Values, Func<int, int, bool>? Green = null);

    /// <summary>Writes atomically (temp file, then rename); a warning if the file is locked (open in Excel), else null.</summary>
    public static string? TryWrite(string path, IReadOnlyList<Table> tables)
    {
        string temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write))
            {
                Write(stream, tables);
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

    public static void Write(Stream output, IReadOnlyList<Table> tables)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, "[Content_Types].xml", ContentTypes(tables.Count));
        Add(zip, "_rels/.rels", RootRels);
        Add(zip, "xl/workbook.xml", Workbook(tables));
        Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(tables.Count));
        Add(zip, "xl/styles.xml", Styles);
        for (int t = 0; t < tables.Count; t++)
        {
            Add(zip, $"xl/worksheets/sheet{t + 1}.xml", Sheet(tables[t], selected: t == 0));
        }
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

    private static string Sheet(Table table, bool selected)
    {
        (string Name, Kind Kind)[] columns = table.Columns;
        IReadOnlyList<object?[]> values = table.Values;
        string lastCell = $"{ColumnName(columns.Length - 1)}{values.Count + 1}";
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
        xml.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
        xml.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        xml.Append($"<dimension ref=\"A1:{lastCell}\"/>");

        // Header row frozen: the pane splits below row 1.
        xml.Append(selected ? "<sheetViews><sheetView tabSelected=\"1\" workbookViewId=\"0\">" : "<sheetViews><sheetView workbookViewId=\"0\">");
        xml.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
        xml.Append("<selection pane=\"bottomLeft\" activeCell=\"A2\" sqref=\"A2\"/>");
        xml.Append("</sheetView></sheetViews>");
        xml.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");

        xml.Append("<cols>");
        for (int c = 0; c < columns.Length; c++)
        {
            double width = FitWidth(columns[c], c, values);
            xml.Append(CultureInfo.InvariantCulture, $"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{width:0.##}\" customWidth=\"1\"/>");
        }

        xml.Append("</cols><sheetData>");

        xml.Append("<row r=\"1\">");
        for (int c = 0; c < columns.Length; c++)
        {
            AppendText(xml, $"{ColumnName(c)}1", columns[c].Name, StyleHeader);
        }

        xml.Append("</row>");

        for (int r = 0; r < values.Count; r++)
        {
            int rowNumber = r + 2;
            xml.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowNumber}\">");
            for (int c = 0; c < columns.Length; c++)
            {
                object? value = values[r][c];
                if (value is null)
                {
                    continue; // blank cell - "not applicable" reads as empty, as in the CSV
                }

                string cell = $"{ColumnName(c)}{rowNumber}";
                switch (columns[c].Kind)
                {
                    case Kind.Text:
                        AppendText(xml, cell, (string)value, StyleText);
                        break;
                    case Kind.Integer:
                        AppendNumber(xml, cell, Convert.ToString(value, CultureInfo.InvariantCulture)!,
                            table.Green?.Invoke(r, c) == true ? StyleGreen : StyleInteger);
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

    /// <summary>An ISO date as an Excel date serial (days since 1899-12-30), or null.</summary>
    public static object? DateSerial(string? recorded) =>
        DateTime.TryParseExact(recorded, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
            ? (date - new DateTime(1899, 12, 30)).TotalDays
            : null;

    private static double FitWidth((string Name, Kind Kind) column, int index, IReadOnlyList<object?[]> values)
    {
        // Header text plus room for the AutoFilter button; numbers measured as
        // displayed, with their thousands separators.
        int longest = column.Name.Length + 3;
        foreach (object?[] row in values)
        {
            int length = row[index] switch
            {
                null => 0,
                string s => s.Length,
                int or long => Convert.ToInt64(row[index], CultureInfo.InvariantCulture).ToString("#,##0", CultureInfo.InvariantCulture).Length,
                double when column.Kind == Kind.Date => 10,
                double d when column.Kind == Kind.Percent => d.ToString("0.0%", CultureInfo.InvariantCulture).Length,
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

    public static string ColumnName(int index)
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

    private static string Workbook(IReadOnlyList<Table> tables)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
        xml.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
        xml.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        xml.Append("<bookViews><workbookView/></bookViews><sheets>");
        for (int t = 0; t < tables.Count; t++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<sheet name=\"{Escape(tables[t].Name)}\" sheetId=\"{t + 1}\" r:id=\"rId{t + 1}\"/>");
        }

        // Excel keeps an AutoFilter's range in this hidden name; without it the
        // filter still works, but Excel rewrites the file on first save.
        xml.Append("</sheets><definedNames>");
        for (int t = 0; t < tables.Count; t++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{t}\" hidden=\"1\">");
            xml.Append(CultureInfo.InvariantCulture, $"{tables[t].Name}!$A$1:${ColumnName(tables[t].Columns.Length - 1)}${tables[t].Values.Count + 1}</definedName>");
        }

        xml.Append("</definedNames></workbook>");
        return xml.ToString();
    }

    private static string ContentTypes(int sheets)
    {
        var xml = new StringBuilder(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        for (int t = 1; t <= sheets; t++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/xl/worksheets/sheet{t}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }

        xml.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        xml.Append("</Types>");
        return xml.ToString();
    }

    private const string RootRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private static string WorkbookRels(int sheets)
    {
        var xml = new StringBuilder(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (int t = 1; t <= sheets; t++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{t}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{t}.xml\"/>");
        }

        xml.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{sheets + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        xml.Append("</Relationships>");
        return xml.ToString();
    }

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
