using System.Diagnostics;

using FishFillets.Cli;
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
        "results" => Results(corpus, argv),
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
        ffsolve - Fish Fillets level solver     (full guide: solver/README.md)

        SOLVE
          ffsolve solve <level>             A*; provably shortest at weight 1
                    [--seconds N] [--nodes N] (default 20,000,000)
                    [--weight W]   >1 = faster, no optimality proof
                    [--macro]      macro-move search (slower; see docs/005)
                    [--out FILE] [--progress SECONDS] [--quiet] [--no-record]
          ffsolve solve --all               every level with a reference solution
          ffsolve solve --levels-list a,b,c just these
                    [--parallel N] (default min(cores,4) - memory binds first)
                    [--seconds N] (default 60) [--nodes N] [--out-dir DIR]

        IMPROVE
          ffsolve improve <level>           shorten a known solution by re-solving
                                            windows of it (local shortcuts only)
                    [--window N] (16) [--stride N] (1) [--nodes N] (2,000,000)
                    [--moves S] [--out FILE] [--quiet] [--no-record]

        INSPECT
          ffsolve results [--unsolved]      what has been solved, and how close
                                            the bound got on what has not
          ffsolve verify <level> | --all | <level> --moves S
          ffsolve reduce <level> | --all    models the analysis proves immobile
          ffsolve info <level>              size, models, goals, branching factor
          ffsolve bench <level> [--rounds N] (200)

        OPTIONS (any command)
          --levels <dir>      level JSON     (default <repo>/solver/levels)
          --solutions <dir>   reference sols (default <repo>/legacy/solution)

        NOTES
          Memory runs out before CPU: roughly 130 bytes per stored state, so
          --nodes 20000000 is about 2.5 GB. That is what --parallel is limited by.

          A level edited in ffedit records where it came from, and solve
          re-verifies its answer against that original automatically - freezing
          or deleting an item changes the physics, so an answer found on a
          simplified room may not be legal in the real one.

          briefcase and windoze run level scripting that drives play (docs/031,
          docs/035), so a physics-only answer for them need not be reachable in
          the real game. Batch runs skip them.
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

    bool macro = argv.Contains("--macro");
    Level level = corpus.LoadLevel(name);
    string? known = corpus.TryReadSolution(name, out string bundledSolution) ? bundledSolution : null;
    LevelReduction reduction = LevelReduction.Verified(level, known);

    Console.WriteLine($"level      {name} ({level.Width}x{level.Height})");
    Console.WriteLine($"reduction  {reduction}");
    Console.WriteLine($"search     {(macro ? "macro-moves" : "per-symbol A*")}, weight {weight}, " +
                      $"node limit {maxNodes:N0}" + (seconds > 0 ? $", {seconds}s limit" : ""));
    Console.WriteLine();

    var options = new SolveOptions
    {
        Weight = weight,
        MaxNodes = maxNodes,
        TimeLimit = seconds > 0 ? TimeSpan.FromSeconds(seconds) : null,
        Progress = quiet ? null : Console.WriteLine,
        ProgressSeconds = double.TryParse(TakeOption(argv, "--progress"), out double ps) ? ps : 1.0,
    };

    SolveResult result = macro
        ? new MacroSolver(level, reduction).Solve(options)
        : new Solver(level, reduction).Solve(options);

    Console.WriteLine(
        $"{result.Status}: expanded {result.Expanded:N0}, stored {result.StatesStored:N0}, " +
        $"f={result.DeepestF}, {result.Elapsed.TotalSeconds:F1} s");

    if (!argv.Contains("--no-record"))
    {
        Record(corpus, name, ToRecord(result, macro));
    }

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

    // A hand-simplified level is a search aid, not the game. Freezing an item
    // stops it falling and deleting one removes something a fish might have stood
    // on, so an answer found here has to be replayed against the real level before
    // it means anything. Doing it automatically beats relying on memory.
    LevelJson raw = LevelLoader.LoadFile(corpus.LevelPath(name));
    if (raw.SourceLevel is { } origin && corpus.HasLevel(origin))
    {
        SolutionResult onOriginal = SolutionValidator.Validate(
            new Room(corpus.LoadLevel(origin)), result.Moves!);

        Console.WriteLine(
            onOriginal.Solved
                ? $"source     also solves '{origin}' - the simplification did not change the answer"
                : $"source     DOES NOT solve '{origin}' ({onOriginal.Error ?? "played out unsolved"}) - " +
                  "this answer is only valid for the simplified room");
    }

    int reference = corpus.TryReadSolution(name, out string bundled) ? bundled.Length : -1;
    string compared = reference < 0
        ? ""
        : result.Moves!.Length < reference ? $"  ({reference - result.Moves.Length} SHORTER than the bundled {reference})"
        : result.Moves.Length == reference ? $"  (matches the bundled {reference})"
        : $"  ({result.Moves.Length - reference} longer than the bundled {reference})";

    Console.WriteLine($"solution   {result.Moves!.Length} moves - verified" +
                      (result.Optimal
                          ? macro ? ", shortest among macro-expressible" : ", PROVABLY SHORTEST"
                          : ", not proven optimal") + compared);
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
    // Workers interleave, so batch progress is prefixed with the level and paced
    // slower than a single run - four levels each shouting once a second is noise.
    double progressSeconds = double.TryParse(TakeOption(argv, "--progress"), out double bps) ? bps : 10.0;

    List<string> levels = list is not null
        ? [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
        : [.. corpus.LevelsWithSolutions().Where(l => !IsScripted(l))];

    if (outDir is not null)
    {
        Directory.CreateDirectory(outDir);
    }

    Console.WriteLine(
        $"solving {levels.Count} level(s), {parallel} at a time " +
        $"(weight {weight}, {maxNodes:N0} nodes, {seconds}s each, progress every {progressSeconds:F0}s)");
    Console.WriteLine();

    var consoleLock = new object();
    var runs = new Dictionary<string, LevelResult>();
    int solved = 0, optimal = 0, shorter = 0, gaveUp = 0, done = 0;
    var sw = Stopwatch.StartNew();

    Parallel.ForEach(levels, new ParallelOptions { MaxDegreeOfParallelism = parallel }, name =>
    {
        string line;
        LevelResult? record = null;
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
                ProgressSeconds = progressSeconds,
                Progress = progressSeconds <= 0
                    ? null
                    : text =>
                    {
                        lock (consoleLock)
                        {
                            Console.WriteLine($"{name,-14}{text}");
                        }
                    },
            });

            record = ToRecord(result, macro: false);

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
            // Saved as each level lands rather than once at the end: a long batch
            // is likely to be interrupted, and losing an hour of search because
            // the run was stopped is a bad trade for one extra file write per
            // level.
            if (record is not null)
            {
                runs[name] = record;
                Record(corpus, name, record);
            }

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

// ----------------------------------------------------------------- results --

/// <summary>Turns one search into a record entry.</summary>
static LevelResult ToRecord(SolveResult result, bool macro) => new()
{
    Moves = result.Moves?.Length ?? 0,
    Proven = result.Solved && result.Optimal && !macro,
    Method = macro ? "macro" : "astar",
    Bound = result.DeepestF,
    Expanded = result.Expanded,
    Stored = result.StatesStored,
    Seconds = Math.Round(result.Elapsed.TotalSeconds, 1),
    Status = result.Status,
    Recorded = DateTime.UtcNow.ToString("yyyy-MM-dd"),
};

static void Record(Corpus corpus, string level, LevelResult run) =>
    ResultsFile.Update(corpus.DocsDir, document => ResultsFile.Merge(document, level, run));

static int Results(Corpus corpus, string[] argv)
{
    ResultsDocument document = ResultsFile.Load(corpus.DocsDir);
    if (document.Levels.Count == 0)
    {
        Console.WriteLine($"no results recorded yet ({ResultsFile.PathFor(corpus.DocsDir)})");
        return 0;
    }

    bool unsolvedOnly = argv.Contains("--unsolved");

    Console.WriteLine($"recorded {document.Updated}   ({ResultsFile.PathFor(corpus.DocsDir)})");
    Console.WriteLine();
    Console.WriteLine($"{"level",-14} {"best",5} {"record",6} {"bundled",7} {"bound",5} {"method",6} " +
                      $"{"expanded",10} {"stored",10} {"sec",7}  status");
    Console.WriteLine(new string('-', 104));

    int solved = 0, proven = 0, matchesRecord = 0, beatsBundled = 0, provesRecord = 0;

    foreach ((string level, LevelResult r) in document.Levels.OrderBy(kv => kv.Key, StringComparer.Ordinal))
    {
        corpus.HallOfFame.TryGetValue(level, out (int Steps, string Author) fame);
        int bundled = corpus.TryReadSolution(level, out string b) ? b.Length : 0;

        if (r.Moves > 0)
        {
            solved++;
            if (r.Proven) proven++;
            if (fame.Steps > 0 && r.Moves == fame.Steps) matchesRecord++;
            if (bundled > 0 && r.Moves < bundled) beatsBundled++;
        }
        else if (!unsolvedOnly && false)
        {
            continue;
        }

        if (unsolvedOnly && r.Moves > 0)
        {
            continue;
        }

        string best = r.Moves > 0 ? r.Moves.ToString() : "-";
        // A bound that reaches the record proves the record optimal, even when we
        // never produced the solution ourselves: everything cheaper was expanded
        // and nothing finished.
        bool boundProvesRecord = fame.Steps > 0 && r.Bound >= fame.Steps;
        if (boundProvesRecord && r.Moves == 0)
        {
            provesRecord++;
        }

        string mark = r.Moves > 0 && r.Proven ? " *" : boundProvesRecord ? " ~" : "";
        Console.WriteLine(
            $"{level,-14} {best,5}{mark,-2} {(fame.Steps > 0 ? fame.Steps : 0),4} {(bundled > 0 ? bundled : 0),7} " +
            $"{r.Bound,5} {r.Method,6} {r.Expanded,10:N0} {r.Stored,10:N0} {r.Seconds,7:F1}  {r.Status}");
    }

    Console.WriteLine();
    Console.WriteLine(
        $"{solved} solved, {proven} proven shortest (*), {matchesRecord} match the hall of fame, " +
        $"{beatsBundled} shorter than the bundled solution");
    if (provesRecord > 0)
    {
        Console.WriteLine(
            $"{provesRecord} more (~) have a bound that reaches the hall-of-fame number, " +
            "which proves that number optimal even though we did not find the moves");
    }

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

    if (!argv.Contains("--no-record"))
    {
        // Window results are never proven optimal - they are the best the
        // decomposition found, not a bound. Recorded so the table shows how a
        // level's best was reached, not just its length.
        Record(corpus, name, new LevelResult
        {
            Moves = result.Moves.Length,
            Proven = false,
            Method = "window",
            Expanded = result.NodesExplored,
            Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
            Status = result.Saved > 0 ? $"improved by {result.Saved}" : "no improvement",
            Recorded = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        });
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
