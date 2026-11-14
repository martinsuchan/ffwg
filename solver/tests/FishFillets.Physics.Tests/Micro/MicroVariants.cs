using System.Collections.Concurrent;

using FishFillets.Search;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>What a solver variant promises, and therefore what the matrix holds it to.</summary>
internal enum Promise
{
    /// <summary>Claims the shortest solution: must return exactly the optimum, flagged optimal.</summary>
    Optimal,

    /// <summary>Weighted A*: a valid solution no longer than weight x optimum.</summary>
    Bounded,

    /// <summary>Knowingly incomplete or inadmissible: any valid solution (it can never be shorter than the optimum).</summary>
    Valid,
}

/// <param name="KnownBug">
/// A finding that breaks this variant on every level, so the whole column runs
/// under the KnownBug category until it is fixed. A level's own
/// <c>known-bug: matrix:&lt;id&gt;=...</c> entry takes precedence.
/// </param>
internal sealed record MicroVariant(
    string Id, Promise Promise, double Weight, Func<Level, SolveResult> Run, string? KnownBug = null);

/// <summary>
/// Every solver configuration the suite runs against every micro-level, and a
/// shared cache of their results so the matrix and the baseline check do not
/// each pay for the same searches.
///
/// <para>Runs are bounded by <b>expansions, not time</b>, so they are
/// deterministic and the baseline counts are comparable between machines and
/// runs. The time limit is only a safety net and should never be what stops a
/// micro-level search.</para>
/// </summary>
internal static class MicroVariants
{
    public const long NodeLimit = 300_000;

    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(60);

    private static SolveOptions Options(double weight = 1.0) => new()
    {
        Weight = weight,
        MaxNodes = NodeLimit,
        TimeLimit = Safety,
    };

    public static readonly IReadOnlyList<MicroVariant> All =
    [
        new("astar", Promise.Optimal, 1, l => new Solver(l).Solve(Options())),
        new("astar-safe", Promise.Optimal, 1, l => new Solver(l, LevelReduction.Safe(l)).Solve(Options())),
        // Stuck-item detection is on by default (docs/020); this column keeps the
        // search without it, so the baseline shows what the rule saves.
        new("astar-nostuck", Promise.Optimal, 1, l => new Solver(l).Solve(Options() with { DetectStuck = false })),
        // The dead-end half of the rule without the stuck-wall distances
        // (docs/021), so the baseline shows what the estimate adds on its own.
        new("astar-nodist", Promise.Optimal, 1, l => new Solver(l).Solve(Options() with { StuckDistances = false })),
        // Every identical item kept distinct (docs/022), so the baseline shows
        // what merging them saves.
        new("astar-nomerge", Promise.Optimal, 1, l => new Solver(l).Solve(Options() with { MergeIdenticalItems = false })),
        new("astar-sleep", Promise.Optimal, 1, l => new Solver(l).Solve(Options() with { PartialOrder = PartialOrderMode.SleepSets })),
        new("astar-pairwise", Promise.Optimal, 1, l => new Solver(l).Solve(Options() with { PartialOrder = PartialOrderMode.Pairwise })),
        new("wastar-2", Promise.Bounded, 2, l => new Solver(l).Solve(Options(2))),
        new("work-3", Promise.Valid, 1, l => new Solver(l).Solve(Options() with { WorkPenalty = 3 })),
        new("macro", Promise.Valid, 1, l => new MacroSolver(l).Solve(Options())),
        new("macro-w2", Promise.Valid, 2, l => new MacroSolver(l).Solve(Options(2))),
        new("items", Promise.Valid, 1, l => new ItemSolver(l).Solve(Options())),
        new("items-w2", Promise.Valid, 2, l => new ItemSolver(l).Solve(Options(2))),
    ];

    public static MicroVariant Get(string id) => All.Single(v => v.Id == id);

    /// <summary>The finding that breaks this (level, variant) cell, or null.</summary>
    public static string? KnownBug(MicroLevel level, MicroVariant variant) =>
        level.KnownBug("matrix:" + variant.Id) ?? variant.KnownBug;

    /// <summary>
    /// A known-bug id ending in '!' marks a cell that is not safe to run at all -
    /// one that hangs or allocates gigabytes - so even the KnownBug run reports it
    /// rather than executing it.
    /// </summary>
    public static bool IsHazard(string? knownBug) => knownBug?.EndsWith('!') == true;

    private static readonly ConcurrentDictionary<(string Level, string Variant), Lazy<SolveResult>> Cache = new();

    public static SolveResult Result(MicroLevel level, MicroVariant variant) =>
        Cache.GetOrAdd(
            (level.Name, variant.Id),
            _ => new Lazy<SolveResult>(() => variant.Run(level.Build()))).Value;

    /// <summary>Matrix rows: (level, variant) for the normal run, or the known-bug ones.</summary>
    public static IEnumerable<object[]> Rows(bool knownBug)
    {
        var rows = new List<object[]>();
        foreach (MicroLevel level in MicroCorpus.All)
        {
            foreach (MicroVariant variant in All)
            {
                if ((KnownBug(level, variant) is not null) == knownBug)
                {
                    rows.Add([level.Name, variant.Id]);
                }
            }
        }

        return rows.Count > 0 ? rows : [[MicroCorpus.None, MicroCorpus.None]];
    }
}
