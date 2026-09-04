using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// Which models can never move again from a given room state, and what that
/// leaves the fish able to reach.
///
/// <para>This is the analysis <see cref="LevelReduction"/> runs once on a level's
/// opening position, lifted so it can run on any state. The rule is the same and
/// so is the reason it is sound: grow the set of models that <i>might</i> move,
/// starting from none, and whatever is left over cannot move whatever happens
/// next. Crucially, "might move" is answered <b>without assuming anything about
/// where the other items or the fish end up</b> - a fish's reach is measured with
/// every mobile item deleted, and a mobile item is assumed able to stand anywhere
/// it fits. Both are deliberate over-approximations, so the analysis can only
/// ever call something mobile that is not, never the reverse.</para>
///
/// <para>That last point is what makes the result usable as a hard fact rather
/// than a guess. A model this reports as stuck is scenery from here on, so
/// treating its cells as wall is exact, not optimistic - and if the fish then has
/// no route to any border, the state has no solution and can be dropped
/// outright.</para>
///
/// <para>See solver/docs/012 for the measurements, and for the two bugs found in
/// the start-state version while lifting it: a push is a chain and the body that
/// lands on the model being shoved need not be the fish's, and the geometry has
/// to come from the settled room rather than the level file.</para>
/// </summary>
internal static class StuckAnalysis
{
    /// <summary>
    /// Models that might still move. Everything else is scenery from here on.
    /// Indexed by model; the border is not included.
    /// </summary>
    /// <param name="fishAnywhere">
    /// Whether a fish is assumed able to stand anywhere it fits, rather than only
    /// where it could swim to from where it is now.
    ///
    /// <para>False is stronger and is right for a one-off analysis of a single
    /// position: a fish sealed in a pocket cannot push what is outside it.</para>
    ///
    /// <para>True makes the answer depend on the items alone, which is what lets
    /// a caller cache it per item arrangement instead of per state - and a state
    /// space where only the fish moved shares its arrangement with nearly all its
    /// neighbours. It is also strictly more cautious, since a fish that might be
    /// anywhere can only make more models look mobile, never fewer.</para>
    /// </param>
    public static bool[] Mobile(Level level, Room room, bool fishAnywhere = false)
    {
        var mobile = new bool[level.Models.Length];
        var gone = new bool[level.Models.Length];

        for (int i = 0; i < level.ModelCount; i++)
        {
            gone[i] = room.State(i).IsOut;
        }

        // A living fish is the thing that moves. A dead one is just an item that
        // happens to be fish-shaped: it can be shoved, and it can shove, but it
        // cannot decide to go anywhere.
        foreach (UnitDef unit in level.Units)
        {
            if (room.State(unit.Model).IsAlive && !gone[unit.Model])
            {
                mobile[unit.Model] = true;
            }
        }

        // Models the type rules alone prove immobile can never join the set.
        var canEverMove = new bool[level.Models.Length];
        foreach (int i in level.MutableModels)
        {
            canEverMove[i] = true;
        }

        var typeImmovable = new bool[level.Models.Length];
        for (int i = 0; i < level.ModelCount; i++)
        {
            typeImmovable[i] = !canEverMove[i];
        }

        var supportOf = new List<int>[level.ModelCount];
        for (int i = 0; i < level.ModelCount; i++)
        {
            supportOf[i] = Neighbours(level, room, i, 0, 1);
        }

        Weight strongest = StrongestFish(level, room);

        bool changed = true;
        while (changed)
        {
            changed = false;
            bool[] solid = SolidCells(level, room, mobile);
            bool[]?[] reachable = PusherReachability(level, room, solid, mobile, gone, fishAnywhere);

            for (int i = 0; i < level.ModelCount; i++)
            {
                if (mobile[i] || gone[i] || !canEverMove[i]
                    || !MightMove(level, room, typeImmovable, reachable, supportOf[i], mobile, gone, strongest, i))
                {
                    continue;
                }

                mobile[i] = true;
                changed = true;
            }
        }

        return mobile;
    }

