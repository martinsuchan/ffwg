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
    public static bool[] Mobile(Level level, Room room, bool fishAnywhere = false) =>
        Mobile(level, room, fishAnywhere, null, out _);

    /// <inheritdoc cref="Mobile(Level, Room, bool)"/>
    /// <param name="fixedSolid">
    /// <see cref="FixedSolid"/> for this level, if the caller keeps it: the cells
    /// of the models that can never move, which are the same in every state and
    /// - with the room's own walls among them - most of what each pass would
    /// otherwise redraw.
    /// </param>
    /// <param name="solid">The solid cells for the answer - <see cref="SolidCells"/> of it.</param>
    public static bool[] Mobile(Level level, Room room, bool fishAnywhere, bool[]? fixedSolid, out bool[] solid)
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

        // Only for models that could join the set: the walls and the rest of
        // the type-immovable are never asked about, and the room's own shape
        // is by far the largest there is.
        var supportOf = new List<int>[level.ModelCount];
        for (int i = 0; i < level.ModelCount; i++)
        {
            if (mobile[i] || gone[i] || !canEverMove[i])
            {
                continue;
            }

            supportOf[i] = Neighbours(level, room, i, 0, 1);

            // With the fish anywhere, where they actually are must not matter -
            // and it would, through support: an item resting on a fish counted
            // as mobile, the same item beside it did not. The answer is cached
            // per item layout, which leaves fish positions out, so a verdict
            // then depended on which state of a layout was analysed first, and
            // parallel workers disagreed with the serial search (docs/020).
            // Dropping living fish as supports is sound: an item a fish alone
            // holds up is left resting on nothing, which counts as mobile; one
            // that also rests on something immobile does not fall when the fish
            // leaves, and a fish lifting it from below is a push, which
            // MightMove already tries from every placement a fish fits.
            if (fishAnywhere)
            {
                supportOf[i].RemoveAll(m => mobile[m] && level.Models[m].IsAlive);
            }
        }

        Weight strongest = StrongestFish(level, room);

        bool changed = true;
        solid = [];
        while (changed)
        {
            changed = false;
            solid = fixedSolid is null ? SolidCells(level, room, mobile) : MutableSolid(level, room, mobile, fixedSolid);
            bool[]?[] reachable = PusherReachability(level, room, solid, mobile, gone, fishAnywhere);

            for (int i = 0; i < level.ModelCount; i++)
            {
                if (mobile[i] || gone[i] || !canEverMove[i]
                    || !MightMove(level, room, typeImmovable, solid, reachable, supportOf[i], mobile, gone, strongest, i))
                {
                    continue;
                }

                mobile[i] = true;
                changed = true;

                // Free its cells at once rather than at the next pass, so the
                // rest of this pass already sees the room it leaves behind. The
                // rule is monotone - more mobile means less solid, which only
                // ever lets more models move - so the fixpoint is the same in
                // any order; this just reaches it in fewer passes. gems spreads
                // mobility through a heap of gems one layer per pass (docs/021).
                Shape shape = level.Models[i].Shape;
                ref readonly ModelState at = ref room.State(i);
                for (int m = 0; m < shape.MarkX.Length; m++)
                {
                    int cx = at.X + shape.MarkX[m], cy = at.Y + shape.MarkY[m];
                    if ((uint)cx < (uint)level.Width && (uint)cy < (uint)level.Height)
                    {
                        solid[(cy * level.Width) + cx] = false;
                    }
                }
            }
        }

        return mobile;
    }

    /// <summary>
    /// The cells of every model that can never move (the room's walls among
    /// them) - the part of <see cref="SolidCells"/> that is the same in every
    /// state of a level.
    /// </summary>
    public static bool[] FixedSolid(Level level, Room room)
    {
        var mutable = new bool[level.Models.Length];
        foreach (int i in level.MutableModels)
        {
            mutable[i] = true;
        }

        var all = new bool[level.Models.Length];
        for (int i = 0; i < level.ModelCount; i++)
        {
            all[i] = mutable[i];
        }

        return SolidCells(level, room, all);
    }

    /// <summary><see cref="SolidCells"/>, starting from <see cref="FixedSolid"/> and adding only the models that can move.</summary>
    private static bool[] MutableSolid(Level level, Room room, bool[] mobile, bool[] fixedSolid)
    {
        var solid = (bool[])fixedSolid.Clone();
        foreach (int i in level.MutableModels)
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
        bool[] solid,
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
        // Direction first: whether the chain in front of the model can give way
        // does not depend on who pushes, so it is worked out once per direction
        // rather than once per pusher and direction (docs/020).
        int[][][] faces = Faces(level);
        int[] shapeClass = ShapeClasses(level);
        var tried = new bool[level.ModelCount];
        var chain = new HashSet<int>();
        for (int d = 0; d < 4; d++)
        {
            (int dx, int dy) = d switch
            {
                0 => (-1, 0),
                1 => (1, 0),
                2 => (0, -1),
                _ => (0, 1),
            };

            chain.Clear();
            Weight heaviest = Weight.None;
            if (!CanShiftChain(level, room, typeImmovable, mobile, gone, model, dx, dy, chain, ref heaviest))
            {
                continue;
            }

            Array.Clear(tried);
            for (int pusher = 0; pusher < level.ModelCount; pusher++)
            {
                if (pusher == model || !mobile[pusher] || gone[pusher] || reachable[pusher] is not bool[] where)
                {
                    continue;
                }

                // Items that may stand anywhere they fit, pushing with the
                // strongest fish's power, differ only in their shape - so one of
                // each shape answers for all of them. gems has 90 movable gems
                // in a handful of shapes; trying every one as a pusher of every
                // other made each analysis cost ~0.4 ms (docs/021).
                if (where == Anywhere && !level.Models[pusher].IsAlive)
                {
                    if (tried[shapeClass[pusher]])
                    {
                        continue;
                    }

                    tried[shapeClass[pusher]] = true;
                }

                // A fish pushing directly is held to its own strength; an item
                // doing the pushing has whatever fish drives the chain behind it.
                Weight power = level.Models[pusher].IsAlive ? level.Models[pusher].Power : strongest;
                if (power >= heaviest
                    && PusherLandsOn(level, room, solid, where, pusher, model, dx, dy, faces[pusher][d], faces[model][d ^ 1]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// "Wherever it fits": the placements are not listed but tested on demand
    /// (<see cref="Fits"/>), because <see cref="PusherLandsOn"/> only ever asks about
    /// the few anchors that would land on its target. Building the full map per
    /// model per pass was most of the analysis's cost (docs/020).
    /// </summary>
    private static readonly bool[] Anywhere = [];

    /// <summary>
    /// Where each model that might move could be standing when it does the
    /// shoving. A fish swims, so plain connectivity from where it is; an item
    /// only goes where it is pushed, so anything tighter than "every placement
    /// that fits" (<see cref="Anywhere"/>) would have to model how it got there.
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

            reachable[i] = Anywhere;
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
    /// Whether the pusher can stand where a step in (dx, dy) lands its shape on
    /// the model. Reaching a pushing position is not enough on its own - the
    /// caller has already checked that the chain in front has somewhere to go
    /// (a push into a wall simply fails, <c>Rules::canMoveOthers</c>), which is
    /// what frees a model wedged tightly on a shelf.
    /// </summary>
    /// <param name="pusherFront">The pusher's marks with no mark of its own one step ahead (see <see cref="Faces"/>).</param>
    /// <param name="targetBack">The target's marks with no mark of its own one step behind.</param>
    private static bool PusherLandsOn(
        Level level, Room room, bool[] solid, bool[] reachable, int pusher, int model, int dx, int dy,
        int[] pusherFront, int[] targetBack)
    {
        int w = level.Width, h = level.Height;
        Shape pusherShape = level.Models[pusher].Shape;
        Shape targetShape = level.Models[model].Shape;
        ref readonly ModelState target = ref room.State(model);

        // A push happens when the pusher steps in this direction and its shape
        // lands on the target - so solve for the anchor directly instead of
        // scanning the room for it. Only the pusher's front face can make the
        // contact, and only on the target's back face: any other pairing puts
        // the two shapes on a common cell before the step, and the target's
        // cells are solid, so Fits would reject that anchor anyway.
        foreach (int t in targetBack)
        {
            int tx = target.X + targetShape.MarkX[t], ty = target.Y + targetShape.MarkY[t];
            foreach (int m in pusherFront)
            {
                int ax = tx - dx - pusherShape.MarkX[m], ay = ty - dy - pusherShape.MarkY[m];
                if ((uint)ax < (uint)w && (uint)ay < (uint)h
                    && (reachable == Anywhere ? Fits(level, pusher, solid, ax, ay) : reachable[(ay * w) + ax]))
                {
                    return true;
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

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Level, int[][][]> FaceCache = [];

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Level, int[]> ShapeClassCache = [];

    /// <summary>
    /// Per model, a small id shared by every model with exactly the same shape
    /// (the same marks, in the same order). Depends on the shapes only, so
    /// worked out once per level.
    /// </summary>
    private static int[] ShapeClasses(Level level) => ShapeClassCache.GetValue(level, static l =>
    {
        var ids = new Dictionary<string, int>();
        var classes = new int[l.ModelCount];
        for (int i = 0; i < l.ModelCount; i++)
        {
            Shape shape = l.Models[i].Shape;
            string key = string.Join(",", Enumerable.Range(0, shape.MarkX.Length).Select(m => $"{shape.MarkX[m]}:{shape.MarkY[m]}"));
            if (!ids.TryGetValue(key, out int id))
            {
                id = ids.Count;
                ids[key] = id;
            }

            classes[i] = id;
        }

        return classes;
    });

    /// <summary>
    /// Per model and direction (0 left, 1 right, 2 up, 3 down - paired so that
    /// <c>d ^ 1</c> is the opposite), the indices of the shape's marks with no
    /// mark of the same shape one step that way: its face on that side.
    /// Depends on the shapes only, so worked out once per level.
    /// </summary>
    private static int[][][] Faces(Level level) => FaceCache.GetValue(level, static l =>
    {
        var faces = new int[l.ModelCount][][];
        for (int i = 0; i < l.ModelCount; i++)
        {
            Shape shape = l.Models[i].Shape;
            var cells = new HashSet<(int, int)>();
            for (int m = 0; m < shape.MarkX.Length; m++)
            {
                cells.Add((shape.MarkX[m], shape.MarkY[m]));
            }

            faces[i] = new int[4][];
            for (int d = 0; d < 4; d++)
            {
                (int dx, int dy) = d switch
                {
                    0 => (-1, 0),
                    1 => (1, 0),
                    2 => (0, -1),
                    _ => (0, 1),
                };

                faces[i][d] = [.. Enumerable.Range(0, shape.MarkX.Length)
                    .Where(m => !cells.Contains((shape.MarkX[m] + dx, shape.MarkY[m] + dy)))];
            }
        }

        return faces;
    });

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
    internal static bool CanLeave(Level level, int model, bool[] solid, int x, int y)
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
