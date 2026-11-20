using FishFillets.Cli;
using FishFillets.Physics;

namespace FishFillets.Physics.Tests;

/// <summary>
/// The results table (solver/docs/results.csv) accumulates weeks of search, and
/// nothing else in the project can reconstruct it. These tests cover the two ways
/// that record could quietly rot: a write that mangles a field, and a merge that
/// throws away a better result.
/// </summary>
[TestClass]
public sealed class ResultsFileTests
{
    /// <summary>
    /// A throwaway corpus in the temp directory, so a test can never write over
    /// the real record. The level directory is left empty on purpose: the derived
    /// columns are then all zero, which is exactly the case a hand-made or
    /// deleted level produces.
    /// </summary>
    private static Corpus TemporaryCorpus(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "ffsolve-tests", Guid.NewGuid().ToString("N"));
        string levels = Path.Combine(root, "levels");
        Directory.CreateDirectory(levels);
        return Corpus.Resolve(levels, Path.Combine(root, "solution"));
    }

    [TestMethod]
    public void ARecordedRunSurvivesTheRoundTrip()
    {
        Corpus corpus = TemporaryCorpus(out string root);
        try
        {
            var run = new LevelResult
            {
                Moves = 163,
                Proven = true,
                Method = "astar",
                Source = "stairs-simple",
                Bound = 163,
                Expanded = 22_581_029,
                Stored = 23_552_378,
                Seconds = 105.2,
                // Deliberately awkward: a comma would split the row and a quote
                // would end the field early, if either were written raw.
                Status = "solved, \"eventually\"",
                Recorded = "2026-08-15",
            };

            ResultsFile.Save(corpus, new Dictionary<string, LevelResult> { ["test-level"] = run });
            Dictionary<string, LevelResult> read = ResultsFile.Load(corpus.DocsDir);

            Assert.AreEqual(
                ResultsFile.CultureSeparator(),
                ResultsFile.DetectSeparator(ResultsFile.PathFor(corpus.DocsDir)),
                "a new file should follow the local Windows list separator");

            Assert.IsTrue(read.TryGetValue("test-level", out LevelResult? back));
            Assert.AreEqual(run.Moves, back!.Moves);
            Assert.IsTrue(back.Proven);
            Assert.AreEqual(run.Method, back.Method);
            Assert.AreEqual(run.Source, back.Source);
            Assert.AreEqual(run.Bound, back.Bound);
            Assert.AreEqual(run.Expanded, back.Expanded);
            Assert.AreEqual(run.Stored, back.Stored);
            Assert.AreEqual(run.Seconds, back.Seconds, 0.05);
            Assert.AreEqual(run.Status, back.Status);
            Assert.AreEqual(run.Recorded, back.Recorded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Every save also writes results.xlsx: official levels only, typed cells,
    /// a frozen header, an AutoFilter, a green "our" where we hold the best
    /// known solution (and a green "bundled" where the game's own already is), and the same bytes for the same table (so a committed
    /// copy only changes with the results).
    /// </summary>
    [TestMethod]
    public void TheWorkbookShowsOfficialLevelsFormatted()
    {
        LevelResult Run(int moves) => new()
        {
            Moves = moves, Proven = true, Method = "astar", Bound = moves,
            Expanded = 22_581_029, Stored = 23_552_378, Seconds = 105.2,
            Status = "solved & <checked>", Recorded = "2026-08-15",
        };

        var rows = new List<ResultsRow>
        {
            new() { Name = "record", Branch = "Fish House", Best = 170, Bundled = 175, Run = Run(163), Order = 1 },
            new() { Name = "behind", Branch = "Fish House", Best = 100, Bundled = 100, Run = Run(110), Order = 2 },
            new() { Name = "record-simple", Best = 170, Run = Run(163), Order = 1, IsDerived = true },
            new() { Name = "hand-made", Run = Run(12), Order = int.MaxValue },
        };

        var first = new MemoryStream();
        ResultsWorkbook.Write(first, rows);
        var second = new MemoryStream();
        ResultsWorkbook.Write(second, rows);
        CollectionAssert.AreEqual(first.ToArray(), second.ToArray(), "the same table should give the same bytes");

        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(first.ToArray()));
        foreach (string part in new[] { "[Content_Types].xml", "xl/workbook.xml", "xl/styles.xml", "xl/worksheets/sheet1.xml" })
        {
            Assert.IsNotNull(zip.GetEntry(part), $"missing part {part}");
        }

        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheet = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd());
        System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        var pane = sheet.Descendants(ns + "pane").Single();
        Assert.AreEqual("frozen", (string?)pane.Attribute("state"));
        Assert.AreEqual("1", (string?)pane.Attribute("ySplit"));
        // Header + the two official levels; the simplified copy and the custom room are left out.
        Assert.AreEqual("A1:P3", (string?)sheet.Descendants(ns + "autoFilter").Single().Attribute("ref"));

        var cells = sheet.Descendants(ns + "c").ToDictionary(c => (string)c.Attribute("r")!);
        string[] header = Enumerable.Range(0, 16).Select(i => cells[$"{(char)('A' + i)}1"].Value).ToArray();
        CollectionAssert.AreEqual(
            new[] { "name", "branch", "best", "bundled", "our", "width", "height", "items", "proven", "bound",
                    "bound %", "expanded", "stored", "seconds", "status", "recorded" },
            header);

        Assert.AreEqual("record", cells["A2"].Value);
        Assert.AreEqual("behind", cells["A3"].Value);
        // Counts are numbers (no t= attribute) in the thousands-separator style.
        Assert.AreEqual("22581029", (string?)cells["L2"].Element(ns + "v"));
        Assert.IsNull(cells["L2"].Attribute("t"));
        Assert.AreEqual("2", (string?)cells["L2"].Attribute("s"));
        Assert.AreEqual("105.2", (string?)cells["N2"].Element(ns + "v"));
        // 2026-08-15 as an Excel date serial.
        Assert.AreEqual("46249", (string?)cells["P2"].Element(ns + "v"));
        Assert.AreEqual("solved & <checked>", cells["O2"].Value);
        // The bound as a share of the shortest known solution, in the percent style:
        // 163 of our 163 (shorter than the record 170); 110 of the record 100.
        Assert.AreEqual("1", (string?)cells["K2"].Element(ns + "v"));
        Assert.AreEqual("1.1", (string?)cells["K3"].Element(ns + "v"));
        Assert.AreEqual("6", (string?)cells["K2"].Attribute("s"));
        // Green where our 163 beats the record 170; plain where 110 trails 100.
        Assert.AreEqual("5", (string?)cells["E2"].Attribute("s"));
        Assert.AreEqual("2", (string?)cells["E3"].Attribute("s"));
        // The same for the bundled solution: 175 trails the record 170, 100 equals the record 100.
        Assert.AreEqual("2", (string?)cells["D2"].Attribute("s"));
        Assert.AreEqual("5", (string?)cells["D3"].Attribute("s"));
    }

    [TestMethod]
    public void SavingAlsoWritesTheWorkbook()
    {
        Corpus corpus = TemporaryCorpus(out string root);
        try
        {
            ResultsFile.Save(corpus, new Dictionary<string, LevelResult>
            {
                ["test-level"] = new() { Moves = 10, Status = "solved", Method = "astar" },
            });
            Assert.IsTrue(File.Exists(ResultsWorkbook.PathFor(corpus.DocsDir)), "a save should write the workbook too");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void SavingKeepsThePreviousTableAsABackup()
    {
        Corpus corpus = TemporaryCorpus(out string root);
        try
        {
            ResultsFile.Save(corpus, new Dictionary<string, LevelResult>
            {
                ["test-level"] = new() { Moves = 10, Status = "solved", Method = "astar" },
            }, separator: ',');
            ResultsFile.Save(corpus, new Dictionary<string, LevelResult>
            {
                ["test-level"] = new() { Moves = 9, Status = "solved", Method = "astar" },
            }, separator: ',');

            string backup = ResultsFile.PathFor(corpus.DocsDir) + ".bak";
            Assert.IsTrue(File.Exists(backup), "the previous table should be kept");
            StringAssert.Contains(File.ReadAllText(backup), ",10,");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A Czech or German Windows opens a comma-separated CSV as a single column,
    /// so the file follows the local list separator - and then has to keep
    /// parsing, including its one decimal column, whichever machine reads it.
    /// </summary>
    [TestMethod]
    [DataRow(',')]
    [DataRow(';')]
    [DataRow('\t')]
    public void AnySeparatorRoundTripsAndSticks(char separator)
    {
        Corpus corpus = TemporaryCorpus(out string root);
        try
        {
            var run = new LevelResult
            {
                Moves = 66,
                Bound = 66,
                Seconds = 171.6,
                Method = "astar",
                Status = "solved",
            };

            ResultsFile.Save(corpus, new Dictionary<string, LevelResult> { ["test-level"] = run }, separator);

            string path = ResultsFile.PathFor(corpus.DocsDir);
            Assert.AreEqual(separator, ResultsFile.DetectSeparator(path));
            Assert.AreEqual(171.6, ResultsFile.Load(corpus.DocsDir)["test-level"].Seconds, 0.05);

            // Writing again without naming a separator must not reflow the file:
            // the record is committed, and a run on a differently-configured
            // machine would otherwise rewrite every line of it.
            ResultsFile.Save(corpus, ResultsFile.Load(corpus.DocsDir));
            Assert.AreEqual(separator, ResultsFile.DetectSeparator(path), "the separator should stick");
            Assert.AreEqual(171.6, ResultsFile.Load(corpus.DocsDir)["test-level"].Seconds, 0.05);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The decimal column is the one that can collide with the field separator.
    /// Whatever it was written as, it has to read back as a number.
    /// </summary>
    [TestMethod]
    public void ADecimalWrittenByAnyLocaleReadsBack()
    {
        Corpus corpus = TemporaryCorpus(out string root);
        try
        {
            string path = ResultsFile.PathFor(corpus.DocsDir);
            Directory.CreateDirectory(corpus.DocsDir);
            File.WriteAllText(
                path,
                "name;our;seconds;status\ntest-level;66;171,6;solved\n");

            Assert.AreEqual(171.6, ResultsFile.Load(corpus.DocsDir)["test-level"].Seconds, 0.05);

            File.WriteAllText(path, "name,our,seconds,status\ntest-level,66,171.6,solved\n");
            Assert.AreEqual(171.6, ResultsFile.Load(corpus.DocsDir)["test-level"].Seconds, 0.05);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void MergeKeepsTheShorterSolutionAndTheHigherBound()
    {
        var runs = new Dictionary<string, LevelResult>
        {
            ["level"] = new() { Moves = 0, Bound = 57, Status = "time limit reached" },
        };

        // An unproven solution found on a simplified room carries no bound of its
        // own - the bound already recorded against the real level must survive it.
        ResultsFile.Merge(runs, "level", new LevelResult { Moves = 59, Proven = false, Source = "level-simple" });
        Assert.AreEqual(59, runs["level"].Moves);
        Assert.AreEqual(57, runs["level"].Bound, "the earlier bound must not be lost");

        // A longer answer never displaces a shorter one...
        ResultsFile.Merge(runs, "level", new LevelResult { Moves = 61, Proven = true });
        Assert.AreEqual(59, runs["level"].Moves);

        // ...but a proof at the same length does, since it says more.
        ResultsFile.Merge(runs, "level", new LevelResult { Moves = 59, Proven = true, Bound = 59 });
        Assert.IsTrue(runs["level"].Proven);
        Assert.AreEqual(59, runs["level"].Bound);
    }

    /// <summary>
    /// The branch and play-order columns come from the game's own Lua. If either
    /// file moves or changes shape the table silently loses its grouping, so this
    /// checks the parse against the real corpus rather than a fixture.
    /// </summary>
    [TestMethod]
    public void EveryLevelOnTheWorldMapHasABranch()
    {
        IReadOnlyDictionary<string, LevelPlace> map = TestCorpus.Instance.WorldMap;
        Assert.IsGreaterThan(75, map.Count, "worldmap.lua should yield every level node");

        foreach (string level in TestCorpus.Instance.LevelNames)
        {
            // 'ending' is the post-game level and is not a map node.
            if (level == "ending")
            {
                continue;
            }

            Assert.IsTrue(map.TryGetValue(level, out LevelPlace place), $"{level} is missing from the world map");
            Assert.IsFalse(string.IsNullOrEmpty(place.Branch), $"{level} has no branch name");
        }

        Assert.AreEqual("Fish House", map["start"].Branch);
        Assert.AreEqual(0, map["start"].Order, "start is the first node in worldmap.lua");
        Assert.AreEqual("Ship Wrecks", map["wreck"].Branch);
    }
}
