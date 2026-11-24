using FishFillets.Cli;
using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// The incremental ladder (solver/docs/030) keeps one state table while the window
/// grows from one start. It must find what a fresh search of each window finds,
/// in both variants: with the pruned children left out, and with them stored.
/// </summary>
[TestClass]
public sealed class LadderTests
{
    /// <summary>
    /// For every start and rung: the same outcome, the same bound, and a shortcut
    /// of the same length. Each ladder shortcut splices into a solution that still
    /// solves the level. In all, the ladder expands no more nodes than the fresh
    /// windows. Run on the solutions the game shipped with, which still hold
    /// shortcuts (hole: 2-4 moves around moves 113-232).
    /// </summary>
    [TestMethod]
    [DataRow("hole", 100, 240, 8, 12, 28, false)]
    [DataRow("hole", 100, 240, 8, 12, 28, true)]
    [DataRow("cannons", 0, 200, 10, 8, 20, false)]
    [DataRow("cannons", 0, 200, 10, 8, 20, true)]
    [DataRow("start", 0, 120, 8, 8, 24, false)]
    [DataRow("start", 0, 120, 8, 8, 24, true)]
    [DataRow("briefcase", 0, 400, 20, 8, 20, true)]
    public void TheLadder_FindsWhatFreshWindowsFind(string name, int from, int to, int every, int window, int maxWindow, bool storePruned)
    {
        Level level = TestCorpus.Instance.LoadLevel(name);
        string moves = TestCorpus.Solution(name);
        var starts = new List<int>();
        for (int i = from; i <= Math.Min(to, moves.Length - window); i += every)
        {
            starts.Add(i);
        }

        IReadOnlyList<LadderStart> results = new WindowOptimizer(level, LevelReduction.Verified(level, moves)).Ladder(
            moves, starts, window, 4, maxWindow, new WindowOptions { NodeLimit = long.MaxValue, Threads = 4 },
            storePruned: storePruned);

        long fresh = 0, ladder = 0;
        int shortcuts = 0;
        foreach (LadderStart r in results)
        {
            Assert.HasCount(r.Fresh.Count, r.Incremental, $"{name} start {r.Start}: the rung counts differ");
            for (int k = 0; k < r.Fresh.Count; k++)
            {
                LadderRung f = r.Fresh[k], c = r.Incremental[k];
                string at = $"{name} start {r.Start}, window {f.Window} (store pruned {storePruned})";
                Assert.AreEqual(f.Window, c.Window, at);
                Assert.AreEqual(f.Outcome, c.Outcome, at);
                Assert.AreEqual(f.Bound, c.Bound, at);
                Assert.AreEqual(f.Shortcut?.Length, c.Shortcut?.Length, at);
                fresh += f.Expanded;
                ladder += c.Expanded;
                if (c.Shortcut is { } shortcut)
                {
                    shortcuts++;
                    string spliced = string.Concat(moves.AsSpan(0, r.Start), shortcut, moves.AsSpan(r.Start + c.Window));
                    Assert.IsTrue(SolutionValidator.Validate(new Room(level), spliced).Solved, $"{at}: the shortcut does not splice in");
                }
            }
        }

        Assert.IsLessThanOrEqualTo(fresh, ladder, $"{name}: the ladder expanded {ladder:N0} nodes, the fresh windows {fresh:N0}");
        if (name == "hole")
        {
            Assert.IsGreaterThan(0, shortcuts, "hole's shipped solution has shortcuts in this stretch; none was found");
        }
    }

    [TestMethod]
    public void APerStartBlock_SkipsThatStartOnly_AndIsCountedPerSize()
    {
        var policy = new GrowPolicy();
        Assert.AreEqual("AA", GrowPolicy.StartOf("AA|BB|16"));
        Assert.IsFalse(policy.Skips("AA|BB|16"));

        policy.Limited("AA|BB|16");
        Assert.IsTrue(policy.Skips("AA|CC|20"), "a bigger window from the blocked start");
        Assert.IsTrue(policy.Skips("AA|DD|20"), "any window from that room, wherever a splice moved it");
        Assert.IsFalse(policy.Skips("BB|CC|20"), "another start goes on");
        Assert.AreEqual(1, policy.Blocked);
        Assert.AreEqual(2, policy.BlockedAtSize);

        policy.NextSize();
        Assert.AreEqual(0, policy.BlockedAtSize);
        Assert.IsTrue(policy.Skips("AA|EE|24"));
        Assert.AreEqual(1, policy.BlockedAtSize);
    }

    /// <summary><see cref="GrowPolicy.WindowCount"/> must count the windows a round really lists, or the per-start run stops early or never.</summary>
    [TestMethod]
    [DataRow("hole", 16, 4)]
    [DataRow("hole", 30, 4)]
    [DataRow("cannons", 12, 3)]
    public void TheWindowCount_MatchesTheFirstPass(string name, int window, int stride)
    {
        Level level = TestCorpus.Instance.LoadLevel(name);
        string moves = TestCorpus.Instance.TryReadSolution(name, out string best) ? best : TestCorpus.Solution(name);
        var reports = new System.Collections.Concurrent.ConcurrentBag<WindowReport>();
        new WindowOptimizer(level, LevelReduction.Verified(level, moves)).Optimize(moves, new WindowOptions
        {
            Window = window, Stride = stride, NodeLimit = 1, Threads = 4, WindowDone = reports.Add,
        });
        Assert.AreEqual(GrowPolicy.WindowCount(moves.Length, window, stride), reports.Count(r => r.Pass == 1));
    }
}
