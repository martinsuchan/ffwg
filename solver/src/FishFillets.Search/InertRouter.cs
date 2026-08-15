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

    public InertRouter(Level level, LevelReduction reduction)
    {
        _level = level;
        _reduction = reduction;
        _width = level.Width;
        _height = level.Height;

        int placements = _width * _height * 2;
        _cost = new int[placements];
        _from = new int[placements];
        _stamp = new int[placements];
        _queue = new int[placements];
    }

    public IReadOnlyList<int> Reached => _reached;

    public int CostOf(int placement) => _cost[placement];

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

    private void TryStep(
        Room room, int fish, int placement, int px, int py, bool pLeft, int dx, int dy, int cost, int ignore)
    {
        int nx = px + dx, ny = py + dy;

        // Only a genuinely free step is travel. Anything else - a push, a drop, or
        // arriving at the border - is an action the search has to see, and
        // MacroExpander picks those up separately.
        if (!IsFreeStep(room, fish, px, py, nx, ny, ignore))
        {
            return;
        }

        Visit(Placement(nx, ny, pLeft), cost + 1, placement);
    }

    /// <summary>
    /// Whether moving the fish from (px,py) to (nx,ny) disturbs nothing: the
    /// destination is clear, the fish is holding nothing up, and it is not
    /// stepping onto the border.
    /// </summary>
    public bool IsFreeStep(Room room, int fish, int px, int py, int nx, int ny, int ignore = -1)
    {
        if (IsHoldingSomethingUp(room, fish, px, py, ignore))
        {
            return false;
        }

        Shape shape = _level.Models[fish].Shape;
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            int cx = nx + shape.MarkX[m], cy = ny + shape.MarkY[m];
            if ((uint)cx >= (uint)_width || (uint)cy >= (uint)_height)
            {
                return false; // the border: leaving is an event, not travel
            }

            int other = room.GetModel(cx, cy);
            if (other != Room.Empty && other != fish && other != ignore)
            {
                return false;
            }
        }

        // Arriving somewhere it would leave from is an event, not a waypoint.
        return !MightEscapeFrom(room, fish, nx, ny);
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
