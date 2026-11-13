using System.Reflection;

using FishFillets.Search;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>
/// Checks on solver internals that no end-to-end run reliably reaches.
/// Reflection on private members on purpose: these pin specific mechanisms, and
/// should be rewritten (or deleted) when the mechanism they test is replaced.
/// </summary>
[TestClass]
public sealed class WhiteBoxTests
{
    /// <summary>
    /// C1: <c>Solver.NoWayOut</c> caches its verdict per <b>item layout</b>, but the
    /// analysis also depends on which fish are still in the room (who can push,
    /// and how hard). In <c>heavy-gate</c>, the same layout - plug unmoved - is
    /// hopeless once the big fish has left and fine while it is still there. A
    /// verdict must not depend on what was evaluated before it.
    ///
    /// <para>The fuzzer never hit this end to end (FIFO order nearly always
    /// computes a layout with both fish present first), which is exactly why it
    /// is pinned here directly.</para>
    /// </summary>
    [TestMethod]
    [TestCategory("KnownBug")]
    public void StuckCache_VerdictDoesNotDependOnHistory_C1()
    {
        Level level = MicroCorpus.Get("heavy-gate").Build();

        // The big fish swims up and out, leaving the plug where it is.
        const string bigFishLeaves = "UURRRR";

        bool fresh = NoWayOut(new Solver(level), level, "");
        Assert.IsFalse(fresh, "setup: at the start the big fish can still free the small one");

        var solver = new Solver(level);
        bool afterExit = NoWayOut(solver, level, bigFishLeaves);
        Assert.IsTrue(afterExit, "setup: with the big fish gone the small fish is walled in for good");

        bool cached = NoWayOut(solver, level, "");
        Assert.AreEqual(fresh, cached, "the start position was condemned because a same-layout state without the big fish was evaluated first");
    }

    /// <summary>Puts the solver's room into the state after <paramref name="moves"/> and asks NoWayOut.</summary>
    private static bool NoWayOut(Solver solver, Level level, string moves)
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        var room = (Room)typeof(Solver).GetField("_room", Private)!.GetValue(solver)!;
        var key = (byte[])typeof(Solver).GetField("_key", Private)!.GetValue(solver)!;
        var mobile = (int[])typeof(Solver).GetField("_mobile", Private)!.GetValue(solver)!;

        room.Reset();
        foreach (char move in moves)
        {
            Assert.IsTrue(room.ApplyMove(move), $"setup: '{move}' in '{moves}' is not possible");
        }

        if (moves.Length > 0)
        {
            Assert.IsTrue(room.State(level.Units.Single(u => level.Models[u.Model].Kind == "fish_big").Model).IsOut, "setup: the big fish should be out");
        }

        room.WriteStateKey(key, mobile);
        return (bool)typeof(Solver).GetMethod("NoWayOut", Private)!.Invoke(solver, null)!;
    }
}
