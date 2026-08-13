namespace FishFillets.Physics;

/// <summary>
/// Port of legacy/src/level/Dir.h (web/src/game/Dir.ts). Values match the
/// original's enum ordering; Lua reads these numerically via getTouchDir().
/// </summary>
public enum Dir : byte
{
    No = 0,
    Up = 1,
    Down = 2,
    Left = 3,
    Right = 4,
}

/// <summary>
/// Port of legacy/src/level/Cube.h's Cube::eWeight (web/src/game/Cube.ts).
/// Order matters - power/weight comparisons use &gt;=.
/// </summary>
public enum Weight : byte
{
    None = 0,
    Light = 1,
    Heavy = 2,
    Fixed = 3,
}

/// <summary>Port of legacy/src/level/Cube.h's Cube::eAction.</summary>
public enum Action : byte
{
    No = 0,
    Fall = 1,
    Move = 2,
}

/// <summary>
/// Port of legacy/src/level/Goal.h (web/src/game/Goal.ts). The original models
/// a goal as two independent tri-states (out/alive, each TRUE/FALSE/IGNORE), but
/// only these four combinations are ever constructed - the "must NOT be out" and
/// "must NOT be alive" branches are dead code in the original too. Collapsing to
/// an enum keeps the checks branch-cheap and allocation-free.
/// <list type="bullet">
/// <item><see cref="No"/>      - out: ignore, alive: ignore</item>
/// <item><see cref="Out"/>     - out: TRUE,   alive: ignore</item>
/// <item><see cref="Escape"/>  - out: TRUE,   alive: TRUE</item>
/// <item><see cref="Alive"/>   - out: ignore, alive: TRUE</item>
/// </list>
/// </summary>
public enum GoalKind : byte
{
    No = 0,
    Out = 1,
    Escape = 2,
    Alive = 3,
}

internal static class DirExtensions
{
    // dir2xy, indexed by Dir - no switch, no allocation.
    private static ReadOnlySpan<sbyte> Dx => [0, 0, 0, -1, 1];
    private static ReadOnlySpan<sbyte> Dy => [0, -1, 1, 0, 0];

    public static int X(this Dir dir) => Dx[(int)dir];

    public static int Y(this Dir dir) => Dy[(int)dir];
}
