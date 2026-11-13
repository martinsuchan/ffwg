namespace FishFillets.Physics;

public sealed partial class Room
{
    // A shortcut through ApplyMove for the moves that cannot change anything but
    // the fish making them: turning round, and swimming into open water from
    // under nothing. Measured over a solver-style exploration, those are 70-75%
    // of all move attempts and 80-89% of the time ApplyMove spends (review P2) -
    // each one running a full round: four passes over every model, death checks,
    // the fall fixpoint and the border walk, to conclude that nothing happened.
    //
    // Why a round after such a move is provably empty, given a fresh room (the
    // last round found nothing to fall and nobody to leave, on this very
    // configuration) and no corpse counting down:
    //
    //  - Turn: nothing moves. Deaths, falls and exits are functions of the
    //    configuration, which is the one the last round already checked.
    //  - Inert step: the destination cells are empty, so nothing is pushed; no
    //    movable model rests on the fish's cells, so nothing loses support;
    //    arriving under a settled stack cannot crush (the stack was already
    //    held by a wall or a strong enough fish, and still is); nothing else was
    //    pushed this round, so CheckDeadMove has nothing to fire on. The one
    //    thing that can change is who may leave the room: the fish itself at its
    //    new place, or another goal model whose way out it was blocking - both
    //    only ever true for a model touching an edge, so any such model sends the
    //    move down the full path instead.
    //
    // What the shortcut does not reproduce: TouchDir is not cleared on every
    // model, as a real round's OccupyNewPos would. It is write-only for the
    // physics (only level Lua reads it, which the solver has none of) and
    // excluded from the state key and the physics fingerprint for exactly this
    // reason. Everything else - every other field of every model, the step
    // count, freshness - is identical, which PhysicsDigestTests checks over
    // random walks on every level.

    // Shape bounds relative to the anchor, per model, for the edge test.
    private sbyte[] _minMarkX = [];
    private sbyte[] _minMarkY = [];
    private sbyte[] _maxMarkX = [];
    private sbyte[] _maxMarkY = [];

    // Models that would walk out of the room by themselves at an edge.
    private int[] _goalModels = [];

    private void InitFastPath()
    {
        int n = Level.Models.Length;
        _minMarkX = new sbyte[n];
        _minMarkY = new sbyte[n];
        _maxMarkX = new sbyte[n];
        _maxMarkY = new sbyte[n];
        var goals = new List<int>();

        for (int i = 0; i < n; i++)
        {
            Shape shape = Level.Models[i].Shape;
            sbyte minX = sbyte.MaxValue, minY = sbyte.MaxValue, maxX = sbyte.MinValue, maxY = sbyte.MinValue;
            for (int m = 0; m < shape.MarkX.Length; m++)
            {
                minX = Math.Min(minX, shape.MarkX[m]);
                minY = Math.Min(minY, shape.MarkY[m]);
                maxX = Math.Max(maxX, shape.MarkX[m]);
                maxY = Math.Max(maxY, shape.MarkY[m]);
            }

            (_minMarkX[i], _minMarkY[i], _maxMarkX[i], _maxMarkY[i]) = (minX, minY, maxX, maxY);
            if (i < ModelCount && ShouldGoOut(i))
            {
                goals.Add(i);
            }
        }

        _goalModels = [.. goals];
    }

    /// <summary>
    /// Applies <paramref name="symbol"/> without simulating a round when that
    /// provably changes nothing else. False means "not handled here" - the caller
    /// runs the full path, which also decides whether the move is legal at all.
    /// </summary>
    private bool TryFastMove(char symbol)
    {
        if (_lastAction != Action.No)
        {
            return false;
        }

        foreach (UnitDef unit in Level.Units)
        {
            Dir dir = symbol == unit.Left ? Dir.Left
                : symbol == unit.Right ? Dir.Right
                : symbol == unit.Up ? Dir.Up
                : symbol == unit.Down ? Dir.Down
                : Dir.No;
            if (dir == Dir.No)
            {
                continue;
            }

            int model = unit.Model;
            ref ModelState m = ref _models[model];
            if (!m.IsAlive || m.IsLost || HasPendingCorpse())
            {
                return false;
            }

            if ((dir == Dir.Left && !m.IsLeft) || (dir == Dir.Right && m.IsLeft))
            {
                m.IsLeft = !m.IsLeft;   // Unit::goLeft/goRight against your facing: a turn
                FinishFastMove();
                return true;
            }

            if (!IsInertStep(model, dir))
            {
                return false;
            }

            Unmask(model);
            m.X += (short)dir.X();
            m.Y += (short)dir.Y();
            Mask(model);
            FinishFastMove();
            return true;
        }

        return false;
    }

    private void FinishFastMove()
    {
        _stepCount++;
        _lastAction = Action.No;
        _lastImpact = Weight.None;
        _lastDeadCount = 0;
    }

    /// <summary>A dead fish not yet removed: rounds tick its removal countdown, so none may be skipped.</summary>
    private bool HasPendingCorpse()
    {
        foreach (UnitDef unit in Level.Units)
        {
            ref ModelState m = ref _models[unit.Model];
            if (!m.IsAlive && !m.IsLost)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsInertStep(int model, Dir dir)
    {
        Shape shape = Level.Models[model].Shape;
        int x = _models[model].X, y = _models[model].Y;
        int dx = dir.X(), dy = dir.Y();

        for (int k = 0; k < shape.MarkX.Length; k++)
        {
            // Into open water only: anything else is a push or a refusal.
            int nx = x + shape.MarkX[k] + dx, ny = y + shape.MarkY[k] + dy;
            if ((uint)nx >= (uint)_width || (uint)ny >= (uint)_height)
            {
                return false;
            }

            int there = _field[(ny * _width) + nx];
            if (there != Empty && there != model)
            {
                return false;
            }

            // Holding nothing up: whatever sits on the fish must be wall.
            int ax = x + shape.MarkX[k], ay = y + shape.MarkY[k] - 1;
            if ((uint)ax < (uint)_width && (uint)ay < (uint)_height)
            {
                int above = _field[(ay * _width) + ax];
                if (above != Empty && above != model && !IsWall(above))
                {
                    return false;
                }
            }
        }

        // Nobody's way out may change: not the fish's at its new place, and not
        // that of another goal model it might have been blocking.
        foreach (int goal in _goalModels)
        {
            ref ModelState g = ref _models[goal];
            if (g.IsLost)
            {
                continue;
            }

            int gx = g.X + (goal == model ? dx : 0), gy = g.Y + (goal == model ? dy : 0);
            if (gx + _minMarkX[goal] <= 0 || gy + _minMarkY[goal] <= 0
                || gx + _maxMarkX[goal] >= _width - 1 || gy + _maxMarkY[goal] >= _height - 1)
            {
                return false;
            }
        }

        return true;
    }
}
