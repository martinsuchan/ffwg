using FishFillets.Physics.Tests.Micro;
using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// The deferred fish field (<see cref="FishField"/>) and the multi-source
/// relaxation that advances it (<see cref="InertRouter.Relax"/>).
///
/// <para>Relax is checked against <see cref="InertRouter.Route"/>, which the
/// micro property <c>ExactRouter_AgreesWithTheEngine</c> already checks against
/// the engine: from a single zero-cost source they must agree placement for
/// placement, and from several sources Relax must be the pointwise minimum of the
/// single-source answers. The ignore variant is checked for the one thing that
/// can go wrong only there: asking the engine with the fish standing on the
/// ignored model would wipe that model out of the grid.</para>
/// </summary>
[TestClass]
public sealed class FishFieldTests
{
    [TestMethod]
    [DataRow("start")]
    [DataRow("wc")]
    public void Relax_FromThePointItStands_EqualsRoute(string levelName)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        LevelReduction reduction = LevelReduction.Compute(level);
        var router = new InertRouter(level, reduction, exactSupport: true);
        var room = new Room(level);
        int placements = level.Width * level.Height * 2;
        var field = new ushort[placements];
        var expected = new ushort[placements];

        foreach (UnitDef unit in level.Units)
        {
            int fish = unit.Model;
            router.Route(room, fish);
            FishField.Clear(expected);
            foreach (int placement in router.Reached)
            {
                expected[placement] = (ushort)router.CostOf(placement);
            }

            (int x, int y, bool isLeft) = room.Position(fish);
            FishField.SetPoint(field, router.Placement(x, y, isLeft));
            router.Relax(room, fish, field);

            CollectionAssert.AreEqual(expected, field, $"{levelName}: {level.Models[fish].Kind}");
            Assert.AreEqual(router.Reached.Count, FishField.Count(field));
        }
    }

    /// <summary>Two sources at different initial costs: the result is the pointwise min of the two relaxations, each shifted by its own cost.</summary>
    [TestMethod]
    [DataRow("start")]
    [DataRow("wc")]
    public void Relax_TwoSources_IsThePointwiseMinimum(string levelName)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        LevelReduction reduction = LevelReduction.Compute(level);
        var router = new InertRouter(level, reduction, exactSupport: true);
        var room = new Room(level);
        int placements = level.Width * level.Height * 2;

        foreach (UnitDef unit in level.Units)
        {
            int fish = unit.Model;
            router.Route(room, fish);

            // A far-away second source: the last placement the BFS reached.
            (int x, int y, bool isLeft) = room.Position(fish);
            int a = router.Placement(x, y, isLeft);
            int b = router.Reached[^1];
            const int costA = 7, costB = 3;

            var fromA = new ushort[placements];
            FishField.SetPoint(fromA, a);
            router.Relax(room, fish, fromA);

            var fromB = new ushort[placements];
            FishField.SetPoint(fromB, b);
            router.Relax(room, fish, fromB);

            var both = new ushort[placements];
            FishField.Clear(both);
            both[a] = costA;
            both[b] = costB;
            router.Relax(room, fish, both);

            for (int q = 0; q < placements; q++)
            {
                int viaA = fromA[q] == FishField.None ? int.MaxValue : fromA[q] + costA;
                int viaB = fromB[q] == FishField.None ? int.MaxValue : fromB[q] + costB;
                int best = Math.Min(viaA, viaB);
                int want = best == int.MaxValue ? FishField.None : best;
                Assert.AreEqual(want, both[q], $"{levelName}: {level.Models[fish].Kind} placement {q}");
            }
        }
    }

    /// <summary>
    /// Relax with the other fish ignored never touches the room - neither its
    /// key nor its grid - and is never dearer than without ignoring it. Walked
    /// along the reference solutions, so fish sit under items with the other
    /// fish close by, which is where the engine is asked.
    /// </summary>
    [TestMethod]
    [DataRow("start")]
    [DataRow("wc")]
    public void RelaxIgnoringTheOtherFish_LeavesTheRoomAlone_AlongTheSolution(string levelName)
    {
        Level level = TestCorpus.Instance.LoadLevel(levelName);
        string moves = TestCorpus.Solution(levelName);
        var room = new Room(level);
        var rooms = new List<RoomSnapshot> { room.Capture() };
        foreach (char move in moves)
        {
            room.ApplyMove(move);
            rooms.Add(room.Capture());
        }

        CheckIgnore(level, rooms);
    }

    /// <summary>The same on every state of heavy-shared, where a heavy bar lies across both fish.</summary>
    [TestMethod]
    public void RelaxIgnoringTheOtherFish_LeavesTheRoomAlone_HeavyShared()
    {
        StateGraph graph = ReferenceBfs.For(MicroCorpus.Get("heavy-shared"));
        var rooms = new List<RoomSnapshot>();
        for (int i = 0; i < graph.Count; i++)
        {
            if (graph.Solvable[i] && !graph.Solved[i])
            {
                rooms.Add(graph.States[i]);
            }
        }

        CheckIgnore(graph.Level, rooms);
    }

    private static void CheckIgnore(Level level, List<RoomSnapshot> states)
    {
        LevelReduction reduction = LevelReduction.Compute(level);
        int[] mobile = reduction.MobileModels;
        var router = new InertRouter(level, reduction, exactSupport: true);
        var room = new Room(level);
        int placements = level.Width * level.Height * 2;
        var plain = new ushort[placements];
        var ignoring = new ushort[placements];
        byte[] before = new byte[reduction.StateKeySize];
        byte[] after = new byte[reduction.StateKeySize];
        int[] gridBefore = new int[level.Width * level.Height];
        int[] gridAfter = new int[level.Width * level.Height];
        int widened = 0;

        foreach (RoomSnapshot state in states)
        {
            room.RestoreFrom(state);
            if (level.Units.Length < 2)
            {
                continue;
            }

            foreach (UnitDef unit in level.Units)
            {
                int fish = unit.Model;
                int other = level.Units[0].Model == fish ? level.Units[1].Model : level.Units[0].Model;
                ref readonly ModelState m = ref room.State(fish);
                ref readonly ModelState o = ref room.State(other);
                if (!m.IsAlive || m.IsOut || m.IsLost || !o.IsAlive || o.IsOut || o.IsLost)
                {
                    continue;
                }

                int start = router.Placement(m.X, m.Y, m.IsLeft);
                room.WriteStateKey(before, mobile);
                Grid(room, level, gridBefore);

                FishField.SetPoint(plain, start);
                router.Relax(room, fish, plain);
                FishField.SetPoint(ignoring, start);
                router.Relax(room, fish, ignoring, ignore: other);

                room.WriteStateKey(after, mobile);
                Grid(room, level, gridAfter);
                Assert.IsTrue(before.AsSpan().SequenceEqual(after), $"{level.Models[fish].Kind}: Relax with ignore changed the state key");
                CollectionAssert.AreEqual(gridBefore, gridAfter, $"{level.Models[fish].Kind}: Relax with ignore changed the grid");

                for (int q = 0; q < placements; q++)
                {
                    Assert.IsLessThanOrEqualTo(plain[q], ignoring[q], $"{level.Models[fish].Kind}: ignoring the other fish made placement {q} dearer");
                    if (ignoring[q] < plain[q])
                    {
                        widened++;
                    }
                }
            }
        }

        // Not an assertion: some levels never have the fish in each other's way.
        Console.WriteLine($"{level.Width}x{level.Height}: {states.Count} states, {widened} placements cheaper with the other fish ignored");
    }

    private static void Grid(Room room, Level level, int[] destination)
    {
        for (int y = 0; y < level.Height; y++)
        {
            for (int x = 0; x < level.Width; x++)
            {
                destination[(y * level.Width) + x] = room.GetModel(x, y);
            }
        }
    }

    // ------------------------------------------------------ field primitives --

    /// <summary>A 4x3 room, 24 placements; a 2x1 fish shape ("XX").</summary>
    private const int W = 4, H = 3;

    private static readonly Shape Wide = new("XX");

    private static int P(int x, int y, bool isLeft) => (((y * W) + x) * 2) + (isLeft ? 0 : 1);

    [TestMethod]
    public void Mask_ForbidsEveryAnchorWhoseShapeCoversACell()
    {
        var field = new ushort[W * H * 2];
        field.AsSpan().Fill(5);

        // Cell (2,1): covered by an "XX" anchored at (2,1) or (1,1), either facing.
        FishField.Mask(field, Wide, W, H, [(1 * W) + 2]);

        for (int q = 0; q < field.Length; q++)
        {
            int cell = q >> 1;
            bool hit = cell == (1 * W) + 2 || cell == (1 * W) + 1;
            Assert.AreEqual(hit ? FishField.None : (ushort)5, field[q], $"placement {q}");
        }

        Assert.AreEqual((W * H * 2) - 4, FishField.Count(field));

        // A cell at the left edge: only the anchor on the cell itself is in the room.
        FishField.Mask(field, Wide, W, H, [0]);
        Assert.AreEqual(FishField.None, field[P(0, 0, true)]);
        Assert.AreEqual(FishField.None, field[P(0, 0, false)]);
        Assert.AreEqual((W * H * 2) - 6, FishField.Count(field));
    }

    [TestMethod]
    public void TryCheapest_LowestCostThenLowestId()
    {
        var field = new ushort[W * H * 2];
        FishField.Clear(field);
        Assert.IsFalse(FishField.TryCheapest(field, out _, out _));

        field[P(3, 2, false)] = 4;
        field[P(1, 1, true)] = 2;
        field[P(2, 2, true)] = 2;
        Assert.IsTrue(FishField.TryCheapest(field, out int placement, out int cost));
        Assert.AreEqual(P(1, 1, true), placement);
        Assert.AreEqual(2, cost);

        FishField.SetPoint(field, P(3, 2, true));
        Assert.IsTrue(FishField.TryCheapest(field, out placement, out cost));
        Assert.AreEqual(P(3, 2, true), placement);
        Assert.AreEqual(0, cost);
        Assert.AreEqual(1, FishField.Count(field));
    }

    [TestMethod]
    public void TryFarthest_FarthestThenCheapestThenLowestId()
    {
        var field = new ushort[W * H * 2];
        FishField.Clear(field);
        Assert.IsFalse(FishField.TryFarthest(field, W, 0, 0, out _));

        field[P(1, 0, true)] = 0;
        field[P(3, 1, true)] = 9;   // distance 4
        field[P(2, 2, false)] = 6;  // distance 4, cheaper
        field[P(2, 2, true)] = 6;   // distance 4, as cheap, lower id
        Assert.IsTrue(FishField.TryFarthest(field, W, 0, 0, out int placement));
        Assert.AreEqual(P(2, 2, true), placement);

        Assert.IsTrue(FishField.TryFarthest(field, W, 3, 2, out placement));
        Assert.AreEqual(P(1, 0, true), placement);
    }

    [TestMethod]
    public void MinPlus_CountsTheFieldAndTheWayOut()
    {
        // fish_small on a micro-level: its exit table is real, the field made up.
        Level level = MicroCorpus.Get("identical-blocks").Build();
        LevelReduction reduction = LevelReduction.Compute(level);
        var heuristic = new ExitHeuristic(level, reduction);
        int fish = level.Units[0].Model;
        RelaxedDistance? exit = heuristic.FishExit(fish);
        Assert.IsNotNull(exit);

        var router = new InertRouter(level, reduction, exactSupport: true);
        var field = new ushort[level.Width * level.Height * 2];
        FishField.Clear(field);
        Assert.AreEqual(RelaxedDistance.Unreachable, FishField.MinPlus(field, level.Width, exit));

        var room = new Room(level);
        (int x, int y, bool isLeft) = room.Position(fish);
        int here = router.Placement(x, y, isLeft);
        field[here] = 3;
        Assert.AreEqual(3 + exit.At(x, y, isLeft), FishField.MinPlus(field, level.Width, exit));

        // Brute force over a relaxed field.
        FishField.SetPoint(field, here);
        router.Relax(room, fish, field);
        int want = RelaxedDistance.Unreachable;
        for (int q = 0; q < field.Length; q++)
        {
            if (field[q] == FishField.None)
            {
                continue;
            }

            (int qx, int qy, bool qLeft) = router.Decode(q);
            int d = exit.At(qx, qy, qLeft);
            if (d < RelaxedDistance.Unreachable)
            {
                want = Math.Min(want, field[q] + d);
            }
        }

        Assert.AreEqual(want, FishField.MinPlus(field, level.Width, exit));
        Assert.IsLessThanOrEqualTo(exit.At(x, y, isLeft), want, "standing still is one of the options");

        // One fish and no goal items: the item bound is 0 and Estimate is that fish's distance.
        Assert.AreEqual(0, heuristic.ItemBound(room));
        Assert.AreEqual(heuristic.Estimate(room), Math.Max(exit.At(x, y, isLeft), heuristic.ItemBound(room)));
        Assert.IsNull(heuristic.FishExit(level.MutableModels.First(i => !level.Models[i].IsAlive)));
    }

    [TestMethod]
    public void FieldCache_EvictsTheLeastRecentlyUsed()
    {
        var cache = new FieldCache(capacity: 2, fieldLength: 3);
        ushort[] a = cache.Rent(10);
        a[0] = 1;
        ushort[] b = cache.Rent(20);
        b[0] = 2;

        Assert.IsTrue(cache.TryGet(10, out ushort[] got));   // 10 is now the most recent
        Assert.AreSame(a, got);
        Assert.AreSame(a, cache.Rent(10), "renting a present key returns its own array");

        ushort[] c = cache.Rent(30);                           // evicts 20
        Assert.AreSame(b, c, "the evicted array is reused");
        Assert.HasCount(3, c);
        Assert.IsFalse(cache.TryGet(20, out _));
        Assert.IsTrue(cache.TryGet(10, out _));
        Assert.IsTrue(cache.TryGet(30, out _));
        Assert.AreEqual(2, cache.Count);

        cache.Rent(40);                                        // evicts 10 (30 was touched last)
        Assert.IsFalse(cache.TryGet(10, out _));
        Assert.IsTrue(cache.TryGet(30, out _));

        cache.Clear();
        Assert.AreEqual(0, cache.Count);
        Assert.IsFalse(cache.TryGet(30, out _));
        ushort[] reused = cache.Rent(50);
        Assert.IsTrue(ReferenceEquals(reused, a) || ReferenceEquals(reused, b), "arrays survive Clear");
    }
}
