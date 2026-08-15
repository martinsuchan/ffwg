using System.Globalization;
using System.Text;

using FishFillets.Physics;

namespace FishFillets.Cli;

/// <summary>What the solver has managed on one level.</summary>
public sealed class LevelResult
{
    /// <summary>Shortest solution found so far, or 0 if none.</summary>
    public int Moves { get; set; }

    /// <summary>Whether that length was proven shortest (plain A* at weight 1).</summary>
    public bool Proven { get; set; }

    /// <summary>"astar" | "macro" | "window" - how it was found.</summary>
    public string Method { get; set; } = "";

    /// <summary>
    /// The level the search actually ran on, when that is not this level: a
    /// hand-simplified room (docs/006) whose answer was replayed against the real
    /// level and passed. Empty for a result found on the level itself.
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// Best lower bound reached, in moves. Meaningful even when unsolved: it is
    /// how far the search proved no solution can be shorter than, so the gap to
    /// the record shows whether a level is short of budget or short of heuristic.
    /// </summary>
    public int Bound { get; set; }

    public long Expanded { get; set; }

    public long Stored { get; set; }

    public double Seconds { get; set; }

    public string Status { get; set; } = "";

    public string Recorded { get; set; } = "";
}

/// <summary>
/// One row of the results table: the facts about a level, and what the solver has
/// managed on it.
///
/// Only <see cref="Run"/> is recorded data. Everything else is derived from the
/// corpus on every write, so the table cannot drift from the levels or the hall
/// of fame the way a hand-maintained one would.
/// </summary>
public sealed class ResultsRow
{
    public string Name { get; set; } = "";

    /// <summary>
    /// The real level this row was hand-simplified from (docs/006), if any. Such a
    /// row borrows its source's hall-of-fame and bundled numbers, because those
    /// are what its result is worth comparing against - this column is what says
    /// the numbers are borrowed.
    /// </summary>
    public string DerivedFrom { get; set; } = "";

    public string Branch { get; set; } = "";

    /// <summary>Hall-of-fame move count (worldfame.lua), 0 if the level has none.</summary>
    public int Best { get; set; }

    public string BestAuthor { get; set; } = "";

    /// <summary>Length of the solution bundled in legacy/solution, 0 if there is none.</summary>
    public int Bundled { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>Pushable models: everything but the fish and the fixed scenery.</summary>
    public int Items { get; set; }

    public LevelResult? Run { get; set; }

    /// <summary>Sort key: play order, with a simplified level following its source.</summary>
    public int Order { get; set; }

    public bool IsDerived { get; set; }
}

/// <summary>
/// The running record of what the solver has achieved, kept at
/// solver/docs/results.csv and updated by the solver itself rather than by hand,
/// so it cannot drift from reality the way a table in a prose document does.
///
/// CSV rather than JSON because the interesting use is comparison - our length
/// against the hall of fame, level size against how far the bound got - and that
/// wants a spreadsheet, not an object graph. It is written in the order the game
/// is played, grouped by branch.
///
/// Merging keeps the best of each dimension independently: the shortest solution,
/// and separately the highest lower bound. A run that fails to solve a level
/// still contributes - knowing that 90 s of search proved f=57 against a
/// 66-move optimum is exactly the measurement that says whether the level needs
/// more budget or a better heuristic.
/// </summary>
public static class ResultsFile
{
    private static readonly string[] Header =
    [
        "name", "derivedFrom", "branch", "best", "bestAuthor", "bundled", "our", "width", "height", "items",
        "proven", "method", "source", "bound", "expanded", "stored", "seconds", "status", "recorded",
    ];

    public static string PathFor(string docsDir) => Path.Combine(docsDir, "results.csv");

    /// <summary>
    /// The separators a results file may use. Anything else Windows might report
    /// as a list separator would need quoting rules of its own, so it falls back
    /// to a comma.
    /// </summary>
    private const string KnownSeparators = ",;\t";

    /// <summary>
    /// What the local Windows culture expects a CSV to be separated by - a
    /// semicolon in the Czech, German and French locales, a comma in the English
    /// ones. This is the same setting Excel reads a CSV with, which is the whole
    /// reason to care: a comma-separated file opens as one column on a machine
    /// configured for semicolons.
    /// </summary>
    public static char CultureSeparator()
    {
        string separator = WindowsLocale.ListSeparator();
        return separator.Length == 1 && KnownSeparators.Contains(separator[0]) ? separator[0] : ',';
    }

