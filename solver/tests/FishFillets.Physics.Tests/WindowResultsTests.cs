using System.IO.Compression;
using System.Xml.Linq;

using FishFillets.Cli;

namespace FishFillets.Physics.Tests;

/// <summary>
/// solver/docs/window-results.csv and .xlsx: the window optimiser's record, kept
/// apart from results.csv. One row per level, window, stride and bound, replaced
/// when that round is run again; a "levels" sheet sums up how far each level got.
/// </summary>
[TestClass]
public sealed class WindowResultsTests
{
    private static WindowRound Round(string level, int window, int to, int? covered, int? limited, string recorded = "2026-11-21") =>
        new(level, window, 4, "full", 512, to, 118, 116, 0, limited, 0, covered, null, 600, 6, 571_321_444, 2954.2, "", recorded);

    [TestMethod]
    public void Rounds_RoundTrip_AreReplacedByKey_AndTheLevelsSheetSumsThemUp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ffwg-window-results-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            WindowResults.LevelFacts facts = level => level == "pavement" ? (500, 512) : (402, 432);
            WindowResults.Record(dir, facts, [Round("pavement", 40, 512, 37, 0), Round("pavement", 44, 512, null, 2), Round("hanoi", 72, 432, 69, 0)]);
            // Run again: the same key replaces the row, it is not added.
            WindowResults.Record(dir, facts, [Round("pavement", 44, 498, null, 1, "2026-11-22")]);

            List<WindowRound> rows = WindowResults.Load(dir);
            Assert.HasCount(3, rows);
            WindowRound again = rows.Single(r => r.Level == "pavement" && r.Window == 44);
            Assert.AreEqual(498, again.To);
            Assert.AreEqual(1, again.Limited);
            Assert.IsNull(again.Covered);
            Assert.AreEqual(600, again.WindowSeconds);
            Assert.IsNull(again.NodeLimit);
            Assert.AreEqual(571_321_444, again.Nodes);

            byte[] csv = File.ReadAllBytes(WindowResults.CsvPath(dir));
            CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, csv[..3], "the CSV has a BOM, so Excel reads it as UTF-8");

            using var zip = ZipFile.OpenRead(WindowResults.XlsxPath(dir));
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var book = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open());
            CollectionAssert.AreEqual(new[] { "levels", "rounds" }, book.Descendants(ns + "sheet").Select(s => (string)s.Attribute("name")!).ToArray());

            var levels = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open())
                .Descendants(ns + "c").ToDictionary(c => (string)c.Attribute("r")!);
            // Sorted by level: hanoi, then pavement. Columns: level, best, bundled, now, covered, largest window...
            Assert.AreEqual("hanoi", levels["A2"].Value);
            Assert.AreEqual("pavement", levels["A3"].Value);
            Assert.AreEqual("498", (string?)levels["D3"].Element(ns + "v"), "now: the shortest any round reached");
            Assert.AreEqual("5", (string?)levels["D3"].Attribute("s"), "498 beats the hall of fame's 500: green");
            Assert.AreEqual("2", (string?)levels["D2"].Attribute("s"), "432 trails 402: plain");
            Assert.AreEqual("37", (string?)levels["E3"].Element(ns + "v"), "covered: the longest stretch a finished round proved");
            Assert.AreEqual("44", (string?)levels["F3"].Element(ns + "v"), "complete up to: every window of 44 was searched");
            Assert.AreEqual("44", (string?)levels["G3"].Element(ns + "v"), "the largest window tried");
            Assert.AreEqual("1", (string?)levels["I3"].Element(ns + "v"), "its windows stopped at a limit");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A note edited by hand may hold a ';'. The row must still load, or the
    /// next save would drop it from the record.
    /// </summary>
    [TestMethod]
    public void ARowWhoseNoteHoldsASemicolon_StillLoads()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ffwg-window-results-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(WindowResults.CsvPath(dir),
                "level;window;stride;heuristic;from;to;windows;ruledOut;shortcuts;limited;notSearched;covered;nodeLimit;windowSeconds;windowMemoryGb;nodes;seconds;note;recorded\n" +
                "barrel;32;1;manhattan;1231;1223;1433;1429;4;;0;;5000000;;;225627053;1138.1;old run; limits not logged;2026-11-19\n");
            WindowRound row = WindowResults.Load(dir).Single();
            Assert.AreEqual(1223, row.To);
            Assert.AreEqual("old run, limits not logged", row.Note);
            Assert.AreEqual("2026-11-19", row.Recorded);
            Assert.IsNull(row.Limited);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
