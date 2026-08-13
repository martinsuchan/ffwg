namespace FishFillets.Physics;

public sealed partial class Room
{
    // Turning a move symbol into a move. Port of legacy/src/level/Unit.cpp's
    // driveOrder/goLeft/goRight/goUp/goDown plus the one entry point of
    // Controls.cpp the solver needs, makeMove() (web/src/game/{Unit,Controls}.ts).
    //
    // Everything about *interactive* control is gone: held/queued keys, the
    // shared arrow keys, which fish is "active", mouse pathfinding. A symbol
    // names both the fish and the direction on its own, which is exactly why a
    // search never has to model an active fish - and why a solution is a plain
    // string that the browser port can replay unchanged (docs/021).

    /// <summary>
    /// Drives whichever unit owns <paramref name="symbol"/>, regardless of which
    /// fish a player would have had selected. Port of Controls::makeMove().
    /// </summary>
    /// <returns>Whether a unit moved or turned.</returns>
    private bool MakeMove(char symbol)
    {
        foreach (UnitDef unit in Level.Units)
        {
            if (DriveOrder(unit, symbol))
            {
                _stepCount++;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Applies <paramref name="symbol"/> if it belongs to this unit. Port of
    /// Unit::driveOrder().
    ///
    /// Gated on <see cref="WillMove"/> (alive and not out), NOT on the
    /// interactive canDrive() - i.e. it ignores `busy`. A recorded symbol is an
    /// already-decided move that provably happened; re-checking `busy` desyncs
    /// windoze's replay, whose live Lua toggles it a round off from when the
    /// string was recorded (docs/035).
    /// </summary>
    private bool DriveOrder(in UnitDef unit, char symbol)
    {
        if (!WillMove(unit.Model))
        {
            return false;
        }

        if (symbol == unit.Left) return GoLeft(unit.Model);
        if (symbol == unit.Right) return GoRight(unit.Model);
        if (symbol == unit.Up) return ActionMoveDir(unit.Model, Dir.Up);
        if (symbol == unit.Down) return ActionMoveDir(unit.Model, Dir.Down);
        return false;
    }

    /// <summary>This unit can still move at some point - alive and not yet out (Unit::willMove()).</summary>
    private bool WillMove(int model)
    {
        ref ModelState m = ref _models[model];
        return m.IsAlive && !m.IsLost;
    }

    /// <summary>
    /// Facing left is required before a left move actually happens - facing right
    /// first just turns you around, and that turn costs a move symbol of its own
    /// (Unit::goLeft()). The same symbol records both, so replaying a string
    /// against the same starting facing reproduces the turn/move split with no
    /// separate marker (docs/021) - which is also why facing has to be part of a
    /// search state.
    /// </summary>
    private bool GoLeft(int model)
    {
        if (_models[model].IsLeft)
        {
            return ActionMoveDir(model, Dir.Left);
        }

        ActionTurnSide(model);
        return true;
    }

    private bool GoRight(int model)
    {
        if (!_models[model].IsLeft)
        {
            return ActionMoveDir(model, Dir.Right);
        }

        ActionTurnSide(model);
        return true;
    }

    /// <summary>Every move symbol this level accepts, for a search's successor generation.</summary>
    public void GetMoveSymbols(Span<char> destination, out int count)
    {
        count = 0;
        foreach (UnitDef unit in Level.Units)
        {
            destination[count++] = unit.Up;
            destination[count++] = unit.Down;
            destination[count++] = unit.Left;
            destination[count++] = unit.Right;
        }
    }

    /// <summary>Upper bound for <see cref="GetMoveSymbols"/>'s buffer.</summary>
    public int MoveSymbolCount => Level.Units.Length * 4;
}