    /// <summary>
    /// The separator an existing file uses, read from its header line, or null if
    /// there is no file yet.
    ///
    /// Detected rather than assumed so that a file written on one machine still
    /// parses on another - the record is committed to the repository, and a
    /// contributor in a different locale must not read it as a single column
    /// either.
    /// </summary>
    public static char? DetectSeparator(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string? header = File.ReadLines(path).FirstOrDefault();
        if (header is null)
        {
            return null;
        }

        foreach (char candidate in KnownSeparators)
        {
            if (header.Contains(candidate))
            {
                return candidate;
            }
        }

        return ',';
    }

    /// <summary>
    /// How the one decimal column is written, which has to agree with the
    /// separator: a Czech machine writes 105,2, and that is only safe once the
    /// fields themselves are separated by something else. Pairing the two is what
    /// makes the file open as numbers rather than text in the local Excel; a
    /// decimal that would collide with the separator falls back to a point.
    /// </summary>
    private static NumberFormatInfo NumberFormat(char separator)
    {
        string point = WindowsLocale.DecimalSeparator();
        if (point.Length != 1 || point[0] == separator || point[0] == '.')
        {
            return CultureInfo.InvariantCulture.NumberFormat;
        }

        var format = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        format.NumberDecimalSeparator = point;
        return format;
    }

    /// <summary>
    /// Reads back the recorded runs. The derived columns are deliberately ignored
    /// - they are rebuilt from the corpus on write, so editing them in a
    /// spreadsheet changes nothing and cannot corrupt the record.
    /// </summary>
    public static Dictionary<string, LevelResult> Load(string docsDir)
    {
        var runs = new Dictionary<string, LevelResult>(StringComparer.Ordinal);
        string path = PathFor(docsDir);
        if (!File.Exists(path))
        {
            return runs;
        }

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        char separator = DetectSeparator(path) ?? ',';
        bool first = true;

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            List<string> fields = SplitCsv(line, separator);
            if (first)
            {
                first = false;
                for (int i = 0; i < fields.Count; i++)
                {
                    index[fields[i]] = i;
                }

                continue;
            }

            string Field(string name) =>
                index.TryGetValue(name, out int i) && i < fields.Count ? fields[i] : "";

            string name = Field("name");
            if (name.Length == 0)
            {
                continue;
            }

            // A row with no move count and no bound is a level we have never
            // searched - carried in the file for completeness, not a result.
            int moves = ParseInt(Field("our"));
            int bound = ParseInt(Field("bound"));
            if (moves == 0 && bound == 0 && Field("status").Length == 0)
            {
                continue;
            }

            runs[name] = new LevelResult
            {
                Moves = moves,
                Proven = Field("proven") is "yes" or "true" or "1",
                Method = Field("method"),
                Source = Field("source"),
                Bound = bound,
                Expanded = ParseLong(Field("expanded")),
                Stored = ParseLong(Field("stored")),
                Seconds = ParseDouble(Field("seconds")),
                Status = Field("status"),
                Recorded = Field("recorded"),
            };
        }

        return runs;
    }

