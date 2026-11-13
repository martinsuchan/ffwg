using System.Collections.Concurrent;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>
/// The oracle: plain breadth-first search over a micro-level's <b>entire</b>
/// state space, built to share as little as possible with the code it judges.
///
/// <list type="bullet">
/// <item>No level reduction, no heuristic, no dead-end rules, no macro moves -
///   just <see cref="Room.ApplyMove"/> for every symbol from every state.</item>
/// <item>Its own state key: every field of every model's
///   <see cref="ModelState"/> except <c>TouchDir</c> (write-only, see
///   <c>Room.WriteStateKey</c>). It does not rely on the solver's claim that a
///   settled state is fully described by position, facing and flags - if that
///   claim were wrong, this graph would just have more states.</item>
/// <item>The only pruning is a dead fish (<see cref="Room.IsSolvable"/>): the
///   game never revives one, so such a state can never become solved. It is
///   still recorded, so checks that look at every generated state see it.</item>
/// </list>
///
/// <para>Its correctness therefore rests on the physics alone, which the 80
/// reference solutions pin. Because micro-levels have at most a few hundred
/// thousand states, it also yields the exact distance-to-goal of every state,
/// which is what lets heuristics and pruning rules be checked state by state
/// rather than along one solution.</para>
/// </summary>
internal sealed class StateGraph
{
    public const int Infinite = int.MaxValue;

    public required Level Level { get; init; }

    /// <summary>Every state reached, index 0 the settled start.</summary>
    public required List<RoomSnapshot> States { get; init; }

    /// <summary>Per state: (symbol, successor). Empty for states that were not expanded (solved or unsolvable).</summary>
    public required List<(char Symbol, int Target)[]> Edges { get; init; }

    public required bool[] Solved { get; init; }

    public required bool[] Solvable { get; init; }

    /// <summary>Moves from the start (BFS depth).</summary>
    public required int[] Depth { get; init; }

    /// <summary>Exact moves still needed to finish, or <see cref="Infinite"/>.</summary>
    public required int[] DistanceToGoal { get; init; }

    public required int[] Parent { get; init; }

    public required char[] ParentMove { get; init; }

    public int Count => States.Count;

    /// <summary>The optimal length, or null when no state is solved.</summary>
    public int? Optimum => DistanceToGoal[0] == Infinite ? null : DistanceToGoal[0];

    /// <summary>One shortest solution.</summary>
    public string? OptimalMoves()
    {
        int best = -1;
        for (int i = 0; i < Count; i++)
        {
            if (Solved[i] && (best < 0 || Depth[i] < Depth[best]))
            {
                best = i;
            }
        }

        if (best < 0)
        {
            return null;
        }

        var path = new List<char>();
        for (int i = best; Parent[i] >= 0; i = Parent[i])
        {
            path.Add(ParentMove[i]);
        }

        path.Reverse();
        return new string(path.ToArray());
    }
}

internal static class ReferenceBfs
{
    private static readonly ConcurrentDictionary<string, Lazy<StateGraph>> Cache = new();

    /// <summary>The graph for a micro-level, built once per test run.</summary>
    public static StateGraph For(MicroLevel micro) =>
        Cache.GetOrAdd(micro.Name, _ => new Lazy<StateGraph>(() => Explore(micro.Build()))).Value;

    public static StateGraph Explore(Level level, int maxStates = 400_000)
    {
        var room = new Room(level);
        var symbols = new char[room.MoveSymbolCount];
        room.GetMoveSymbols(symbols, out int symbolCount);

        var index = new Dictionary<string, int>();
        var states = new List<RoomSnapshot>();
        var edges = new List<(char, int)[]>();
        var solved = new List<bool>();
        var solvable = new List<bool>();
        var depth = new List<int>();
        var parent = new List<int>();
        var parentMove = new List<char>();

        int Add(int from, char move, int d)
        {
            string key = Key(room);
            if (index.TryGetValue(key, out int existing))
            {
                return existing;
            }

            if (states.Count >= maxStates)
            {
                throw new InvalidOperationException(
                    $"{level.Name}: more than {maxStates:N0} states - too big for a micro-level oracle");
            }

            int id = states.Count;
            index[key] = id;
            states.Add(room.Capture());
            edges.Add([]);
            solved.Add(room.IsSolved());
            solvable.Add(room.IsSolvable());
            depth.Add(d);
            parent.Add(from);
            parentMove.Add(move);
            return id;
        }

        Add(-1, '\0', 0);

        var successors = new List<(char, int)>(symbolCount);
        for (int id = 0; id < states.Count; id++)
        {
            if (solved[id] || !solvable[id])
            {
                continue;
            }

            successors.Clear();
            for (int s = 0; s < symbolCount; s++)
            {
                room.RestoreFrom(states[id]);
                if (room.ApplyMove(symbols[s]))
                {
                    successors.Add((symbols[s], Add(id, symbols[s], depth[id] + 1)));
                }
            }

            edges[id] = [.. successors];
        }

        // Exact distance to the nearest solved state: BFS backwards over the
        // explicit graph from every solved state at once.
        int n = states.Count;
        var reverse = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            reverse[i] = [];
        }

        for (int i = 0; i < n; i++)
        {
            foreach ((_, int target) in edges[i])
            {
                reverse[target].Add(i);
            }
        }

        var distance = new int[n];
        Array.Fill(distance, StateGraph.Infinite);
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            if (solved[i])
            {
                distance[i] = 0;
                queue.Enqueue(i);
            }
        }

        while (queue.Count > 0)
        {
            int t = queue.Dequeue();
            foreach (int s in reverse[t])
            {
                if (distance[s] == StateGraph.Infinite)
                {
                    distance[s] = distance[t] + 1;
                    queue.Enqueue(s);
                }
            }
        }

        return new StateGraph
        {
            Level = level,
            States = states,
            Edges = edges,
            Solved = [.. solved],
            Solvable = [.. solvable],
            Depth = [.. depth],
            DistanceToGoal = distance,
            Parent = [.. parent],
            ParentMove = [.. parentMove],
        };
    }

    /// <summary>Every field of every model except TouchDir.</summary>
    private static string Key(Room room)
    {
        int n = room.ModelCount;
        var bytes = new byte[n * 12];
        for (int i = 0; i < n; i++)
        {
            ref readonly ModelState m = ref room.State(i);
            int o = i * 12;
            bytes[o] = (byte)m.X;
            bytes[o + 1] = (byte)(m.X >> 8);
            bytes[o + 2] = (byte)m.Y;
            bytes[o + 3] = (byte)(m.Y >> 8);
            bytes[o + 4] = (byte)m.Weight;
            bytes[o + 5] = (byte)(
                (m.IsAlive ? 1 : 0) | (m.IsOut ? 2 : 0) | (m.IsLost ? 4 : 0) | (m.IsLeft ? 8 : 0)
                | (m.Busy ? 16 : 0) | (m.ReadyToDie ? 32 : 0) | (m.ReadyToTurn ? 64 : 0) | (m.ReadyToActive ? 128 : 0));
            bytes[o + 6] = (byte)((m.Pushing ? 1 : 0) | (m.LastFall ? 2 : 0));
            bytes[o + 7] = (byte)m.OutDir;
            bytes[o + 8] = (byte)m.OutCapacity;
            bytes[o + 9] = (byte)m.Dir;
            bytes[o + 10] = (byte)m.OutDepth;
            bytes[o + 11] = m.DeathRoundsLeft;
        }

        return Convert.ToBase64String(bytes);
    }
}
