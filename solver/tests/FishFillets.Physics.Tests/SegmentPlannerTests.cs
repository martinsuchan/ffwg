using System.Diagnostics;

using FishFillets.Physics.Tests.Micro;
using FishFillets.Search;

namespace FishFillets.Physics.Tests;

/// <summary>
/// <see cref="SegmentPlanner"/>: the joint BFS of two fish travelling inertly in a
/// frozen layout, and the cheap-orders-first composition built on it.
///
/// <para>The property that matters most is that every plan <b>replays</b>: each
/// symbol is accepted by the real engine and nothing but the two fish ever moves.
/// The planner decides legality with <see cref="InertRouter.IsFreeStep"/> on a room
/// stamped with both fish, so this is what catches a planner that tests a step
/// against the wrong arrangement of the other fish.</para>
///
/// <para>Optimality is checked against an independent brute force over
/// <see cref="Room.ApplyMove"/> - every symbol of both fish, keeping only moves
/// that leave everything but the fish untouched. It is only run on rooms with no
/// way out: <see cref="InertRouter.MightEscapeFrom"/> is deliberately generous,
/// so on an open room the brute force could pass through border placements the
/// planner (rightly, for a search) treats as events.</para>
/// </summary>
[TestClass]
public sealed class SegmentPlannerTests
{
    /// <summary>
    /// A sealed corridor the big fish blocks, with a bay above it the big fish
    /// can step into. The small fish can only get to the right end if the big one
    /// gets out of its way first.
    /// </summary>
    private const string Corridor = """
        # expect: none
        # s: fish_small >
        # b: fish_big <
        XXXXXXXXXXXXXXXXXX
        XXXXXXX....XXXXXXX
        XXXXXXX....XXXXXXX
        X.sss...bbbb.....X
        X.......bbbb.....X
        XXXXXXXXXXXXXXXXXX
        """;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Joint_AgreesWithSingleFishRouting_WhenTheFishDoNotInteract()
    {
        Level level = TestCorpus.Instance.LoadLevel("start");
        var fixture = new Fixture(level);

        int checkedTargets = 0;
        for (int unitA = 0; unitA < 2; unitA++)
        {
            int unitB = 1 - unitA;
            int fishA = level.Units[unitA].Model, fishB = level.Units[unitB].Model;
            int startB = fixture.PlacementOf(fishB);

            var solid = new InertRouter(level, fixture.Reduction, exactSupport: true);
            var ignoring = new InertRouter(level, fixture.Reduction, exactSupport: true);
            fixture.Reset();
            solid.Route(fixture.Room, fishA);
            ignoring.Route(fixture.Room, fishA, ignore: fishB);
            var ignoredReach = ignoring.Reached.ToHashSet();

            bool[] allowedB = new bool[fixture.PlacementCount];
            allowedB[startB] = true;

            // Only targets B does not stand in the way of: there a detour round
            // B could still be beaten by B stepping aside and back.
            IReadOnlyList<int> reached = solid.Reached;
            for (int i = 1; i < reached.Count; i += Math.Max(1, reached.Count / 12))
            {
                int target = reached[i];
                if (!ignoredReach.Contains(target) || ignoring.CostOf(target) != solid.CostOf(target))
                {
                    continue;
                }

                var moves = new List<char>();
                bool found = fixture.Planner.TryJoint(fixture.Room, unitA, target, unitB, allowedB, out int cost, out int endB, moves);

                Assert.IsTrue(found, $"unit {unitA} to {target}");
                Assert.AreEqual(solid.CostOf(target), cost, $"unit {unitA} to {target}");
                Assert.AreEqual(startB, endB);
                Assert.HasCount(cost, moves);
                fixture.AssertUntouched();
                fixture.AssertReplays(moves, unitA, target, unitB, endB);
                checkedTargets++;
            }
        }

        Assert.IsGreaterThanOrEqualTo(10, checkedTargets, "too few targets checked");
    }

