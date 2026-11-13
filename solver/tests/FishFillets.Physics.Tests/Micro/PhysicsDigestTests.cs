using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace FishFillets.Physics.Tests.Micro;

/// <summary>
/// A fingerprint of the physics: for every corpus and micro level, a seeded
/// random walk of thousands of moves - most of them off any solution path,
/// rejected ones and deaths included - hashed into one number, kept in
/// <c>Micro/physics-digest.csv</c>.
///
/// <para>This is the safety net for making <c>Room.ApplyMove</c> faster (the
/// review's P2/P3: skipping the round simulation for moves that provably change
/// nothing else). Such a change must be invisible here: any difference in the
/// result of any move, from any state the walk visits, changes the digest. The
/// 80 reference solutions only cover the states along winning paths.</para>
///
/// <para>Hashed per move: the symbol, whether it was accepted, and every field of
/// every model except <c>TouchDir</c>, plus the step count and freshness.
/// <c>TouchDir</c> is excluded deliberately - it is write-only for physics (only
/// level Lua reads it), and a fast path that skips a round may legitimately
/// leave a stale value there.</para>
///
/// <para>If the physics is changed on purpose, accept the new digests with
/// <c>UPDATE_BASELINE=1</c> - and say why in the commit.</para>
/// </summary>
[TestClass]
public sealed class PhysicsDigestTests
{
    private const int Steps = 6_000;

    private static string DigestPath => Path.Combine(MicroCorpus.SourceDir, "physics-digest.csv");

