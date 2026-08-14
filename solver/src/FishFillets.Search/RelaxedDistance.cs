using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// Per-model lower bounds on "moves still needed", precomputed once per level by
/// BFS over a relaxed room that contains only the immovable walls.
///
/// The relaxation is sound because of a property specific to this game:
/// <b>fish swim, they do not climb.</b> A fish needs no support and is never
/// helped by an item - items can only ever be in the way. So deleting every
/// movable item can only make a fish's path shorter, and the distance measured
/// on the walls-only room can never overestimate the real one.
///
/// Distances are indexed by (cell, facing), not just cell, because moving
/// against your facing costs a move that gets you nowhere
/// (<c>Unit::goLeft/goRight</c>) - so turns are counted, and the bound stays
/// tight enough to be useful on levels where the fish has to double back.
/// </summary>
internal sealed class RelaxedDistance
{
    public const int Unreachable = int.MaxValue / 4;

    private readonly int _width;
    private readonly int _height;
    private readonly int[] _distance;   // [(y * w + x) * 2 + facing], facing 0 = left

    private RelaxedDistance(int width, int height, int[] distance)
    {
        _width = width;
        _height = height;
        _distance = distance;
    }

    /// <summary>Moves needed at best, from this anchor cell and facing.</summary>
    public int At(int x, int y, bool isLeft)
    {
        if ((uint)x >= (uint)_width || (uint)y >= (uint)_height)
        {
            // Already crossing the border - it is on its way out for free.
            return 0;
        }

        return _distance[(((y * _width) + x) * 2) + (isLeft ? 0 : 1)];
    }

    /// <summary>
    /// Distance to being out of the room, for a model that leaves by the border
    /// (goal_escape / goal_out).
    /// </summary>
    /// <param name="freeFall">
    /// Whether downward steps are free. True for a pushed item, which gravity
    /// moves at no cost in moves; false for a fish, which must swim every cell.
    /// </param>
    public static RelaxedDistance ToExit(Level level, int model, bool[] walls, bool freeFall)
    {
        int w = level.Width, h = level.Height;
        var distance = new int[w * h * 2];
        Array.Fill(distance, Unreachable);

        // 0-1 BFS: free-fall edges cost 0, everything else 1. A deque keeps that
        // in order without a full priority queue.
        var queue = new LinkedList<int>();

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
                    queue.AddLast(node);
                }
            }
        }

        while (queue.Count > 0)
        {
            int node = queue.First!.Value;
            queue.RemoveFirst();

            int cost = distance[node];
            int facing = node & 1;
            int cell = node >> 1;
            int x = cell % w, y = cell / w;

            // Predecessors: which (cell, facing) could have reached this one in
            // one move. Vertical moves keep facing; a horizontal move only
            // happens when already facing that way; and a turn changes facing
            // without moving.
            Relax(x, y + 1, facing, 1);                       // came from below, moved up
            Relax(x, y - 1, facing, 1);                       // came from above, moved down
            if (facing == 0) Relax(x + 1, y, 0, 1);           // moved left while facing left
            else Relax(x - 1, y, 1, 1);                       // moved right while facing right
            Relax(x, y, facing ^ 1, 1);                       // turned to face this way

            if (freeFall)
            {
                // Gravity: an item can arrive from above at no cost in moves.
                Relax(x, y - 1, facing, 0);
            }

            void Relax(int px, int py, int pf, int edge)
            {
                if ((uint)px >= (uint)w || (uint)py >= (uint)h || !Fits(level, model, walls, px, py))
                {
                    return;
                }

                int prev = (((py * w) + px) * 2) + pf;
                if (distance[prev] <= cost + edge)
                {
                    return;
                }

                distance[prev] = cost + edge;
                if (edge == 0)
                {
                    queue.AddFirst(prev);
                }
                else
                {
                    queue.AddLast(prev);
                }
            }
        }

        return new RelaxedDistance(w, h, distance);
    }

    /// <summary>The model's whole shape sits on non-wall cells at this anchor.</summary>
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

    /// <summary>
    /// From this anchor the model would walk out of the room by itself - the
    /// walls-only reading of <c>MarkMask::getBorderDir()</c>: it touches an edge,
    /// and nothing solid blocks the way off it. Leaving costs no move at all
    /// (<c>Rules::actionOut()</c> runs during the settle), so these are the
    /// zero-distance sources.
    /// </summary>
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
                // Out of bounds is the border, which never blocks a model that
                // is leaving; only a wall does.
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

    /// <summary>
    /// Cells permanently occupied by something that can never move - each level's
    /// room shape, plus any scenery welded to it. Everything else is treated as
    /// open water by the relaxation.
    /// </summary>
    public static bool[] BuildWallGrid(Level level)
    {
        var walls = new bool[level.Width * level.Height];
        var mutable = new HashSet<int>(level.MutableModels);

        for (int i = 0; i < level.ModelCount; i++)
        {
            if (mutable.Contains(i))
            {
                continue;
            }

            ModelDef def = level.Models[i];
            for (int m = 0; m < def.Shape.MarkX.Length; m++)
            {
                int cx = def.X + def.Shape.MarkX[m], cy = def.Y + def.Shape.MarkY[m];
                if ((uint)cx < (uint)level.Width && (uint)cy < (uint)level.Height)
                {
                    walls[(cy * level.Width) + cx] = true;
                }
            }
        }

        return walls;
    }
}
