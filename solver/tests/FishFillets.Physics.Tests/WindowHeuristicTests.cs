using FishFillets.Physics;
using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// The window search's lower bound (<see cref="WindowHeuristic"/>) must never
/// overestimate, or a window would miss a shortcut it should find. Two checks:
/// against the corpus solutions themselves, and against a search with no bound.
/// </summary>
[TestClass]
public sealed class WindowHeuristicTests
{
    public static IEnumerable<object[]> Levels =>
        TestCorpus.Instance.LevelsWithSolutions().Select(name => new object[] { name });

    /// <summary>
    /// A solution's own moves from step k to step j are a path between those two
    /// rooms, so no admissible bound may say more than j - k. Checked for every
    /// pair up to 40 moves apart, on every level with a recorded solution, with
    /// identical items merged and kept apart.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(Levels))]
    public void TheBound_NeverExceedsTheMovesASolutionTakes(string name)
    {
        const int Span = 40;
        Level level = TestCorpus.Instance.LoadLevel(name);
        string moves = TestCorpus.Solution(name);
        var room = new Room(level);
        var states = new List<RoomSnapshot> { room.Capture() };
        foreach (char move in moves)
        {
            Assert.IsTrue(room.ApplyMove(move), $"{name}: the solution does not replay");
            states.Add(room.Capture());
        }

        var optimizer = new WindowOptimizer(level, LevelReduction.Verified(level, moves));
        foreach (bool merge in new[] { true, false })
        {
            Func<RoomSnapshot, RoomSnapshot, int> estimate = optimizer.Estimator(new WindowOptions { MergeIdenticalItems = merge });
            int tightest = 0;
            for (int j = 1; j < states.Count; j++)
            {
                for (int k = Math.Max(0, j - Span); k < j; k++)
                {
                    int h = estimate(states[k], states[j]);
                    Assert.IsLessThanOrEqualTo(j - k, h, $"{name} (merge {merge}): from step {k} to {j} the bound says {h}, the solution takes {j - k}");
                    tightest = Math.Max(tightest, h);
                }
            }
        }
    }

    /// <summary>
    /// The bounds change how much a window searches, never what it finds: on
    /// every window, each one reports the same shortcut length (or none) as a
    /// plain breadth-first search, and the walls and items bounds search no
    /// more than the plain Manhattan one.
    /// </summary>
    [TestMethod]
    [DataRow("cannons", 10)]
    [DataRow("hole", 12)]
    [DataRow("start", 12)]
    [DataRow("briefcase", 10)]
    [DataRow("barrel", 8)]
    public void EveryBound_FindsWhatABlindSearchFinds(string name, int window)
    {
        Level level = TestCorpus.Instance.LoadLevel(name);
        string moves = TestCorpus.Solution(name);
        var reduction = LevelReduction.Verified(level, moves);

        Dictionary<int, (WindowOutcome, int)> Run(WindowHeuristic heuristic, out long nodes)
        {
            var reports = new System.Collections.Concurrent.ConcurrentBag<WindowReport>();
            OptimizeResult result = new WindowOptimizer(level, reduction).Optimize(moves, new WindowOptions
            {
                Window = window, Heuristic = heuristic, NodeLimit = long.MaxValue, Threads = 4, WindowDone = reports.Add,
            });
            nodes = result.NodesExplored;
            return reports.Where(r => r.Pass == 1).ToDictionary(r => r.Start, r => (r.Outcome, r.Outcome == WindowOutcome.Shortcut ? r.Bound : 0));
        }

        Dictionary<int, (WindowOutcome, int)> blind = Run(WindowHeuristic.None, out long blindNodes);
        Dictionary<int, (WindowOutcome, int)> manhattan = Run(WindowHeuristic.Manhattan, out long manhattanNodes);
        Dictionary<int, (WindowOutcome, int)> walls = Run(WindowHeuristic.Walls, out long wallsNodes);
        Dictionary<int, (WindowOutcome, int)> full = Run(WindowHeuristic.WallsAndItems, out long fullNodes);

        foreach ((string label, Dictionary<int, (WindowOutcome, int)> got) in new[] { ("manhattan", manhattan), ("walls", walls), ("walls+items", full) })
        {
            CollectionAssert.AreEquivalent(blind.Keys.ToArray(), got.Keys.ToArray(), $"{name}: {label} searched other windows");
            foreach ((int start, (WindowOutcome, int) expected) in blind)
            {
                Assert.AreEqual(expected, got[start], $"{name}: window at {start}, {label} disagrees with the blind search");
            }
        }

        Assert.IsLessThanOrEqualTo(manhattanNodes, wallsNodes, $"{name}: the walls bound searched more than Manhattan");
        Assert.IsLessThanOrEqualTo(wallsNodes, fullNodes, $"{name}: adding the items searched more");
        Assert.IsLessThan(blindNodes, manhattanNodes);
    }
}
