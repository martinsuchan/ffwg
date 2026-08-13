namespace FishFillets.Physics;

public readonly record struct SolutionResult(bool Solved, int Steps, char FailedSymbol, string? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>
/// Replays a whole move string against a room. Port of web/src/game/
/// SolutionValidator.ts (docs/022), which is itself how the original replays a
/// saved solution.
///
/// This is the port's correctness harness: legacy/solution/*.lua holds a
/// recorded solution for 81 levels, and every one of them must drive this engine
/// to <see cref="Room.IsSolved"/>. Between them they exercise pushing, chained
/// falls, crush deaths, escapes, goal_out items and multi-cell shapes, so a
/// green run is strong evidence the port matches the game. See solver/docs/001.
/// </summary>
public static class SolutionValidator
{
    /// <summary>Replays <paramref name="moves"/> against a freshly reset room.</summary>
    public static SolutionResult Validate(Room room, ReadOnlySpan<char> moves)
    {
        room.Reset();
        for (int i = 0; i < moves.Length; i++)
        {
            if (!room.ApplyMove(moves[i]))
            {
                return new SolutionResult(false, i, moves[i], $"move {i} ('{moves[i]}') is not possible");
            }
        }

        return new SolutionResult(room.IsSolved(), moves.Length, '\0', null);
    }
}
