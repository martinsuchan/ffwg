namespace FishFillets.Physics;

/// <summary>
/// The tri-state "is this model, directly or through a stack, resting on X"
/// tests. Port of legacy/src/level/{OnStack,OnWall,OnStrongPad}.h
/// (web/src/game/OnCondition.ts), as an enum rather than an interface so the
/// recursion carries no allocation and no virtual dispatch.
/// </summary>
internal enum OnCond : byte
{
    /// <summary>
    /// OnStack: an inert, settled object that is itself safely supported.
    /// Unreachable, like the original's OnStack.h - legacy Rules exposes
    /// isOnStack() but nothing ever calls it (the browser port carries the same
    /// dead branch). Kept so the condition set matches the game's.
    /// </summary>
    Stack,

    /// <summary>OnWall: the wall (or the border) itself.</summary>
    Wall,

    /// <summary>OnStrongPad(weight): the wall, or a fish strong enough to hold `weight`.</summary>
    StrongPad,
}

public sealed partial class Room
{
    // Movement, pushing, falling and death rules for one model. Port of
    // legacy/src/level/Rules.h/.cpp (web/src/game/Rules.ts); see docs/007 for
    // the plain-English writeup of what each check means.
    //
    // Every method takes the model index the original would have called on
    // `this`, so the structure stays readable side by side with Rules.cpp.
    //
    // Intentionally dropped: save/undo (change_setLocation), the "strict_rules"
    // option (this always uses the strict, default branch of checkDeadMove),
    // and the visual move-streak. touchDir and touchSpec ARE ported - touchSpec
    // is load-bearing for windoze's output plug.

    /// <summary>Applies the move decided last round (Rules::occupyNewPos()).</summary>
    private void OccupyNewPos(int model)
    {
        ref ModelState m = ref _models[model];
        m.TouchDir = Dir.No;
        if (m.Dir != Dir.No)
        {
            m.Pushing = false;
            m.X += (short)m.Dir.X();
            m.Y += (short)m.Dir.Y();
            Mask(model);
        }
    }

    private void FreeOldPos(int model)
    {
        if (_models[model].Dir != Dir.No)
        {
            Unmask(model);
        }
    }

    private bool CheckDead(int model, Action lastAction)
    {
        bool dead = false;
        if (_models[model].IsAlive)
        {
            dead = lastAction switch
            {
                Action.Fall => CheckDeadFall(model),
                Action.Move => CheckDeadMove(model),
                _ => false,
            };

            if (!dead)
            {
                dead = CheckDeadStress(model);
            }

            if (dead)
            {
                _models[model].ReadyToDie = true;
            }
        }

        return dead;
    }

