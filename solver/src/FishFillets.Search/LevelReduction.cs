using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// Works out which of a level's models can never move, so the search can treat
/// them as scenery: they drop out of the state key (shrinking the state space)
/// and become part of the wall grid the heuristic relaxes over (sharpening the
/// bound).
///
/// <para><b>Why levels need this.</b> Most rooms are mostly decoration. A model
/// only earns a place in the state key if some sequence of moves can actually
/// disturb it, and for a lot of them nothing can - they are wedged behind walls,
/// or in a part of the room no fish can ever swim to.</para>
///
/// <para><b>The rule.</b> This grows the set of models that <i>might</i> move,
/// starting from none, and freezes whatever is left over. A model joins the
/// mobile set when either
/// <list type="number">
/// <item>something holding it up might move, so it could start falling; or</item>
/// <item>a fish strong enough to push it can reach a position that would push
///   it - fish need no support, so reachability is plain connectivity over
///   placements.</item>
/// </list>
/// Reachability is measured in a room where every model <i>already known</i> to
/// be mobile is deleted and everything else stands where the level put it.
/// Newly-mobile models delete more of the room, which lets the fish reach
/// further, which can make more models mobile - so this runs to a fixpoint.</para>
///
/// <para><b>Why growing rather than shrinking.</b> The obvious formulation -
/// assume everything moves, then prove models immobile - measures fish
/// reachability with every item deleted, so the fish reaches essentially the
/// whole room and nothing is ever provably stuck. Growing works because of an
/// induction on <i>which item moves first</i>: whatever moves first must have
/// been pushed with every item still at its starting position, which is exactly
/// what the first pass looks at. The second pass then finds whatever can move
/// second, and so on. Deleting a mobile model rather than tracking where it
/// could slide to is deliberate over-approximation - it can only ever call
/// something mobile that is not, never the reverse.</para>
///
/// <para>Chained pushes need no special case: if item A sits between the fish
/// and item B, then once A is known mobile it is deleted, the fish reaches B's
/// neighbouring cell, and B is marked mobile in the next pass.</para>
///
/// <para><b>Why an error here cannot produce a wrong answer.</b> Freezing only
/// affects the state key and the heuristic. Every edge a search follows is still
/// a real <c>Room.ApplyMove</c>, and the goal test is still the real
/// <c>Room.IsSolved</c>, so a returned path is a genuine sequence of legal moves
/// that genuinely solves the level. If this analysis were ever too aggressive,
/// two distinct states could collide in the key and the search might prune a
/// path it needed - costing optimality or a solution, never correctness. The
/// solver verifies its output by replay regardless, and
/// <c>FrozenModelsNeverMoveDuringReferenceSolutions</c> checks the analysis
/// against all 80 recorded solutions.</para>
/// </summary>
public sealed class LevelReduction
{
    private readonly Level _level;

    private LevelReduction(Level level, bool[] walls, int[] mobileModels, int[] frozenModels)
    {
        _level = level;
        Walls = walls;
        MobileModels = mobileModels;
        FrozenModels = frozenModels;
    }

    /// <summary>Cells occupied by something that can never move.</summary>
    public bool[] Walls { get; }

    /// <summary>Models that can still change - the state key's contents.</summary>
    public int[] MobileModels { get; }

    /// <summary>Models proven immobile beyond those <see cref="Level.MutableModels"/> already drops.</summary>
    public int[] FrozenModels { get; }

    public int StateKeySize => MobileModels.Length * 5;

    /// <summary>
    /// The type-level reduction only: drops what the model kinds alone prove
    /// immobile (each level's room shape, mainly). Always sound, and weak.
    /// </summary>
    public static LevelReduction Safe(Level level)
    {
        var none = new bool[level.Models.Length];
        foreach (int i in level.MutableModels)
        {
            none[i] = true;
        }

        foreach (UnitDef unit in level.Units)
        {
            none[unit.Model] = true;
        }

        return new LevelReduction(level, BuildWalls(level, none), level.MutableModels, []);
    }

