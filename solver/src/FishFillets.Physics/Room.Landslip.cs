namespace FishFillets.Physics;

public sealed partial class Room
{
    // Per-round gravity: works out which inert objects are unsupported and lets
    // them fall one cell. Port of legacy/src/level/Landslip.h/.cpp
    // (web/src/game/Landslip.ts). Fish never fall - alive models count as fixed.
    //
    // The original allocates a Landslip object (and a std::set of "stoned"
    // models) every round; here the marker array is owned by the Room and just
    // cleared, so a round costs no allocation.
    //
    // "Stoned" means supported: fixed in place, or resting (transitively) on
    // something that is. Everything else falls. This is a fixpoint - rescan every
    // model until a whole pass changes nothing.
    //
    // It reads as O(n^2), and docs/001 recorded it as the thing to make incremental
    // "on the item-heavy levels". Measured, that is wrong for almost every level:
    // StoneModel short-circuits on an already-stoned model, so once the first pass
    // has stoned nearly everything, later passes are n cheap boolean checks. The
    // passes actually needed, per call:
    //
    //     start 2.00   wc 2.00   cannons 2.03   alibaba 3.39
    //     columns 3.00   experiments 7.00   gems 8.99
    //
    // So it is 2 passes - not n - on the rooms that are not deep stacks.
    //
    // A worklist version was built and measured against this (solver/docs/011):
    // seed in one pass, then propagate upwards from each newly stoned model, since
    // support only ever travels up. Same answer everywhere, and **slower on all but
    // one level**: 0.75-0.88x where the fixpoint needs 2-3.4 passes, 0.98x at 7
    // passes, and only 1.24x on gems at 9. It was not worth a second code path in
    // the physics hot path for one level, so this stands.

    /// <returns>Whether anything fell.</returns>
    private bool ComputeFall()
    {
        Array.Clear(_stoned);

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < ModelCount; i++)
            {
                if (StoneModel(i))
                {
                    changed = true;
                }
            }
        }

        bool falling = false;
        for (int i = 0; i < ModelCount; i++)
        {
            if (FallModel(i))
            {
                falling = true;
            }
        }

        return falling;
    }

    private bool StoneModel(int model)
    {
        if (!IsStoned(model) && (IsFixedForFall(model) || IsOnPad(model)))
        {
            _stoned[model] = true;
            return true;
        }

        return false;
    }

    private bool IsOnPad(int model)
    {
        using ResistArena.Frame resist = GetResist(model, Dir.Down);
        foreach (int pad in resist.Items)
        {
            if (IsFixedForFall(pad))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsFixedForFall(int model)
    {
        ref ModelState m = ref _models[model];
        return IsStoned(model) || IsWall(model) || m.IsAlive || m.IsLost;
    }

    private bool IsStoned(int model) => IsBorder(model) || _stoned[model];

    private bool FallModel(int model)
    {
        if (!IsFixedForFall(model))
        {
            ActionFall(model);
            return true;
        }

        if (ClearLastFall(model) && _lastImpact < _models[model].Weight)
        {
            _lastImpact = _models[model].Weight;
        }

        return false;
    }
}
