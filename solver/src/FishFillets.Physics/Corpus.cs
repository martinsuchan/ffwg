namespace FishFillets.Physics;

/// <summary>
/// Locates and reads the level corpus: the exported levels in
/// <c>solver/levels</c> and the game's own recorded solutions in
/// <c>legacy/solution</c>.
///
/// File-system concerns rather than physics, but they live in this assembly
/// because both the CLI and the test project need them, and
/// <see cref="LevelLoader"/> already reads levels off disk here.
/// </summary>
public sealed class Corpus
{
    private readonly Lazy<IReadOnlyList<string>> _levelNames;

    private Corpus(string levelsDir, string solutionsDir, string docsDir)
    {
        LevelsDir = levelsDir;
        SolutionsDir = solutionsDir;
        DocsDir = docsDir;
        _levelNames = new Lazy<IReadOnlyList<string>>(() => LevelLoader.LoadIndex(LevelsDir));
        _hallOfFame = new Lazy<IReadOnlyDictionary<string, (int, string)>>(LoadHallOfFame);
    }

    /// <summary>Where the solver's own reference data lives (the hall of fame, results).</summary>
    public string DocsDir { get; }

    public string LevelsDir { get; }

    public string SolutionsDir { get; }

    /// <summary>Level codenames from the export's index.json, in export order.</summary>
    public IReadOnlyList<string> LevelNames => _levelNames.Value;

    /// <summary>
    /// Finds the corpus by walking up from the running assembly (and the working
    /// directory) for an ancestor holding both directories - the CLI and the
    /// tests both run from wherever the build put them, not from the repo root.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">
    /// If neither an override nor a repository root resolves.
    /// </exception>
    public static Corpus Resolve(string? levelsDir = null, string? solutionsDir = null)
    {
        string? root = (levelsDir is null || solutionsDir is null) ? FindRepoRoot() : null;

        levelsDir ??= root is null ? null : Path.Combine(root, "solver", "levels");
        solutionsDir ??= root is null ? null : Path.Combine(root, "legacy", "solution");

        if (levelsDir is null || solutionsDir is null)
        {
            throw new DirectoryNotFoundException(
                "could not locate the repository root (looked for a directory holding both " +
                "solver/levels and legacy/solution). Run scripts\\export-levels.ps1, or pass " +
                "the directories explicitly.");
        }

        return new Corpus(levelsDir, solutionsDir, Path.Combine(Path.GetDirectoryName(levelsDir) ?? ".", "docs"));
    }

    public bool HasLevel(string name) => File.Exists(LevelPath(name));

    public string LevelPath(string name) => Path.Combine(LevelsDir, name + ".json");

    public Level LoadLevel(string name) => Level.Build(LevelLoader.LoadFile(LevelPath(name)));

    /// <summary>
    /// Reads a reference solution out of <c>legacy/solution/&lt;name&gt;.lua</c>.
    /// The whole file is one assignment:
    /// <code>saved_moves = 'uuuuuulllDDDRRR...'</code>
    /// which is the same flat symbol string the browser port records and replays
    /// (docs/021), so no Lua interpreter is involved.
    /// </summary>
    public bool TryReadSolution(string name, out string moves)
    {
        moves = "";
        string path = Path.Combine(SolutionsDir, name + ".lua");
        if (!File.Exists(path))
        {
            return false;
        }

        string text = File.ReadAllText(path);
        int assignment = text.IndexOf("saved_moves", StringComparison.Ordinal);
        if (assignment < 0)
        {
            return false;
        }

        int open = text.IndexOf('\'', assignment);
        if (open < 0)
        {
            return false;
        }

        int close = text.IndexOf('\'', open + 1);
        if (close < 0)
        {
            return false;
        }

        moves = text[(open + 1)..close];
        return true;
    }

    /// <summary>
    /// The hall of fame: the best move count known for each level, and who set
    /// it. Parsed from solver/docs/worldfame.lua, which is the game's own format:
    /// <code>node_bestSolution("start", 54, "Pavel Danihelka")</code>
    /// These are the numbers the solver is measured against - the bundled
    /// solutions in legacy/solution are by various authors and are not always the
    /// best known (12 of 80 are longer - see docs/003).
    /// </summary>
    public IReadOnlyDictionary<string, (int Steps, string Author)> HallOfFame => _hallOfFame.Value;

    private readonly Lazy<IReadOnlyDictionary<string, (int, string)>> _hallOfFame;

    private IReadOnlyDictionary<string, (int, string)> LoadHallOfFame()
    {
        var best = new Dictionary<string, (int, string)>(StringComparer.Ordinal);
        string path = Path.Combine(DocsDir, "worldfame.lua");
        if (!File.Exists(path))
        {
            return best;
        }

        foreach (string line in File.ReadLines(path))
        {
            int open = line.IndexOf("node_bestSolution(", StringComparison.Ordinal);
            if (open < 0)
            {
                continue;
            }

            string[] parts = line[(open + 18)..].Split(',');
            if (parts.Length < 3 || !int.TryParse(parts[1].Trim(), out int steps))
            {
                continue;
            }

            best[parts[0].Trim().Trim('"')] = (steps, parts[2].Trim().TrimEnd(')').Trim('"'));
        }

        return best;
    }

    /// <summary>Levels that have both exported geometry and a recorded solution.</summary>
    public IEnumerable<string> LevelsWithSolutions()
    {
        foreach (string name in LevelNames)
        {
            if (HasLevel(name) && TryReadSolution(name, out _))
            {
                yield return name;
            }
        }
    }

    private static string? FindRepoRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "solver", "levels"))
                    && Directory.Exists(Path.Combine(dir.FullName, "legacy", "solution")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }

        return null;
    }
}
