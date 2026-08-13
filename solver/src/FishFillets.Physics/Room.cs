using System.Runtime.CompilerServices;

namespace FishFillets.Physics;

/// <summary>
/// One playable room: the occupancy grid plus every model's live state, and the
/// round pipeline that drives them. Port of legacy/src/level/Room.h/.cpp merged
/// with Field.cpp and MarkMask.cpp (web/src/game/{Room,Field,MarkMask}.ts) - the
/// rules themselves live in Room.Rules.cs, gravity in Room.Landslip.cs, and
/// move symbols in Room.Controls.cs, all partials of this class.
///
/// Merged rather than mirrored as separate classes on purpose: Field/MarkMask
/// are per-model objects in the original that exist only to reach the one shared
/// grid, and collapsing them removes an indirection from the hottest inner loop
/// (getResist) without changing any behaviour.
///
/// Dropped vs. the browser port, because the solver has no player and no
/// screen: input handling (InputProvider/Controls.driving/MouseControl/
/// FinderAlg), the active-fish switching scheme, animation state and the
/// visual move-streak. What remains is exactly the simulation - moving,
/// pushing, falling, dying, escaping - plus the win/lose predicates.
/// See solver/docs/001.
/// </summary>
public sealed partial class Room
{
    /// <summary>Empty water. Stored in the grid; never a model index.</summary>
    public const int Empty = -1;

    /// <summary>
    /// Rounds a dead model stays masked and solid before it is unmasked and
    /// removed - port of EffectDisintegrate's pixel-dissolve counter
    /// (ceil(400/30) = 14 render frames, counted here in physics rounds).
    /// See docs/011.
    ///
    /// A solver normally prunes the moment a fish dies (both fish goals fail on
    /// death, so <see cref="IsSolvable"/> goes false), which makes this
    /// unreachable in practice - it is ported anyway so that any state the
    /// search does keep behaves exactly like the game.
    /// </summary>
    private const byte DeathRemoveRounds = 14;

    private const int MaxSettleRounds = 1000;

    public Level Level { get; }

    private readonly short[] _field;
    private readonly ModelState[] _models;
    private readonly ResistArena _arena;
    private readonly ModelCollector _pads;
    private readonly ModelCollector _falling;
    private readonly ModelCollector _heavier;
    private readonly bool[] _stoned;

    private readonly int _width;
    private readonly int _height;
    private readonly int _borderIndex;

    private Action _lastAction = Action.No;
    private Weight _lastImpact = Weight.None;
    private bool _fastFalling;
    private int _stepCount;
    private int _lastDeadCount;

    public Room(Level level)
    {
        Level = level;
        _width = level.Width;
        _height = level.Height;
        _borderIndex = level.BorderIndex;

        _field = new short[_width * _height];
        _field.AsSpan().Fill(Empty);

        int slots = level.Models.Length;
        _models = new ModelState[slots];
        _arena = new ResistArena(slots, Math.Max(_width, _height));
        _pads = new ModelCollector(slots);
        _falling = new ModelCollector(slots);
        _heavier = new ModelCollector(slots);
        _stoned = new bool[slots];

        Reset();
    }

    /// <summary>Number of real models (the border excluded) - loop bound everywhere.</summary>
    public int ModelCount => Level.ModelCount;

    public ref readonly ModelState State(int model) => ref _models[model];

    /// <summary>Nothing is still moving or falling (legacy Room::isFresh()).</summary>
    public bool IsFresh => _lastAction == Action.No;

    /// <summary>Moves successfully applied so far (legacy StepCounter).</summary>
    public int StepCount => _stepCount;

    /// <summary>Models that died during the most recently finished round.</summary>
    public int LastDeadCount => _lastDeadCount;

    /// <summary>Weight of whatever just landed after falling (legacy Landslip::getImpact()).</summary>
    public Weight LastImpact => _lastImpact;

