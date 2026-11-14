using System.Text;

using FishFillets.Search;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>A small deterministic PRNG - System.Random's seeded sequence is not a contract across runtime versions.</summary>
internal sealed class XorShift
{
    private ulong _state;

    public XorShift(ulong seed) => _state = (seed * 0x9E3779B97F4A7C15UL) | 1;

    public ulong NextULong()
    {
        _state ^= _state << 13;
        _state ^= _state >> 7;
        _state ^= _state << 17;
        return _state;
    }

    /// <summary>Uniform in [min, max).</summary>
    public int Next(int min, int max) => min + (int)(NextULong() % (ulong)(max - min));

    public bool Chance(double p) => (NextULong() % 1_000_000) < p * 1_000_000;
}

/// <summary>A random room in editable form: a wall grid plus placed models.</summary>
internal sealed class FuzzRoom
{
    public sealed record Placed(char Key, string Kind, List<(int X, int Y)> Cells, bool IsLeft, string Goal);

    public required bool[,] Walls { get; init; }   // [y, x]

    public required List<Placed> Models { get; init; }

    public int Width => Walls.GetLength(1);

    public int Height => Walls.GetLength(0);

    public FuzzRoom With(bool[,]? walls = null, List<Placed>? models = null) =>
        new() { Walls = walls ?? Walls, Models = models ?? Models };

    /// <summary>The same room as a .lvl file, so a fuzz finding can be pasted straight into Levels/.</summary>
    public string ToLvl(string? header = null)
    {
        var grid = new char[Height, Width];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                grid[y, x] = Walls[y, x] ? 'X' : '.';
            }
        }

        foreach (Placed m in Models)
        {
            foreach ((int x, int y) in m.Cells)
            {
                grid[y, x] = m.Key;
            }
        }

        var text = new StringBuilder(header);
        foreach (Placed m in Models)
        {
            text.Append($"# {m.Key}: {m.Kind}{(m.IsLeft ? "" : " >")}{(m.Goal == DefaultGoal(m.Kind) ? "" : " " + m.Goal)}\n");
        }

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                text.Append(grid[y, x]);
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    public LevelJson ToJson(string name) => MicroLevel.Parse(name, ToLvl("# expect: none\n")).Json;

    private static string DefaultGoal(string kind) => kind.StartsWith("fish_", StringComparison.Ordinal) ? "goal_escape" : "goal_no";
}

/// <summary>
/// Differential fuzzing: random small rooms, every check of
/// <see cref="MicroChecks"/> against the oracle, and every violation either
/// attributed to an open finding (C1-C7) or reported as new.
///
/// <para>Attribution must be by mechanism, not by guess: when the review's
/// C1-C7 were open, a heuristic violation counted as C3 only if it disappeared
/// with item facing ignored, a stuck one as C7 only if the condemned fish never
/// had to leave, and so on (solver/docs/017). All are fixed now (docs/018), so
/// only a variant-wide <see cref="MicroVariant.KnownBug"/> and PO-PAIRWISE
/// (docs/020) still attribute; anything else is new.</para>
/// </summary>
internal static class MicroFuzzer
{
    internal sealed record Finding(string Check, string? Cause, string Message);

    internal sealed record Case(int Index, FuzzRoom Room, int? Optimum, int States, List<Finding> Findings);

    private static readonly (int X, int Y)[][] ItemShapes =
    [
        [(0, 0)],
        [(0, 0), (1, 0)],
        [(0, 0), (0, 1)],
        [(0, 0), (1, 0), (0, 1), (1, 1)],
        [(0, 0), (1, 0), (2, 0)],
    ];

    private static readonly (int X, int Y)[] SmallFish = [(0, 0), (1, 0), (2, 0)];

    private static readonly (int X, int Y)[] BigFish = [(0, 0), (1, 0), (2, 0), (3, 0), (0, 1), (1, 1), (2, 1), (3, 1)];

    /// <param name="identical">
    /// Give every item without a goal the same kind and shape - rooms built to
    /// exercise merging identical items (docs/022). Off, the rooms are exactly
    /// those of earlier runs, so recorded seeds still reproduce.
    /// </param>
    public static FuzzRoom? Generate(ulong seed, int index, bool identical = false)
    {
        var r = new XorShift((seed * 1_000_003UL) + (ulong)index);
        int w = r.Next(8, 14), h = r.Next(5, 10);
        var walls = new bool[h, w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                walls[y, x] = y == 0 || x == 0 || y == h - 1 || x == w - 1;
            }
        }

