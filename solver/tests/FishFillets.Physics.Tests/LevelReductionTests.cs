using FishFillets.Physics;
using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// The level reduction claims certain models can never move, and the search then
/// leaves them out of its state key. If that claim is wrong the search can prune
/// a path it needed, so the claim is checked against the strongest evidence
/// available: 80 real solutions, 32,031 moves of actual play.
/// </summary>
[TestClass]
public sealed class LevelReductionTests
{
    /// <summary>
    /// The soundness test. Every model the analysis freezes must still be exactly
    /// where the level put it after every move of that level's recorded solution.
    /// One counter-example anywhere in the corpus would sink the whole reduction.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(ReferenceSolutionTests.SolvableLevels), typeof(ReferenceSolutionTests))]
    public void FrozenModelsNeverMoveDuringReferenceSolutions(string levelName)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        string moves = TestCorpus.Solution(levelName);
        LevelReduction reduction = LevelReduction.Verified(level, moves);

        var room = new Room(level);
        var frozen = reduction.FrozenModels;

        AssertStill(room, level, frozen, levelName, -1);
        for (int i = 0; i < moves.Length; i++)
        {
            Assert.IsTrue(room.ApplyMove(moves[i]), $"{levelName}: move {i} was rejected");
            AssertStill(room, level, frozen, levelName, i);
        }
    }

    private static void AssertStill(Room room, Level level, int[] frozen, string levelName, int move)
    {
        foreach (int model in frozen)
        {
            ref readonly ModelState m = ref room.State(model);
            ModelDef def = level.Models[model];
            Assert.IsTrue(
                m.X == def.X && m.Y == def.Y && !m.IsLost && !m.IsOut,
                $"{levelName}: model {model} ({def.Kind}) was frozen by the analysis but moved from " +
                $"({def.X},{def.Y}) to ({m.X},{m.Y}) after move {move}");
        }
    }

    /// <summary>
    /// The reduction has to earn its place - if it froze nothing it would be
    /// sound but pointless. Pins the aggregate so a regression that quietly stops
    /// freezing shows up.
    ///
    /// The number is modest on purpose. The unchecked analysis freezes far more
    /// (~1,055 of 1,619), but a real solution contradicts it on most levels, and
    /// <see cref="LevelReduction.Verified"/> then falls back to the type-level
    /// reduction there. What survives is what evidence supports. See
    /// solver/docs/004 on why a complete reducer is not a realistic goal.
    /// </summary>
    [TestMethod]
    public void FreezesAMeaningfulShareOfTheCorpus()
    {
        int mutable = 0, mobile = 0, levelsReduced = 0;
        foreach (string name in TestCorpus.Instance.LevelsWithSolutions())
        {
            Level level = TestCorpus.Instance.LoadLevel(name);
            LevelReduction reduction = LevelReduction.Verified(level, TestCorpus.Solution(name));
            mutable += level.MutableModels.Length;
            mobile += reduction.MobileModels.Length;
            if (reduction.FrozenModels.Length > 0)
            {
                levelsReduced++;
            }
        }

        Assert.IsGreaterThan(50, mutable - mobile, $"expected real reduction, got {mobile}/{mutable} mobile");
        Assert.IsGreaterThan(5, levelsReduced, $"only {levelsReduced} level(s) reduced at all");
    }

    /// <summary>
    /// A model the analysis keeps must genuinely be in the key, so the state key
    /// size follows the mobile set rather than the type-level one.
    /// </summary>
    [TestMethod]
    public void StateKeyCoversExactlyTheMobileModels()
    {
        Level level = TestCorpus.Instance.LoadLevel("library");
        LevelReduction reduction = LevelReduction.Verified(level, TestCorpus.Solution("library"));

        Assert.AreEqual(reduction.MobileModels.Length * 5, reduction.StateKeySize);
        Assert.AreEqual(
            level.MutableModels.Length,
            reduction.MobileModels.Length + reduction.FrozenModels.Length,
            "every type-mutable model should be classified either mobile or frozen");
    }
}
