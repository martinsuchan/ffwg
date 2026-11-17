using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// Works out, for one fish in one state, every placement it can swim to
/// <b>without disturbing anything</b>, and how many moves each costs.
///
/// <para><b>Inert moves.</b> A move is inert when it changes nothing except that
/// fish's own position and facing. That happens exactly when the cells it moves
/// into are empty (so nothing is pushed) and the fish is not currently holding
/// anything up (so nothing falls when it leaves). Both are grid tests, so routing
/// runs with no simulation at all - and because an inert route leaves the rest of
/// the world untouched, the state it arrives at is just the starting state with
/// this fish moved. A whole route therefore costs zero physics calls; only the
/// action at the end needs the engine.</para>
///
/// <para>The "holding something up" test is deliberately coarse - it asks whether
/// anything sits directly on the fish, not whether that thing would actually
/// fall (it may be resting on a wall too). Being wrong here only turns a travel
/// step into a decision point, which costs a search node and never a solution.
/// It also lines up with how a player thinks about it: swimming out from under
/// something is a move with a purpose, not travel.</para>
///
/// <para><b>Turns are always inert</b> - a turn moves the fish nowhere, so
/// nothing can fall - which is why placements are keyed by cell <i>and</i> facing.
/// Moving against your facing costs a move and gets you nowhere, and that has to
/// be counted or the route lengths are wrong.</para>
///
/// <para><b>Exits are not traversable.</b> A fish that reaches the border with a
/// clear way out leaves during the settle, so arriving there is an event, not a
/// waypoint. Those placements are reported as actions and the search applies real
/// physics to them.</para>
/// </summary>
internal sealed class InertRouter
{
    private readonly Level _level;
    private readonly LevelReduction _reduction;
    private readonly int _width;
    private readonly int _height;

    private readonly int[] _cost;
    private readonly int[] _from;      // predecessor placement, -1 at the root
    private readonly int[] _stamp;
    private int _generation;

    private readonly int[] _queue;
    private int _head;
    private int _tail;

    /// <summary>Placements reached, in BFS order. Index into cost/from by placement id.</summary>
    private readonly List<int> _reached = [];

    /// <summary>
    /// Whether "holding something up" is decided exactly - by asking the engine
    /// whether anything would fall - rather than by "something is overhead".
    ///
    /// <para>The coarse test is enough for <see cref="MacroSolver"/>, where a
    /// false positive turns travel into an action node. <see cref="ItemSolver"/>
    /// needs the exact one: there a step that is refused as travel and then moves
    /// no item is no edge at all, so a fish under an item that is also propped up
    /// elsewhere could never move again (solver/docs/009, docs/023).</para>
    /// </summary>
    private readonly bool _exactSupport;

    public InertRouter(Level level, LevelReduction reduction, bool exactSupport = false)
    {
        _level = level;
        _reduction = reduction;
        _exactSupport = exactSupport;
        _width = level.Width;
        _height = level.Height;

        int placements = _width * _height * 2;
        _cost = new int[placements];
        _from = new int[placements];
        _stamp = new int[placements];
        _queue = new int[placements];

        _relaxQueue = new int[placements];
        _relaxSources = new long[placements];
        _relaxDone = new int[placements];
    }

    public IReadOnlyList<int> Reached => _reached;

    /// <summary>
    /// Steps the last <see cref="Route"/> refused for one reason only: the way
    /// was clear, but something would have fallen (or a fish been crushed).
    /// Those are the drops, found as a by-product of routing. Only filled with
    /// exact support.
    /// </summary>
    public IReadOnlyList<(int Placement, int Dx, int Dy)> Drops => _drops;

    private readonly List<(int Placement, int Dx, int Dy)> _drops = [];

    public int CostOf(int placement) => _cost[placement];

    /// <summary>The placement the last route stepped from to reach this one; -1 at its start.</summary>
    public int PredecessorOf(int placement) => _from[placement];

