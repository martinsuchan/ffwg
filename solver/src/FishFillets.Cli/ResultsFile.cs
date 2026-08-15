using System.Text.Json;
using System.Text.Json.Serialization;

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

public sealed class ResultsDocument
{
    public string Updated { get; set; } = "";

    public Dictionary<string, LevelResult> Levels { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ResultsDocument))]
internal partial class ResultsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The running record of what the solver has achieved, kept at
/// solver/docs/results.json and updated by the solver itself rather than by hand,
/// so it cannot drift from reality the way a table in a prose document does.
///
/// Merging keeps the best of each dimension independently: the shortest solution,
/// and separately the highest lower bound. A run that fails to solve a level
/// still contributes - knowing that 90 s of search proved f=57 against a
/// 66-move optimum is exactly the measurement that says whether the level needs
/// more budget or a better heuristic.
/// </summary>
internal static class ResultsFile
{
    public static string PathFor(string docsDir) => Path.Combine(docsDir, "results.json");

    public static ResultsDocument Load(string docsDir)
    {
        string path = PathFor(docsDir);
        if (!File.Exists(path))
        {
            return new ResultsDocument();
        }

        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, ResultsJsonContext.Default.ResultsDocument)
               ?? new ResultsDocument();
    }

    /// <summary>
    /// Read-modify-write under an exclusive lock, then replace the file
    /// atomically.
    ///
    /// More than one solver process can be recording at once - a batch run and a
    /// separate improve, say - and each does load, merge, save. Without this they
    /// race and the last writer silently drops the other's work. The lock file is
    /// separate from results.json so the atomic rename cannot pull the ground out
    /// from under a waiting process.
    /// </summary>
    public static void Update(string docsDir, Action<ResultsDocument> mutate)
    {
        Directory.CreateDirectory(docsDir);
        string lockPath = Path.Combine(docsDir, ".results.lock");

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var guard = new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                ResultsDocument document = Load(docsDir);
                mutate(document);
                Save(docsDir, document);
                return;
            }
            catch (IOException) when (attempt < 100)
            {
                Thread.Sleep(50); // another process is mid-write
            }
        }
    }

    public static void Save(string docsDir, ResultsDocument document)
    {
        document.Updated = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        Directory.CreateDirectory(docsDir);

        // Stable ordering: the file is committed, so churn matters.
        document.Levels = document.Levels
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        // Write beside the real file and rename over it, so a reader (or a
        // crash) never sees a half-written record.
        string temporary = PathFor(docsDir) + ".tmp";
        using (FileStream stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, document, ResultsJsonContext.Default.ResultsDocument);
        }

        File.Move(temporary, PathFor(docsDir), overwrite: true);
    }

    /// <summary>Folds one run into the record, keeping whatever was better.</summary>
    public static void Merge(ResultsDocument document, string level, LevelResult run)
    {
        if (!document.Levels.TryGetValue(level, out LevelResult? existing))
        {
            document.Levels[level] = run;
            return;
        }

        // A proven-optimal result always wins; otherwise the shorter solution does.
        bool better = run.Moves > 0
                      && (existing.Moves == 0
                          || (run.Proven && !existing.Proven)
                          || (run.Proven == existing.Proven && run.Moves < existing.Moves));

        if (better)
        {
            int keptBound = Math.Max(existing.Bound, run.Bound);
            document.Levels[level] = run;
            document.Levels[level].Bound = keptBound;
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
                existing.Recorded = run.Recorded;
            }
        }
    }
}
