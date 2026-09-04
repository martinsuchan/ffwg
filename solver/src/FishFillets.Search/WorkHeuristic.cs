using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// A route-cost estimate that notices what is in the way.
///
/// <para><see cref="ExitHeuristic"/> measures the way out of a room with every
/// movable item deleted. That is what makes it admissible, and on levels whose
/// difficulty <i>is</i> the items it leaves almost nothing to go on: <c>map</c>
/// estimates 8 moves for a 2127-move solution, <c>city</c> 69 for 485. The
/// estimate still tracks progress along a solution closely (correlation 0.96 on
/// <c>map</c>) - the problem is its size, not its shape. With h at 1% of the
/// truth, f = g + h is g, and A* degenerates into breadth-first search. Scaling
/// it with <see cref="SolveOptions.Weight"/> cannot repair that either, because
/// multiplying every estimate by the same factor reorders nothing.</para>
///
/// <para>So this charges a toll instead: a step into cells holding a movable item
/// costs <c>1 + penalty</c> rather than 1, on the reasoning that whatever is in
/// the way generally has to be dealt with first. Measured over the reference
/// solutions, that lifts the estimate from 33-81% of the true remaining cost to
/// 95-105% at a penalty of about 5.</para>
///
/// <para><b>This is not admissible</b> - an item shoved along ahead of a fish
/// costs no extra move at all, and the toll charges for it anyway - so a search
/// using it can return a solution that is not the shortest. It is for finding an
/// answer on levels that have never produced one, and
/// <see cref="SolveResult.Optimal"/> is false whenever it is in use. The
/// admissible bound is still what proves shortest, and still what the dead-end
/// test uses.</para>
///
/// <para>Measured backwards from the exits, so one pass serves every fish
/// position sharing an item layout - which is what makes caching it worthwhile,
/// since items move on a small fraction of edges and fish on all of them.</para>
/// </summary>
internal sealed class WorkHeuristic
{
    public const int Unreachable = int.MaxValue / 4;

    private readonly int _width;
    private readonly int[][] _distance;   // per unit, [(y * w + x) * 2 + facing]
    private readonly int[] _fishModel;

    private WorkHeuristic(int width, int[][] distance, int[] fishModel)
    {
        _width = width;
        _distance = distance;
        _fishModel = fishModel;
    }

    /// <summary>Roughly how much work both fish have left, items included.</summary>
    public int Estimate(Room room)
    {
        int total = 0;
        for (int u = 0; u < _distance.Length; u++)
        {
            ref readonly ModelState s = ref room.State(_fishModel[u]);
            if (s.IsOut)
            {
                continue;
            }

            if (!s.IsAlive)
            {
                return Unreachable;
            }

            int d = _distance[u][(((s.Y * _width) + s.X) * 2) + (s.IsLeft ? 0 : 1)];
            if (d >= Unreachable)
            {
                return Unreachable;
            }

            total += d;
        }

        return total;
    }

    public static WorkHeuristic Build(Level level, Room room, bool[] walls, int penalty)
    {
        int w = level.Width, h = level.Height;

        // Cells a movable item stands in right now. Permanent walls are already
        // in `walls`, so anything landing on one of those is scenery and adds
        // nothing.
        var occupied = new bool[w * h];
        for (int i = 0; i < level.ModelCount; i++)
        {
            ref readonly ModelState s = ref room.State(i);
            if (s.IsOut || level.Models[i].IsAlive)
            {
                continue;
            }

            Shape shape = level.Models[i].Shape;
            for (int m = 0; m < shape.MarkX.Length; m++)
            {
                int cx = s.X + shape.MarkX[m], cy = s.Y + shape.MarkY[m];
                if ((uint)cx < (uint)w && (uint)cy < (uint)h)
                {
                    occupied[(cy * w) + cx] = true;
                }
            }
        }

        var distance = new int[level.Units.Length][];
        var fishModel = new int[level.Units.Length];
        for (int u = 0; u < level.Units.Length; u++)
        {
            fishModel[u] = level.Units[u].Model;
            distance[u] = TolledDistance(level, level.Units[u].Model, walls, occupied, penalty);
        }

        return new WorkHeuristic(w, distance, fishModel);
    }

