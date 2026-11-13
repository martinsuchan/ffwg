using System.Globalization;
using System.Text;

using FishFillets.Search;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>
/// Hardware-independent performance ratchet: for every (level, variant) the
/// matrix runs, the status, solution length, expansions and stored states are
/// kept in <c>Micro/micro-baseline.csv</c>, and a run that does worse fails.
///
/// <para>Counts rather than seconds, because they are deterministic (searches
/// are node-limited, not time-limited) and do not depend on what else the
/// machine is doing - wall-clock swings of 2-3x on this machine are documented
/// in solver/docs/011.</para>
///
/// <para>Regressions fail the test; improvements are printed and pass. After a
/// deliberate change, accept the new numbers with</para>
/// <code>$env:UPDATE_BASELINE=1; dotnet test solver\FishFillets.Solver.slnx -c Release --filter MicroBaseline</code>
/// <para>and commit the CSV diff alongside the change, so the review shows what
/// it bought.</para>
/// </summary>
[TestClass]
public sealed class MicroBaselineTests
{
    /// <summary>Slack before more work counts as a regression: 5% plus a few nodes, for tiny searches.</summary>
    private const double Tolerance = 1.05;

    private const long Slack = 20;

    private static string BaselinePath => Path.Combine(MicroCorpus.SourceDir, "micro-baseline.csv");

    private sealed record Row(string Level, string Variant, string Status, int Length, long Expanded, int Stored);

    [TestMethod]
    public void MicroBaseline_NoRegression()
    {
        var current = new List<Row>();
        foreach (MicroLevel level in MicroCorpus.All)
        {
            foreach (MicroVariant variant in MicroVariants.All)
            {
                // Known-broken cells are not a baseline: they would pin the bug's
                // own numbers (and some are hazardous to run). When the fix lands
                // and the known-bug entry goes, the cell shows up here as new and
                // the baseline is updated to include it.
                if (MicroVariants.KnownBug(level, variant) is not null)
                {
                    continue;
                }

                SolveResult r = MicroVariants.Result(level, variant);
                current.Add(new Row(level.Name, variant.Id, StatusOf(r), r.Moves?.Length ?? -1, r.Expanded, r.StatesStored));
            }
        }

        if (Environment.GetEnvironmentVariable("UPDATE_BASELINE") == "1")
        {
            Write(current);
            Console.WriteLine($"baseline rewritten: {current.Count} rows -> {BaselinePath}");
            return;
        }

        Assert.IsTrue(File.Exists(BaselinePath), $"no baseline yet - create it with UPDATE_BASELINE=1 ({BaselinePath})");
        Dictionary<(string, string), Row> saved = Read().ToDictionary(r => (r.Level, r.Variant));

        var regressions = new List<string>();
        var improvements = new List<string>();
        foreach (Row now in current)
        {
            string id = $"{now.Level} / {now.Variant}";
            if (!saved.TryGetValue((now.Level, now.Variant), out Row? before))
            {
                regressions.Add($"{id}: not in the baseline (new level or variant?) - rerun with UPDATE_BASELINE=1");
                continue;
            }

            if (before.Status == "solved" && now.Status != "solved")
            {
                regressions.Add($"{id}: was solved, now '{now.Status}'");
                continue;
            }

            if (before.Status != "solved" && now.Status == "solved")
            {
                improvements.Add($"{id}: now solved ({before.Status} before)");
            }

            if (now.Status == "solved" && before.Status == "solved")
            {
                if (now.Length > before.Length)
                {
                    regressions.Add($"{id}: solution {before.Length} -> {now.Length} moves");
                }
                else if (now.Length < before.Length)
                {
                    improvements.Add($"{id}: solution {before.Length} -> {now.Length} moves");
                }
            }

            Compare(id, "expanded", before.Expanded, now.Expanded, regressions, improvements);
            Compare(id, "stored", before.Stored, now.Stored, regressions, improvements);
        }

        foreach (string line in improvements)
        {
            Console.WriteLine("improved  " + line);
        }

        Assert.IsEmpty(
            regressions,
            $"{regressions.Count} regressions against {Path.GetFileName(BaselinePath)}:\n  " + string.Join("\n  ", regressions) +
            "\n(if intended, accept with UPDATE_BASELINE=1)");
    }

    private static void Compare(string id, string what, long before, long now, List<string> regressions, List<string> improvements)
    {
        if (now > (before * Tolerance) + Slack)
        {
            regressions.Add($"{id}: {what} {before:N0} -> {now:N0} (+{Percent(before, now)})");
        }
        else if (now < before / Tolerance - Slack)
        {
            improvements.Add($"{id}: {what} {before:N0} -> {now:N0} (-{Percent(now, before)})");
        }
    }

    private static string Percent(long low, long high) =>
        low <= 0 ? "inf" : ((high - low) * 100.0 / low).ToString("F0", CultureInfo.InvariantCulture) + "%";

    /// <summary>The status without the variable parts, so a limit hit is one bucket however it is worded.</summary>
    private static string StatusOf(SolveResult r) =>
        r.Solved ? "solved"
        : r.Status.Contains("limit", StringComparison.Ordinal) ? "limit"
        : r.Status.StartsWith("unsolvable", StringComparison.Ordinal) ? "unsolvable"
        : "exhausted";

    private static void Write(List<Row> rows)
    {
        var text = new StringBuilder("level;variant;status;length;expanded;stored\n");
        foreach (Row r in rows.OrderBy(r => r.Level, StringComparer.Ordinal).ThenBy(r => r.Variant, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"{r.Level};{r.Variant};{r.Status};{r.Length};{r.Expanded};{r.Stored}\n");
        }

        File.WriteAllText(BaselinePath, text.ToString());
    }

    private static IEnumerable<Row> Read() =>
        File.ReadAllLines(BaselinePath)
            .Skip(1)
            .Where(l => l.Length > 0)
            .Select(l => l.Split(';'))
            .Select(p => new Row(
                p[0], p[1], p[2],
                int.Parse(p[3], CultureInfo.InvariantCulture),
                long.Parse(p[4], CultureInfo.InvariantCulture),
                int.Parse(p[5], CultureInfo.InvariantCulture)));
}
