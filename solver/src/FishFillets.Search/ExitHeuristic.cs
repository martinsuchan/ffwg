using FishFillets.Physics;

namespace FishFillets.Search;

/// <summary>
/// The lower bound on moves remaining, shared by both solvers.
///
/// Every model with goal_escape or goal_out has to reach the border, and
/// <see cref="RelaxedDistance"/> measures how far that is in a room stripped down
/// to its immovable walls. Fish distances <b>add</b> - one symbol steps exactly
/// one fish, so no move makes progress for two at once. Item distances do
/// <b>not</b> add: a single push shifts a whole chain and gravity is free, so the
/// bound takes the largest rather than the sum.
///
/// The estimate doubles as a dead-end test. An infinite distance means the model
/// cannot reach the border even with every item deleted, so no arrangement of
/// them can help - the "dropped somewhere it can never be recovered from" case.
/// </summary>
internal sealed class ExitHeuristic
{
    private readonly int[] _models;
    private readonly RelaxedDistance[] _distance;
    private readonly bool[] _isFish;

    public ExitHeuristic(Level level, LevelReduction reduction)
    {
        var models = new List<int>();
        var distances = new List<RelaxedDistance>();
        var isFish = new List<bool>();

        for (int i = 0; i < level.ModelCount; i++)
        {
            if (level.Models[i].Goal is not (GoalKind.Escape or GoalKind.Out))
            {
                continue;
            }

            bool fish = level.Models[i].IsAlive;
            models.Add(i);
            // A fish swims every cell it travels; a pushed item gets downward
            // steps free, because gravity moves it at no cost in moves.
            distances.Add(RelaxedDistance.ToExit(level, i, reduction.Walls, freeFall: !fish));
            isFish.Add(fish);
        }

        _models = [.. models];
        _distance = [.. distances];
        _isFish = [.. isFish];
    }

    public int Estimate(Room room)
    {
        int fishTotal = 0;
        int itemMax = 0;

        for (int i = 0; i < _models.Length; i++)
        {
            ref readonly ModelState m = ref room.State(_models[i]);
            if (m.IsOut)
            {
                continue;
            }

            int d = _distance[i].At(m.X, m.Y, m.IsLeft);
            if (d >= RelaxedDistance.Unreachable)
            {
                return RelaxedDistance.Unreachable;
            }

            if (_isFish[i])
            {
                fishTotal += d;
            }
            else if (d > itemMax)
            {
                itemMax = d;
            }
        }

        return Math.Max(fishTotal, itemMax);
    }
}