    /// <summary>
    /// The full analysis, checked against a known solution and dropped back to
    /// <see cref="Safe"/> if that solution contradicts it.
    ///
    /// This is the honest form of the reduction. The analysis below is a sound
    /// over-approximation of several individual effects, but it is not a complete
    /// decision procedure and is not claimed to be one - see the class remarks.
    /// Where a real solution demonstrably moves something the analysis froze, the
    /// evidence wins.
    /// </summary>
    public static LevelReduction Verified(Level level, string? knownSolution)
    {
        LevelReduction full = Compute(level);
        if (knownSolution is null || full.FrozenModels.Length == 0)
        {
            return full;
        }

        var room = new Room(level);
        foreach (char symbol in knownSolution)
        {
            if (!room.ApplyMove(symbol))
            {
                break;
            }

            foreach (int model in full.FrozenModels)
            {
                ref readonly ModelState m = ref room.State(model);
                ModelDef def = level.Models[model];
                if (m.X != def.X || m.Y != def.Y || m.IsLost || m.IsOut)
                {
                    return Safe(level);
                }
            }
        }

        return full;
    }

    public static LevelReduction Compute(Level level)
    {
        var mobile = new bool[level.Models.Length];

        // Fish are always mobile - they are the thing that moves.
        foreach (UnitDef unit in level.Units)
        {
            mobile[unit.Model] = true;
        }

        // Models the type rules alone prove immobile can never join the mobile
        // set, whatever the fish do.
        var canEverMove = new bool[level.Models.Length];
        foreach (int i in level.MutableModels)
        {
            canEverMove[i] = true;
        }

        // The starting position is settled, so "what is holding this up" reads
        // straight off a freshly built room.
        var typeImmovable = new bool[level.Models.Length];
        for (int i = 0; i < level.ModelCount; i++)
        {
            typeImmovable[i] = !canEverMove[i];
        }

        var room = new Room(level);
        var supportOf = new List<int>[level.ModelCount];
        for (int i = 0; i < level.ModelCount; i++)
        {
            supportOf[i] = Neighbours(level, room, i, 0, 1);
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            bool[] walls = BuildWalls(level, mobile);
            List<bool[]> reachable = FishReachability(level, walls);

            for (int i = 0; i < level.ModelCount; i++)
            {
                if (mobile[i] || !canEverMove[i] || !MightMove(level, room, typeImmovable, reachable, supportOf[i], mobile, i))
                {
                    continue;
                }

                mobile[i] = true;
                changed = true;
            }
        }

        var mobileList = new List<int>();
        var frozenList = new List<int>();
        foreach (int i in level.MutableModels)
        {
            (mobile[i] ? mobileList : frozenList).Add(i);
        }

        return new LevelReduction(level, BuildWalls(level, mobile), mobileList.ToArray(), frozenList.ToArray());
    }