    public int Placement(int x, int y, bool isLeft) => (((y * _width) + x) * 2) + (isLeft ? 0 : 1);

    public (int X, int Y, bool IsLeft) Decode(int placement) =>
        ((placement >> 1) % _width, (placement >> 1) / _width, (placement & 1) == 0);

    /// <summary>
    /// Explores everywhere <paramref name="fish"/> can reach inertly from where it
    /// stands in <paramref name="room"/>. Call <see cref="Reached"/> afterwards.
    /// </summary>
    /// <param name="ignore">
    /// A model to route as though it were not there. Used to ask what a fish
    /// could reach if the other one moved out of the way - see
    /// <c>MacroSolver.BlocksAnotherUnit</c>.
    /// </param>
    public void Route(Room room, int fish, int ignore = -1)
    {
        _reached.Clear();
        _drops.Clear();
        _head = _tail = 0;
        _generation++;

        (int x, int y, bool isLeft) = room.Position(fish);
        int start = Placement(x, y, isLeft);
        Visit(start, 0, -1);

        while (_head < _tail)
        {
            int placement = _queue[_head++];
            _reached.Add(placement);

            (int px, int py, bool pLeft) = Decode(placement);
            int cost = _cost[placement];

            // Turning is always inert: the fish stays put, so nothing it might be
            // holding up can fall.
            Visit(Placement(px, py, !pLeft), cost + 1, placement);

            // Vertical moves keep facing. Horizontal ones only happen when
            // already facing that way - otherwise the key press is the turn
            // above, not a move.
            TryStep(room, fish, placement, px, py, pLeft, 0, -1, cost, ignore);
            TryStep(room, fish, placement, px, py, pLeft, 0, 1, cost, ignore);
            TryStep(room, fish, placement, px, py, pLeft, pLeft ? -1 : 1, 0, cost, ignore);
        }
    }

    // Relax's own scratch, so that a relaxation never disturbs what the last
    // Route left behind (CostOf, PredecessorOf and WriteRoute read it later).
    private readonly int[] _relaxQueue;
    private readonly long[] _relaxSources;
    private readonly int[] _relaxDone;
    private int _relaxGeneration;

