using System.Runtime.CompilerServices;
using System.Text;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>
/// A hand-drawn micro-level for the regression suite: a few-dozen-cell room in
/// ASCII, with a header saying what it is for and what its optimum is.
///
/// <code>
/// # expect: 4                  optimal length, or "none" for unsolvable
/// # tags:   goal_out, facing
/// # why:    free text - any key that is not one of the below is a comment
/// # detour: lllul              a valid but longer solution, for the window optimiser
/// # known-bug: admissible=C3   checks this level is known to fail, with the bug's id
/// # s: fish_small &gt; goal_alive  legend: char, kind, facing (&lt; or &gt;), goal
/// # A: item_light goal_out
/// XXXXXXXXXX
/// X........X
/// X.sssA....
/// XXXXXXXXXX
/// </code>
///
/// <para>'X' is the room's wall (one item_fixed model, index 0), '.' is water,
/// and every legend character is one model whose shape is all the cells drawn
/// with it. Models are numbered wall first, then in legend order. Fish default to
/// goal_escape and everything else to no goal; facing defaults to left.</para>
///
/// <para>The <c>expect</c> value is written by hand and checked against the
/// independent oracle (<see cref="ReferenceBfs"/>) - never against a solver under
/// test.</para>
///
/// <para><c>known-bug</c> entries name a check (<c>admissible</c>,
/// <c>matrix:astar-stuck</c>, or a prefix like <c>matrix</c>) and the id of the
/// finding that breaks it. The check then skips this level in the normal run and
/// runs it instead under <c>[TestCategory("KnownBug")]</c>, where it is expected
/// to fail until the fix lands - at which point the line is deleted and the
/// level rejoins the normal run.</para>
/// </summary>
internal sealed class MicroLevel
{
    public required string Name { get; init; }

    /// <summary>The optimal length, or null when the level has no solution at all.</summary>
    public required int? Expect { get; init; }

    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>check -&gt; finding id.</summary>
    public required IReadOnlyDictionary<string, string> KnownBugs { get; init; }

    public string? Detour { get; init; }

    public required LevelJson Json { get; init; }

    public Level Build() => Level.Build(Json);

    /// <summary>The finding that breaks <paramref name="check"/> on this level, or null.</summary>
    public string? KnownBug(string check)
    {
        foreach ((string key, string id) in KnownBugs)
        {
            if (check == key || check.StartsWith(key + ":", StringComparison.Ordinal))
            {
                return id;
            }
        }

        return null;
    }

    public override string ToString() => Name;

    public static MicroLevel Parse(string name, string text)
    {
        var legend = new List<(char Key, string Kind, bool IsLeft, string Goal)>();
        var tags = new List<string>();
        var bugs = new Dictionary<string, string>();
        int? expect = null;
        bool expectSeen = false;
        string? detour = null;
        var map = new List<string>();
        bool mapEnded = false;

        foreach (string raw in text.Replace("\r\n", "\n").TrimStart('﻿').Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith('#'))
            {
                if (map.Count > 0)
                {
                    throw Error(name, "header line after the map");
                }

                string body = line[1..].Trim();
                int colon = body.IndexOf(':');
                if (colon <= 0)
                {
                    continue; // plain comment
                }

                string key = body[..colon].Trim();
                string value = body[(colon + 1)..].Trim();

                if (key.Length == 1)
                {
                    legend.Add(ParseLegend(name, key[0], value));
                    continue;
                }

                switch (key)
                {
                    case "expect":
                        expectSeen = true;
                        expect = value == "none" ? null : int.Parse(value);
                        break;
                    case "tags":
                        tags.AddRange(value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                        break;
                    case "detour":
                        detour = value;
                        break;
                    case "known-bug":
                        foreach (string entry in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        {
                            string[] parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
                            if (parts.Length != 2)
                            {
                                throw Error(name, $"known-bug entry must be check=ID: '{entry}'");
                            }

                            bugs[parts[0]] = parts[1];
                        }

                        break;
                    default:
                        break; // any other key is a comment (why, note, ...)
                }

                continue;
            }

            if (line.Length == 0)
            {
                mapEnded |= map.Count > 0;
                continue;
            }

            if (mapEnded)
            {
                throw Error(name, "blank line inside the map");
            }

            map.Add(line);
        }

        if (!expectSeen)
        {
            throw Error(name, "missing '# expect:' (a number, or 'none')");
        }

        if (map.Count == 0)
        {
            throw Error(name, "no map");
        }

        int width = map.Max(l => l.Length);
        int height = map.Count;

        var cells = new Dictionary<char, List<(int X, int Y)>>();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < map[y].Length; x++)
            {
                char c = map[y][x];
                if (c == '.')
                {
                    continue;
                }

                if (c != 'X' && !legend.Any(l => l.Key == c))
                {
                    throw Error(name, $"map character '{c}' at ({x},{y}) is not in the legend");
                }

                if (!cells.TryGetValue(c, out List<(int, int)>? list))
                {
                    cells[c] = list = [];
                }

                list.Add((x, y));
            }
        }