    [TestMethod]
    public void Physics_RandomWalkDigestsUnchanged()
    {
        var levels = new List<(string Name, Level Level)>();
        foreach (string name in TestCorpus.Instance.LevelNames)
        {
            levels.Add(("corpus:" + name, TestCorpus.Instance.LoadLevel(name)));
        }

        foreach (MicroLevel micro in MicroCorpus.All)
        {
            levels.Add(("micro:" + micro.Name, micro.Build()));
        }

        var digests = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(levels, entry => digests[entry.Name] = Walk(entry.Level, entry.Name).ToString("x16", CultureInfo.InvariantCulture));

        if (Environment.GetEnvironmentVariable("UPDATE_BASELINE") == "1")
        {
            var text = new StringBuilder("level;steps;digest\n");
            foreach ((string name, string digest) in digests.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                text.Append(CultureInfo.InvariantCulture, $"{name};{Steps};{digest}\n");
            }

            File.WriteAllText(DigestPath, text.ToString());
            Console.WriteLine($"physics digests rewritten: {digests.Count} levels -> {DigestPath}");
            return;
        }

        Assert.IsTrue(File.Exists(DigestPath), $"no digests yet - create them with UPDATE_BASELINE=1 ({DigestPath})");
        Dictionary<string, string> saved = File.ReadAllLines(DigestPath)
            .Skip(1)
            .Where(l => l.Length > 0)
            .Select(l => l.Split(';'))
            .ToDictionary(p => p[0], p => p[2]);

        var problems = new List<string>();
        foreach ((string name, string digest) in digests.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (!saved.TryGetValue(name, out string? before))
            {
                problems.Add($"{name}: not in the digest file (new level?) - rerun with UPDATE_BASELINE=1");
            }
            else if (before != digest)
            {
                problems.Add($"{name}: physics changed ({before} -> {digest})");
            }
        }

        Assert.IsEmpty(problems, $"{problems.Count} levels behave differently:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// The fast path (Room.FastPath.cs, review P2) against the full round, move
    /// for move: two rooms walked in lockstep, one with the shortcut and one
    /// without, compared field by field after every move. Stronger than the
    /// digest for this purpose - it names the first move that differs.
    /// </summary>
    [TestMethod]
    public void Physics_FastPathMatchesFullRound()
    {
        var levels = TestCorpus.Instance.LevelNames.Select(n => ("corpus:" + n, TestCorpus.Instance.LoadLevel(n)))
            .Concat(MicroCorpus.All.Select(m => ("micro:" + m.Name, m.Build())))
            .ToList();

        var problems = new ConcurrentBag<string>();
        Parallel.ForEach(levels, entry =>
        {
            (string name, Level level) = entry;
            var fast = new Room(level) { UseFastPath = true };
            var full = new Room(level) { UseFastPath = false };
            var symbols = new char[fast.MoveSymbolCount];
            fast.GetMoveSymbols(symbols, out int symbolCount);

            var rng = new XorShift((ulong)name.GetHashCode(StringComparison.Ordinal) ^ 0x5DEECE66DUL);
            var pool = new List<RoomSnapshot> { fast.Capture() };

            for (int step = 0; step < Steps; step++)
            {
                if (rng.Next(0, 16) == 0)
                {
                    RoomSnapshot from = pool[rng.Next(0, pool.Count)];
                    fast.RestoreFrom(from);
                    full.RestoreFrom(from);
                }

                char symbol = symbols[rng.Next(0, symbolCount)];
                bool okFast = fast.ApplyMove(symbol);
                bool okFull = full.ApplyMove(symbol);
                string? difference = Difference(fast, full);
                if (okFast != okFull || difference is not null)
                {
                    problems.Add($"{name}, step {step}, '{symbol}': {(okFast != okFull ? $"accepted {okFast} vs {okFull}" : difference)}");
                    return;
                }

                if (!fast.IsSolvable() || fast.IsSolved())
                {
                    fast.RestoreFrom(pool[0]);
                    full.RestoreFrom(pool[0]);
                }
                else if (okFast && rng.Next(0, 4) == 0 && pool.Count < 256)
                {
                    pool.Add(fast.Capture());
                }
            }
        });

        Assert.IsEmpty(problems, $"{problems.Count} levels where the fast path differs:\n  " + string.Join("\n  ", problems.Take(10)));
    }

    /// <summary>First field (other than TouchDir) where the two rooms differ, or null.</summary>
    private static string? Difference(Room a, Room b)
    {
        if (a.StepCount != b.StepCount || a.IsFresh != b.IsFresh)
        {
            return $"step count {a.StepCount}/{b.StepCount}, fresh {a.IsFresh}/{b.IsFresh}";
        }

        for (int i = 0; i < a.ModelCount; i++)
        {
            ModelState x = a.State(i), y = b.State(i);
            x.TouchDir = Dir.No;
            y.TouchDir = Dir.No;
            if (!x.Equals(y))
            {
                return $"model {i} ({a.Level.Models[i].Kind}): fast ({x.X},{x.Y}) alive={x.IsAlive} left={x.IsLeft} out={x.IsOut}, " +
                       $"full ({y.X},{y.Y}) alive={y.IsAlive} left={y.IsLeft} out={y.IsOut}";
            }
        }

        for (int y = 0; y < a.Level.Height; y++)
        {
            for (int x = 0; x < a.Level.Width; x++)
            {
                if (a.GetModel(x, y) != b.GetModel(x, y))
                {
                    return $"grid cell ({x},{y}): {a.GetModel(x, y)} vs {b.GetModel(x, y)}";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The walk: random symbols, branching from a pool of earlier states so it
    /// spreads out instead of wandering in one corridor, and starting over from
    /// the opening whenever the level is lost or won.
    /// </summary>
    private static ulong Walk(Level level, string name)
    {
        var room = new Room(level);
        var symbols = new char[room.MoveSymbolCount];
        room.GetMoveSymbols(symbols, out int symbolCount);

        ulong seed = 1469598103934665603UL;
        foreach (char c in name)
        {
            seed = (seed ^ c) * 1099511628211UL;
        }

        var rng = new XorShift(seed);
        var pool = new List<RoomSnapshot> { room.Capture() };
        ulong hash = 14695981039346656037UL;

        void Mix(long value)
        {
            hash = (hash ^ (ulong)value) * 1099511628211UL;
        }

        for (int step = 0; step < Steps; step++)
        {
            if (rng.Next(0, 16) == 0)
            {
                room.RestoreFrom(pool[rng.Next(0, pool.Count)]);
            }

            char symbol = symbols[rng.Next(0, symbolCount)];
            bool ok = room.ApplyMove(symbol);
            Mix(symbol);
            Mix(ok ? 1 : 0);
            Mix(room.StepCount);
            Mix(room.IsFresh ? 1 : 0);

            for (int i = 0; i < room.ModelCount; i++)
            {
                ref readonly ModelState m = ref room.State(i);
                Mix(m.X);
                Mix(m.Y);
                Mix((long)m.Weight | ((long)m.Dir << 8) | ((long)m.OutDir << 16) | ((long)(byte)m.OutCapacity << 24) | ((long)m.DeathRoundsLeft << 32));
                Mix(m.OutDepth);
                Mix(
                    (m.IsAlive ? 1 : 0) | (m.IsOut ? 2 : 0) | (m.IsLost ? 4 : 0) | (m.IsLeft ? 8 : 0) | (m.Busy ? 16 : 0)
                    | (m.ReadyToDie ? 32 : 0) | (m.ReadyToTurn ? 64 : 0) | (m.ReadyToActive ? 128 : 0)
                    | (m.Pushing ? 256 : 0) | (m.LastFall ? 512 : 0));
            }

            if (!room.IsSolvable() || room.IsSolved())
            {
                room.RestoreFrom(pool[0]);
            }
            else if (ok && rng.Next(0, 4) == 0)
            {
                if (pool.Count < 256)
                {
                    pool.Add(room.Capture());
                }
                else
                {
                    room.CaptureTo(pool[rng.Next(1, pool.Count)]);
                }
            }
        }

        return hash;
    }
}