    /// <summary>
    /// <see cref="RelaxedDistance.ToExit"/>'s backward search, with a variable
    /// step cost - so a priority queue rather than that method's 0-1 deque.
    /// </summary>
    private static int[] TolledDistance(Level level, int model, bool[] walls, bool[] occupied, int penalty)
    {
        int w = level.Width, h = level.Height;
        var distance = new int[w * h * 2];
        Array.Fill(distance, Unreachable);
        var queue = new PriorityQueue<int, int>();

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!Fits(level, model, walls, x, y) || !CanLeave(level, model, walls, x, y))
                {
                    continue;
                }

                for (int f = 0; f < 2; f++)
                {
                    int node = (((y * w) + x) * 2) + f;
                    distance[node] = 0;
                    queue.Enqueue(node, 0);
                }
            }
        }

        while (queue.TryDequeue(out int node, out int cost))
        {
            if (cost > distance[node])
            {
                continue;
            }

            int facing = node & 1;
            int cell = node >> 1;
            int x = cell % w, y = cell / w;

            // The move runs from the predecessor into THIS placement, so the toll
            // is whatever stands here.
            int step = 1 + (Blocked(level, model, occupied, x, y) ? penalty : 0);

            Relax(x, y + 1, facing);
            Relax(x, y - 1, facing);
            if (facing == 0)
            {
                Relax(x + 1, y, 0);
            }
            else
            {
                Relax(x - 1, y, 1);
            }

            Relax(x, y, facing ^ 1);

            void Relax(int px, int py, int pf)
            {
                if ((uint)px >= (uint)w || (uint)py >= (uint)h || !Fits(level, model, walls, px, py))
                {
                    return;
                }

                int prev = (((py * w) + px) * 2) + pf;
                if (distance[prev] <= cost + step)
                {
                    return;
                }

                distance[prev] = cost + step;
                queue.Enqueue(prev, cost + step);
            }
        }

        return distance;
    }

    private static bool Blocked(Level level, int model, bool[] occupied, int x, int y)
    {
        Shape shape = level.Models[model].Shape;
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            int cx = x + shape.MarkX[m], cy = y + shape.MarkY[m];
            if ((uint)cx < (uint)level.Width && (uint)cy < (uint)level.Height
                && occupied[(cy * level.Width) + cx])
            {
                return true;
            }
        }

        return false;
    }

    private static bool Fits(Level level, int model, bool[] walls, int x, int y)
    {
        Shape shape = level.Models[model].Shape;
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            int cx = x + shape.MarkX[m], cy = y + shape.MarkY[m];
            if ((uint)cx >= (uint)level.Width || (uint)cy >= (uint)level.Height)
            {
                return false;
            }

            if (walls[(cy * level.Width) + cx])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>As <see cref="RelaxedDistance"/>'s, and for the same reason.</summary>
    private static bool CanLeave(Level level, int model, bool[] walls, int x, int y)
    {
        Shape shape = level.Models[model].Shape;
        int w = level.Width, h = level.Height;

        for (int d = 0; d < 4; d++)
        {
            (int dx, int dy) = d switch
            {
                0 => (-1, 0),
                1 => (1, 0),
                2 => (0, -1),
                _ => (0, 1),
            };

            bool touchesThatEdge = false;
            bool blocked = false;

            for (int m = 0; m < shape.MarkX.Length; m++)
            {
                int cx = x + shape.MarkX[m], cy = y + shape.MarkY[m];
                touchesThatEdge |= dx < 0 ? cx == 0
                    : dx > 0 ? cx == w - 1
                    : dy < 0 ? cy == 0
                    : cy == h - 1;

                int nx = cx + dx, ny = cy + dy;
                if ((uint)nx < (uint)w && (uint)ny < (uint)h && walls[(ny * w) + nx])
                {
                    blocked = true;
                    break;
                }
            }

            if (touchesThatEdge && !blocked)
            {
                return true;
            }
        }

        return false;
    }
}
