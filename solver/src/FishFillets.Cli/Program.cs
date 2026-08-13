using System.Diagnostics;

using FishFillets.Physics;

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
