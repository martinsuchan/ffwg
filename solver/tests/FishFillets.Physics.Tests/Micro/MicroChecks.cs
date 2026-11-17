using System.Text;

using FishFillets.Search;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>
/// The checks themselves, as plain functions returning what went wrong, so the
/// curated suite (<see cref="MicroTests"/>) and the fuzzer
/// (<see cref="MicroFuzzer"/>) apply exactly the same rules.
/// </summary>
internal static class MicroChecks
{
    /// <summary>
    /// A violation, and the open finding it is explained by (null: unexplained -
    /// a new bug). Every finding from the review is fixed, so nothing attributes
    /// today; when a new bug is found and left open, give its check a cause here
    /// so the fuzzer can keep counting it without failing.
    /// </summary>
    internal sealed record Violation(string Message, string? Cause);

    /// <summary>
    /// Matrix cell: the variant held to its promise. <paramref name="requireSolved"/>
    /// makes a Valid variant fail when it returns nothing on a solvable level -
    /// right for curated levels, wrong for random ones, where the incomplete
    /// decompositions are expected to miss.
    /// </summary>
    public static string? Matrix(Level level, int? optimum, MicroVariant variant, SolveResult result, bool requireSolved)
    {
        string where = $"{variant.Id}: {result.Status}, expanded {result.Expanded:N0}";

        if (result.Solved && !SolutionValidator.Validate(new Room(level), result.Moves).Solved)
        {
            return $"{where}: the returned solution does not replay";
        }

        if (optimum is null)
        {
            if (result.Solved)
            {
                return $"{where}: claims a solution to an unsolvable level";
            }

            if (variant.Promise == Promise.Optimal
                && !result.Status.StartsWith("exhausted", StringComparison.Ordinal)
                && !result.Status.StartsWith("unsolvable", StringComparison.Ordinal))
            {
                return $"{where}: a complete search should prove there is no solution";
            }

            return null;
        }

        if (!result.Solved)
        {
            return variant.Promise == Promise.Valid && !requireSolved ? null : $"{where}: no solution, the optimum is {optimum}";
        }

        int length = result.Moves!.Length;
        if (length < optimum)
        {
            return $"{where}: {length} moves is shorter than the proven optimum {optimum}";
        }

        return variant.Promise switch
        {
            Promise.Optimal when length != optimum => $"{where}: optimum is {optimum}, got {length}",
            Promise.Optimal when !result.Optimal => $"{where}: right length but not claimed optimal",
            Promise.Bounded when length > (int)Math.Floor(variant.Weight * optimum.Value) =>
                $"{where}: {length} moves exceeds weight {variant.Weight} x optimum {optimum}",
            _ => null,
        };
    }

    /// <summary>h(s) &lt;= d*(s) in every state that can still be finished.</summary>
    public static List<Violation> Admissible(StateGraph graph)
    {
        var reduction = LevelReduction.Compute(graph.Level);
        var heuristic = new ExitHeuristic(graph.Level, reduction);
        var room = new Room(graph.Level);
        var violations = new List<Violation>();

        for (int i = 0; i < graph.Count; i++)
        {
            if (!graph.Solvable[i] || graph.Solved[i] || graph.DistanceToGoal[i] == StateGraph.Infinite)
            {
                continue;
            }

            room.RestoreFrom(graph.States[i]);
            int h = heuristic.Estimate(room);
            if (h > graph.DistanceToGoal[i])
            {
                violations.Add(new($"h={h} > d*={graph.DistanceToGoal[i]} at {Describe(room)}", null));
            }
        }

        return violations;
    }

