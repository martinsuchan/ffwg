namespace FishFillets.Physics;

/// <summary>
/// Everything that changes about one model during play. Port of the mutable
/// half of legacy/src/level/Cube.h plus Rules.h's per-model bookkeeping
/// (web/src/game/Cube.ts + Rules.ts), flattened into a struct so a whole room's
/// state is two contiguous arrays that can be snapshotted with a memcpy.
///
/// Immutable per-model data (shape, power, control symbols) lives in
/// <see cref="ModelDef"/> instead, so it is shared and never copied.
///
/// Deliberately absent vs. the browser port: moveStreak (a visual swim-speed
/// counter, docs/017), and the animation/dialog fields - the solver has no
/// rendering. See solver/docs/001.
/// </summary>
public struct ModelState
{
    public short X;
    public short Y;

    /// <summary>Mutable: changeGoingOut() raises it to Fixed, changeRemove() drops it to None.</summary>
    public Weight Weight;

    public bool IsAlive;
    public bool IsOut;
    public bool IsLost;
    public bool IsLeft;

    /// <summary>legacy Cube::m_busy - a fish frozen out of player control (windoze only).</summary>
    public bool Busy;

    /// <summary>"output_DIR" plug direction (windoze's spuntik); <see cref="Dir.No"/> for every normal model.</summary>
    public Dir OutDir;

    /// <summary>How many more fish this plug absorbs before becoming a normal light item.</summary>
    public sbyte OutCapacity;

    // --- Rules state (legacy Rules.h's per-model fields) ---

    /// <summary>Move decided this round, applied at the start of the next one (see docs/007).</summary>
    public Dir Dir;

    /// <summary>Direction this model pushed against something it couldn't move (legacy Rules::m_touchDir).</summary>
    public Dir TouchDir;

    public bool ReadyToDie;
    public bool ReadyToTurn;
    public bool ReadyToActive;
    public bool Pushing;
    public bool LastFall;

    public short OutDepth;

    /// <summary>Rounds left before a corpse unmasks and is removed - see Room.DeathRemoveRounds.</summary>
    public byte DeathRoundsLeft;
}

/// <summary>
/// The immutable half of a model: what the level file declared. Shared by every
/// <see cref="Room"/> built from the same <see cref="Level"/>, so search threads
/// and snapshots never copy it.
/// </summary>
public sealed class ModelDef
{
    public required string Kind { get; init; }

    public required Shape Shape { get; init; }

    public required Weight Weight { get; init; }

    /// <summary>Immutable - only <see cref="ModelState.Weight"/> ever changes.</summary>
    public required Weight Power { get; init; }

    public required bool IsAlive { get; init; }

    public required GoalKind Goal { get; init; }

    public required bool IsLeft { get; init; }

    public required short X { get; init; }

    public required short Y { get; init; }

    public Dir OutDir { get; init; } = Dir.No;

    public sbyte OutCapacity { get; init; }
}

/// <summary>
/// A driveable fish. Port of legacy/src/level/Unit.h (web/src/game/Unit.ts),
/// reduced to what a solver needs: the recorded move symbols. The interactive
/// keyboard/mouse layer (KeyControl, driveBorrowed, the active-fish scheme) is
/// deliberately dropped - a move symbol names both the fish and the direction on
/// its own, which is exactly what makes the search's successor generation
/// independent of which fish is "active". See solver/docs/001.
/// </summary>
public readonly struct UnitDef
{
    public required int Model { get; init; }

    public required char Up { get; init; }

    public required char Down { get; init; }

    public required char Left { get; init; }

    public required char Right { get; init; }
}