        var models = new List<ModelJson>();
        if (cells.TryGetValue('X', out List<(int X, int Y)>? walls))
        {
            models.Add(Model("item_fixed", walls, isLeft: true, goal: "goal_no"));
        }

        foreach ((char key, string kind, bool isLeft, string goal) in legend)
        {
            if (!cells.TryGetValue(key, out List<(int X, int Y)>? modelCells))
            {
                throw Error(name, $"legend character '{key}' does not appear in the map");
            }

            models.Add(Model(kind, modelCells, isLeft, goal));
        }

        return new MicroLevel
        {
            Name = name,
            Expect = expect,
            Tags = tags,
            KnownBugs = bugs,
            Detour = detour,
            Json = new LevelJson { Name = name, Width = width, Height = height, Models = models },
        };
    }

    private static (char, string, bool, string) ParseLegend(string name, char key, string value)
    {
        if (key is 'X' or '.')
        {
            throw Error(name, $"'{key}' is reserved and cannot be a legend key");
        }

        string[] tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            throw Error(name, $"legend '{key}' has no kind");
        }

        string kind = tokens[0];
        bool isFish = kind.StartsWith("fish_", StringComparison.Ordinal);
        bool isLeft = true;
        string goal = isFish ? "goal_escape" : "goal_no";

        foreach (string token in tokens.Skip(1))
        {
            switch (token)
            {
                case "<": isLeft = true; break;
                case ">": isLeft = false; break;
                case "goal_escape" or "goal_out" or "goal_alive" or "goal_no": goal = token; break;
                default: throw Error(name, $"legend '{key}': unknown token '{token}'");
            }
        }

        return (key, kind, isLeft, goal);
    }

    private static ModelJson Model(string kind, List<(int X, int Y)> cells, bool isLeft, string goal)
    {
        int minX = cells.Min(c => c.X), minY = cells.Min(c => c.Y);
        int maxX = cells.Max(c => c.X), maxY = cells.Max(c => c.Y);
        var occupied = cells.ToHashSet();

        var shape = new StringBuilder();
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                shape.Append(occupied.Contains((x, y)) ? 'X' : '.');
            }

            shape.Append('\n');
        }

        return new ModelJson
        {
            Kind = kind,
            X = minX,
            Y = minY,
            Shape = shape.ToString(),
            Goal = goal,
            IsLeft = isLeft,
        };
    }

    private static InvalidDataException Error(string name, string message) => new($"{name}.lvl: {message}");
}

/// <summary>The micro-level set, loaded once from the source tree.</summary>
internal static class MicroCorpus
{
    private static readonly Lazy<IReadOnlyList<MicroLevel>> Lazy = new(Load, isThreadSafe: true);

    /// <summary>The test sources' Micro directory - levels and the baseline live beside the code that reads them.</summary>
    public static string SourceDir { get; } = ThisDir();

    public static IReadOnlyList<MicroLevel> All => Lazy.Value;

    public static MicroLevel Get(string name) => All.Single(l => l.Name == name);

    /// <summary>
    /// DynamicData rows for one check: the levels that run it normally, or
    /// (<paramref name="knownBug"/>) the ones it is known to fail on. A sentinel
    /// row stands in for an empty set, since a data-driven test with no data is an
    /// error rather than a pass.
    /// </summary>
    public static IEnumerable<object[]> Rows(string check, bool knownBug, Func<MicroLevel, bool>? applies = null)
    {
        var rows = All
            .Where(l => applies is null || applies(l))
            .Where(l => (l.KnownBug(check) is not null) == knownBug)
            .Select(l => new object[] { l.Name })
            .ToList();

        return rows.Count > 0 ? rows : [[None]];
    }

    public const string None = "(none)";

    private static IReadOnlyList<MicroLevel> Load() =>
        Directory.EnumerateFiles(Path.Combine(SourceDir, "Levels"), "*.lvl")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => MicroLevel.Parse(Path.GetFileNameWithoutExtension(p), File.ReadAllText(p)))
            .ToList();

    private static string ThisDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
