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
    // Known optimisation for later (see solver/docs/001): this is a fixpoint over
    // ALL models each round, i.e. O(n^2) on the item-heavy levels (gems has 111
    // movable items). Since a move only ever disturbs support locally, the search
    // will want an incremental version that reconsiders just the models whose
    // support actually changed. Left faithful for now - correctness first.

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