    [TestMethod]
    public void Joint_InterleavesWhenTheOtherFishIsInTheWay()
    {
        var fixture = new Fixture(MicroLevel.Parse("corridor", Corridor).Build());
        (int unitA, int unitB) = fixture.Units("fish_small", "fish_big");
        int fishA = fixture.Level.Units[unitA].Model, fishB = fixture.Level.Units[unitB].Model;
        int toA = fixture.Router.Placement(13, 3, isLeft: false);

        // The precondition, so the test cannot go vacuous: alone, A is blocked;
        // with B out of the picture, A walks straight there.
        var router = new InertRouter(fixture.Level, fixture.Reduction, exactSupport: true);
        router.Route(fixture.Room, fishA);
        Assert.IsFalse(router.Reached.Contains(toA), "B should block the corridor");
        router.Route(fixture.Room, fishA, ignore: fishB);
        Assert.IsTrue(router.Reached.Contains(toA), "A should reach the end with B ignored");
        int ignoredCost = router.CostOf(toA);

        bool[] anywhere = new bool[fixture.PlacementCount];
        Array.Fill(anywhere, true);

        var moves = new List<char>();
        var stopwatch = Stopwatch.StartNew();
        bool found = fixture.Planner.TryJoint(fixture.Room, unitA, toA, unitB, anywhere, out int cost, out int endB, moves);
        stopwatch.Stop();
        TestContext.WriteLine(
            $"corridor: cost {cost}, {fixture.Planner.LastStatesPopped} states popped, " +
            $"{stopwatch.Elapsed.TotalMilliseconds:F2} ms, moves {new string([.. moves])}");

        Assert.IsTrue(found);
        Assert.HasCount(cost, moves);
        Assert.IsGreaterThanOrEqualTo(ignoredCost, cost, "cheaper than with B out of the room");
        Assert.AreEqual(14, cost, "B steps into the bay (3), A swims along (11)");
        Assert.AreEqual(BruteForce(fixture, unitA, toA, unitB, anywhere), cost, "not optimal");
        fixture.AssertUntouched();
        fixture.AssertReplays(moves, unitA, toA, unitB, endB);
    }

    /// <param name="denseLimit">0 runs the hash-set visited set the big rooms use.</param>
    [TestMethod]
    [DataRow(1L << 23)]
    [DataRow(0L)]
    public void Joint_IsOptimal_InEveryTargetOfTheCorridor(long denseLimit)
    {
        var fixture = new Fixture(MicroLevel.Parse("corridor", Corridor).Build(), denseLimit);

        int interleaved = 0;
        foreach ((int unitA, int unitB) in new[] { fixture.Units("fish_small", "fish_big"), fixture.Units("fish_big", "fish_small") })
        {
            int fishA = fixture.Level.Units[unitA].Model, fishB = fixture.Level.Units[unitB].Model;

            // B has to end where it started: the strictest goal, and the one
            // that forces it to step aside and come back.
            bool[] home = new bool[fixture.PlacementCount];
            home[fixture.PlacementOf(fishB)] = true;

            var router = new InertRouter(fixture.Level, fixture.Reduction, exactSupport: true);
            router.Route(fixture.Room, fishA, ignore: fishB);
            int[] targets = [.. router.Reached];

            foreach (int toA in targets)
            {
                var moves = new List<char>();
                bool found = fixture.Planner.TryJoint(fixture.Room, unitA, toA, unitB, home, out int cost, out int endB, moves);
                int expected = BruteForce(fixture, unitA, toA, unitB, home);
                fixture.AssertUntouched();

                Assert.AreEqual(expected, found ? cost : -1, $"unit {unitA} to {fixture.Router.Decode(toA)}");
                if (found)
                {
                    fixture.AssertReplays(moves, unitA, toA, unitB, endB);
                    interleaved += cost > router.CostOf(toA) ? 1 : 0;
                }
            }
        }

        Assert.IsGreaterThan(0, interleaved, "some target should need B to move and come back");
    }

    [TestMethod]
    public void Compose_UsesTheCheapOrder_WhenOneWorks()
    {
        var fixture = new Fixture(MicroLevel.Parse("corridor", Corridor).Build());
        (int unitA, int unitB) = fixture.Units("fish_small", "fish_big");
        int toA = fixture.Router.Placement(13, 3, isLeft: false);
        int bay = fixture.Router.Placement(7, 1, isLeft: true);

        var moves = new List<char> { '#' }; // appends, never replaces
        Assert.IsTrue(fixture.Planner.TryCompose(fixture.Room, unitA, toA, unitB, bay, moves));
        fixture.AssertUntouched();
        Assert.AreEqual('#', moves[0]);
        moves.RemoveAt(0);

        Assert.HasCount(14, moves, new string([.. moves]));
        fixture.AssertReplays(moves, unitA, toA, unitB, bay);
    }