    /// <summary>
    /// Restores the room to its as-loaded state. Cheaper than building a new
    /// <see cref="Room"/> (no shape re-parse, no reallocation), so a search or a
    /// batch verify reuses one instance per thread.
    /// </summary>
    public void Reset()
    {
        _field.AsSpan().Fill(Empty);
        _lastAction = Action.No;
        _lastImpact = Weight.None;
        _fastFalling = false;
        _stepCount = 0;
        _lastDeadCount = 0;

        for (int i = 0; i < _models.Length; i++)
        {
            ModelDef def = Level.Models[i];
            _models[i] = new ModelState
            {
                X = def.X,
                Y = def.Y,
                Weight = def.Weight,
                IsAlive = def.IsAlive,
                IsLeft = def.IsLeft,
                OutDir = def.OutDir,
                OutCapacity = def.OutCapacity,
                Dir = Dir.No,
                TouchDir = Dir.No,
            };
        }

        // legacy Room::addModel -> Rules::takeField, in declaration order: model
        // 0 (the room's wall shape) masks first, then the items on top of it.
        // The border is never masked - it sits out of bounds at (-1,-1), where
        // writes are dropped anyway.
        for (int i = 0; i < ModelCount; i++)
        {
            if (HasPlacedResist(i, _models[i].X, _models[i].Y))
            {
                throw new InvalidDataException(
                    $"{Level.Name}: position is occupied at build: model {i} ({Level.Models[i].Kind}) " +
                    $"at ({_models[i].X},{_models[i].Y})");
            }

            Mask(i);
        }

        // A room's items can start unsupported - the original settles them at
        // load, before the player gets control (docs/019's "initial level-load
        // input freeze"). Doing it here is what lets ApplyMove() settle *after*
        // its move and still match the original's settle-before-move order.
        SettleAll();
    }

    // ---------------------------------------------------------------- Field --

    /// <summary>
    /// The model occupying (x, y): a model index, <see cref="Empty"/> for empty
    /// in-bounds water, or the border index for anything out of bounds. Port of
    /// Field::getModel() - the original's "hack border everywhere in outer
    /// space".
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetModel(int x, int y) =>
        (uint)x < (uint)_width && (uint)y < (uint)_height
            ? _field[(y * _width) + x]
            : _borderIndex;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetModel(int x, int y, short model)
    {
        if ((uint)x < (uint)_width && (uint)y < (uint)_height)
        {
            _field[(y * _width) + x] = model;
        }
    }

    /// <summary>Clears a cell, but only if this model is the one occupying it (Field::setModel's toOverride).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearModel(int x, int y, int model)
    {
        if ((uint)x < (uint)_width && (uint)y < (uint)_height)
        {
            ref short cell = ref _field[(y * _width) + x];
            if (cell == model)
            {
                cell = Empty;
            }
        }
    }

    // ------------------------------------------------------------- MarkMask --