    /// <summary>h(s) &lt;= h(t) + 1 along every edge between living states.</summary>
    public static List<Violation> Consistent(StateGraph graph)
    {
        var reduction = LevelReduction.Compute(graph.Level);
        var heuristic = new ExitHeuristic(graph.Level, reduction);
        var room = new Room(graph.Level);

        var h = new int[graph.Count];
        for (int i = 0; i < graph.Count; i++)
        {
            room.RestoreFrom(graph.States[i]);
            // A finished room is queued with h = 0, whatever the estimate says (Solver.Solve).
            h[i] = graph.Solved[i] ? 0 : heuristic.Estimate(room);
        }

        var violations = new List<Violation>();
        for (int s = 0; s < graph.Count; s++)
        {
            foreach ((char symbol, int t) in graph.Edges[s])
            {
                if (!graph.Solvable[t] || h[s] >= RelaxedDistance.Unreachable || h[t] >= RelaxedDistance.Unreachable)
                {
                    continue;
                }

                if (h[s] > h[t] + 1)
                {
                    room.RestoreFrom(graph.States[s]);
                    violations.Add(new($"'{symbol}': h {h[s]} -> {h[t]} from {Describe(room)}", null));
                }
            }
        }

        return violations;
    }

    /// <summary>No frozen model moves in any generated state (pruned ones included).</summary>
    public static List<Violation> Reduction(StateGraph graph)
    {
        LevelReduction reduction = LevelReduction.Compute(graph.Level);
        var room = new Room(graph.Level);
        room.RestoreFrom(graph.States[0]);
        var start = reduction.FrozenModels.ToDictionary(m => m, m => (room.State(m).X, room.State(m).Y));

        var violations = new List<Violation>();
        for (int i = 0; i < graph.Count; i++)
        {
            room.RestoreFrom(graph.States[i]);
            foreach (int model in reduction.FrozenModels)
            {
                ref readonly ModelState m = ref room.State(model);
                if ((m.X, m.Y) != start[model] || m.IsOut || m.IsLost)
                {
                    violations.Add(new($"frozen model {model} moved to ({m.X},{m.Y}) at {Describe(room)}", null));
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// The exact-support router (<c>ItemSolver</c>'s) agrees with the engine, in
    /// every state that can still be finished and for every fish:
    /// <list type="bullet">
    /// <item>routing leaves the room untouched;</item>
    /// <item>a step it calls travel changes nothing but that fish - so building
    ///   the arrival state by rewriting the fish's entry in the key is right.
    ///   Checked one step at a time from the rewritten predecessor, which covers
    ///   whole routes by induction;</item>
    /// <item>a step it reports as a drop is a legal move that does disturb
    ///   something: an item moves, or a fish is crushed.</item>
    /// </list>
    /// </summary>
    public static List<Violation> ExactRouter(StateGraph graph)
    {
        Level level = graph.Level;
        LevelReduction reduction = LevelReduction.Compute(level);
        int[] mobile = reduction.MobileModels;
        var router = new InertRouter(level, reduction, exactSupport: true);
        var room = new Room(level);

        byte[] parent = new byte[reduction.StateKeySize];
        byte[] from = new byte[reduction.StateKeySize];
        byte[] expected = new byte[reduction.StateKeySize];
        byte[] actual = new byte[reduction.StateKeySize];

        var violations = new List<Violation>();
        for (int i = 0; i < graph.Count && violations.Count < 20; i++)
        {
            if (!graph.Solvable[i] || graph.Solved[i])
            {
                continue;
            }

            room.RestoreFrom(graph.States[i]);
            room.WriteStateKey(parent, mobile);

            foreach (UnitDef unit in level.Units)
            {
                int fish = unit.Model;
                int slot = Array.IndexOf(mobile, fish);
                room.RestoreMobile(parent, mobile);
                if (slot < 0 || !room.State(fish).IsAlive || room.State(fish).IsLost)
                {
                    continue;
                }

                router.Route(room, fish);
                room.WriteStateKey(actual, mobile);
                if (!actual.AsSpan().SequenceEqual(parent))
                {
                    violations.Add(new($"routing {level.Models[fish].Kind} changed the room at {Describe(room)}", null));
                    continue;
                }

                foreach (int placement in router.Reached)
                {
                    int before = router.PredecessorOf(placement);
                    if (before < 0)
                    {
                        continue;
                    }

                    MoveFish(parent, from, slot, router.Decode(before));
                    MoveFish(parent, expected, slot, router.Decode(placement));
                    char symbol = StepSymbol(unit, router.Decode(before), router.Decode(placement));

                    room.RestoreMobile(from, mobile);
                    bool moved = room.ApplyMove(symbol);
                    room.WriteStateKey(actual, mobile);
                    if (!moved || !actual.AsSpan().SequenceEqual(expected))
                    {
                        room.RestoreMobile(from, mobile);
                        violations.Add(new(
                            $"'{symbol}' was routed as travel but {(moved ? "changed more than the fish" : "is refused")} at {Describe(room)}",
                            null));
                    }
                }

                foreach ((int placement, int dx, int dy) in router.Drops)
                {
                    (int x, int y, bool isLeft) = router.Decode(placement);
                    MoveFish(parent, from, slot, (x, y, isLeft));
                    MoveFish(parent, expected, slot, (x + dx, y + dy, isLeft));
                    char symbol = StepSymbol(unit, (x, y, isLeft), (x + dx, y + dy, isLeft));

                    room.RestoreMobile(from, mobile);
                    bool moved = room.ApplyMove(symbol);
                    room.WriteStateKey(actual, mobile);
                    if (!moved || actual.AsSpan().SequenceEqual(expected))
                    {
                        room.RestoreMobile(from, mobile);
                        violations.Add(new(
                            $"'{symbol}' was reported as a drop but {(moved ? "disturbed nothing" : "is refused")} at {Describe(room)}",
                            null));
                    }
                }
            }
        }

        return violations;
    }

    /// <summary>States per level the footprint check probes, in the oracle's order - a deterministic cap.</summary>
    private const int FootprintStateCap = 4_000;

    /// <summary>
    /// The layout solver's founding rule (<c>LayoutFootprint</c>): outside an
    /// event's footprint the other fish does not matter. In every state that can
    /// still be finished, for every fish A and every key of A's that moves an
    /// item or takes a fish out: the footprint F is computed from a reference
    /// outcome in which the other fish stood clear of it, and then for every
    /// placement q the other fish can swim to inertly that F does not cover, the
    /// same key with the other fish at q must give exactly the reference outcome
    /// - every model in the same place - with the other fish still standing at q.
    ///
    /// <para>The reference is the first placement the other fish can reach
    /// (where it stands, if it can) whose outcome can still be finished, is
    /// clear of its own footprint, and does not need the fish there to keep
    /// something up (<c>LayoutFootprint.NeedsIdleSupport</c> - the very test the
    /// solver binds on). Anything else is the solver's "bound" edge, where the
    /// outcome depends on where the fish stood and the fish is committed there -
    /// not a counter-example. The support part is not optional:
    /// <c>support-bound</c> is a level where the idle fish, far from the push,
    /// is what keeps a heavy bar off the actor (as on <c>submarine</c>), and no
    /// footprint of the outcome covers it.</para>
    ///
    /// <para><b>The interaction half.</b> Inside F the outcome may change, but a
    /// different outcome that can still be finished must come only from a
    /// placement meeting the moved items' after or swept cells
    /// (<c>LayoutFootprint.InteractionCells</c>) - those, plus the cells under
    /// where they came to rest, are all the solver tries for an interaction
    /// child. Any other placement inside F must give the reference outcome, a
    /// refusal, or a room that cannot be finished. Stricter than "unless the
    /// fish is then needed as support": with that exemption the check could not
    /// see wc's interaction at all. The micro-levels never contain such an
    /// outcome; <c>LayoutSolverTests</c> runs this along recorded corpus
    /// solutions, where wc's does (emptying the interaction cells makes it fail
    /// there).</para>
    /// </summary>
    public static List<Violation> Footprint(StateGraph graph)
    {
        var states = new List<RoomSnapshot>();
        for (int i = 0; i < graph.Count && states.Count < FootprintStateCap; i++)
        {
            if (graph.Solvable[i] && !graph.Solved[i])
            {
                states.Add(graph.States[i]);
            }
        }

        return Footprint(graph.Level, states);
    }

    /// <summary>
    /// <see cref="Footprint(StateGraph)"/> over given states - which have to be
    /// settled, still solvable and not solved. Lets the same check run along a
    /// real level's recorded solution, where the micro-levels are too small to
    /// contain an interaction at all.
    /// </summary>
    public static List<Violation> Footprint(Level level, IReadOnlyList<RoomSnapshot> states)
    {
        LevelReduction reduction = LevelReduction.Compute(level);
        int[] mobile = reduction.MobileModels;
        int[] items = [.. mobile.Where(m => !level.Models[m].IsAlive)];
        var footprint = new LayoutFootprint(level);
        var router = new InertRouter(level, reduction, exactSupport: true);
        var room = new Room(level);
        int width = level.Width, height = level.Height;

        byte[] state = new byte[reduction.StateKeySize];
        byte[] start = new byte[reduction.StateKeySize];
        byte[] reference = new byte[reduction.StateKeySize];
        byte[] actual = new byte[reduction.StateKeySize];
        int[] grid = new int[width * height];
        var before = new (int X, int Y, bool Gone)[items.Length];
        var cells = new List<int>();
        var probeCells = new List<int>();
        var mask = new bool[width * height];
        var interactionMask = new bool[width * height];
        var interaction = new List<int>();
        byte[] probeKey = new byte[reduction.StateKeySize];
        bool[] goneBefore = new bool[level.Units.Length];
        var violations = new List<Violation>();

        for (int i = 0; i < states.Count && violations.Count < 20; i++)
        {
            room.RestoreFrom(states[i]);
            room.WriteStateKey(state, mobile);
            for (int c = 0; c < grid.Length; c++)
            {
                grid[c] = room.GetModel(c % width, c / width);
            }

            for (int n = 0; n < items.Length; n++)
            {
                ref readonly ModelState s = ref room.State(items[n]);
                before[n] = (s.X, s.Y, s.IsOut || s.IsLost);
            }

            for (int u = 0; u < level.Units.Length; u++)
            {
                ref readonly ModelState s = ref room.State(level.Units[u].Model);
                goneBefore[u] = s.IsOut || s.IsLost;
            }

            foreach (UnitDef unitA in level.Units)
            {
                int fishA = unitA.Model;
                if (!InRoom(room, fishA))
                {
                    continue;
                }

                foreach (UnitDef unitV in level.Units)
                {
                    int fishV = unitV.Model;
                    int slotV = Array.IndexOf(mobile, fishV);
                    room.RestoreMobile(state, mobile);
                    if (fishV == fishA || slotV < 0 || !InRoom(room, fishV) || room.State(fishV).Busy)
                    {
                        continue;
                    }

                    (int ax, int ay, _) = room.Position(fishA);
                    router.Route(room, fishV);
                    int[] reach = [.. router.Reached];

                    foreach ((int dx, int dy, char symbol) in Keys(unitA))
                    {
                        // The reference: the state as it is, or with the other fish
                        // moved to the first placement clear of its own outcome's
                        // footprint.
                        int refPlacement = -1;
                        for (int r = 0; r < reach.Length && r < 16 && refPlacement < 0; r++)
                        {
                            (int rx, int ry, _) = router.Decode(reach[r]);
                            if (!Probe(room, state, probeKey, mobile, slotV, router.Decode(reach[r]), symbol, items, before, goneBefore, level, out bool isEvent)
                                || !isEvent || !room.IsSolvable()
                                || LayoutFootprint.NeedsIdleSupport(room, router, fishV, rx, ry, fishA))
                            {
                                continue;
                            }

                            footprint.Compute(room, items, before, fishA, ax, ay, dx, dy, probeCells);
                            if (!Covers(level.Models[fishV].Shape, router.Decode(reach[r]), probeCells, width, height))
                            {
                                refPlacement = reach[r];
                                room.WriteStateKey(reference, mobile);
                                (cells, probeCells) = (probeCells, cells);
                                interaction.Clear();
                                interaction.AddRange(footprint.InteractionCells);
                            }
                        }

                        if (refPlacement < 0)
                        {
                            continue; // no event, or the outcome always depends on the other fish
                        }

                        Array.Clear(mask);
                        foreach (int cell in cells)
                        {
                            mask[cell] = true;
                        }

                        Array.Clear(interactionMask);
                        foreach (int cell in interaction)
                        {
                            interactionMask[cell] = true;
                        }

                        foreach (int q in reach)
                        {
                            (int qx, int qy, bool qLeft) = router.Decode(q);
                            if (q == refPlacement)
                            {
                                continue;
                            }

                            if (CoversAny(level.Models[fishV].Shape, qx, qy, mask, width, height))
                            {
                                // Inside the footprint the outcome may change - but a
                                // different outcome that can still be finished has to
                                // come from a placement in the moved items' after or
                                // swept cells, which is all the solver tries for an
                                // interaction child.
                                if (!CoversAny(level.Models[fishV].Shape, qx, qy, interactionMask, width, height)
                                    && Probe(room, state, probeKey, mobile, slotV, (qx, qy, qLeft), symbol, items, before, goneBefore, level, out _)
                                    && room.IsSolvable())
                                {
                                    room.WriteStateKey(actual, mobile);
                                    bool vStayed = room.State(fishV).IsAlive && !room.State(fishV).IsOut && !room.State(fishV).IsLost
                                        && room.Position(fishV) == (qx, qy, qLeft);
                                    bool differs = !vStayed;
                                    for (int b = 0; b < actual.Length && !differs; b++)
                                    {
                                        differs = b / StateKeyEdit.SlotSize != slotV && actual[b] != reference[b];
                                    }

                                    if (differs)
                                    {
                                        string after = Describe(room);
                                        room.RestoreMobile(state, mobile);
                                        violations.Add(new(
                                            $"'{symbol}' of {level.Models[fishA].Kind} with {level.Models[fishV].Kind} at ({qx},{qy}), " +
                                            $"outside the after/swept cells: a different outcome that can still be finished, {after}; " +
                                            $"from {Describe(room)}",
                                            null));
                                    }
                                }

                                continue;
                            }

                            bool moved = Probe(room, state, probeKey, mobile, slotV, (qx, qy, qLeft), symbol, items, before, goneBefore, level, out _);
                            room.WriteStateKey(actual, mobile);
                            string? what = null;
                            if (!moved)
                            {
                                what = "refused";
                            }
                            else
                            {
                                (int vx, int vy, bool vLeft) = room.Position(fishV);
                                if (!room.State(fishV).IsAlive || room.State(fishV).IsOut || room.State(fishV).IsLost
                                    || (vx, vy, vLeft) != (qx, qy, qLeft))
                                {
                                    what = "the other fish moved, died or left";
                                }
                                else
                                {
                                    for (int b = 0; b < actual.Length; b++)
                                    {
                                        if (b / StateKeyEdit.SlotSize != slotV && actual[b] != reference[b])
                                        {
                                            what = $"outcome differs: {Describe(room)}";
                                            break;
                                        }
                                    }
                                }
                            }

                            if (what is not null)
                            {
                                room.RestoreMobile(state, mobile);
                                (int rx, int ry, bool rLeft) = router.Decode(refPlacement);
                                violations.Add(new(
                                    $"'{symbol}' of {level.Models[fishA].Kind} with {level.Models[fishV].Kind} at ({qx},{qy}) " +
                                    $"instead of ({rx},{ry}){(rLeft ? "<" : ">")}: {what}; from {Describe(room)}",
                                    null));
                            }
                        }

                        // The probes rewrite the room from keys: it has to come
                        // back to exactly the state, grid included, or every later
                        // probe of this state is testing something else.
                        room.RestoreMobile(state, mobile);
                        room.WriteStateKey(actual, mobile);
                        bool same = actual.AsSpan().SequenceEqual(state);
                        for (int c = 0; c < grid.Length && same; c++)
                        {
                            same = grid[c] == room.GetModel(c % width, c / width);
                        }

                        if (!same)
                        {
                            violations.Add(new($"the room was not restored after probing at {Describe(room)}", null));
                        }
                    }
                }
            }
        }

        return violations;
    }

    /// <summary>The state with the other fish moved to <paramref name="at"/>, then one key; false if the key is refused.</summary>
    private static bool Probe(
        Room room, byte[] state, byte[] key, int[] mobile, int slotV, (int X, int Y, bool IsLeft) at, char symbol,
        int[] items, (int X, int Y, bool Gone)[] before, bool[] goneBefore, Level level, out bool isEvent)
    {
        MoveFish(state, key, slotV, at);
        room.RestoreMobile(key, mobile);

        isEvent = false;
        if (!room.ApplyMove(symbol))
        {
            return false;
        }

        for (int n = 0; n < items.Length && !isEvent; n++)
        {
            ref readonly ModelState s = ref room.State(items[n]);
            isEvent = (s.X, s.Y, s.IsOut || s.IsLost) != before[n];
        }

        for (int u = 0; u < level.Units.Length && !isEvent; u++)
        {
            ref readonly ModelState s = ref room.State(level.Units[u].Model);
            isEvent = (s.IsOut || s.IsLost) != goneBefore[u];
        }

        return true;
    }

    private static bool InRoom(Room room, int fish)
    {
        ref readonly ModelState s = ref room.State(fish);
        return s.IsAlive && !s.IsOut && !s.IsLost
            && (uint)s.X < (uint)room.Level.Width && (uint)s.Y < (uint)room.Level.Height;
    }

    private static IEnumerable<(int Dx, int Dy, char Symbol)> Keys(UnitDef unit) =>
        [(-1, 0, unit.Left), (1, 0, unit.Right), (0, -1, unit.Up), (0, 1, unit.Down)];

    private static bool Covers(Shape shape, (int X, int Y, bool IsLeft) at, List<int> cells, int width, int height)
    {
        var mask = new bool[width * height];
        foreach (int cell in cells)
        {
            mask[cell] = true;
        }

        return CoversAny(shape, at.X, at.Y, mask, width, height);
    }

    private static bool CoversAny(Shape shape, int x, int y, bool[] mask, int width, int height)
    {
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            int cx = x + shape.MarkX[m], cy = y + shape.MarkY[m];
            if ((uint)cx < (uint)width && (uint)cy < (uint)height && mask[(cy * width) + cx])
            {
                return true;
            }
        }

        return false;
    }

    private static void MoveFish(byte[] source, byte[] destination, int slot, (int X, int Y, bool IsLeft) to)
    {
        source.CopyTo(destination, 0);
        int offset = slot * 5;
        destination[offset] = (byte)to.X;
        destination[offset + 1] = (byte)(to.X >> 8);
        destination[offset + 2] = (byte)to.Y;
        destination[offset + 3] = (byte)(to.Y >> 8);
        destination[offset + 4] = (byte)((destination[offset + 4] & ~1) | (to.IsLeft ? 1 : 0));
    }

    private static char StepSymbol(UnitDef unit, (int X, int Y, bool IsLeft) from, (int X, int Y, bool IsLeft) to) =>
        to.IsLeft != from.IsLeft ? (to.IsLeft ? unit.Left : unit.Right)
        : to.Y < from.Y ? unit.Up
        : to.Y > from.Y ? unit.Down
        : to.X < from.X ? unit.Left
        : unit.Right;

    /// <summary>
    /// The stuck-item rule (<c>--stuck</c>) never condemns a state that can still
    /// be finished. Asks the solver's own <c>NoWayOut</c> - cache included, on one
    /// solver for the whole graph, in the oracle's order - so this tests the real
    /// code path, including the per-layout cache that review C1 found keyed too
    /// coarsely. Reflection on private members, like <see cref="WhiteBoxTests"/>.
    /// </summary>
    public static List<Violation> Stuck(StateGraph graph)
    {
        var probe = new SolverProbe(new Solver(graph.Level));
        var violations = new List<Violation>();

        for (int i = 0; i < graph.Count; i++)
        {
            if (graph.Solved[i] || graph.DistanceToGoal[i] == StateGraph.Infinite)
            {
                continue;
            }

            if (probe.NoWayOut(graph.States[i]))
            {
                violations.Add(new($"d*={graph.DistanceToGoal[i]}, condemned at {Describe(probe.Room)}", null));
            }
        }

        return violations;
    }

    /// <summary>
    /// Merging identical items (docs/022) is sound: in every state, swapping
    /// which identical item stands where changes nothing the physics can tell.
    /// Each class of interchangeable items has its positions rotated by one,
    /// every move is applied to both rooms, and the two outcomes must agree -
    /// the move allowed or not, solved, solvable, able to move, and the same
    /// canonical key. This is what would catch a round whose outcome depends on
    /// the order it processes models in.
    /// </summary>
    public static List<Violation> SymmetrySound(StateGraph graph)
    {
        Level level = graph.Level;
        int[] mobile = LevelReduction.Compute(level).MobileModels;
        int[][] classes = ItemSymmetry.Classes(level, mobile);
        var violations = new List<Violation>();
        if (classes.Length == 0)
        {
            return violations;
        }

        var codec = new StateKeyCodec(level, mobile) { Canonical = true };
        var a = new Room(level);
        var b = new Room(level);
        var symbols = new char[a.MoveSymbolCount];
        a.GetMoveSymbols(symbols, out int symbolCount);
        var wide = new byte[mobile.Length * 5];
        var rotated = new byte[mobile.Length * 5];
        var keyA = new byte[mobile.Length * 5];
        var keyB = new byte[mobile.Length * 5];
        var compactA = new byte[codec.CompactSize];
        var compactB = new byte[codec.CompactSize];

        for (int i = 0; i < graph.Count; i++)
        {
            if (graph.Solved[i])
            {
                continue;
            }

            a.RestoreFrom(graph.States[i]);
            a.WriteStateKey(wide, mobile);
            wide.CopyTo(rotated, 0);
            foreach (int[] members in classes)
            {
                for (int k = 0; k < members.Length; k++)
                {
                    wide.AsSpan(members[(k + 1) % members.Length] * 5, 5).CopyTo(rotated.AsSpan(members[k] * 5, 5));
                }
            }

            for (int s = 0; s < symbolCount; s++)
            {
                a.RestoreFrom(graph.States[i]);
                b.RestoreFrom(graph.States[i]);
                b.RestoreMobile(rotated, mobile);

                bool movedA = a.ApplyMove(symbols[s]);
                bool movedB = b.ApplyMove(symbols[s]);
                string? what = null;
                if (movedA != movedB)
                {
                    what = $"move allowed {movedA} vs {movedB}";
                }
                else if (movedA)
                {
                    a.WriteStateKey(keyA, mobile);
                    b.WriteStateKey(keyB, mobile);
                    codec.Encode(keyA, compactA);
                    codec.Encode(keyB, compactB);
                    if (a.IsSolved() != b.IsSolved() || a.IsSolvable() != b.IsSolvable() || a.CannotMove() != b.CannotMove())
                    {
                        what = "solved/solvable/can-move differ";
                    }
                    else if (!compactA.AsSpan().SequenceEqual(compactB))
                    {
                        what = $"outcomes differ: {Describe(a)} vs {Describe(b)}";
                    }
                }

                if (what is not null)
                {
                    a.RestoreFrom(graph.States[i]);
                    violations.Add(new($"'{symbols[s]}' from {Describe(a)}: {what}", null));
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// The estimate with the stuck-wall distances (docs/021) never exceeds the
    /// true distance d* - on every solvable state, through the solver's own
    /// detector and cache, in the oracle's order. Consistency is not required:
    /// the search restores it with pathmax.
    /// </summary>
    public static List<Violation> StuckEstimate(StateGraph graph)
    {
        var probe = new SolverProbe(new Solver(graph.Level));
        var violations = new List<Violation>();

        for (int i = 0; i < graph.Count; i++)
        {
            if (!graph.Solvable[i] || graph.Solved[i] || graph.DistanceToGoal[i] == StateGraph.Infinite)
            {
                continue;
            }

            int h = probe.StuckEstimate(graph.States[i]);
            if (h > graph.DistanceToGoal[i])
            {
                violations.Add(new($"h={h} > d*={graph.DistanceToGoal[i]} at {Describe(probe.Room)}", null));
            }
        }

        return violations;
    }

    /// <summary>
    /// The stuck-item detector - the analysis made fast (per-level wall work
    /// done once, per-fish reach from precomputed fits, verdicts cached per
    /// layout, docs/020) - gives on every state the answer the same rule gives
    /// computed the slow, direct way with <see cref="StuckAnalysis"/>. Mirrors
    /// the one shortcut the detector takes: with nothing stuck beyond the
    /// opening walls it says nothing, because a fish that cannot leave then is
    /// already rejected by the walls-only heuristic.
    /// </summary>
    public static List<Violation> StuckDetectorAgrees(StateGraph graph)
    {
        Level level = graph.Level;
        var solver = new Solver(level);
        var probe = new SolverProbe(solver);
        LevelReduction reduction = solver.Reduction;
        int[] goalOut = [.. reduction.MobileModels.Where(m => !level.Models[m].IsAlive && level.Models[m].Goal == GoalKind.Out)];
        var violations = new List<Violation>();

        for (int i = 0; i < graph.Count; i++)
        {
            if (graph.Solved[i])
            {
                continue;
            }

            bool fast = probe.NoWayOut(graph.States[i]);
            bool direct = DirectNoWayOut(level, probe.Room, reduction, goalOut);
            if (fast != direct)
            {
                violations.Add(new($"detector {fast}, direct {direct} at {Describe(probe.Room)}", null));
            }
        }

        return violations;
    }

    private static bool DirectNoWayOut(Level level, Room room, LevelReduction reduction, int[] goalOut)
    {
        bool[] mobile = StuckAnalysis.Mobile(level, room, fishAnywhere: true);
        if (goalOut.Any(m => !mobile[m] && !room.State(m).IsOut))
        {
            return true;
        }

        bool[] solid = StuckAnalysis.SolidCells(level, room, mobile);
        bool extra = false;
        for (int c = 0; c < solid.Length; c++)
        {
            extra |= solid[c] && !reduction.Walls[c];
            solid[c] |= reduction.Walls[c];
        }

        if (!extra)
        {
            return false;
        }

        foreach (UnitDef unit in level.Units)
        {
            ref readonly ModelState s = ref room.State(unit.Model);
            if (level.Models[unit.Model].Goal is not (GoalKind.Escape or GoalKind.Out) || s.IsOut || !s.IsAlive
                || (uint)s.X >= (uint)level.Width || (uint)s.Y >= (uint)level.Height)
            {
                continue;
            }

            if (!StuckAnalysis.CanStillLeave(level, room, solid, unit.Model)[(s.Y * level.Width) + s.X])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Where every model is, compactly - enough to redraw the state by hand.</summary>
    public static string Describe(Room room)
    {
        var text = new StringBuilder("[");
        for (int i = 0; i < room.ModelCount; i++)
        {
            if (room.Level.Models[i].Weight == Physics.Weight.Fixed && !room.Level.Models[i].IsAlive)
            {
                continue;
            }

            ref readonly ModelState m = ref room.State(i);
            text.Append($" {i}:{(m.IsOut ? "out" : $"({m.X},{m.Y}){(room.Level.Models[i].IsAlive ? (m.IsLeft ? "<" : ">") : "")}")}");
        }

        return text.Append(" ]").ToString();
    }
}

/// <summary>Drives a <see cref="Solver"/>'s private <c>NoWayOut</c> on arbitrary states.</summary>
internal sealed class SolverProbe
{
    private const System.Reflection.BindingFlags Private =
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    private readonly Solver _solver;
    private readonly byte[] _key;
    private readonly int[] _mobile;
    private readonly System.Reflection.MethodInfo _noWayOut;
    private readonly System.Reflection.MethodInfo _stuckEstimate;

    public SolverProbe(Solver solver)
    {
        _solver = solver;
        Room = (Room)typeof(Solver).GetField("_room", Private)!.GetValue(solver)!;
        _key = (byte[])typeof(Solver).GetField("_key", Private)!.GetValue(solver)!;
        _mobile = (int[])typeof(Solver).GetField("_mobile", Private)!.GetValue(solver)!;
        _noWayOut = typeof(Solver).GetMethod("NoWayOut", Private)!;
        _stuckEstimate = typeof(Solver).GetMethod("StuckEstimate", Private)!;
    }

    public Room Room { get; }

    public bool NoWayOut(RoomSnapshot state)
    {
        Room.RestoreFrom(state);
        Room.WriteStateKey(_key, _mobile);
        return (bool)_noWayOut.Invoke(_solver, null)!;
    }

    /// <summary>The solver's estimate of a state with the stuck-item rule on (see Solver.StuckEstimate).</summary>
    public int StuckEstimate(RoomSnapshot state)
    {
        Room.RestoreFrom(state);
        Room.WriteStateKey(_key, _mobile);
        return (int)_stuckEstimate.Invoke(_solver, null)!;
    }
}