    /// <summary>
    /// Multi-source inert travel, in place. On entry <c>field[q]</c> is the cost
    /// of having the fish at placement q (<see cref="ushort.MaxValue"/> = not
    /// there); on exit <c>field[q]</c> = min over sources s of
    /// <c>field_in[s]</c> + inert distance s -> q in <paramref name="room"/> as it
    /// stands. Same step rules as <see cref="Route"/> (turn costs 1 and is always
    /// inert; vertical keeps facing; horizontal only when facing that way;
    /// <see cref="IsFreeStep(Room, int, int, int, int, int, int)"/> decides each
    /// step), so with a single source of cost 0 at the fish's own position the
    /// result equals Route's <see cref="CostOf"/> for every reached placement and
    /// MaxValue elsewhere.
    ///
    /// <para><b>Why not Dijkstra with a heap.</b> Every edge costs 1, so the only
    /// thing out of order is the sources themselves. Sorting them once and
    /// merging that list with a plain FIFO - always taking the cheaper head -
    /// pops placements in non-decreasing cost. Pops being monotone means a
    /// placement is improved by a step at most once, so the FIFO never holds more
    /// than one entry per placement; a source improved by a step before its own
    /// turn comes is recognised as stale and skipped.</para>
    ///
    /// <para>The fish stays masked at its real position. That is fine for any
    /// source: its own cells count as free in <see cref="IsFreeStep(Room, int, int, int, int, int, int)"/>
    /// and <see cref="Room.WouldDisturbWithFishAt"/> evaluates the destination
    /// with the fish moved there. What it cannot see is support the fish gives
    /// at its real position and would not give at a far-away source - the room
    /// passed in has to be one that stays put without that fish where it is.</para>
    /// </summary>
    /// <param name="ignore">
    /// A model passed through as though it were not an obstacle. With exact
    /// support it still holds things up where it really stands.
    /// </param>
    public void Relax(Room room, int fish, ushort[] field, int ignore = -1)
    {
        int placements = _relaxQueue.Length;
        if (field.Length != placements)
        {
            throw new ArgumentException($"field has {field.Length} placements, the room {placements}", nameof(field));
        }

        _relaxGeneration++;
        int generation = _relaxGeneration;

        int sources = 0;
        for (int q = 0; q < placements; q++)
        {
            if (field[q] != ushort.MaxValue)
            {
                _relaxSources[sources++] = ((long)field[q] << 32) | (uint)q;
            }
        }

        _relaxSources.AsSpan(0, sources).Sort();

        int next = 0;          // into the sorted sources
        int head = 0, tail = 0; // the FIFO of placements improved by a step

        while (true)
        {
            int placement;
            int cost;

            // The cheaper of the two heads; a tie goes to the FIFO, which is as
            // good as any order between equal costs.
            if (head < tail && (next >= sources || field[_relaxQueue[head]] <= (int)(_relaxSources[next] >> 32)))
            {
                placement = _relaxQueue[head++];
                cost = field[placement];
            }
            else if (next < sources)
            {
                long entry = _relaxSources[next++];
                placement = (int)(entry & 0xFFFFFFFF);
                cost = (int)(entry >> 32);
                if (field[placement] != cost)
                {
                    continue; // a step got here more cheaply first
                }
            }
            else
            {
                break;
            }

            if (_relaxDone[placement] == generation)
            {
                continue;
            }

            _relaxDone[placement] = generation;

            int stepCost = cost + 1;
            if (stepCost >= ushort.MaxValue)
            {
                continue; // nothing further can be represented
            }

            (int px, int py, bool pLeft) = Decode(placement);

            // Turning is always inert.
            RelaxTo(field, Placement(px, py, !pLeft), stepCost, generation, ref tail);

            RelaxStep(room, fish, field, px, py, pLeft, 0, -1, stepCost, generation, ignore, ref tail);
            RelaxStep(room, fish, field, px, py, pLeft, 0, 1, stepCost, generation, ignore, ref tail);
            RelaxStep(room, fish, field, px, py, pLeft, pLeft ? -1 : 1, 0, stepCost, generation, ignore, ref tail);
        }
    }

    private void RelaxStep(
        Room room, int fish, ushort[] field, int px, int py, bool pLeft, int dx, int dy, int stepCost, int generation,
        int ignore, ref int tail)
    {
        int nx = px + dx, ny = py + dy;
        if ((uint)nx >= (uint)_width || (uint)ny >= (uint)_height)
        {
            return; // never a placement - and IsFreeStep would refuse it anyway
        }

        int target = Placement(nx, ny, pLeft);
        if (_relaxDone[target] == generation || field[target] <= stepCost)
        {
            return; // settled or no better: skip the (possibly engine-backed) test
        }

        if (!IsFreeStep(room, fish, px, py, nx, ny, out _, ignore))
        {
            return;
        }

        RelaxTo(field, target, stepCost, generation, ref tail);
    }

    private void RelaxTo(ushort[] field, int target, int stepCost, int generation, ref int tail)
    {
        if (_relaxDone[target] == generation || field[target] <= stepCost)
        {
            return;
        }

        field[target] = (ushort)stepCost;
        _relaxQueue[tail++] = target;
    }

    private void TryStep(
        Room room, int fish, int placement, int px, int py, bool pLeft, int dx, int dy, int cost, int ignore)
    {
        int nx = px + dx, ny = py + dy;

        // Only a genuinely free step is travel. Anything else - a push, a drop, or
        // arriving at the border - is an action the search has to see, and
        // MacroExpander picks those up separately.
        if (!IsFreeStep(room, fish, px, py, nx, ny, out bool drops, ignore))
        {
            if (drops)
            {
                _drops.Add((placement, dx, dy));
            }

            return;
        }

        Visit(Placement(nx, ny, pLeft), cost + 1, placement);
    }