    /// <summary>Writes this model's shape into the grid (MarkMask::mask()).</summary>
    private void Mask(int model)
    {
        Shape shape = Level.Models[model].Shape;
        int x = _models[model].X, y = _models[model].Y;
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            SetModel(x + shape.MarkX[m], y + shape.MarkY[m], (short)model);
        }
    }

    /// <summary>Lifts this model out of the grid (MarkMask::unmask()).</summary>
    private void Unmask(int model)
    {
        Shape shape = Level.Models[model].Shape;
        int x = _models[model].X, y = _models[model].Y;
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            ClearModel(x + shape.MarkX[m], y + shape.MarkY[m], model);
        }
    }

    /// <summary>
    /// Everything (other than this model) occupying the cells its shape would
    /// cover one step along <paramref name="dir"/>. Port of
    /// MarkMask::getResist(); the returned frame must be consumed inside its
    /// `using` - see <see cref="ResistArena"/>.
    /// </summary>
    private ResistArena.Frame GetResist(int model, Dir dir) =>
        GetPlacedResist(model, _models[model].X + dir.X(), _models[model].Y + dir.Y());

    /// <summary>Everything (other than this model) occupying the cells its shape would cover at (lx, ly).</summary>
    private ResistArena.Frame GetPlacedResist(int model, int lx, int ly)
    {
        Shape shape = Level.Models[model].Shape;
        ResistArena.Frame frame = _arena.Open();
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            int resist = GetModel(lx + shape.MarkX[m], ly + shape.MarkY[m]);
            if (resist != Empty && resist != model)
            {
                frame.Push(resist);
            }
        }

        return frame;
    }

    /// <summary>Allocation-free "is anything in the way at (lx, ly)" - avoids opening a frame just to test emptiness.</summary>
    private bool HasPlacedResist(int model, int lx, int ly)
    {
        Shape shape = Level.Models[model].Shape;
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            int resist = GetModel(lx + shape.MarkX[m], ly + shape.MarkY[m]);
            if (resist != Empty && resist != model)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A direction leading out of the room with nothing blocking, or <see cref="Dir.No"/> (MarkMask::getBorderDir()).</summary>
    private Dir GetBorderDir(int model)
    {
        Shape shape = Level.Models[model].Shape;
        int x = _models[model].X, y = _models[model].Y;
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            int cx = x + shape.MarkX[m], cy = y + shape.MarkY[m];
            if (cx == 0 && CanMoveOthers(model, Dir.Left, Weight.Fixed)) return Dir.Left;
            if (cx == _width - 1 && CanMoveOthers(model, Dir.Right, Weight.Fixed)) return Dir.Right;
            if (cy == 0 && CanMoveOthers(model, Dir.Up, Weight.Fixed)) return Dir.Up;
            if (cy == _height - 1 && CanMoveOthers(model, Dir.Down, Weight.Fixed)) return Dir.Down;
        }

        return Dir.No;
    }

    /// <summary>Every cell of this model's shape is out of the room (MarkMask::isFullyOut()).</summary>
    private bool IsFullyOut(int model)
    {
        Shape shape = Level.Models[model].Shape;
        int x = _models[model].X, y = _models[model].Y;
        for (int m = 0; m < shape.MarkX.Length; m++)
        {
            if (GetModel(x + shape.MarkX[m], y + shape.MarkY[m]) != _borderIndex)
            {
                return false;
            }
        }

        return true;
    }

    // --------------------------------------------------------- Cube helpers --

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsWall(int model) => _models[model].Weight >= Weight.Fixed;

    /// <summary>How much this model can push/hold. Immutable, so it lives on <see cref="ModelDef"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Weight Power(int model) => Level.Models[model].Power;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsBorder(int model) => model == _borderIndex;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldGoOut(int model)
    {
        GoalKind goal = Level.Models[model].Goal;
        return goal is GoalKind.Out or GoalKind.Escape;
    }

    /// <summary>Port of Goal::isSatisfy() - see <see cref="GoalKind"/> for the tri-state mapping.</summary>
    private bool IsSatisfy(int model)
    {
        ref ModelState m = ref _models[model];
        return Level.Models[model].Goal switch
        {
            GoalKind.No => true,
            GoalKind.Out => m.IsOut,
            GoalKind.Escape => m.IsOut && m.IsAlive,
            GoalKind.Alive => m.IsAlive,
            _ => true,
        };
    }

    /// <summary>Port of Goal::isWrong() - this goal can never be satisfied again.</summary>
    private bool IsWrong(int model)
    {
        GoalKind goal = Level.Models[model].Goal;
        return (goal is GoalKind.Escape or GoalKind.Alive) && !_models[model].IsAlive;
    }

    private void ChangeDie(int model) => _models[model].IsAlive = false;

    /// <summary>Going out of the room - immovable while crossing the border (Cube::changeGoingOut()).</summary>
    private void ChangeGoingOut(int model) => _models[model].Weight = Weight.Fixed;

    private void ChangeGoOut(int model)
    {
        _models[model].IsOut = true;
        ChangeRemove(model);
    }

    private void ChangeRemove(int model)
    {
        ref ModelState m = ref _models[model];
        m.IsLost = true;
        m.Weight = Weight.None;
        m.X = -1000;
        m.Y = -1000;
    }

    /// <summary>
    /// A fish just went out through this plug (Cube::decOutCapacity()). Once
    /// spent, the plug stops being an output and becomes a normal light item.
    /// </summary>
    private void DecOutCapacity(int model)
    {
        ref ModelState m = ref _models[model];
        if (m.OutCapacity > 0)
        {
            m.OutCapacity--;
            if (m.OutCapacity == 0)
            {
                m.OutDir = Dir.No;
                m.Weight = Weight.Light;
                m.OutCapacity = -1;
            }
        }
    }

    // ------------------------------------------------------------- Outcomes --

    /// <summary>Every goal is currently satisfied and nothing is still moving (Room::isSolved()).</summary>
    public bool IsSolved()
    {
        if (!IsFresh)
        {
            return false;
        }

        for (int i = 0; i < ModelCount; i++)
        {
            if (!IsSatisfy(i))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Every goal can still possibly be satisfied (Room::isSolvable()). Goes
    /// false forever the moment a fish dies - both fish goals require alive - so
    /// this is the search's primary prune.
    /// </summary>
    public bool IsSolvable()
    {
        for (int i = 0; i < ModelCount; i++)
        {
            if (IsWrong(i))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>No fish will ever move again - all dead or already out (Room::cannotMove()).</summary>
    public bool CannotMove()
    {
        foreach (UnitDef unit in Level.Units)
        {
            if (WillMove(unit.Model))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>game_setFastFalling(): settle every pending fall in one round (windoze, docs/035).</summary>
    public void SetFastFalling(bool value) => _fastFalling = value;

    /// <summary>model_setBusy(): freeze a fish out of player control (windoze, docs/035).</summary>
    public void SetBusy(int model, bool busy) => _models[model].Busy = busy;
}