    /// <summary>
    /// Cells nothing can ever vacate: the room itself plus everything standing
    /// where it will stand forever. Models already out of the room occupy
    /// nothing.
    /// </summary>
    public static bool[] SolidCells(Level level, Room room, bool[] mobile)
    {
        var solid = new bool[level.Width * level.Height];
        for (int i = 0; i < level.ModelCount; i++)
        {
            if (mobile[i] || room.State(i).IsOut)
            {
                continue;
            }

            Shape shape = level.Models[i].Shape;
            ref readonly ModelState at = ref room.State(i);
            for (int m = 0; m < shape.MarkX.Length; m++)
            {
                int cx = at.X + shape.MarkX[m], cy = at.Y + shape.MarkY[m];
                if ((uint)cx < (uint)level.Width && (uint)cy < (uint)level.Height)
                {
                    solid[(cy * level.Width) + cx] = true;
                }
            }
        }

        return solid;
    }

    /// <summary>
    /// Anchors from which this model could still get out of the room, given cells
    /// that will never clear. Plain connectivity: a fish needs no support, and
    /// everything not in <paramref name="solid"/> can in principle be gone by the
    /// time it swims through.
    ///
    /// <para>Answered for every anchor at once, from the exits backwards, because
    /// it then serves every position the model might be in for as long as the
    /// solid set holds - which is what makes it worth caching.</para>
    /// </summary>
    public static bool[] CanStillLeave(Level level, Room room, bool[] solid, int model)
    {
        int w = level.Width, h = level.Height;
        var seen = new bool[w * h];
        var queue = new Queue<int>();

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (seen[(y * w) + x] || !Fits(level, model, solid, x, y) || !CanLeave(level, model, solid, x, y))
                {
                    continue;
                }

                seen[(y * w) + x] = true;
                queue.Enqueue((y * w) + x);
            }
        }

        while (queue.Count > 0)
        {
            int cell = queue.Dequeue();
            int x = cell % w, y = cell / w;

            Step(x - 1, y);
            Step(x + 1, y);
            Step(x, y - 1);
            Step(x, y + 1);

            void Step(int nx, int ny)
            {
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h)
                {
                    return;
                }

                int next = (ny * w) + nx;
                if (seen[next] || !Fits(level, model, solid, nx, ny))
                {
                    return;
                }

                seen[next] = true;
                queue.Enqueue(next);
            }
        }

        return seen;
    }

    /// <summary>Whether this model could join the mobile set on this pass.</summary>
    private static bool MightMove(
        Level level,
        Room room,
        bool[] typeImmovable,
        bool[]?[] reachable,
        List<int> supportOf,
        bool[] mobile,
        bool[] gone,
        Weight strongest,
        int model)
    {
        // Anything holding it up that might move could drop it.
        foreach (int support in supportOf)
        {
            if (mobile[support])
            {
                return true;
            }
        }

        // Resting on nothing at all would mean falling. A settled room only does
        // this where a level floats a model (the border counts as support and
        // never moves).
        if (supportOf.Count == 0)
        {
            return true;
        }

        // Anything already on the move could come to rest against it and shove.
        for (int pusher = 0; pusher < level.ModelCount; pusher++)
        {
            if (pusher == model || !mobile[pusher] || gone[pusher] || reachable[pusher] is not bool[] where)
            {
                continue;
            }

            if (CanPush(level, room, typeImmovable, mobile, gone, where, strongest, pusher, model))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Where each model that might move could be standing when it does the
    /// shoving. A fish swims, so plain connectivity from where it is; an item
    /// only goes where it is pushed, so anything tighter than "every placement
    /// that fits" would have to model how it got there.
    /// </summary>
    private static bool[]?[] PusherReachability(
        Level level, Room room, bool[] solid, bool[] mobile, bool[] gone, bool fishAnywhere)
    {
        var reachable = new bool[]?[level.ModelCount];

        if (!fishAnywhere)
        {
            foreach (UnitDef unit in level.Units)
            {
                if (mobile[unit.Model])
                {
                    reachable[unit.Model] = SwimReach(level, room, solid, unit.Model);
                }
            }
        }

        for (int i = 0; i < level.ModelCount; i++)
        {
            if (!mobile[i] || gone[i] || reachable[i] is not null)
            {
                continue;
            }

            var fits = new bool[level.Width * level.Height];
            for (int y = 0; y < level.Height; y++)
            {
                for (int x = 0; x < level.Width; x++)
                {
                    fits[(y * level.Width) + x] = Fits(level, i, solid, x, y);
                }
            }

            reachable[i] = fits;
        }

        return reachable;
    }

    /// <summary>Anchors this fish can swim to from where it is.</summary>
    private static bool[] SwimReach(Level level, Room room, bool[] solid, int model)
    {
        int w = level.Width, h = level.Height;
        var seen = new bool[w * h];
        ref readonly ModelState at = ref room.State(model);

        if (at.IsOut || !Fits(level, model, solid, at.X, at.Y))
        {
            return seen;
        }

        var queue = new Queue<int>();
        seen[(at.Y * w) + at.X] = true;
        queue.Enqueue((at.Y * w) + at.X);

        while (queue.Count > 0)
        {
            int cell = queue.Dequeue();
            int x = cell % w, y = cell / w;

            Step(x - 1, y);
            Step(x + 1, y);
            Step(x, y - 1);
            Step(x, y + 1);

            void Step(int nx, int ny)
            {
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h)
                {
                    return;
                }

                int next = (ny * w) + nx;
                if (seen[next] || !Fits(level, model, solid, nx, ny))
                {
                    return;
                }

                seen[next] = true;
                queue.Enqueue(next);
            }
        }

        return seen;
    }

    private static Weight StrongestFish(Level level, Room room)
    {
        Weight strongest = Weight.None;
        foreach (UnitDef unit in level.Units)
        {
            ref readonly ModelState s = ref room.State(unit.Model);
            if (s.IsAlive && !s.IsOut && level.Models[unit.Model].Power > strongest)
            {
                strongest = level.Models[unit.Model].Power;
            }
        }

        return strongest;
    }

    /// <summary>
    /// Whether any placement of the pusher would actually shift this model.
    /// Reaching a pushing position is not enough - the model has to have
    /// somewhere to go. A push into a wall simply fails
    /// (<c>Rules::canMoveOthers</c>), so a direction whose destination cells are
    /// permanently blocked can be ruled out entirely, which is what frees a model
    /// wedged tightly on a shelf.
    /// </summary>
    /// <param name="strongest">
    /// The power available to an item doing the pushing: whatever fish drives the
    /// chain behind it. A fish pushing directly is held to its own strength.
    /// </param>
    private static bool CanPush(
        Level level,
        Room room,
        bool[] typeImmovable,
        bool[] mobile,
        bool[] gone,
        bool[] reachable,
        Weight strongest,
        int pusher,
        int model)
    {
        int w = level.Width, h = level.Height;
        Shape pusherShape = level.Models[pusher].Shape;
        Shape targetShape = level.Models[model].Shape;
        ref readonly ModelState target = ref room.State(model);
        Weight power = level.Models[pusher].IsAlive ? level.Models[pusher].Power : strongest;

        for (int d = 0; d < 4; d++)
        {
            (int dx, int dy) = d switch
            {
                0 => (-1, 0),
                1 => (1, 0),
                2 => (0, -1),
                _ => (0, 1),
            };

            var chain = new HashSet<int>();
            Weight heaviest = Weight.None;
            if (!CanShiftChain(level, room, typeImmovable, mobile, gone, model, dx, dy, chain, ref heaviest)
                || power < heaviest)
            {
                continue;
            }

            // A push happens when the pusher steps in this direction and its
            // shape lands on the target - so solve for the anchor directly
            // instead of scanning the room for it.
            for (int t = 0; t < targetShape.MarkX.Length; t++)
            {
                int tx = target.X + targetShape.MarkX[t], ty = target.Y + targetShape.MarkY[t];
                for (int m = 0; m < pusherShape.MarkX.Length; m++)
                {
                    int ax = tx - dx - pusherShape.MarkX[m], ay = ty - dy - pusherShape.MarkY[m];
                    if ((uint)ax < (uint)w && (uint)ay < (uint)h && reachable[(ay * w) + ax])
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a push in this direction could actually shift the model - a
    /// static reading of <c>Rules::canMoveOthers()</c>.
    ///
    /// A push is not a single body moving into a gap: it propagates. The fish
    /// pushes A, A pushes B, and the whole chain moves together
    /// (<c>Rules::moveDirBrute</c> recurses), succeeding as long as the far end
    /// has somewhere to go and the fish is strong enough for everything in it.
    /// Treating a neighbouring item as a blocker instead is what made an earlier
    /// version of this analysis unsound: two items each leaning on the other were
    /// both declared stuck, when in reality either push moves both.
    /// </summary>
    /// <param name="heaviest">The heaviest model in the chain - the push must match it.</param>
    private static bool CanShiftChain(
        Level level,
        Room room,
        bool[] typeImmovable,
        bool[] mobile,
        bool[] gone,
        int model,
        int dx,
        int dy,
        HashSet<int> visited,
        ref Weight heaviest)
    {
        if (!visited.Add(model))
        {
            return true; // already accounted for in this chain
        }

        ModelDef def = level.Models[model];
        ref readonly ModelState at = ref room.State(model);
        if (def.Weight > heaviest)
        {
            heaviest = def.Weight;
        }

        for (int m = 0; m < def.Shape.MarkX.Length; m++)
        {
            int cx = at.X + def.Shape.MarkX[m] + dx;
            int cy = at.Y + def.Shape.MarkY[m] + dy;

            if ((uint)cx >= (uint)level.Width || (uint)cy >= (uint)level.Height)
            {
                // Off the edge is the border: passable only for something whose
                // goal is to leave, solid for everything else.
                if (def.Goal is GoalKind.Out or GoalKind.Escape)
                {
                    continue;
                }

                return false;
            }

            int other = room.GetModel(cx, cy);
            if (other == Room.Empty || other == model || other >= level.ModelCount || gone[other])
            {
                continue;
            }

            if (typeImmovable[other])
            {
                return false; // the chain runs into the room itself
            }

            if (mobile[other])
            {
                // Something already known to move might well have moved away by
                // the time this push is attempted, so it cannot be relied on to
                // block. Treating it as gone is the safe direction: it can only
                // make this model look more mobile than it is.
                continue;
            }

            if (!CanShiftChain(level, room, typeImmovable, mobile, gone, other, dx, dy, visited, ref heaviest))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The model's whole shape sits on cells that are not solid.</summary>
    public static bool Fits(Level level, int model, bool[] solid, int x, int y)
    {
        Shape shape = level.Models[model].Shape;
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            int cx = x + shape.MarkX[m], cy = y + shape.MarkY[m];
            if ((uint)cx >= (uint)level.Width || (uint)cy >= (uint)level.Height)
            {
                return false;
            }

            if (solid[(cy * level.Width) + cx])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// From this anchor the model would walk out of the room by itself - the
    /// walls-only reading of <c>MarkMask::getBorderDir()</c>: it touches an edge,
    /// and nothing solid blocks the way off it.
    /// </summary>
    private static bool CanLeave(Level level, int model, bool[] solid, int x, int y)
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
                if ((uint)nx < (uint)w && (uint)ny < (uint)h && solid[(ny * w) + nx])
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

    /// <summary>Distinct models occupying the cells this model's shape would cover one step away.</summary>
    private static List<int> Neighbours(Level level, Room room, int model, int dx, int dy)
    {
        var found = new List<int>();
        ModelDef def = level.Models[model];
        ref readonly ModelState at = ref room.State(model);

        for (int m = 0; m < def.Shape.MarkX.Length; m++)
        {
            int cx = at.X + def.Shape.MarkX[m] + dx;
            int cy = at.Y + def.Shape.MarkY[m] + dy;
            int other = room.GetModel(cx, cy);
            if (other != Room.Empty && other != model && other < level.ModelCount && !found.Contains(other))
            {
                found.Add(other);
            }
        }

        return found;
    }
}