    [TestMethod]
    public void Compose_FallsBackToTheJointSearch_WhenNeitherOrderWorks()
    {
        var fixture = new Fixture(MicroLevel.Parse("corridor", Corridor).Build());
        (int unitA, int unitB) = fixture.Units("fish_small", "fish_big");
        int toA = fixture.Router.Placement(13, 3, isLeft: false);
        int homeB = fixture.PlacementOf(fixture.Level.Units[unitB].Model);

        // B has to be back where it started: neither "B, then A" (B is already
        // there) nor "A, then B" (A is blocked) gets A past.
        var moves = new List<char>();
        Assert.IsTrue(fixture.Planner.TryCompose(fixture.Room, unitA, toA, unitB, homeB, moves));
        fixture.AssertUntouched();

        bool[] home = new bool[fixture.PlacementCount];
        home[homeB] = true;
        Assert.HasCount(BruteForce(fixture, unitA, toA, unitB, home), moves, new string([.. moves]));
        fixture.AssertReplays(moves, unitA, toA, unitB, homeB);
    }

    [TestMethod]
    public void Compose_ReportsImpossible_AndLeavesTheRoom()
    {
        var fixture = new Fixture(MicroLevel.Parse("corridor", Corridor).Build());
        (int unitA, int unitB) = fixture.Units("fish_small", "fish_big");

        // Inside the wall: no plan at all, after exhausting the joint space.
        int wall = fixture.Router.Placement(1, 1, isLeft: true);
        var moves = new List<char>();
        Assert.IsFalse(fixture.Planner.TryCompose(fixture.Room, unitA, wall, unitB, fixture.PlacementOf(fixture.Level.Units[unitB].Model), moves));
        Assert.IsEmpty(moves);
        fixture.AssertUntouched();

        bool[] anywhere = new bool[fixture.PlacementCount];
        Array.Fill(anywhere, true);
        Assert.IsFalse(fixture.Planner.TryJoint(fixture.Room, unitA, wall, unitB, anywhere, out int cost, out int endB, moves));
        Assert.AreEqual(-1, cost);
        Assert.AreEqual(-1, endB);
        Assert.IsEmpty(moves);
        fixture.AssertUntouched();
    }

    [TestMethod]
    public void Joint_RespectsTheStateCap()
    {
        var fixture = new Fixture(MicroLevel.Parse("corridor", Corridor).Build());
        (int unitA, int unitB) = fixture.Units("fish_small", "fish_big");
        int toA = fixture.Router.Placement(13, 3, isLeft: false);

        bool[] anywhere = new bool[fixture.PlacementCount];
        Array.Fill(anywhere, true);

        var moves = new List<char>();
        Assert.IsFalse(fixture.Planner.TryJoint(fixture.Room, unitA, toA, unitB, anywhere, out _, out _, moves, maxStates: 1));
        Assert.IsEmpty(moves);
        Assert.AreEqual(1, fixture.Planner.LastStatesPopped);
        fixture.AssertUntouched();

        // And the same call without the cap still works afterwards.
        Assert.IsTrue(fixture.Planner.TryJoint(fixture.Room, unitA, toA, unitB, anywhere, out int cost, out _, moves));
        Assert.AreEqual(14, cost);
        fixture.AssertUntouched();
    }

    [TestMethod]
    public void Joint_StartThatIsAlreadyTheGoal_CostsNothing()
    {
        var fixture = new Fixture(MicroLevel.Parse("corridor", Corridor).Build());
        (int unitA, int unitB) = fixture.Units("fish_small", "fish_big");
        int startA = fixture.PlacementOf(fixture.Level.Units[unitA].Model);
        int startB = fixture.PlacementOf(fixture.Level.Units[unitB].Model);

        bool[] home = new bool[fixture.PlacementCount];
        home[startB] = true;

        var moves = new List<char>();
        Assert.IsTrue(fixture.Planner.TryJoint(fixture.Room, unitA, startA, unitB, home, out int cost, out int endB, moves));
        Assert.AreEqual(0, cost);
        Assert.AreEqual(startB, endB);
        Assert.IsEmpty(moves);
        Assert.IsTrue(fixture.Planner.TryCompose(fixture.Room, unitA, startA, unitB, startB, moves));
        Assert.IsEmpty(moves);
        fixture.AssertUntouched();
    }