    /// <summary>
    /// Whether moving the fish from (px,py) to (nx,ny) disturbs nothing: the
    /// destination is clear, the fish is holding nothing up, and it is not
    /// stepping onto the border.
    /// </summary>
    public bool IsFreeStep(Room room, int fish, int px, int py, int nx, int ny, int ignore = -1) =>
        IsFreeStep(room, fish, px, py, nx, ny, out _, ignore);

    /// <param name="drops">
    /// With exact support: the step is refused only because something would
    /// fall or a fish would be crushed - the way itself is clear.
    /// </param>
    private bool IsFreeStep(Room room, int fish, int px, int py, int nx, int ny, out bool drops, int ignore)
    {
        drops = false;

        // The coarse test is a sound pre-filter for the exact one: with nothing
        // overhead nothing can lose its support.
        bool holding = IsHoldingSomethingUp(room, fish, px, py, ignore);
        if (holding && !_exactSupport)
        {
            return false;
        }

        Shape shape = _level.Models[fish].Shape;
        bool overlapsIgnored = false;
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            int cx = nx + shape.MarkX[m], cy = ny + shape.MarkY[m];
            if ((uint)cx >= (uint)_width || (uint)cy >= (uint)_height)
            {
                return false; // the border: leaving is an event, not travel
            }

            int other = room.GetModel(cx, cy);
            if (ignore >= 0 && other == ignore)
            {
                overlapsIgnored = true;
                continue;
            }

            if (other != Room.Empty && other != fish)
            {
                return false;
            }
        }

        // Arriving somewhere it would leave from is an event, not a waypoint.
        if (MightEscapeFrom(room, fish, nx, ny))
        {
            return false;
        }

        // The ignored model is passed through as an obstacle but still holds
        // things up where it really stands, so the engine can be asked as usual -
        // unless the fish would stand on it. WouldDisturbWithFishAt masks the
        // fish at the destination and unmasks it again, which would wipe the
        // ignored model's cells out of the grid; the conservative answer (not
        // travel, and not a known drop either) keeps the room intact.
        if (holding && overlapsIgnored)
        {
            return false;
        }