    /// <summary>Whether this model could join the mobile set on this pass.</summary>
    private static bool MightMove(
        Level level,
        Room room,
        bool[] typeImmovable,
        List<bool[]> reachable,
        List<int> supportOf,
        bool[] mobile,
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

        // Resting on nothing at all would mean falling. The room is settled here,
        // so this only guards a level that floats a model (the border counts as
        // support and never moves).
        if (supportOf.Count == 0)
        {
            return true;
        }

        // A fish strong enough could swim to a spot from which it pushes.
        for (int u = 0; u < level.Units.Length; u++)
        {
            int fish = level.Units[u].Model;
            if (CanPush(level, room, typeImmovable, mobile, reachable[u], fish, model))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether any reachable placement of the fish would actually shift this
    /// model. Reaching a pushing position is not enough - the model has to have
    /// somewhere to go. A push into a wall simply fails
    /// (<c>Rules::canMoveOthers</c>), so a direction whose destination cells are
    /// permanently blocked can be ruled out entirely, which is what frees a model
    /// wedged tightly on a shelf.
    /// </summary>
    private static bool CanPush(
        Level level, Room room, bool[] typeImmovable, bool[] mobile, bool[] reachable, int fish, int model)
    {
        int w = level.Width, h = level.Height;
        Shape fishShape = level.Models[fish].Shape;
        ModelDef target = level.Models[model];

        // Cells the target occupies where it stands.
        var occupied = new HashSet<int>();
        for (int m = 0; m < target.Shape.MarkX.Length; m++)
        {
            occupied.Add(((target.Y + target.Shape.MarkY[m]) * w) + target.X + target.Shape.MarkX[m]);
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!reachable[(y * w) + x])
                {
                    continue;
                }

                // A push happens when the fish steps in some direction and its
                // shape would land on the target.
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
                    if (!CanShiftChain(level, room, typeImmovable, mobile, model, dx, dy, chain, ref heaviest)
                        || level.Models[fish].Power < heaviest)
                    {
                        continue;
                    }

                    for (int m = 0; m < fishShape.MarkX.Length; m++)
                    {
                        int cx = x + fishShape.MarkX[m] + dx;
                        int cy = y + fishShape.MarkY[m] + dy;
                        if ((uint)cx < (uint)w && (uint)cy < (uint)h && occupied.Contains((cy * w) + cx))
                        {
                            return true;
                        }
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
    /// <param name="heaviest">The heaviest model in the chain - the fish must match it.</param>
    private static bool CanShiftChain(
        Level level,
        Room room,
        bool[] typeImmovable,
        bool[] mobile,
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
        if (def.Weight > heaviest)
        {
            heaviest = def.Weight;
        }

        for (int m = 0; m < def.Shape.MarkX.Length; m++)
        {
            int cx = def.X + def.Shape.MarkX[m] + dx;
            int cy = def.Y + def.Shape.MarkY[m] + dy;

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
            if (other == Room.Empty || other == model || other >= level.ModelCount)
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

            if (!CanShiftChain(level, room, typeImmovable, mobile, other, dx, dy, visited, ref heaviest))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Every anchor cell each fish could ever occupy, as plain connectivity over
    /// placements that clear the walls. Sound because a fish needs no support and
    /// items only ever obstruct it, so deleting the unfrozen ones can only widen
    /// this - which makes freezing strictly more cautious.
    /// </summary>
    private static List<bool[]> FishReachability(Level level, bool[] walls)
    {
        int w = level.Width, h = level.Height;
        var result = new List<bool[]>(level.Units.Length);

        foreach (UnitDef unit in level.Units)
        {
            var seen = new bool[w * h];
            var queue = new Queue<int>();
            ModelDef fish = level.Models[unit.Model];

            if (Fits(level, unit.Model, walls, fish.X, fish.Y))
            {
                seen[(fish.Y * w) + fish.X] = true;
                queue.Enqueue((fish.Y * w) + fish.X);
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
                    if (seen[next] || !Fits(level, unit.Model, walls, nx, ny))
                    {
                        return;
                    }

                    seen[next] = true;
                    queue.Enqueue(next);
                }
            }

            result.Add(seen);
        }

        return result;
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

    /// <summary>Distinct models occupying the cells this model's shape would cover one step away.</summary>
    private static List<int> Neighbours(Level level, Room room, int model, int dx, int dy)
    {
        var found = new List<int>();
        ModelDef def = level.Models[model];

        for (int m = 0; m < def.Shape.MarkX.Length; m++)
        {
            int cx = def.X + def.Shape.MarkX[m] + dx;
            int cy = def.Y + def.Shape.MarkY[m] + dy;
            int other = room.GetModel(cx, cy);
            if (other != Room.Empty && other != model && other < level.ModelCount && !found.Contains(other))
            {
                found.Add(other);
            }
        }

        return found;
    }

    /// <summary>
    /// Cells blocked for the purposes of the analysis: everything not (yet) known
    /// to be mobile, standing where the level put it. Models already known mobile
    /// are deleted outright - deliberately generous, since over-estimating where
    /// a fish can go only ever over-estimates what is mobile.
    /// </summary>
    private static bool[] BuildWalls(Level level, bool[] mobile)
    {
        var walls = new bool[level.Width * level.Height];
        for (int i = 0; i < level.ModelCount; i++)
        {
            if (mobile[i])
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

    public override string ToString() =>
        $"{_level.Name}: {MobileModels.Length} mobile, {FrozenModels.Length} frozen " +
        $"(of {_level.MutableModels.Length} type-mutable)";
}
