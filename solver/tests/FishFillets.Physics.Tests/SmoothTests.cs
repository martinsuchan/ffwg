using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// <see cref="SolutionSmoother"/> (solver/docs/031) reorders a solution's moves
/// without making it longer: the game's original moves where they fit, elsewhere
/// fewer fish switches.
/// </summary>
[TestClass]
public sealed class SmoothTests
{
    /// <summary>The levels whose original a shorter solution replaced (legacy/solution/old).</summary>
    public static IEnumerable<object[]> ImprovedLevels()
    {
        Corpus c = TestCorpus.Instance;
        string oldDir = Path.Combine(c.SolutionsDir, Corpus.OriginalSolutionsFolder);
        return c.LevelNames.Where(l => File.Exists(Path.Combine(oldDir, l + ".lua"))).Select(l => new object[] { l });
    }

    /// <summary>
    /// On every improved solution: the result still solves the level, is no longer,
    /// takes over no fewer of the original's moves, passes through no fewer of its
    /// states, and with those equal switches fish no more often. A second run changes
    /// nothing.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(ImprovedLevels))]
    public void Smoothing_KeepsTheSolution_AndIsIdempotent(string level)
    {
        Corpus c = TestCorpus.Instance;
        Assert.IsTrue(c.TryReadSolution(level, out string moves));
        string original = TestCorpus.Solution(level);
        var smoother = new SolutionSmoother(c.LoadLevel(level));

        SmoothResult r = smoother.Smooth(moves, original);

        Assert.IsTrue(SolutionValidator.Validate(TestCorpus.Room(level), r.Moves).Solved, $"{level}: no longer solves");
        Assert.IsLessThanOrEqualTo(moves.Length, r.Moves.Length, $"{level}: longer");
        Assert.IsGreaterThanOrEqualTo(r.ReferenceMovesBefore, r.ReferenceMovesAfter, $"{level}: fewer original moves");
        Assert.IsGreaterThanOrEqualTo(r.SharedStatesBefore, r.SharedStatesAfter, $"{level}: fewer shared states");
        if (r.SharedStatesAfter == r.SharedStatesBefore && r.ReferenceMovesAfter == r.ReferenceMovesBefore)
        {
            Assert.IsLessThanOrEqualTo(r.SwitchesBefore, r.SwitchesAfter, $"{level}: more fish switches");
        }

        SmoothResult again = smoother.Smooth(r.Moves, original);
        Assert.AreEqual(r.Moves, again.Moves, $"{level}: a second run changed it");
    }

    /// <summary>
    /// map: from move 463 on, our solution and the original move identical items
    /// into each other's places, so with plain keys they share no state after it
    /// and only 22% of the moves could come from the original. Merging identical
    /// items in the keys (as A* does, docs/022) matches the two again.
    /// </summary>
    [TestMethod]
    public void IdenticalItemsThatTradedPlaces_StillMatchTheOriginal()
    {
        Corpus c = TestCorpus.Instance;
        Assert.IsTrue(c.TryReadSolution("map", out string moves));
        SmoothResult r = new SolutionSmoother(c.LoadLevel("map")).Smooth(moves, TestCorpus.Solution("map"));

        Assert.IsGreaterThan(0.7, (double)r.ReferenceMovesAfter / r.Moves.Length, "map: too few original moves");
        Assert.IsGreaterThan(1400, r.SharedStatesAfter, "map: too few shared states");
    }

    /// <summary>The game's own solutions, smoothed against themselves, stay exactly as they are.</summary>
    [TestMethod]
    [DataRow("hole")]
    [DataRow("barrel")]
    [DataRow("duckie")]
    [DataRow("cannons")]
    public void AnOriginal_AgainstItself_IsUnchanged(string level)
    {
        string original = TestCorpus.Solution(level);
        SmoothResult r = new SolutionSmoother(TestCorpus.Instance.LoadLevel(level)).Smooth(original, original);
        Assert.AreEqual(original, r.Moves);
        Assert.AreEqual(original.Length, r.ReferenceMovesAfter);
    }

    /// <summary>
    /// Interleave a run of one fish with the next run of the other (AAA bbb ->
    /// AbAbAb) somewhere the room still lets it play out. Smoothing against the
    /// original restores the original exactly (step 1); without a reference, the
    /// fish switches go back down to at most the original's (step 2).
    /// </summary>
    [TestMethod]
    [DataRow("hole")]
    [DataRow("barrel")]
    [DataRow("map")]
    [DataRow("linux")]
    [DataRow("start")]
    public void AnInterleavedStretch_IsRegrouped(string level)
    {
        string original = TestCorpus.Solution(level);
        string? scrambled = FirstValidInterleave(level, original);
        Assert.IsNotNull(scrambled, $"{level}: no stretch where two runs interleave and still solve");
        Assert.IsGreaterThan(SolutionSmoother.Switches(original), SolutionSmoother.Switches(scrambled));

        var smoother = new SolutionSmoother(TestCorpus.Instance.LoadLevel(level));
        Assert.AreEqual(original, smoother.Smooth(scrambled, original).Moves, $"{level}: step 1 did not restore the original");

        SmoothResult free = smoother.Smooth(scrambled);
        Assert.IsTrue(SolutionValidator.Validate(TestCorpus.Room(level), free.Moves).Solved);
        Assert.IsLessThanOrEqualTo(SolutionSmoother.Switches(original), free.SwitchesAfter, $"{level}: still interleaved");
    }

    private static string? FirstValidInterleave(string level, string moves)
    {
        Room room = TestCorpus.Room(level);
        int i = 0;
        while (i < moves.Length)
        {
            int a = i, r1 = Run(moves, a);
            if (r1 >= moves.Length)
            {
                break;
            }

            int r2 = Run(moves, r1);
            int k = Math.Min(Math.Min(r1 - a, r2 - r1), 5);
            if (k >= 3)
            {
                // The last k moves of the first run with the first k of the second.
                var mixed = new System.Text.StringBuilder();
                for (int t = 0; t < k; t++)
                {
                    mixed.Append(moves[r1 - k + t]).Append(moves[r1 + t]);
                }

                string candidate = moves[..(r1 - k)] + mixed + moves[(r1 + k)..];
                if (candidate != moves && SolutionValidator.Validate(room, candidate).Solved
                    && KeyAfter(room, candidate, r1 + k).SequenceEqual(KeyAfter(room, moves, r1 + k)))
                {
                    return candidate;
                }
            }

            i = r1;
        }

        return null;
    }

    private static byte[] KeyAfter(Room room, string moves, int count)
    {
        room.Reset();
        for (int t = 0; t < count; t++)
        {
            room.ApplyMove(moves[t]);
        }

        var key = new byte[room.StateKeySize];
        room.WriteStateKey(key);
        return key;
    }

    private static int Run(string moves, int start)
    {
        int e = start + 1;
        while (e < moves.Length && char.IsUpper(moves[e]) == char.IsUpper(moves[start]))
        {
            e++;
        }

        return e;
    }
}