        // Something is overhead: travel only if it all stays up, and crushes
        // nobody, once the fish has moved on. The destination is clear, so the
        // engine can be asked with the fish standing there.
        drops = holding && room.WouldDisturbWithFishAt(fish, nx, ny);
        return !drops;
    }

    /// <summary>
    /// Whether a model that has a goal to leave the room might walk out from this
    /// placement.
    ///
    /// A fish does NOT escape by stepping out of bounds - it escapes the moment it
    /// stands against the border with a clear way out, and <c>Rules::actionOut()</c>
    /// carries it away during the settle, costing no move. So border placements
    /// cannot be routed *through*: a route that walks over one would have the fish
    /// leave halfway along, and the rest of the route would be moves for a fish
    /// that is no longer in the room.
    ///
    /// Deliberately generous - it asks whether the way out is free of permanent
    /// wall, not whether the real push chain would clear. Over-reporting only
    /// turns travel into an action, which costs a node and never a solution.
    /// </summary>
    public bool MightEscapeFrom(Room room, int model, int x, int y)
    {
        if (_level.Models[model].Goal is not (GoalKind.Escape or GoalKind.Out))
        {
            return false;
        }

        Shape shape = _level.Models[model].Shape;

        for (int d = 0; d < 4; d++)
        {
            (int dx, int dy) = d switch
            {
                0 => (-1, 0),
                1 => (1, 0),
                2 => (0, -1),
                _ => (0, 1),
            };

            bool touchesEdge = false;
            bool blocked = false;

            for (int m = 0; m < shape.MarkX.Length; m++)
            {
                int cx = x + shape.MarkX[m], cy = y + shape.MarkY[m];
                touchesEdge |= dx < 0 ? cx == 0
                    : dx > 0 ? cx == _width - 1
                    : dy < 0 ? cy == 0
                    : cy == _height - 1;

                int nx = cx + dx, ny = cy + dy;
                if ((uint)nx >= (uint)_width || (uint)ny >= (uint)_height)
                {
                    continue; // the border never blocks something on its way out
                }

                if (_reduction.Walls[(ny * _width) + nx])
                {
                    blocked = true;
                    break;
                }
            }

            if (touchesEdge && !blocked)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether anything rests directly on the fish at this placement. Coarse on
    /// purpose - it does not ask whether that thing is also propped up elsewhere,
    /// because guessing wrong in this direction only costs a search node.
    /// </summary>
    public bool IsHoldingSomethingUp(Room room, int fish, int x, int y, int ignore = -1)
    {
        Shape shape = _level.Models[fish].Shape;
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            int cx = x + shape.MarkX[m], cy = y + shape.MarkY[m] - 1;
            int above = room.GetModel(cx, cy);
            if (above != Room.Empty && above != fish && above != ignore && above < _level.ModelCount
                && !_reduction.IsFrozen(above))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the fish standing here is worth recording as a state in its own
    /// right, even though it has not done anything.
    ///
    /// <para>There is exactly one such placement: <b>under something</b>. Support
    /// can change hands without anything falling - one fish slides in under part
    /// of a wide item's footprint, the fish that was holding it leaves, and the
    /// item never moves. That is a real maneuver and the search has to be able to
    /// express it, so arriving beneath a mobile model is a state.</para>
    ///
    /// <para><b>Nothing else is.</b> The earlier rule kept any placement with line
    /// of sight to something mobile, on the reasoning that a parked fish is solid
    /// and could catch a falling item or stop a push. Measured against the engine
    /// (<c>FishAsObstacleTests</c>), neither happens:</para>
    /// <list type="bullet">
    /// <item>a falling item <b>kills</b> whatever it lands on - any weight, either
    ///   fish - and a dead fish fails its goal, so the state is pruned as
    ///   unsolvable regardless;</item>
    /// <item>a fish <b>cannot be pushed</b>, directly or through a chain: the push
    ///   is simply refused, so a fish in the way only forbids a move that would
    ///   otherwise be available, which never helps.</item>
    /// </list>
    /// <para>So a fish parked anywhere else affects nothing, and its route can
    /// always be recomputed later from wherever it actually needs to be.</para>
    /// </summary>
    public bool IsParking(Room room, int fish, int x, int y)
    {
        if (IsHoldingSomethingUp(room, fish, x, y))
        {
            return true;
        }

        // Nothing else. Three wider rules were measured (solver/docs/008) - "in
        // another fish's way", "beside an item", "on an item's push lane" - and
        // every one of them bought wc a few moves while costing every other level
        // an order of magnitude in states. Only the original see-anything rule
        // reaches wc's optimum, and it is the slowest of the lot.
        return false;
    }

    /// <summary>Rebuilds the move symbols for the shortest inert route to a placement.</summary>
    public void WriteRoute(int placement, UnitDef unit, List<char> destination)
    {
        int start = destination.Count;
        for (int p = placement; _from[p] >= 0; p = _from[p])
        {
            (int x, int y, bool isLeft) = Decode(p);
            (int fx, int fy, bool fLeft) = Decode(_from[p]);

            destination.Add(
                isLeft != fLeft ? (isLeft ? unit.Left : unit.Right)
                : y < fy ? unit.Up
                : y > fy ? unit.Down
                : x < fx ? unit.Left
                : unit.Right);
        }

        destination.Reverse(start, destination.Count - start);
    }

    private void Visit(int placement, int cost, int from)
    {
        // Every edge costs 1 and the queue is FIFO, so the first time a placement
        // is reached is already by a shortest route. Never re-enqueueing also
        // keeps the fixed-size queue from overflowing.
        if (_stamp[placement] == _generation)
        {
            return;
        }

        _stamp[placement] = _generation;
        _cost[placement] = cost;
        _from[placement] = from;
        _queue[_tail++] = placement;
    }
}
