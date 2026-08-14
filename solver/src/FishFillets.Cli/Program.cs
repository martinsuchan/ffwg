using System.Diagnostics;

using FishFillets.Physics;
using FishFillets.Search;

// ffsolve - console front end for the Fish Fillets solver (see solver/README.md).
//
// This milestone ships the physics port plus the commands that exercise it:
//   ffsolve verify [<level>|--all] [--moves <string>]
//   ffsolve info <level>
//   ffsolve bench <level> [--rounds N]
// The search itself is the next step - see solver/docs/001.
//
// `verify --all` is also a test case (solver/tests), so CI can gate on
// `dotnet test` without shelling out to this.

string[] argv = args;
if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

string command = argv[0];

Corpus corpus;
try
{
    corpus = Corpus.Resolve(TakeOption(argv, "--levels"), TakeOption(argv, "--solutions"));
}
catch (DirectoryNotFoundException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

if (!Directory.Exists(corpus.LevelsDir))
{
    Console.Error.WriteLine(
        $"levels directory not found: '{corpus.LevelsDir}'\n" +
        "Run scripts\\export-levels.ps1 from the repo root, or pass --levels <dir>.");
    return 2;
}

try
{
    return command switch
    {
        "verify" => Verify(corpus, argv),
        "info" => Info(corpus, argv),
        "bench" => Bench(corpus, argv),
        "improve" => Improve(corpus, argv),
        "solve" => Solve(corpus, argv),
        "reduce" => Reduce(corpus, argv),
        _ => Unknown(command),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 3;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"unknown command: {command}");
    PrintUsage();
    return 2;
}

static void PrintUsage()
{
    Console.WriteLine("""
        ffsolve - Fish Fillets level solver

        Usage:
          ffsolve verify <level>            replay that level's reference solution
          ffsolve verify --all              replay every reference solution
          ffsolve verify <level> --moves S  replay the move string S instead
          ffsolve info <level>              room size, models, goals, move symbols
          ffsolve bench <level> [--rounds N]  time the simulation
          ffsolve improve <level>           shorten a solution by re-solving windows of it
                    [--window N] [--stride N] [--nodes N] [--moves S] [--out F] [--quiet]
          ffsolve solve <level>             solve from scratch (A*, shortest at weight 1)
                    [--weight W] [--nodes N] [--seconds N] [--out F] [--quiet]
          ffsolve solve --all               solve every level, several at a time
          ffsolve solve --levels-list a,b,c solve just these
                    [--parallel N] [--weight W] [--nodes N] [--seconds N] [--out-dir D]
          ffsolve reduce <level>|--all      report which models the analysis can freeze

        Note: briefcase and windoze run level scripting that affects play
        (docs/031, docs/035), so a physics-only solution for them is not
        necessarily reachable in the real game - see solver/docs/004.

        Options:
          --levels <dir>      level JSON directory (default <repo>/solver/levels)
          --solutions <dir>   reference solutions  (default <repo>/legacy/solution)
        """);
}

// ------------------------------------------------------------------ verify --

static int Verify(Corpus corpus, string[] argv)
{
    string? moves = TakeOption(argv, "--moves");
    bool all = argv.Contains("--all");
    if (!all && argv.Length < 2)
    {
        Console.Error.WriteLine("verify needs a level name, or --all");
        return 2;
    }

    IReadOnlyList<string> levels = all ? corpus.LevelNames : [argv[1]];

    int solved = 0, failed = 0, missing = 0;
    long totalMoves = 0;
    var sw = Stopwatch.StartNew();

    foreach (string level in levels)
    {
        if (!corpus.HasLevel(level))
        {
            Console.WriteLine($"{"SKIP",-7} {level,-14} no exported level ({level}.json)");
            missing++;
            continue;
        }

        string solution;
        if (moves is not null)
        {
            solution = moves;
        }
        else if (!corpus.TryReadSolution(level, out solution))
        {
            Console.WriteLine($"{"SKIP",-7} {level,-14} no reference solution");
            missing++;
            continue;
        }

        var room = new Room(corpus.LoadLevel(level));
        SolutionResult result = SolutionValidator.Validate(room, solution);
        totalMoves += result.Steps;

        if (result.Solved)
        {
            solved++;
            Console.WriteLine($"{"SOLVED",-7} {level,-14} {result.Steps,5} moves");
        }
        else
        {
            failed++;
            string why = result.Error ?? "played out but the room is not solved";
            Console.WriteLine($"{"FAILED",-7} {level,-14} {result.Steps,5} moves - {why}");
        }
    }

    sw.Stop();
    Console.WriteLine();
    Console.WriteLine(
        $"{solved}/{solved + failed} solved" +
        (missing > 0 ? $", {missing} skipped" : "") +
        $" - {totalMoves} moves in {sw.Elapsed.TotalMilliseconds:F0} ms");
    return failed == 0 ? 0 : 1;
}

// -------------------------------------------------------------------- info --

static int Info(Corpus corpus, string[] argv)
{
    if (argv.Length < 2)
    {
        Console.Error.WriteLine("info needs a level name");
        return 2;
    }

    Level level = corpus.LoadLevel(argv[1]);
    var room = new Room(level);

    int light = 0, heavy = 0, fixedCount = 0, fish = 0;
    for (int i = 0; i < level.ModelCount; i++)
    {
        switch (level.Models[i].Weight)
        {
            case Weight.Fixed: fixedCount++; break;
            case Weight.Heavy: heavy++; break;
            default: light++; break;
        }

        if (level.Models[i].IsAlive)
        {
            fish++;
        }
    }

    Console.WriteLine($"level      {level.Name}");
    Console.WriteLine($"room       {level.Width} x {level.Height} cells");
    Console.WriteLine($"models     {level.ModelCount} ({fish} fish, {light} light, {heavy} heavy, {fixedCount} fixed)");

    Span<char> symbols = stackalloc char[room.MoveSymbolCount];
    room.GetMoveSymbols(symbols, out int count);
    Console.WriteLine($"symbols    {new string(symbols[..count])}  (branching factor {count})");

    Console.Write("goals      ");
    for (int i = 0; i < level.ModelCount; i++)
    {
        if (level.Models[i].Goal != GoalKind.No)
        {
            Console.Write($"[{i}] {level.Models[i].Kind}={level.Models[i].Goal}  ");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"settled    fresh={room.IsFresh} solvable={room.IsSolvable()} solved={room.IsSolved()}");
    return 0;
}

// ------------------------------------------------------------------- solve --

/// <summary>
/// Levels whose Lua scripting takes over play, so a physics-only answer for them
/// is not necessarily reachable in the real game: briefcase runs a scripted
/// auto-play tutorial (docs/031), windoze drives busy/fastFalling from code.lua
/// (docs/035). Skipped by batch runs; still solvable if named explicitly.
/// </summary>
static bool IsScripted(string level) => level is "briefcase" or "windoze";

static int Solve(Corpus corpus, string[] argv)
{
    string? list = TakeOption(argv, "--levels-list");
    bool all = argv.Contains("--all");

    if (all || list is not null)
    {
        return SolveBatch(corpus, argv, list);
    }

    if (argv.Length < 2)
    {
        Console.Error.WriteLine("solve needs a level name, --all, or --levels-list a,b,c");
        return 2;
    }

    string name = argv[1];
    double weight = double.TryParse(TakeOption(argv, "--weight"), out double wt) ? wt : 1.0;
    long maxNodes = long.TryParse(TakeOption(argv, "--nodes"), out long n) ? n : 20_000_000;
    int seconds = int.TryParse(TakeOption(argv, "--seconds"), out int sec) ? sec : 0;
    string? outPath = TakeOption(argv, "--out");
    bool quiet = argv.Contains("--quiet");

    Level level = corpus.LoadLevel(name);
    string? known = corpus.TryReadSolution(name, out string bundledSolution) ? bundledSolution : null;
    var solver = new Solver(level, LevelReduction.Verified(level, known));

    Console.WriteLine($"level      {name} ({level.Width}x{level.Height})");
    Console.WriteLine($"reduction  {solver.Reduction}");
    Console.WriteLine($"search     weight {weight}, node limit {maxNodes:N0}" +
                      (seconds > 0 ? $", {seconds}s limit" : ""));
    Console.WriteLine();

    SolveResult result = solver.Solve(new SolveOptions
    {
        Weight = weight,
        MaxNodes = maxNodes,
        TimeLimit = seconds > 0 ? TimeSpan.FromSeconds(seconds) : null,
        Progress = quiet ? null : Console.WriteLine,
    });

    Console.WriteLine(
        $"{result.Status}: expanded {result.Expanded:N0}, stored {result.StatesStored:N0}, " +
        $"f={result.DeepestF}, {result.Elapsed.TotalSeconds:F1} s");

    if (!result.Solved)
    {
        return 1;
    }

    // Never report a solution on the search's word - replay it.
    SolutionResult check = SolutionValidator.Validate(new Room(level), result.Moves!);
    if (!check.Solved)
    {
        Console.Error.WriteLine($"BUG: the solver's answer does not solve the level ({check.Error})");
        return 3;
    }

    int reference = corpus.TryReadSolution(name, out string bundled) ? bundled.Length : -1;
    string compared = reference < 0
        ? ""
        : result.Moves!.Length < reference ? $"  ({reference - result.Moves.Length} SHORTER than the bundled {reference})"
        : result.Moves.Length == reference ? $"  (matches the bundled {reference})"
        : $"  ({result.Moves.Length - reference} longer than the bundled {reference})";

    Console.WriteLine($"solution   {result.Moves!.Length} moves - verified" +
                      (result.Optimal ? ", PROVABLY SHORTEST" : ", not proven optimal") + compared);
    Console.WriteLine();
    Console.WriteLine(result.Moves);

    if (outPath is not null)
    {
        File.WriteAllText(outPath, $"\nsaved_moves = '{result.Moves}'\n");
        Console.WriteLine($"written to {outPath}");
    }

    return 0;
}

// ------------------------------------------------------------- solve batch --

/// <summary>
/// Solves many levels at once, one per worker.
///
/// Different levels share nothing - each <see cref="Solver"/> owns its room,
/// transposition table and node array - so this needs no locking beyond the
/// console. Parallelising a *single* level's search is a different and much
/// harder problem (one shared frontier and visited set; see solver/docs/001 on
/// HDA*), and is not what this does.
///
/// Concurrency defaults well below the core count on purpose: a single hard
/// level can hold tens of millions of states (`stairs` peaked around 1.6 GB), so
/// memory, not CPU, is what runs out first.
/// </summary>
static int SolveBatch(Corpus corpus, string[] argv, string? list)
{
    double weight = double.TryParse(TakeOption(argv, "--weight"), out double wt) ? wt : 1.0;
    long maxNodes = long.TryParse(TakeOption(argv, "--nodes"), out long n) ? n : 20_000_000;
    int seconds = int.TryParse(TakeOption(argv, "--seconds"), out int sec) ? sec : 60;
    string? outDir = TakeOption(argv, "--out-dir");
    int parallel = int.TryParse(TakeOption(argv, "--parallel"), out int p)
        ? p
        : Math.Max(1, Math.Min(Environment.ProcessorCount, 4));

    List<string> levels = list is not null
        ? [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
        : [.. corpus.LevelsWithSolutions().Where(l => !IsScripted(l))];

    if (outDir is not null)
    {
        Directory.CreateDirectory(outDir);
    }

    Console.WriteLine(
        $"solving {levels.Count} level(s), {parallel} at a time " +
        $"(weight {weight}, {maxNodes:N0} nodes, {seconds}s each)");
    Console.WriteLine();

    var consoleLock = new object();
    int solved = 0, optimal = 0, shorter = 0, gaveUp = 0, done = 0;
    var sw = Stopwatch.StartNew();

    Parallel.ForEach(levels, new ParallelOptions { MaxDegreeOfParallelism = parallel }, name =>
    {
        string line;
        try
        {
            Level level = corpus.LoadLevel(name);
            string? known = corpus.TryReadSolution(name, out string bundled) ? bundled : null;
            var solver = new Solver(level, LevelReduction.Verified(level, known));

            SolveResult result = solver.Solve(new SolveOptions
            {
                Weight = weight,
                MaxNodes = maxNodes,
                TimeLimit = TimeSpan.FromSeconds(seconds),
            });

            if (!result.Solved)
            {
                Interlocked.Increment(ref gaveUp);
                line = $"{name,-14} {"-",6}  {result.Status} (bound f={result.DeepestF}, {result.Elapsed.TotalSeconds:F0}s)";
            }
            else if (!SolutionValidator.Validate(new Room(level), result.Moves!).Solved)
            {
                Interlocked.Increment(ref gaveUp);
                line = $"{name,-14} {"BUG",6}  the answer does not replay";
            }
            else
            {
                Interlocked.Increment(ref solved);
                int len = result.Moves!.Length;
                if (result.Optimal)
                {
                    Interlocked.Increment(ref optimal);
                }

                string vs = known is null ? "" :
                    len < known.Length ? $"  {known.Length - len} SHORTER than {known.Length}" :
                    len == known.Length ? $"  matches {known.Length}" :
                    $"  {len - known.Length} longer than {known.Length}";

                if (known is not null && len < known.Length)
                {
                    Interlocked.Increment(ref shorter);
                }

                if (outDir is not null)
                {
                    File.WriteAllText(Path.Combine(outDir, name + ".lua"), $"\nsaved_moves = '{result.Moves}'\n");
                }

                line = $"{name,-14} {len,6}  {(result.Optimal ? "shortest" : "found   ")}{vs}" +
                       $"  ({result.Elapsed.TotalSeconds:F0}s)";
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref gaveUp);
            line = $"{name,-14} {"ERR",6}  {ex.Message}";
        }

        lock (consoleLock)
        {
            Console.WriteLine($"[{Interlocked.Increment(ref done),3}/{levels.Count}] {line}");
        }
    });

    sw.Stop();
    Console.WriteLine();
    Console.WriteLine(
        $"{solved} solved ({optimal} proven shortest, {shorter} shorter than bundled), " +
        $"{gaveUp} unfinished - {sw.Elapsed.TotalMinutes:F1} min wall clock");
    return 0;
}

// ------------------------------------------------------------------ reduce --

static int Reduce(Corpus corpus, string[] argv)
{
    IReadOnlyList<string> levels = argv.Contains("--all")
        ? corpus.LevelNames
        : argv.Length > 1 ? [argv[1]] : [];

    if (levels.Count == 0)
    {
        Console.Error.WriteLine("reduce needs a level name, or --all");
        return 2;
    }

    Console.WriteLine($"{"level",-14} {"models",6} {"mutable",8} {"mobile",7}  frozen by analysis");
    Console.WriteLine(new string('-', 64));

    foreach (string name in levels)
    {
        if (!corpus.HasLevel(name))
        {
            continue;
        }

        Level level = corpus.LoadLevel(name);
        LevelReduction reduction = LevelReduction.Verified(
            level, corpus.TryReadSolution(name, out string known) ? known : null);
        Console.WriteLine(
            $"{name,-14} {level.ModelCount,6} {level.MutableModels.Length,8} {reduction.MobileModels.Length,7}  " +
            (reduction.FrozenModels.Length == 0 ? "-" : string.Join(",", reduction.FrozenModels)));
    }

    return 0;
}

// ----------------------------------------------------------------- improve --

static int Improve(Corpus corpus, string[] argv)
{
    if (argv.Length < 2)
    {
        Console.Error.WriteLine("improve needs a level name");
        return 2;
    }

    string name = argv[1];
    int window = int.TryParse(TakeOption(argv, "--window"), out int w) ? w : 16;
    int stride = int.TryParse(TakeOption(argv, "--stride"), out int s) ? s : 1;
    long nodeLimit = long.TryParse(TakeOption(argv, "--nodes"), out long n) ? n : 2_000_000;
    string? outPath = TakeOption(argv, "--out");
    bool quiet = argv.Contains("--quiet");

    Level level = corpus.LoadLevel(name);

    string moves = TakeOption(argv, "--moves") ?? "";
    if (moves.Length == 0 && !corpus.TryReadSolution(name, out moves))
    {
        Console.Error.WriteLine($"no solution to improve for {name} - pass --moves");
        return 2;
    }

    Console.WriteLine($"{name}: {moves.Length} moves, window {window}, stride {stride}, node limit {nodeLimit:N0}");

    var optimizer = new WindowOptimizer(level);
    var sw = Stopwatch.StartNew();
    OptimizeResult result = optimizer.Optimize(
        moves, window, stride, nodeLimit, quiet ? null : Console.WriteLine);
    sw.Stop();

    // Never trust a spliced solution on the optimiser's word - replay it.
    SolutionResult check = SolutionValidator.Validate(new Room(level), result.Moves);
    if (!check.Solved)
    {
        Console.Error.WriteLine($"BUG: the improved solution does not solve the level ({check.Error})");
        return 3;
    }

    Console.WriteLine();
    Console.WriteLine($"{result.OriginalLength} -> {result.Moves.Length} moves ({result.Saved} saved) - verified");
    Console.WriteLine(
        $"{result.Passes} pass(es), {result.WindowsImproved} window(s) improved, " +
        $"{result.NodesExplored:N0} nodes in {sw.Elapsed.TotalSeconds:F1} s");

    if (result.Saved > 0)
    {
        Console.WriteLine();
        Console.WriteLine(result.Moves);
    }

    if (outPath is not null)
    {
        File.WriteAllText(outPath, $"\nsaved_moves = '{result.Moves}'\n");
        Console.WriteLine($"written to {outPath}");
    }

    return 0;
}

// ------------------------------------------------------------------- bench --

static int Bench(Corpus corpus, string[] argv)
{
    if (argv.Length < 2)
    {
        Console.Error.WriteLine("bench needs a level name");
        return 2;
    }

    string name = argv[1];
    int rounds = int.TryParse(TakeOption(argv, "--rounds"), out int r) ? r : 200;

    Level level = corpus.LoadLevel(name);
    if (!corpus.TryReadSolution(name, out string moves))
    {
        Console.Error.WriteLine($"no reference solution for {name} - bench replays it");
        return 2;
    }

    var room = new Room(level);

    // Warm up the JIT (a no-op under Native AOT) before measuring.
    SolutionValidator.Validate(room, moves);

    long before = GC.GetAllocatedBytesForCurrentThread();
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < rounds; i++)
    {
        SolutionValidator.Validate(room, moves);
    }

    sw.Stop();
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    long applied = (long)rounds * moves.Length;
    Console.WriteLine($"level          {name} ({level.Width}x{level.Height}, {level.ModelCount} models)");
    Console.WriteLine($"replays        {rounds} x {moves.Length} moves = {applied} moves");
    Console.WriteLine($"elapsed        {sw.Elapsed.TotalMilliseconds:F1} ms");
    Console.WriteLine($"throughput     {applied / sw.Elapsed.TotalSeconds / 1_000_000:F2} M moves/s");
    Console.WriteLine($"allocated      {allocated} bytes ({(double)allocated / applied:F3} per move)");
    return 0;
}

// ----------------------------------------------------------------- options --

/// <summary>Reads "--name value" out of the argument list (absent -> null).</summary>
static string? TakeOption(string[] argv, string name)
{
    for (int i = 0; i < argv.Length - 1; i++)
    {
        if (argv[i] == name)
        {
            return argv[i + 1];
        }
    }

    return null;
}