    /// <summary>
    /// <c>heavy-shared</c>: a heavy bar across both fish, where support changing
    /// hands kills the small one. Every plan from the start must replay, which
    /// only holds if each step is tested against the room with the other fish
    /// where the plan has it at that moment.
    /// </summary>
    [TestMethod]
    public void Joint_Replays_UnderASharedHeavyBar()
    {
        var fixture = new Fixture(MicroCorpus.Get("heavy-shared").Build());
        (int small, int big) = fixture.Units("fish_small", "fish_big");

        bool[] anywhere = new bool[fixture.PlacementCount];
        Array.Fill(anywhere, true);

        int plans = 0;
        foreach ((int unitA, int unitB) in new[] { (small, big), (big, small) })
        {
            int fishA = fixture.Level.Units[unitA].Model, fishB = fixture.Level.Units[unitB].Model;
            var router = new InertRouter(fixture.Level, fixture.Reduction, exactSupport: true);
            router.Route(fixture.Room, fishA, ignore: fishB);
            var targets = router.Reached.ToHashSet();
            router.Route(fixture.Room, fishA);
            targets.UnionWith(router.Reached);

            foreach (int toA in targets)
            {
                var moves = new List<char>();
                if (fixture.Planner.TryJoint(fixture.Room, unitA, toA, unitB, anywhere, out int cost, out int endB, moves))
                {
                    Assert.HasCount(cost, moves);
                    fixture.AssertReplays(moves, unitA, toA, unitB, endB);
                    plans++;
                }

                fixture.AssertUntouched();
            }
        }

        Assert.IsGreaterThan(20, plans, "too few plans");

        // And the refusal: three steps right the big fish no longer reaches
        // under the bar. Then either the bar rests on the small fish alone, which
        // crushes it, or on nothing, and it falls - wherever the small fish goes
        // first, there is no inert way there. Two steps right still is one.
        int bigFish = fixture.Level.Units[big].Model, smallFish = fixture.Level.Units[small].Model;
        (int bx, int by, bool bLeft) = fixture.Room.Position(bigFish);
        bool[] home = new bool[fixture.PlacementCount];
        home[fixture.PlacementOf(smallFish)] = true;

        int crushing = fixture.Router.Placement(bx + 3, by, bLeft);
        Assert.IsFalse(fixture.Planner.TryJoint(fixture.Room, big, crushing, small, anywhere, out _, out _));
        Assert.AreEqual(-1, BruteForce(fixture, big, crushing, small, anywhere), "the oracle disagrees");

        int sharing = fixture.Router.Placement(bx + 2, by, bLeft);
        var plan = new List<char>();
        Assert.IsTrue(fixture.Planner.TryJoint(fixture.Room, big, sharing, small, home, out int sharedCost, out int sharedEnd, plan));
        Assert.AreEqual(BruteForce(fixture, big, sharing, small, home), sharedCost, "the oracle disagrees");
        fixture.AssertReplays(plan, big, sharing, small, sharedEnd);
        fixture.AssertUntouched();
    }