        int openings = r.Next(1, 3);
        for (int o = 0; o < openings; o++)
        {
            int side = r.Next(0, 4);
            if (side < 2)
            {
                int y0 = r.Next(1, h - 1), len = r.Next(1, 3);
                for (int k = 0; k < len && y0 + k < h - 1; k++)
                {
                    walls[y0 + k, side == 0 ? 0 : w - 1] = false;
                }
            }
            else
            {
                int x0 = r.Next(1, w - 1), len = r.Next(1, 4);
                for (int k = 0; k < len && x0 + k < w - 1; k++)
                {
                    walls[side == 2 ? 0 : h - 1, x0 + k] = false;
                }
            }
        }

        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                walls[y, x] = r.Chance(0.08);
            }
        }

        int ledges = r.Next(0, 3);
        for (int l = 0; l < ledges; l++)
        {
            int y = r.Next(2, h - 1), x0 = r.Next(1, w - 1), len = r.Next(2, 5);
            for (int k = 0; k < len && x0 + k < w - 1; k++)
            {
                walls[y, x0 + k] = true;
            }
        }

        bool alive = r.Chance(0.25);
        string fishGoal = alive ? "goal_alive" : "goal_escape";
        var occupied = new bool[h, w];
        var models = new List<FuzzRoom.Placed>();

        bool Place(char key, string kind, (int X, int Y)[] shape, bool isLeft, string goal)
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                int ax = r.Next(1, w - 1), ay = r.Next(1, h - 1);
                var cells = shape.Select(c => (X: ax + c.X, Y: ay + c.Y)).ToList();
                if (cells.All(c => c.X > 0 && c.Y > 0 && c.X < w - 1 && c.Y < h - 1 && !walls[c.Y, c.X] && !occupied[c.Y, c.X]))
                {
                    foreach ((int x, int y) in cells)
                    {
                        occupied[y, x] = true;
                    }

                    models.Add(new FuzzRoom.Placed(key, kind, cells, isLeft, goal));
                    return true;
                }
            }

            return false;
        }

        if (!Place('s', "fish_small", SmallFish, r.Chance(0.5), fishGoal))
        {
            return null;
        }

        if (r.Chance(0.6) && !Place('b', "fish_big", BigFish, r.Chance(0.5), fishGoal))
        {
            return null;
        }

        int items = r.Next(0, 5);
        if (alive && items == 0)
        {
            items = 1;
        }

        string sharedKind = "";
        (int X, int Y)[] sharedShape = [];
        if (identical)
        {
            items = Math.Max(items, (alive ? 1 : 0) + 3);
            sharedKind = r.Chance(0.3) ? "item_heavy" : "item_light";
            sharedShape = ItemShapes[r.Next(0, 3)];   // the small ones, so several fit
        }

        for (int i = 0; i < items; i++)
        {
            bool goalOut = alive && i == 0;
            string kind = !goalOut && r.Chance(0.3) ? "item_heavy" : "item_light";
            (int X, int Y)[] shape = ItemShapes[r.Next(0, ItemShapes.Length)];
            if (identical && !goalOut)
            {
                kind = sharedKind;
                shape = sharedShape;
            }

            if (!Place((char)('A' + i), kind, shape, true, goalOut ? "goal_out" : "goal_no"))
            {
                return null;
            }
        }

        return new FuzzRoom { Walls = walls, Models = models };
    }

    /// <summary>Runs everything on one room. Null when the room is unusable (overlaps, dead or solved at load, too big).</summary>
    public static Case? Evaluate(int index, FuzzRoom room, int stateCap, bool keepUnsolvable)
    {
        Level level;
        StateGraph graph;
        try
        {
            level = Level.Build(room.ToJson($"fuzz{index}"));
            var start = new Room(level);
            if (start.IsSolved() || !start.IsSolvable())
            {
                return null;
            }

            graph = ReferenceBfs.Explore(level, stateCap);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null; // more states than the cap
        }

        int? optimum = graph.Optimum;
        if (optimum is null ? !keepUnsolvable : optimum < 2)
        {
            return null;
        }

        var findings = new List<Finding>();
        void AddAll(string check, List<MicroChecks.Violation> violations)
        {
            foreach (IGrouping<string?, MicroChecks.Violation> group in violations.GroupBy(v => v.Cause))
            {
                findings.Add(new Finding(check, group.Key, $"{group.Count()}x, e.g. {group.First().Message}"));
            }
        }

        AddAll("admissible", MicroChecks.Admissible(graph));
        AddAll("consistent", MicroChecks.Consistent(graph));
        AddAll("reduction", MicroChecks.Reduction(graph));
        AddAll("stuck", MicroChecks.Stuck(graph));
        AddAll("stuck-detector", MicroChecks.StuckDetectorAgrees(graph));
        AddAll("stuck-estimate", MicroChecks.StuckEstimate(graph));
        AddAll("symmetry", MicroChecks.SymmetrySound(graph));

        bool plainOk = true;
        foreach (MicroVariant variant in MicroVariants.All)
        {
            SolveResult result = variant.Run(level);
            string? failure = MicroChecks.Matrix(level, optimum, variant, result, requireSolved: false);
            if (variant.Id == "astar")
            {
                plainOk = failure is null;
            }

            if (failure is null)
            {
                continue;
            }

            // PO-PAIRWISE (docs/020): the pairwise move-ordering reduction drops
            // orderings it should keep and can lose every solution. Attributed
            // by mechanism - only when plain A*, the same search without the
            // reduction, passed on this room. Predates the review; the mode is
            // off by default (docs/011) and pinned by micro-level pairwise-unsound.
            string? cause = variant.KnownBug
                ?? (variant.Id == "astar-pairwise" && plainOk ? "PO-PAIRWISE" : null);
            findings.Add(new Finding("matrix:" + variant.Id, cause, failure));
        }

        return new Case(index, room, optimum, graph.Count, findings);
    }

    /// <summary>
    /// Shrinks a room while <paramref name="stillFails"/> holds: drops models
    /// (keeping one fish), then knocks out interior wall cells. Greedy, bounded.
    /// </summary>
    public static FuzzRoom Minimize(FuzzRoom room, Func<FuzzRoom, bool> stillFails, int budget = 120)
    {
        bool progress = true;
        while (progress && budget > 0)
        {
            progress = false;

            for (int i = room.Models.Count - 1; i >= 0 && budget > 0; i--)
            {
                List<FuzzRoom.Placed> fewer = room.Models.Where((_, k) => k != i).ToList();
                if (!fewer.Any(m => m.Kind.StartsWith("fish_", StringComparison.Ordinal)))
                {
                    continue;
                }

                budget--;
                FuzzRoom candidate = room.With(models: fewer);
                if (stillFails(candidate))
                {
                    room = candidate;
                    progress = true;
                }
            }

            for (int y = 1; y < room.Height - 1 && budget > 0; y++)
            {
                for (int x = 1; x < room.Width - 1 && budget > 0; x++)
                {
                    if (!room.Walls[y, x])
                    {
                        continue;
                    }

                    budget--;
                    var walls = (bool[,])room.Walls.Clone();
                    walls[y, x] = false;
                    FuzzRoom candidate = room.With(walls: walls);
                    if (stillFails(candidate))
                    {
                        room = candidate;
                        progress = true;
                    }
                }
            }
        }

        return room;
    }

    /// <summary>Runs <paramref name="count"/> room indices in parallel; results in index order.</summary>
    public static List<Case> Run(ulong seed, int start, int count, int stateCap, bool identical = false)
    {
        var cases = new Case?[count];
        Parallel.For(0, count, i =>
        {
            int index = start + i;
            FuzzRoom? room = Generate(seed, index, identical);
            // Keep roughly one unsolvable room in four: they exercise exhaustion,
            // but random rooms are mostly unsolvable and would crowd the rest out.
            cases[i] = room is null ? null : Evaluate(index, room, stateCap, keepUnsolvable: index % 4 == 0);
        });

        return [.. cases.OfType<Case>()];
    }
}