    /// <summary>
    /// Read-modify-write under an exclusive lock, then replace the file
    /// atomically.
    ///
    /// More than one solver process can be recording at once - a batch run and a
    /// separate improve, say - and each does load, merge, save. Without this they
    /// race and the last writer silently drops the other's work. The lock file is
    /// separate from the results so the atomic rename cannot pull the ground out
    /// from under a waiting process.
    /// </summary>
    public static void Update(
        Corpus corpus, Action<Dictionary<string, LevelResult>> mutate, char? separator = null)
    {
        Directory.CreateDirectory(corpus.DocsDir);
        string lockPath = Path.Combine(corpus.DocsDir, ".results.lock");

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var guard = new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                Dictionary<string, LevelResult> runs = Load(corpus.DocsDir);
                mutate(runs);
                Save(corpus, runs, separator);
                return;
            }
            catch (IOException) when (attempt < 100)
            {
                Thread.Sleep(50); // another process is mid-write
            }
        }
    }

    /// <summary>
    /// Writes the table. The separator sticks: an existing file keeps whatever it
    /// already uses, so a run on a semicolon machine never reflows a comma file
    /// (and vice versa) and the committed record does not churn. Only a brand-new
    /// file follows the local culture, and <paramref name="separator"/> - which
    /// `results --rebuild --separator` passes - overrides both.
    /// </summary>
    public static void Save(Corpus corpus, Dictionary<string, LevelResult> runs, char? separator = null)
    {
        string path = PathFor(corpus.DocsDir);
        Directory.CreateDirectory(corpus.DocsDir);

        char sep = separator ?? DetectSeparator(path) ?? CultureSeparator();

        var text = new StringBuilder();
        text.Append(string.Join(sep, Header)).Append('\n');
        foreach (ResultsRow row in Rows(corpus, runs))
        {
            WriteRow(text, row, sep);
        }

        // Keep the previous table beside the new one. The whole point of this
        // file is that it accumulates weeks of search, and a single bad write
        // would otherwise be unrecoverable.
        if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }

        // Write beside the real file and rename over it, so a reader (or a
        // crash) never sees a half-written record. With a BOM, because half the
        // hall of fame has accented names and Excel reads a BOM-less UTF-8 CSV
        // as the local codepage.
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException ex)
        {
            // Overwhelmingly this means the CSV is open in Excel, which holds an
            // exclusive handle. Say so, and leave the new table on disk rather
            // than throwing the run away.
            throw new IOException(
                $"could not write {path} - it is probably open in another program (Excel locks CSVs " +
                $"while they are open). The new table has been left at {temporary}.", ex);
        }
    }

    /// <summary>
    /// Builds the full table: every level we have, whether or not it has been
    /// searched, in play order with a simplified level directly after the level
    /// it came from.
    /// </summary>
    public static List<ResultsRow> Rows(Corpus corpus, Dictionary<string, LevelResult> runs)
    {
        var names = new HashSet<string>(runs.Keys, StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(corpus.LevelsDir, "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name != "index")
            {
                names.Add(name);
            }
        }

        var rows = new List<ResultsRow>(names.Count);
        foreach (string name in names)
        {
            LevelFacts facts = LevelFacts.For(corpus, name);
            string basis = facts.SourceLevel ?? name;
            corpus.HallOfFame.TryGetValue(basis, out (int Steps, string Author) fame);
            corpus.WorldMap.TryGetValue(basis, out LevelPlace place);

            rows.Add(new ResultsRow
            {
                Name = name,
                DerivedFrom = facts.SourceLevel ?? "",
                // A level absent from the map leaves LevelPlace at its default,
                // whose Branch is a null string rather than an empty one.
                Branch = place.Branch ?? "",
                Best = fame.Steps,
                // As with Branch below: a level with no hall-of-fame entry leaves
                // the tuple at its default, whose Author is null, not empty.
                BestAuthor = fame.Author ?? "",
                Bundled = corpus.TryReadSolution(basis, out string bundled) ? bundled.Length : 0,
                Width = facts.Width,
                Height = facts.Height,
                Items = facts.Items,
                Run = runs.GetValueOrDefault(name),
                // Levels missing from the map (a hand-made room, say) sort last
                // rather than first, where they would push the real game aside.
                Order = corpus.WorldMap.ContainsKey(basis) ? place.Order : int.MaxValue,
                IsDerived = facts.SourceLevel is not null,
            });
        }

        rows.Sort((a, b) =>
        {
            int c = a.Order.CompareTo(b.Order);
            if (c != 0) return c;
            c = a.IsDerived.CompareTo(b.IsDerived);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });

        return rows;
    }

    /// <summary>Folds one run into the record, keeping whatever was better.</summary>
    public static void Merge(Dictionary<string, LevelResult> runs, string level, LevelResult run)
    {
        if (!runs.TryGetValue(level, out LevelResult? existing))
        {
            runs[level] = run;
            return;
        }

        // The shorter solution always wins; at equal length, the proven one does.
        //
        // Length outranks the proof deliberately. Every recorded solution has been
        // replayed to Solved, so a shorter one is real - and a "proof" at a
        // greater length can only mean the two runs were not about the same room
        // (a simplified copy, say). Keeping the shorter answer is then both the
        // useful choice and the honest one.
        bool better = run.Moves > 0
                      && (existing.Moves == 0
                          || run.Moves < existing.Moves
                          || (run.Moves == existing.Moves && run.Proven && !existing.Proven));

        if (better)
        {
            int keptBound = Math.Max(existing.Bound, run.Bound);
            runs[level] = run;
            runs[level].Bound = keptBound;
        }
        else if (run.Bound > existing.Bound)
        {
            existing.Bound = run.Bound;
            if (existing.Moves == 0)
            {
                // Still unsolved - keep the most informative failed attempt.
                existing.Expanded = run.Expanded;
                existing.Stored = run.Stored;
                existing.Seconds = run.Seconds;
                existing.Status = run.Status;
                existing.Method = run.Method;
                existing.Source = run.Source;
                existing.Recorded = run.Recorded;
            }
        }
    }

    // --------------------------------------------------------------------- csv --

    private static void WriteRow(StringBuilder text, ResultsRow row, char separator)
    {
        // Counts are plain integers, so they are written invariantly whatever the
        // locale - a thousands separator here would be noise in a file whose
        // numbers are meant to be compared, and a hazard in one whose fields may
        // be comma-separated. Only the elapsed seconds is a decimal.
        LevelResult? r = row.Run;
        NumberFormatInfo numbers = NumberFormat(separator);
        string[] fields =
        [
            row.Name,
            row.DerivedFrom,
            row.Branch,
            Number(row.Best),
            row.BestAuthor,
            Number(row.Bundled),
            Number(r?.Moves ?? 0),
            Number(row.Width),
            Number(row.Height),
            row.Items.ToString(CultureInfo.InvariantCulture),
            r is null ? "" : r.Proven ? "yes" : "no",
            r?.Method ?? "",
            r?.Source ?? "",
            r is null ? "" : Number(r.Bound),
            r is null ? "" : r.Expanded.ToString(CultureInfo.InvariantCulture),
            r is null ? "" : r.Stored.ToString(CultureInfo.InvariantCulture),
            r is null ? "" : r.Seconds.ToString("F1", numbers),
            r?.Status ?? "",
            r?.Recorded ?? "",
        ];

        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                text.Append(separator);
            }

            text.Append(Escape(fields[i], separator));
        }

        text.Append('\n');
    }

    /// <summary>Zero means "not applicable" everywhere in this table, so it reads as blank.</summary>
    private static string Number(int value) => value == 0 ? "" : value.ToString(CultureInfo.InvariantCulture);

    private static string Escape(string? field, char separator) =>
        field is null ? ""
            : field.IndexOf(separator) < 0 && field.AsSpan().IndexOfAny("\"\n\r") < 0
                ? field
                : $"\"{field.Replace("\"", "\"\"")}\"";

    private static List<string> SplitCsv(string line, char separator)
    {
        var fields = new List<string>(Header.Length);
        var current = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c != '"')
                {
                    current.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static int ParseInt(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static long ParseLong(string text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;

    /// <summary>
    /// Reads a decimal written by any locale. The field has already been split
    /// off, so a decimal comma cannot be confused with a separator here and
    /// swapping it for a point is unambiguous - which is what lets a semicolon
    /// file written in Prague be read on a machine set to English.
    /// </summary>
    private static double ParseDouble(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return value;
        }

        return double.TryParse(
            text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0;
    }
}

/// <summary>
/// The facts a level's own JSON carries, cached for the process: reading all 81
/// files on every save would otherwise cost more than the write it describes.
/// </summary>
public sealed record LevelFacts(int Width, int Height, int Items, string? SourceLevel)
{
    private static readonly Dictionary<string, LevelFacts> Cache = new(StringComparer.Ordinal);

    public static LevelFacts For(Corpus corpus, string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out LevelFacts? cached))
            {
                return cached;
            }

            LevelFacts facts;
            try
            {
                LevelJson json = LevelLoader.LoadFile(corpus.LevelPath(name));

                // "Items" is what a solver has to think about: the fish are not
                // items, and fixed scenery (including the room shape itself, which
                // is just another item_fixed model) can never be pushed anywhere.
                int items = json.Models.Count(m =>
                    !m.Kind.StartsWith("fish_", StringComparison.Ordinal) && m.Kind != "item_fixed");

                facts = new LevelFacts(json.Width, json.Height, items, json.SourceLevel);
            }
            catch (Exception)
            {
                // A recorded level whose JSON has since been removed or renamed
                // still deserves its row - the run happened.
                facts = new LevelFacts(0, 0, 0, null);
            }

            Cache[name] = facts;
            return facts;
        }
    }
}