    /// <summary>
    /// Interacting plans on a real level: along <c>start</c>'s reference
    /// solution, every state where B stands between A and somewhere A could
    /// otherwise reach. Each plan must replay; the timing is logged for the record.
    /// </summary>
    [TestMethod]
    public void Joint_Replays_OnACorpusLevel()
    {
        Level level = TestCorpus.Instance.LoadLevel("start");
        var fixture = new Fixture(level);
        int[] mobile = fixture.Reduction.MobileModels;
        string solution = TestCorpus.Solution("start");

        bool[] anywhere = new bool[fixture.PlacementCount];
        Array.Fill(anywhere, true);

        var walker = new Room(level);
        byte[] key = new byte[fixture.Reduction.StateKeySize];
        var router = new InertRouter(level, fixture.Reduction, exactSupport: true);

        int plans = 0, calls = 0;
        long popped = 0;
        double milliseconds = 0;
        for (int step = 0; step <= solution.Length && calls < 40; step++)
        {
            if (step > 0)
            {
                Assert.IsTrue(walker.ApplyMove(solution[step - 1]));
            }

            if (walker.IsSolved() || !walker.State(level.Units[0].Model).IsAlive || !walker.State(level.Units[1].Model).IsAlive
                || walker.State(level.Units[0].Model).IsOut || walker.State(level.Units[1].Model).IsOut)
            {
                continue;
            }

            walker.WriteStateKey(key, mobile);
            fixture.SetStart(key);

            for (int unitA = 0; unitA < 2 && calls < 40; unitA++)
            {
                int unitB = 1 - unitA;
                int fishA = level.Units[unitA].Model, fishB = level.Units[unitB].Model;
                router.Route(fixture.Room, fishA);
                var solid = router.Reached.ToHashSet();
                router.Route(fixture.Room, fishA, ignore: fishB);

                // Only where B is in the way: those are the calls the solver makes.
                foreach (int toA in router.Reached.Where(p => !solid.Contains(p)).Take(2))
                {
                    var moves = new List<char>();
                    var stopwatch = Stopwatch.StartNew();
                    bool found = fixture.Planner.TryJoint(fixture.Room, unitA, toA, unitB, anywhere, out int cost, out int endB, moves);
                    stopwatch.Stop();
                    calls++;
                    popped += fixture.Planner.LastStatesPopped;
                    milliseconds += stopwatch.Elapsed.TotalMilliseconds;
                    TestContext.WriteLine(
                        $"start step {step} unit {unitA} to {fixture.Router.Decode(toA)}: {(found ? $"cost {cost}" : "none")}, " +
                        $"{fixture.Planner.LastStatesPopped} popped, {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
                    fixture.AssertUntouched();

                    if (found)
                    {
                        fixture.AssertReplays(moves, unitA, toA, unitB, endB);
                        plans++;
                    }
                }
            }
        }

        TestContext.WriteLine(
            $"{calls} interacting calls, {plans} plans replayed, {popped / Math.Max(1, calls)} popped and " +
            $"{milliseconds / Math.Max(1, calls):F2} ms per call");
        Assert.IsGreaterThan(0, plans, "no interacting case found along the solution");
    }

    /// <summary>
    /// The independent oracle: BFS over full state keys, applying every symbol of
    /// both fish with the real engine and keeping only moves after which nothing
    /// but the two fish's placements differs from the start. -1 if no plan.
    /// </summary>
    private static int BruteForce(Fixture fixture, int unitA, int toA, int unitB, bool[] allowedB)
    {
        Level level = fixture.Level;
        int[] mobile = fixture.Reduction.MobileModels;
        int slotA = Array.IndexOf(mobile, level.Units[unitA].Model);
        int slotB = Array.IndexOf(mobile, level.Units[unitB].Model);
        byte[] start = fixture.StartKey;
        var router = fixture.Router;

        char[] symbols =
        [
            level.Units[unitA].Up, level.Units[unitA].Down, level.Units[unitA].Left, level.Units[unitA].Right,
            level.Units[unitB].Up, level.Units[unitB].Down, level.Units[unitB].Left, level.Units[unitB].Right,
        ];

        var room = new Room(level);
        var seen = new HashSet<string> { Convert.ToHexString(start) };
        var frontier = new List<byte[]> { start };
        byte[] after = new byte[start.Length];
        byte[] normalised = new byte[start.Length];

        for (int depth = 0; frontier.Count > 0; depth++)
        {
            var next = new List<byte[]>();
            foreach (byte[] key in frontier)
            {
                (int ax, int ay, bool aLeft) = StateKeyEdit.GetPlacement(key, slotA);
                (int bx, int by, bool bLeft) = StateKeyEdit.GetPlacement(key, slotB);
                if (router.Placement(ax, ay, aLeft) == toA && allowedB[router.Placement(bx, by, bLeft)])
                {
                    return depth;
                }

                foreach (char symbol in symbols)
                {
                    room.RestoreMobile(key, mobile);
                    if (!room.ApplyMove(symbol))
                    {
                        continue;
                    }

                    room.WriteStateKey(after, mobile);

                    // Inert: put both fish back where they started, and nothing
                    // else may differ.
                    after.CopyTo(normalised, 0);
                    (int nax, int nay, bool naLeft) = StateKeyEdit.GetPlacement(after, slotA);
                    (int nbx, int nby, bool nbLeft) = StateKeyEdit.GetPlacement(after, slotB);
                    (int sax, int say, bool saLeft) = StateKeyEdit.GetPlacement(start, slotA);
                    (int sbx, int sby, bool sbLeft) = StateKeyEdit.GetPlacement(start, slotB);
                    StateKeyEdit.SetPlacement(normalised, slotA, sax, say, saLeft);
                    StateKeyEdit.SetPlacement(normalised, slotB, sbx, sby, sbLeft);
                    if (!normalised.AsSpan().SequenceEqual(start)
                        || (uint)nax >= (uint)level.Width || (uint)nay >= (uint)level.Height
                        || (uint)nbx >= (uint)level.Width || (uint)nby >= (uint)level.Height)
                    {
                        continue;
                    }

                    if (seen.Add(Convert.ToHexString(after)))
                    {
                        next.Add((byte[])after.Clone());
                    }
                }
            }

            frontier = next;
        }

        return -1;
    }

    /// <summary>One level's planner, a room at its start, and the checks every test applies.</summary>
    private sealed class Fixture
    {
        public Fixture(Level level, long denseLimit = 1L << 23)
        {
            Level = level;
            Reduction = LevelReduction.Compute(level);
            Planner = new SegmentPlanner(level, Reduction, denseLimit);
            Router = new InertRouter(level, Reduction, exactSupport: true);
            Room = new Room(level);
            StartKey = new byte[Reduction.StateKeySize];
            Room.WriteStateKey(StartKey, Reduction.MobileModels);
        }

        /// <summary>Moves the fixture to another settled state of the same level.</summary>
        public void SetStart(byte[] key)
        {
            key.CopyTo(StartKey, 0);
            Reset();
        }

        public Level Level { get; }

        public LevelReduction Reduction { get; }

        public SegmentPlanner Planner { get; }

        /// <summary>For encoding placements only.</summary>
        public InertRouter Router { get; }

        public Room Room { get; }

        public byte[] StartKey { get; }

        public int PlacementCount => Level.Width * Level.Height * 2;

        public void Reset() => Room.RestoreMobile(StartKey, Reduction.MobileModels);

        public int PlacementOf(int fish)
        {
            (int x, int y, bool isLeft) = Room.Position(fish);
            return Router.Placement(x, y, isLeft);
        }

        public (int A, int B) Units(string kindA, string kindB) =>
            (UnitOf(kindA), UnitOf(kindB));

        private int UnitOf(string kind)
        {
            for (int u = 0; u < Level.Units.Length; u++)
            {
                if (Level.Models[Level.Units[u].Model].Kind == kind)
                {
                    return u;
                }
            }

            throw new InvalidOperationException($"no {kind} in {Level.Name}");
        }

        /// <summary>The planner must leave the room exactly as it found it.</summary>
        public void AssertUntouched()
        {
            byte[] now = new byte[StartKey.Length];
            Room.WriteStateKey(now, Reduction.MobileModels);
            CollectionAssert.AreEqual(StartKey, now, "the planner left the room changed");
        }

        /// <summary>
        /// Plays the symbols with the real engine from a fresh copy of the start:
        /// each must be accepted and change nothing but the two fish, and they must
        /// end at (<paramref name="toA"/>, <paramref name="endB"/>).
        /// </summary>
        public void AssertReplays(List<char> moves, int unitA, int toA, int unitB, int endB)
        {
            int[] mobile = Reduction.MobileModels;
            int slotA = Array.IndexOf(mobile, Level.Units[unitA].Model);
            int slotB = Array.IndexOf(mobile, Level.Units[unitB].Model);
            (int sax, int say, bool saLeft) = StateKeyEdit.GetPlacement(StartKey, slotA);
            (int sbx, int sby, bool sbLeft) = StateKeyEdit.GetPlacement(StartKey, slotB);

            var room = new Room(Level);
            room.RestoreMobile(StartKey, mobile);
            byte[] key = new byte[StartKey.Length];

            string text = new([.. moves]);
            for (int i = 0; i < moves.Count; i++)
            {
                Assert.IsTrue(room.ApplyMove(moves[i]), $"'{moves[i]}' at {i} of {text} is refused");

                room.WriteStateKey(key, mobile);
                StateKeyEdit.SetPlacement(key, slotA, sax, say, saLeft);
                StateKeyEdit.SetPlacement(key, slotB, sbx, sby, sbLeft);
                CollectionAssert.AreEqual(StartKey, key, $"'{moves[i]}' at {i} of {text} changed more than the fish");
            }

            byte[] expected = (byte[])StartKey.Clone();
            (int ax, int ay, bool aLeft) = Router.Decode(toA);
            (int bx, int by, bool bLeft) = Router.Decode(endB);
            StateKeyEdit.SetPlacement(expected, slotA, ax, ay, aLeft);
            StateKeyEdit.SetPlacement(expected, slotB, bx, by, bLeft);

            room.WriteStateKey(key, mobile);
            CollectionAssert.AreEqual(expected, key, $"{text} does not end where the plan says");
        }
    }
}
