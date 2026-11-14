using System.Globalization;
using System.Text;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>
/// Differential fuzzing against the oracle (<see cref="MicroFuzzer"/>).
///
/// <para><b>Smoke</b> runs in the default suite: a fixed seed and a few hundred
/// candidate rooms, so it is deterministic and costs seconds. It fails on any
/// violation not attributed to an open finding, and prints how many each open
/// finding accounted for - a fix shows up as its count dropping to zero.</para>
///
/// <para><b>Deep</b> is on demand, for hunting:</para>
/// <code>$env:FUZZ_COUNT=5000; $env:FUZZ_SEED=7; dotnet test solver\FishFillets.Solver.slnx -c Release --filter TestCategory=Fuzz</code>
/// <para>Each new kind of failure is minimised and written as a ready-to-commit
/// <c>.lvl</c> file under <c>%TEMP%\ffwg-fuzz\</c>, and printed.</para>
/// </summary>
[TestClass]
public sealed class MicroFuzzTests
{
    private const ulong SmokeSeed = 20261113;

    private const int SmokeCandidates = 400;

    private const int StateCap = 40_000;

    [TestMethod]
    public void Fuzz_Smoke_NoUnexplainedFailures() => Fuzz(SmokeSeed, 0, SmokeCandidates, minimize: false);

    /// <summary>Smoke over rooms whose plain items are all identical, for merging them (docs/022).</summary>
    [TestMethod]
    public void Fuzz_Smoke_IdenticalItems() => Fuzz(SmokeSeed + 1, 0, SmokeCandidates, minimize: false, identical: true);

    [TestMethod]
    [TestCategory("Fuzz")]
    public void Fuzz_Deep()
    {
        string? countText = Environment.GetEnvironmentVariable("FUZZ_COUNT");
        if (countText is null)
        {
            Assert.Inconclusive("set FUZZ_COUNT (and optionally FUZZ_SEED, FUZZ_START) to run the deep fuzz");
        }

        ulong seed = ulong.Parse(Environment.GetEnvironmentVariable("FUZZ_SEED") ?? "1", CultureInfo.InvariantCulture);
        int start = int.Parse(Environment.GetEnvironmentVariable("FUZZ_START") ?? "0", CultureInfo.InvariantCulture);
        bool identical = Environment.GetEnvironmentVariable("FUZZ_IDENTICAL") == "1";
        Fuzz(seed, start, int.Parse(countText, CultureInfo.InvariantCulture), minimize: true, identical);
    }

    private static void Fuzz(ulong seed, int start, int count, bool minimize, bool identical = false)
    {
        List<MicroFuzzer.Case> cases = MicroFuzzer.Run(seed, start, count, StateCap, identical);

        int solvable = cases.Count(c => c.Optimum is not null);
        Console.WriteLine(
            $"seed {seed}: {cases.Count} usable rooms of {count} candidates ({solvable} solvable, optimum " +
            $"{(solvable == 0 ? "-" : $"{cases.Where(c => c.Optimum is not null).Min(c => c.Optimum)}-{cases.Max(c => c.Optimum)}")}, " +
            $"up to {(cases.Count == 0 ? 0 : cases.Max(c => c.States)):N0} states)");

        var all = cases.SelectMany(c => c.Findings.Select(f => (Case: c, Finding: f))).ToList();
        foreach (IGrouping<string, (MicroFuzzer.Case Case, MicroFuzzer.Finding Finding)> group in
                 all.Where(x => x.Finding.Cause is not null).GroupBy(x => $"{x.Finding.Cause,-6} {x.Finding.Check}").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  known  {group.Key}: {group.Count()} rooms");
        }

        var unexplained = all.Where(x => x.Finding.Cause is null).ToList();
        if (unexplained.Count == 0)
        {
            return;
        }

        var report = new StringBuilder($"{unexplained.Count} unexplained failures (seed {seed}):\n");
        foreach (IGrouping<string, (MicroFuzzer.Case Case, MicroFuzzer.Finding Finding)> group in unexplained.GroupBy(x => x.Finding.Check))
        {
            (MicroFuzzer.Case first, MicroFuzzer.Finding finding) = group.OrderBy(x => x.Case.States).First();
            report.Append($"\n== {group.Key}: {group.Count()} rooms; smallest is #{first.Index} ({first.States} states)\n   {finding.Message}\n");

            FuzzRoom room = first.Room;
            if (minimize)
            {
                room = MicroFuzzer.Minimize(room, candidate =>
                    MicroFuzzer.Evaluate(first.Index, candidate, StateCap, keepUnsolvable: true) is { } c
                    && c.Findings.Any(f => f.Check == group.Key && f.Cause is null));
            }

            MicroFuzzer.Case? shrunk = MicroFuzzer.Evaluate(first.Index, room, StateCap, keepUnsolvable: true);
            string lvl = room.ToLvl(
                $"# expect: {(shrunk?.Optimum?.ToString(CultureInfo.InvariantCulture) ?? "none")}\n" +
                $"# tags:   fuzz\n" +
                $"# why:    found by the fuzzer (seed {seed}, room #{first.Index}): {group.Key}\n" +
                $"#         {shrunk?.Findings.FirstOrDefault(f => f.Check == group.Key)?.Message ?? finding.Message}\n");
            report.Append(lvl);

            if (minimize)
            {
                string dir = Path.Combine(Path.GetTempPath(), "ffwg-fuzz");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"fuzz-{seed}-{first.Index}-{group.Key.Replace(':', '-')}.lvl");
                File.WriteAllText(path, lvl);
                report.Append($"   written to {path}\n");
            }
        }

        Assert.Fail(report.ToString());
    }
}
