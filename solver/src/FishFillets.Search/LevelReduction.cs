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
/// <item>something already known to move could reach a position that would shove
///   it, hard enough to shift whatever chain it is part of.</item>
/// </list>
/// Reachability is measured in a room where every model <i>already known</i> to
/// be mobile is deleted and everything else stands where it currently is.
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
/// <para><b>Any mobile model is a pusher, not just a fish</b> (solver/docs/012).
/// A push is a chain, and the body that lands on the model being moved need not
/// be the fish's. In <c>magnet</c> the big fish pushes a seven-cell item whose
/// own bottom cell is what shifts a heavy bar, from a square the fish can never
/// occupy - right of the bar is solid and the fish is two cells tall. An earlier
/// version argued chains needed no special case, because deleting the item in
/// between lets the fish reach the vacated square and push directly; that is
/// wrong whenever the two shapes differ. <c>corals</c> is the same defect one
/// level deeper (fish, then three items in series), where the last item only
/// becomes pushable 122 moves in, once other items have moved next to it. Since
/// an item goes where it is shoved rather than where it swims, the honest bound
/// on where one might stand is every placement that fits at all, and the honest
/// bound on its strength is the strongest fish.</para>
///
/// <para><b>Positions come from the settled room, not the level file</b>
/// (solver/docs/012). A level may declare a model in mid-air and let the opening
/// settle drop it - <c>society</c> writes item 11 at (7,2) and it comes to rest
/// at (7,12). Reading shapes from <c>ModelDef.X/Y</c> while querying occupancy
/// through <see cref="Room.GetModel"/> mixes two different geometries on exactly
/// those levels.</para>
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

    private readonly bool[] _frozen;

    private LevelReduction(Level level, bool[] walls, int[] mobileModels, int[] frozenModels)
    {
        _level = level;
        Walls = walls;
        MobileModels = mobileModels;
        FrozenModels = frozenModels;

        _frozen = new bool[level.Models.Length];
        Array.Fill(_frozen, true);
        foreach (int i in mobileModels)
        {
            _frozen[i] = false;
        }

        foreach (UnitDef unit in level.Units)
        {
            _frozen[unit.Model] = false;
        }
    }

    /// <summary>Cells occupied by something that can never move.</summary>
    public bool[] Walls { get; }

    /// <summary>Models that can still change - the state key's contents.</summary>
    public int[] MobileModels { get; }

    /// <summary>Models proven immobile beyond those <see cref="Level.MutableModels"/> already drops.</summary>
    public int[] FrozenModels { get; }

    public int StateKeySize => MobileModels.Length * 5;

    /// <summary>
    /// Whether this model is scenery to the search - proven immobile either by
    /// the type rules or by the analysis. Precomputed: this is called from inside
    /// the router's per-cell scans.
    /// </summary>
    public bool IsFrozen(int model) => _frozen[model];

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

        return new LevelReduction(level, StuckAnalysis.SolidCells(level, new Room(level), none), level.MutableModels, []);
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

        // Against the settled opening, not ModelDef.X/Y: a level that floats a
        // model in its file has it fall before the first move, and calling that
        // first fall "a frozen model moved" throws the analysis away on levels
        // where nothing is wrong (solver/docs/012).
        var startX = new int[level.ModelCount];
        var startY = new int[level.ModelCount];
        for (int i = 0; i < level.ModelCount; i++)
        {
            startX[i] = room.State(i).X;
            startY[i] = room.State(i).Y;
        }

        foreach (char symbol in knownSolution)
        {
            if (!room.ApplyMove(symbol))
            {
                break;
            }

            foreach (int model in full.FrozenModels)
            {
                ref readonly ModelState m = ref room.State(model);
                if (m.X != startX[model] || m.Y != startY[model] || m.IsLost || m.IsOut)
                {
                    return Safe(level);
                }
            }
        }

        return full;
    }

    public static LevelReduction Compute(Level level)
    {
        // The opening position is just one state, so this is StuckAnalysis run on
        // it. Keeping one implementation matters: the two bugs solver/docs/012
        // found were both in this analysis, and a copy of it would have had to be
        // fixed twice.
        var room = new Room(level);
        bool[] mobile = StuckAnalysis.Mobile(level, room);

        var mobileList = new List<int>();
        var frozenList = new List<int>();
        foreach (int i in level.MutableModels)
        {
            (mobile[i] ? mobileList : frozenList).Add(i);
        }

        return new LevelReduction(
            level, StuckAnalysis.SolidCells(level, room, mobile), [.. mobileList], [.. frozenList]);
    }

    public override string ToString() =>
        $"{_level.Name}: {MobileModels.Length} mobile, {FrozenModels.Length} frozen " +
        $"(of {_level.MutableModels.Length} type-mutable)";
}
