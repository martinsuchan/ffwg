namespace FishFillets.Physics;

public sealed partial class Room
{
    // The round pipeline. Port of legacy/src/level/Room.cpp's
    // nextRound/beginFall/prepareRound/fallout/falldown/loadMove
    // (web/src/game/Room.ts), minus everything that needs a player or a screen.
    //
    // The engine's decide-this-round / apply-next-round split (docs/007) is kept
    // exactly: a move only sets Rules.dir, and the position is committed by the
    // NEXT round's prepareRound(). What that means for a search is spelled out on
    // ApplyMove() below.

    /// <summary>
    /// Settles pending falls, checks who died, then lets escaping models step
    /// toward the border or unsupported items fall one cell. Port of
    /// Room::beginFall().
    /// </summary>
    private void BeginFall()
    {
        PrepareRound();
        _lastAction = Action.No;
        // Reset every round, so a round where nothing falls (or where fallout()
        // pre-empts falldown() entirely) reports "no impact" rather than a stale
        // value from an earlier round.
        _lastImpact = Weight.None;

        if (Fallout())
        {
            _lastAction = Action.Move;
        }
        else if (ComputeFall())
        {
            _lastAction = Action.Fall;
        }
    }

    /// <summary>Applies last round's pending moves, then checks who died as a result.</summary>
    private void PrepareRound()
    {
        for (int i = 0; i < ModelCount; i++)
        {
            FreeOldPos(i);
        }

        for (int i = 0; i < ModelCount; i++)
        {
            OccupyNewPos(i);
        }

        _lastDeadCount = 0;
        for (int i = 0; i < ModelCount; i++)
        {
            if (CheckDead(i, _lastAction))
            {
                _lastDeadCount++;
            }
        }

        for (int i = 0; i < ModelCount; i++)
        {
            ChangeState(i);
        }
    }

    /// <summary>Lets goal_escape/goal_out models walk toward and through the border.</summary>
    private bool Fallout()
    {
        bool wentOut = false;
        for (int i = 0; i < ModelCount; i++)
        {
            if (!_models[i].IsLost && ActionOut(i) > 0)
            {
                wentOut = true;
            }
        }

        return wentOut;
    }

    /// <summary>
    /// Applies exactly one move symbol and settles the room completely, leaving
    /// it fresh. Port of Room::loadMove() ("let object to fall fast"), which is
    /// how the original replays a recorded solution.
    ///
    /// This is also the search's edge function. The original settles BEFORE
    /// applying and leaves the consequences to the next call; this settles after,
    /// so that every state a search touches is a settled one. The two produce the
    /// same sequence of settled states because <see cref="Reset"/> also settles
    /// (a room's items can start unsupported, which is the load-time settling the
    /// player sees) and settling is idempotent on an already-fresh room. The one
    /// thing that is NOT idempotent is a corpse's removal countdown, and that
    /// only matters in states a solver has already pruned (a dead fish fails its
    /// goal - see <see cref="IsSolvable"/>).
    ///
    /// On a rejected move the room is unchanged except for TouchDir, which
    /// <see cref="SetTouched"/> records on whatever was pushed against. That is
    /// write-only as far as physics goes (the next round's OccupyNewPos clears
    /// it), but a search must therefore leave TouchDir out of its state key.
    /// </summary>
    /// <returns>False if no unit owns the symbol, or the move it names is blocked.</returns>
    public bool ApplyMove(char symbol)
    {
        if (!MakeMove(symbol))
        {
            return false;
        }

        _lastAction = Action.Move;
        SettleAll();
        return true;
    }

    /// <summary>
    /// Resolves every pending fall and border crossing until nothing is left
    /// (<see cref="IsFresh"/>). Bounded defensively: a real level can never fall
    /// forever, so hitting the cap means a bug, not a big level.
    /// </summary>
    public void SettleAll()
    {
        int rounds = 0;
        do
        {
            BeginFall();
            if (++rounds > MaxSettleRounds)
            {
                throw new InvalidOperationException(
                    $"{Level.Name}: settling did not converge - possible infinite fall loop");
            }
        }
        while (!IsFresh);
    }
}
