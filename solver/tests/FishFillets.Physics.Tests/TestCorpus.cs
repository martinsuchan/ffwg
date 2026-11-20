using FishFillets.Physics;

namespace FishFillets.Physics.Tests;

/// <summary>
/// The shared, resolved-once level corpus. Resolving walks the directory tree,
/// and reading index.json touches the disk, so it happens once for the whole run
/// rather than per test case.
/// </summary>
internal static class TestCorpus
{
    private static readonly Lazy<Corpus> Lazy = new(() => Corpus.Resolve(), isThreadSafe: true);

    public static Corpus Instance => Lazy.Value;

    /// <summary>A fresh, settled room for a corpus level.</summary>
    public static Room Room(string level) => new(Instance.LoadLevel(level));

    /// <summary>
    /// The game's own solution, from legacy/solution/old/ where a shorter one
    /// replaced it. Tests pin node counts, shortcuts and reductions to these
    /// move strings, so they stay put when a better solution lands.
    /// </summary>
    public static string Solution(string level)
    {
        Assert.IsTrue(Instance.TryReadOriginalSolution(level, out string moves), $"no reference solution for {level}");
        return moves;
    }
}