    /// <summary>Crushed by an object that was just pushed squarely onto your back.</summary>
    private bool CheckDeadMove(int model)
    {
        using ResistArena.Frame resist = GetResist(model, Dir.Up);
        foreach (int r in resist.Items)
        {
            if (!_models[r].IsAlive)
            {
                Dir resistDir = _models[r].Dir;
                if (resistDir != Dir.No && resistDir != Dir.Up && IsOnHolderBacks(r))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Something actively falling lands on you with no wall in its support chain.</summary>
    private bool CheckDeadFall(int model)
    {
        CollectWhoIsFalling(model);
        foreach (int k in _falling.Items)
        {
            if (!IsOnWall(k))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Something too heavy for your power rests on you with no other adequate support.</summary>
    private bool CheckDeadStress(int model)
    {
        CollectWhoIsHeavier(model, Power(model));
        foreach (int k in _heavier.Items)
        {
            if (!IsOnStrongPad(k, _models[k].Weight))
            {
                return true;
            }
        }

        return false;
    }

    private void ChangeState(int model)
    {
        ref ModelState m = ref _models[model];
        m.Dir = Dir.No;

        if (m.ReadyToTurn)
        {
            m.ReadyToTurn = false;
            m.IsLeft = !m.IsLeft;
        }

        m.ReadyToActive = false;

        if (m.ReadyToDie)
        {
            m.ReadyToDie = false;
            ChangeDie(model);
            m.DeathRoundsLeft = DeathRemoveRounds;
        }
        else if (!m.IsLost && m.DeathRoundsLeft > 0)
        {
            m.DeathRoundsLeft--;
            if (m.DeathRoundsLeft == 0)
            {
                Unmask(model);
                ChangeRemove(model);
            }
        }
    }

    /// <summary>
    /// Lets a goal_escape/goal_out model walk toward and through the border
    /// (Rules::actionOut()). Note this is automatic: once a fish stands at the
    /// edge with a clear way out it leaves without being driven, which is why
    /// escaping never costs a move symbol.
    /// </summary>
    /// <returns>Out-depth: 0 not going out, -1 just vanished.</returns>
    private int ActionOut(int model)
    {
        ref ModelState m = ref _models[model];
        if (!m.IsLost && !m.Busy && m.Dir == Dir.No && ShouldGoOut(model))
        {
            if (IsFullyOut(model))
            {
                ChangeGoOut(model);
                m.OutDepth = -1;
            }
            else
            {
                Dir borderDir = GetBorderDir(model);
                if (borderDir != Dir.No)
                {
                    ChangeGoingOut(model);
                    MoveDirBrute(model, borderDir);
                    m.OutDepth++;
                }
                else
                {
                    m.OutDepth = 0;
                }
            }
        }

        return m.OutDepth;
    }

    private void ActionFall(int model)
    {
        ref ModelState m = ref _models[model];
        m.Dir = Dir.Down;
        m.LastFall = true;
    }

    private bool ClearLastFall(int model)
    {
        ref ModelState m = ref _models[model];
        bool last = m.LastFall;
        m.LastFall = false;
        return last;
    }

    // ------------------------------------------------------ support queries --

    private bool IsOnCond(int model, OnCond cond, Weight padWeight)
    {
        if (CondSatisfy(model, cond, padWeight)) return true;
        if (CondWrong(model, cond, padWeight)) return false;

        Unmask(model);
        bool result = false;
        using (ResistArena.Frame resist = GetResist(model, Dir.Down))
        {
            foreach (int r in resist.Items)
            {
                if (IsOnCond(r, cond, padWeight))
                {
                    result = true;
                    break;
                }
            }
        }

        Mask(model);
        return result;
    }

    private bool CondSatisfy(int model, OnCond cond, Weight padWeight)
    {
        ref ModelState m = ref _models[model];
        return cond switch
        {
            OnCond.Stack => !m.IsAlive && m.Dir == Dir.No && IsOnStrongPad(model, Weight.Light),
            OnCond.Wall => IsWall(model),
            OnCond.StrongPad => IsWall(model) || (m.IsAlive && Power(model) >= padWeight),
            _ => false,
        };
    }

    private bool CondWrong(int model, OnCond cond, Weight padWeight)
    {
        ref ModelState m = ref _models[model];
        return cond switch
        {
            OnCond.Stack => m.IsAlive,
            OnCond.Wall => m.IsAlive,
            OnCond.StrongPad => m.IsAlive && Power(model) < padWeight,
            _ => false,
        };
    }

    private bool IsOnWall(int model) => IsOnCond(model, OnCond.Wall, Weight.None);

    private bool IsOnStrongPad(int model, Weight weight) => IsOnCond(model, OnCond.StrongPad, weight);

    /// <summary>
    /// Every alive model directly under us fully accounts for all our (possibly
    /// indirect) support (Rules::isOnHolderBacks()).
    /// </summary>
    private bool IsOnHolderBacks(int model)
    {
        int numDirectHolders = 0;
        using (ResistArena.Frame resist = GetResist(model, Dir.Down))
        {
            foreach (int r in resist.Items)
            {
                if (_models[r].IsAlive)
                {
                    numDirectHolders++;
                }
            }
        }

        CollectPads(model);
        return numDirectHolders == _pads.Count;
    }

    /// <summary>All alive fish and walls (transitively) supporting us from below (Rules::getPads()).</summary>
    private void CollectPads(int model)
    {
        _pads.Reset();
        CollectPadsInto(model);
    }

    private void CollectPadsInto(int model)
    {
        Unmask(model);
        using (ResistArena.Frame resist = GetResist(model, Dir.Down))
        {
            foreach (int r in resist.Items)
            {
                if (_models[r].IsAlive || IsWall(r))
                {
                    _pads.Add(r);
                }
                else
                {
                    CollectPadsInto(r);
                }
            }
        }

        Mask(model);
    }

    private bool IsFalling(int model) => !_models[model].IsAlive && _models[model].Dir == Dir.Down;

    /// <summary>Actively-falling objects above us, through a stack of inert items (Rules::whoIsFalling()).</summary>
    private void CollectWhoIsFalling(int model)
    {
        _falling.Reset();
        CollectWhoIsFallingInto(model);
    }

    private void CollectWhoIsFallingInto(int model)
    {
        Unmask(model);
        using (ResistArena.Frame resist = GetResist(model, Dir.Up))
        {
            foreach (int r in resist.Items)
            {
                if (!IsWall(r) && !_models[r].IsAlive)
                {
                    if (IsFalling(r))
                    {
                        _falling.Add(r);
                    }
                    else
                    {
                        CollectWhoIsFallingInto(r);
                    }
                }
            }
        }

        Mask(model);
    }

    private bool IsHeavier(int model, Weight power)
    {
        ref ModelState m = ref _models[model];
        return !IsWall(model) && !m.IsAlive && m.Weight > power;
    }

    /// <summary>Objects above us (transitively) too heavy for `power` to hold (Rules::whoIsHeavier()).</summary>
    private void CollectWhoIsHeavier(int model, Weight power)
    {
        _heavier.Reset();
        CollectWhoIsHeavierInto(model, power);
    }

    private void CollectWhoIsHeavierInto(int model, Weight power)
    {
        Unmask(model);
        using (ResistArena.Frame resist = GetResist(model, Dir.Up))
        {
            foreach (int r in resist.Items)
            {
                if (!IsWall(r))
                {
                    if (IsHeavier(r, power))
                    {
                        _heavier.Add(r);
                    }
                    else
                    {
                        CollectWhoIsHeavierInto(r, power);
                    }
                }
            }
        }

        Mask(model);
    }

    // -------------------------------------------------------------- pushing --

    /// <summary>Everything resisting us in `dir` would retreat before a push of `power` (Rules::canMoveOthers()).</summary>
    private bool CanMoveOthers(int model, Dir dir, Weight power)
    {
        bool result = true;
        Unmask(model);
        using (ResistArena.Frame resist = GetResist(model, dir))
        {
            bool shouldGoOut = ShouldGoOut(model);
            foreach (int r in resist.Items)
            {
                // A model on its way out is allowed to push "into" the border.
                if (shouldGoOut && IsBorder(r))
                {
                    continue;
                }

                if (!CanDir(r, dir, power))
                {
                    result = false;
                    break;
                }
            }
        }

        Mask(model);
        return result;
    }

    /// <summary>Whether others pushing with `power` can move us. Alive models can never be pushed (Rules::canDir()).</summary>
    private bool CanDir(int model, Dir dir, Weight power)
    {
        ref ModelState m = ref _models[model];
        if (!m.IsAlive && power >= m.Weight)
        {
            if (IsWall(model) && !ShouldGoOut(model))
            {
                return false;
            }

            return CanMoveOthers(model, dir, power);
        }

        return false;
    }

    /// <summary>Commits the move for us and everything we push (Rules::moveDirBrute()).</summary>
    private void MoveDirBrute(int model, Dir dir)
    {
        Unmask(model);
        using (ResistArena.Frame resist = GetResist(model, dir))
        {
            foreach (int r in resist.Items)
            {
                if (!IsBorder(r))
                {
                    MoveDirBrute(r, dir);
                    _models[model].Pushing = true;
                }
            }
        }

        _models[model].Dir = dir;
        Mask(model);
    }

    /// <summary>
    /// Tries to move. Only sets <see cref="ModelState.Dir"/> - the position is
    /// committed by <see cref="OccupyNewPos"/> next round (docs/007). Either
    /// everything resisting retreats, or nothing moves at all.
    /// </summary>
    private bool ActionMoveDir(int model, Dir dir)
    {
        if (CanMoveOthers(model, dir, Power(model)))
        {
            MoveDirBrute(model, dir);
            return true;
        }

        // Blocked. Two special cases before recording a plain touch, matching
        // Rules::actionMoveDir's canMoveOthers -> touchSpec -> setTouched.
        if (TouchSpec(model, dir))
        {
            return true;
        }

        SetTouched(model, dir);
        return false;
    }

    /// <summary>
    /// A fish blocked by exactly one "output_DIR" plug that opens the way it is
    /// pushing goes out through it (Rules::touchSpec()). windoze's spuntik only:
    /// the guard is "resisted by that plug and nothing else", so no other level
    /// can reach it.
    /// </summary>
    private bool TouchSpec(int model, Dir dir)
    {
        int plug = -1;
        using (ResistArena.Frame resist = GetResist(model, dir))
        {
            ReadOnlySpan<int> items = resist.Items;
            if (items.Length != 1 || _models[items[0]].OutDir != dir)
            {
                return false;
            }

            plug = items[0];
        }

        DecOutCapacity(plug);
        Unmask(model);
        ChangeGoOut(model);
        return true;
    }

    /// <summary>
    /// Records which way this model - and everything dead it is pushing against -
    /// pushed without being able to move (Rules::setTouched()). Write-only as far
    /// as physics is concerned; a level's code.lua reads it via getTouchDir()
    /// (docs/033), so it is ported to keep state identical, not because the
    /// solver needs it.
    /// </summary>
    private void SetTouched(int model, Dir dir)
    {
        _models[model].TouchDir = dir;
        if (!IsWall(model))
        {
            Unmask(model);
            using (ResistArena.Frame resist = GetResist(model, dir))
            {
                foreach (int r in resist.Items)
                {
                    if (!_models[r].IsAlive)
                    {
                        SetTouched(r, dir);
                    }
                }
            }

            Mask(model);
        }
    }

    private void ActionTurnSide(int model) => _models[model].ReadyToTurn = true;

    /// <summary>Rules::isAtBorder() - sits against the room's edge with a clear way out.</summary>
    public bool IsAtBorder(int model) => GetBorderDir(model) != Dir.No;
}
