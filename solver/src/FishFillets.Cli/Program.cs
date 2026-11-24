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

// A detached search would otherwise run in Windows' efficiency mode (PowerThrottling.cs).
PowerThrottling.Disable();

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
        "smooth" => Smooth(corpus, argv),
        "info" => Info(corpus, argv),
        "bench" => Bench(corpus, argv),
        "improve" => Improve(corpus, argv),
        "ladder" => Ladder(corpus, argv),
        "solve" => Solve(corpus, argv),
        "reduce" => Reduce(corpus, argv),
        "results" => Results(corpus, argv),
        _ => Unknown(command),
    };
}
catch (Exception ex)
{
    // The stack is worth having when something unexpected breaks; the message
    // alone is enough for the errors this tool raises on purpose.
    Console.Error.WriteLine(
        Environment.GetEnvironmentVariable("FFSOLVE_TRACE") is null ? $"error: {ex.Message}" : ex.ToString());
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
                    [--threads N]  parallel expansion (default: all cores;
                                   same answer and counts as 1 at weight 1)
                    [--macro]      macro-move search (slower; see docs/005)
                    [--layouts]    item-layout search with deferred fish
                                   positions - a finder, not a proof (docs/024)
                    [--no-stuck]   keep states where something is stuck for
                                   good (detection is on by default - docs/020)
                    [--no-stuck-dist]  keep the dead ends, but not the
                                   stuck-wall distances in the estimate (docs/021)
                    [--no-merge]   keep identical items apart instead of
                                   storing their swaps as one state (docs/022)
                    [--no-skip-reversals]  also simulate the move that just
                                   undoes the last turn or fish-only step (docs/028)
                    [--partial-order sleep|pairwise]  move-ordering reduction
                                   (correct, but measured a net loss - docs/011)
                    [--work N]     charge N extra per cell of a movable item on
                                   a fish's way out (docs/013). Guides far better
                                   on item-heavy rooms; gives up "shortest". ~5
                    [--out FILE] [--progress SECONDS] [--quiet] [--no-record]
          ffsolve solve --all               every level with a reference solution
          ffsolve solve --levels-list a,b,c just these
                    [--parallel N] (default min(cores,4) - memory binds first)
                    [--threads N]  per level (default 1 - levels run side by side)
                    [--seconds N] (default 60) [--nodes N] [--out-dir DIR]

        IMPROVE
          ffsolve improve <level>           shorten a known solution by re-solving
                                            windows of it (local shortcuts only)
                    [--window N] (16) [--stride N] (1) [--nodes N] (2,000,000)
                                   per window
                    [--threads N]  windows side by side (default: all cores;
                                   the result does not depend on it)
                    [--seconds N]  stop after N s, keeping what was found
                    [--no-merge]   match the window's end state exactly, not
                                   up to swapped identical items
                    [--no-skip-reversals]  also simulate moves that undo a
                                   fast move
                    [--grow N] [--max-window N]  after a full round, grow the
                                   window by N and go again, until a window hits
                                   a per-window limit (or --max-window). The
                                   stride defaults to N: each round still covers
                                   every window of the round before
                    [--grow-per-start]  with --grow: a window that hits a limit
                                   stops only its own start - the next sizes
                                   skip that start and go on with the others,
                                   until every start is stopped (docs/030)
                    [--window-seconds N] [--window-memory GB]  per-window
                                   limits, as --nodes (0 = no node limit)
                    [--order sequential|spread] (sequential)  from the start
                                   of the solution; or spread: the first
                                   windows at 1/(t+1), 2/(t+1)... of it, then
                                   always the middle of the widest stretch
                                   not yet searched
                    [--heuristic full|walls|manhattan] (full)  the window
                                   bound: the fish's distance around the walls
                                   and the items' pushes still needed (full),
                                   the fish alone (walls), or straight lines
                    [--state FILE] every window searched, kept across runs:
                                   a rerun skips what it covers
                    [--resume]     start from --out if it holds a solution
                    [--no-window-record]  do not add the rounds to
                                   docs/window-results.csv (and .xlsx); results.csv
                                   gets the run only if it shortened the solution
                    [--window-log FILE]  one line per window searched, to the
                                   console and appended to FILE as CSV: outcome,
                                   bound reached, nodes, stored states, seconds
                    [--moves S] [--out FILE] (rewritten after every pass that
                                   shortens the solution) [--quiet] [--no-record]

        INSPECT
          ffsolve results [--unsolved]      what has been solved, and how close
                                            the bound got on what has not
                    [--rebuild]   refresh docs/results.csv from the corpus
                    [--separator culture|comma|semicolon|tab]
                                  rewrite it with that field separator; "culture"
                                  is the local Windows one, which is what Excel
                                  opens a CSV with. Sticks for later writes.
          ffsolve verify <level> | --all | <level> --moves S
          ffsolve smooth <level>            reorder a solution's moves, same length,
                                            so it reads like a person's (docs/031):
                                            the original's moves where they fit,
                                            elsewhere fewer fish switches
                    [--moves S | --from FILE.lua]  (default legacy/solution)
                    [--reference FILE.lua | --no-reference]  (default the
                                   game's original, legacy/solution/old)
                    [--out FILE]
          ffsolve smooth --all [--write]    every level whose original a shorter
                                            solution replaced; --write puts the
                                            result in legacy/solution and
                                            solver/solutions
          ffsolve reduce <level> | --all    models the analysis proves immobile
          ffsolve info <level>              size, models, goals, branching factor
          ffsolve ladder <level>            experiment (docs/030): per start, the
                                            growing windows searched fresh vs on
                                            one incremental table
                    [--starts a,b,c | --every N] (40) [--window N] (32)
                    [--grow N] (4) [--max-window N] [--threads N] (1)
                    [--window-seconds N] [--window-memory GB] [--nodes N]
                    [--moves S | --from FILE.lua] [--log FILE.csv]
                    [--store-pruned]  the ladder also keeps the children a
                                   rung leaves out: no node expanded twice
          ffsolve bench <level> [--rounds N] (200)   physics replay throughput
          ffsolve bench --suite             wall clock of fixed A* searches, to
                    [--reps N] (3)          compare builds: per-expansion
                    [--threads N] (1)       speed-up and bytes per stored state
                    [--quick] [--no-stuck] [--no-stuck-dist] [--no-merge]
                    [--no-skip-reversals]
                    [--out FILE] [--compare FILE]

        OPTIONS (any command)
          --levels <dir>      level JSON     (default <repo>/solver/levels)
          --solutions <dir>   reference sols (default <repo>/legacy/solution)

        NOTES
          Memory runs out before CPU: roughly 40-60 bytes per stored state, so
          --nodes 20000000 is about 1.5 GB. That is what --parallel is limited by.

          A level edited in ffedit records where it came from, and solve
          re-verifies its answer against that original automatically - freezing
          or deleting an item changes the physics, so an answer found on a
          simplified room may not be legal in the real one. When it does replay
          on the original, it is recorded against that level too, as a solution
          but not a proof.

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

    // Precedence when several are given: --layouts, then --items, then --macro.
    bool layouts = argv.Contains("--layouts");
    bool items = !layouts && argv.Contains("--items");
    bool macro = !layouts && !items && argv.Contains("--macro");
    string method = layouts ? "layouts" : items ? "items" : macro ? "macro" : "astar";
    Level level = corpus.LoadLevel(name);
    string? known = corpus.TryReadSolution(name, out string bundledSolution) ? bundledSolution : null;
    LevelReduction reduction = LevelReduction.Verified(level, known);

    LevelJson raw = LevelLoader.LoadFile(corpus.LevelPath(name));
    PartialOrderMode partialOrder = TakeOption(argv, "--partial-order") switch
    {
        "pairwise" => PartialOrderMode.Pairwise,
        "sleep" or "" => PartialOrderMode.SleepSets,
        _ => PartialOrderMode.Off,
    };

    int workPenalty = int.TryParse(TakeOption(argv, "--work"), out int wp) ? wp : 0;
    bool detectStuck = !argv.Contains("--no-stuck");
    bool stuckDistances = !argv.Contains("--no-stuck-dist");
    bool mergeIdentical = !argv.Contains("--no-merge");
    bool skipReversals = !argv.Contains("--no-skip-reversals");

    Console.WriteLine($"level      {name} ({level.Width}x{level.Height})");
    ReportOriginalSolution(corpus, raw, level);
    Console.WriteLine($"reduction  {reduction}");
    Console.WriteLine($"search     {(layouts ? "layout table" : items ? "item-moves" : macro ? "macro-moves" : "per-symbol A*")}, " +
                      $"weight {weight}, node limit {maxNodes:N0}" +
                      (seconds > 0 ? $", {seconds}s limit" : "") +
                      (method == "astar" && partialOrder != PartialOrderMode.Off ? $", partial-order {partialOrder}" : "") +
                      (workPenalty > 0 ? $", item toll {workPenalty} (not admissible - no shortest proof)" : "") +
                      (detectStuck ? (stuckDistances ? "" : ", no stuck-wall distances") : ", no stuck-item detection") +
                      (mergeIdentical ? "" : ", identical items kept apart"));
    Console.WriteLine();

    var options = new SolveOptions
    {
        Weight = weight,
        MaxNodes = maxNodes,
        TimeLimit = seconds > 0 ? TimeSpan.FromSeconds(seconds) : null,
        Progress = quiet ? null : Console.WriteLine,
        ProgressSeconds = double.TryParse(TakeOption(argv, "--progress"), out double ps) ? ps : 1.0,
        PartialOrder = partialOrder,
        WorkPenalty = workPenalty,
        DetectStuck = detectStuck,
        StuckDistances = stuckDistances,
        MergeIdenticalItems = mergeIdentical,
        SkipReversals = skipReversals,
        // Plain A* expands in parallel by default (docs/019); same answer and
        // counts as one thread at weight 1.
        Threads = int.TryParse(TakeOption(argv, "--threads"), out int th) ? Math.Max(1, th) : Environment.ProcessorCount,
    };

    LayoutSolver? layoutSolver = layouts ? new LayoutSolver(level, reduction) : null;
    SolveResult result = layoutSolver is not null
        ? layoutSolver.Solve(options)
        : items
            ? new ItemSolver(level, reduction).Solve(options)
            : macro
                ? new MacroSolver(level, reduction).Solve(options)
                : new Solver(level, reduction).Solve(options);

    Console.WriteLine(
        $"{result.Status}: expanded {result.Expanded:N0}, stored {result.StatesStored:N0}, " +
        $"f={result.DeepestF}, {result.Elapsed.TotalSeconds:F1} s");

    if (layoutSolver is not null)
    {
        Console.WriteLine(
            $"layouts    {layoutSolver.Layouts:N0} layouts, {layoutSolver.Entries:N0} entries, " +
            $"travel plans {layoutSolver.JointCalls:N0} ({layoutSolver.StepAsides:N0} step-asides, {layoutSolver.JointFailures:N0} failed), " +
            $"bound {layoutSolver.BoundEdges:N0}, rejects {layoutSolver.ReconstructionRejects:N0}" +
            (layoutSolver.ReplayFailures > 0 ? $" ({layoutSolver.ReplayFailures:N0} REPLAY FAILURES - a bug)" : "") +
            $", physics {layoutSolver.PhysicsCalls:N0} ({layoutSolver.Retries:N0} retries), " +
            $"interaction edges {layoutSolver.InteractionEdges:N0} ({layoutSolver.InteractionPhysics:N0} presses), stale {layoutSolver.StaleSkips:N0}" +
            (layoutSolver.LastRejectReason is { } why ? $"; last reject: {why}" : ""));
    }

    if (!argv.Contains("--no-record"))
    {
        Record(corpus, name, ToRecord(result, method != "astar", method));
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
    if (raw.SourceLevel is { } origin && corpus.HasLevel(origin))
    {
        SolutionResult onOriginal = SolutionValidator.Validate(
            new Room(corpus.LoadLevel(origin)), result.Moves!);

        Console.WriteLine(
            onOriginal.Solved
                ? $"source     also solves '{origin}' - the simplification did not change the answer"
                : $"source     DOES NOT solve '{origin}' ({onOriginal.Error ?? "played out unsolved"}) - " +
                  "this answer is only valid for the simplified room");

        if (onOriginal.Solved && !argv.Contains("--no-record"))
        {
            // A solution that replays on the real level IS a solution to the real
            // level, so it belongs on that row - otherwise the work hides under a
            // name that is not in the game.
            //
            // What does NOT carry across is optimality. The simplified room has a
            // different state space (a frozen item cannot fall, a deleted one
            // cannot be stood on), so "shortest here" is only an upper bound
            // there, and the bound the search proved is about the wrong level
            // entirely - hence Proven false and no Bound.
            Record(corpus, origin, new LevelResult
            {
                Moves = result.Moves!.Length,
                Proven = false,
                Method = method,
                Source = name,
                // The provenance lives in Source, so the status stays plain -
                // "solved" is what happened, and the missing proof is already
                // visible in the proven column.
                Status = "solved",
                Seconds = Math.Round(result.Elapsed.TotalSeconds, 1),
                Recorded = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            });

            Console.WriteLine($"           recorded against '{origin}' as well" +
                              (outPath is null ? $" - save the moves with:  --out solutions\\{origin}.lua" : ""));
        }
    }

    // A simplified room has no bundled solution of its own, so it is measured
    // against the one for the level it came from - which is the number that
    // actually says whether the detour was worth taking.
    int reference = corpus.TryReadSolution(raw.SourceLevel ?? name, out string bundled) ? bundled.Length : -1;
    string compared = reference < 0
        ? ""
        : result.Moves!.Length < reference ? $"  ({reference - result.Moves.Length} SHORTER than the bundled {reference})"
        : result.Moves.Length == reference ? $"  (matches the bundled {reference})"
        : $"  ({result.Moves.Length - reference} longer than the bundled {reference})";

    Console.WriteLine($"solution   {result.Moves!.Length} moves - verified" +
                      (result.Optimal
                          ? method == "macro" ? ", shortest among macro-expressible" : ", PROVABLY SHORTEST"
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

/// <summary>
/// For a hand-simplified level, replays the <b>original</b> level's own solution
/// against the simplified room before the search starts.
///
/// <para>It answers the question you actually want answered before spending an
/// hour on a search: is this still the same puzzle? A pass means the original
/// path survived the edit, so the room is definitely solvable and that solution's
/// length is already an upper bound. A failure means the edit removed something
/// the original path used.</para>
///
/// <para><b>A failure is not an error, and does not stop the search.</b> Freezing
/// an item stops it falling and deleting one removes something a fish stood on,
/// so breaking the original path is often the whole point of the edit - and the
/// simplified room can still be solvable by a different, shorter route. What it
/// does predict is that an answer found here is less likely to replay back on the
/// real level, which is the check that runs after the search.</para>
/// </summary>
static void ReportOriginalSolution(Corpus corpus, LevelJson raw, Level level)
{
    if (raw.SourceLevel is not { } origin
        || !corpus.HasLevel(origin)
        || !corpus.TryReadSolution(origin, out string moves))
    {
        return;
    }

    SolutionResult replay = SolutionValidator.Validate(new Room(level), moves);
    Console.WriteLine(
        replay.Solved
            ? $"source     '{origin}' - its {moves.Length}-move solution still solves this room, " +
              "so the edit kept the original path (and that is an upper bound)"
            : $"source     '{origin}' - its {moves.Length}-move solution NO LONGER solves this room " +
              $"({replay.Error ?? "played out unsolved"}); the edit changed the puzzle");
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
    int workPenalty = int.TryParse(TakeOption(argv, "--work"), out int wp) ? wp : 0;
    bool detectStuck = !argv.Contains("--no-stuck");
    bool stuckDistances = !argv.Contains("--no-stuck-dist");
    bool mergeIdentical = !argv.Contains("--no-merge");
    bool skipReversals = !argv.Contains("--no-skip-reversals");
    string? outDir = TakeOption(argv, "--out-dir");
    int parallel = int.TryParse(TakeOption(argv, "--parallel"), out int p)
        ? p
        : Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
    int threadsPerLevel = int.TryParse(TakeOption(argv, "--threads"), out int tpl) ? Math.Max(1, tpl) : 1;
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
        $"(weight {weight}, {maxNodes:N0} nodes, {seconds}s each, progress every {progressSeconds:F0}s" +
        (workPenalty > 0 ? $", item toll {workPenalty}" : "") + ")");
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
                WorkPenalty = workPenalty,
                DetectStuck = detectStuck,
                StuckDistances = stuckDistances,
                MergeIdenticalItems = mergeIdentical,
                SkipReversals = skipReversals,
                // Levels already run side by side here; threads per level only on request.
                Threads = threadsPerLevel,
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

            record = ToRecord(result, approximate: false);

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

                // Honours --no-record like the single-level path does. Merging is
                // keep-best, so writing can only improve the table - but a flag
                // the caller passed should still do what it says.
                if (!argv.Contains("--no-record"))
                {
                    Record(corpus, name, record);
                }
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

/// <summary>
/// Turns one search into a record entry. <paramref name="approximate"/> covers
/// the decomposed searches, whose expansions are knowingly incomplete.
/// </summary>
static LevelResult ToRecord(SolveResult result, bool approximate, string method = "macro") => new()
{
    Moves = result.Moves?.Length ?? 0,
    Proven = result.Solved && result.Optimal && !approximate,
    Method = approximate ? method : "astar",
    // A macro run's f is only a bound on solutions the decomposition can
    // express, and that expansion is knowingly incomplete (docs/008). Recording
    // it as this level's lower bound would be a false claim - the table uses
    // `bound` to argue that a hall-of-fame number is optimal.
    Bound = approximate ? 0 : result.DeepestF,
    Expanded = result.Expanded,
    Stored = result.StatesStored,
    Seconds = Math.Round(result.Elapsed.TotalSeconds, 1),
    Status = result.Status,
    Recorded = DateTime.UtcNow.ToString("yyyy-MM-dd"),
};

static void Record(Corpus corpus, string level, LevelResult run) =>
    ResultsFile.Update(corpus, runs => ResultsFile.Merge(runs, level, run));

static int Results(Corpus corpus, string[] argv)
{
    Dictionary<string, LevelResult> runs = ResultsFile.Load(corpus.DocsDir);

    if (argv.Contains("--rebuild"))
    {
        // The derived columns - branch, hall of fame, size, item count - are
        // recomputed on every write anyway. This just forces one without a
        // search, for after worldfame.lua changes or a level is added.
        char? separator = ParseSeparator(TakeOption(argv, "--separator"));
        if (separator is null && TakeOption(argv, "--separator") is { } bad)
        {
            Console.Error.WriteLine($"--separator must be one of: comma , semicolon ; tab, or culture (got '{bad}')");
            return 2;
        }

        ResultsFile.Update(corpus, _ => { }, separator);
        if (WindowResults.Rebuild(corpus) is { } windowWarning)
        {
            Console.Error.WriteLine($"warning: {windowWarning}");
        }

        char used = ResultsFile.DetectSeparator(ResultsFile.PathFor(corpus.DocsDir)) ?? ',';
        Console.WriteLine(
            $"rebuilt {ResultsFile.PathFor(corpus.DocsDir)} ({runs.Count} recorded runs, " +
            $"{(used == '\t' ? "tab" : used == ';' ? "semicolon" : "comma")}-separated)");
        return 0;
    }

    List<ResultsRow> rows = ResultsFile.Rows(corpus, runs);
    bool unsolvedOnly = argv.Contains("--unsolved");

    Console.WriteLine(ResultsFile.PathFor(corpus.DocsDir));
    Console.WriteLine();
    Console.WriteLine($"{"level",-14} {"our",5}   {"best",5} {"bundled",7} {"bound",5} {"method",6} " +
                      $"{"expanded",10} {"stored",10} {"sec",8}  status");

    int solved = 0, proven = 0, matchesRecord = 0, beatsBundled = 0, provesRecord = 0;
    string branch = "";

    foreach (ResultsRow row in rows)
    {
        LevelResult r = row.Run ?? new LevelResult();

        // A simplified room is a search aid, not a level: it is shown, but it
        // does not count towards what the solver has achieved on the game.
        if (!row.IsDerived && r.Moves > 0)
        {
            solved++;
            if (r.Proven) proven++;
            if (row.Best > 0 && r.Moves <= row.Best) matchesRecord++;
            if (row.Bundled > 0 && r.Moves < row.Bundled) beatsBundled++;
        }

        // A bound that reaches the record proves the record optimal, even when we
        // never produced the solution ourselves: everything cheaper was expanded
        // and nothing finished. Only on the real level, though - a bound proved
        // against a simplified room says nothing about the game.
        bool boundProvesRecord = !row.IsDerived && row.Best > 0 && r.Bound >= row.Best;
        if (boundProvesRecord && r.Moves == 0)
        {
            provesRecord++;
        }

        if (unsolvedOnly && r.Moves > 0)
        {
            continue;
        }

        if (row.Branch != branch)
        {
            branch = row.Branch;
            Console.WriteLine();
            Console.WriteLine($"-- {(branch.Length > 0 ? branch : "not on the map")} " +
                              new string('-', Math.Max(0, 100 - branch.Length)));
        }

        string mark = r.Moves > 0 && r.Proven ? " *" : boundProvesRecord ? " ~" : "";
        Console.WriteLine(
            $"{row.Name,-14} {(r.Moves > 0 ? r.Moves.ToString() : "-"),5}{mark,-2} {row.Best,5} {row.Bundled,7} " +
            $"{r.Bound,5} {r.Method,6} {r.Expanded,10:N0} {r.Stored,10:N0} {r.Seconds,8:F1}  " +
            $"{r.Status}{(r.Source.Length > 0 ? $" (on {r.Source})" : "")}");
    }

    Console.WriteLine();
    Console.WriteLine(
        $"{solved} solved, {proven} proven shortest (*), {matchesRecord} match or beat the hall of fame, " +
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

// ------------------------------------------------------------------ ladder --

// The ladder experiment (solver/docs/030): per start, the growing windows searched
// fresh, one A* each, against one incremental search on a single state table.
static int Ladder(Corpus corpus, string[] argv)
{
    if (argv.Length < 2)
    {
        Console.Error.WriteLine("ladder needs a level name");
        return 2;
    }

    var inv = System.Globalization.CultureInfo.InvariantCulture;
    string name = argv[1];
    int window = int.TryParse(TakeOption(argv, "--window"), out int w) ? w : 32;
    int grow = int.TryParse(TakeOption(argv, "--grow"), out int g) ? Math.Max(1, g) : 4;
    int maxWindow = int.TryParse(TakeOption(argv, "--max-window"), out int mw) ? mw : window + (4 * grow);
    long nodeLimit = long.TryParse(TakeOption(argv, "--nodes"), out long n) ? (n <= 0 ? long.MaxValue : n) : long.MaxValue;
    int threads = int.TryParse(TakeOption(argv, "--threads"), out int th) ? Math.Max(1, th) : 1;
    int windowSeconds = int.TryParse(TakeOption(argv, "--window-seconds"), out int wsec) ? Math.Max(0, wsec) : 0;
    double windowGb = double.TryParse(TakeOption(argv, "--window-memory"), System.Globalization.NumberStyles.Float, inv, out double gb) ? gb : 0;
    string? startsText = TakeOption(argv, "--starts");
    int every = int.TryParse(TakeOption(argv, "--every"), out int ev) ? Math.Max(1, ev) : 0;
    string? from = TakeOption(argv, "--from");
    string? logPath = TakeOption(argv, "--log");
    string moves = TakeOption(argv, "--moves") ?? "";
    bool storePruned = argv.Contains("--store-pruned");

    Level level = corpus.LoadLevel(name);
    if (moves.Length == 0 && from is not null)
    {
        moves = ReadSavedMoves(File.ReadAllText(from)) ?? "";
    }

    if (moves.Length == 0 && !corpus.TryReadSolution(name, out moves))
    {
        Console.Error.WriteLine($"no solution for {name} - pass --moves or --from");
        return 2;
    }

    List<int> starts = startsText is not null
        ? [.. startsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.Parse(s, inv))]
        : [.. Enumerable.Range(0, ((moves.Length - window) / Math.Max(1, every == 0 ? 40 : every)) + 1).Select(k => k * (every == 0 ? 40 : every))];
    starts = [.. starts.Where(s => s >= 0 && s + window <= moves.Length)];

    var options = new WindowOptions
    {
        NodeLimit = nodeLimit,
        Threads = threads,
        WindowTimeLimit = windowSeconds > 0 ? TimeSpan.FromSeconds(windowSeconds) : null,
        WindowMemoryLimit = windowGb > 0 ? (long)(windowGb * 1024 * 1024 * 1024) : null,
    };

    Console.WriteLine(
        $"{name}: {moves.Length} moves, {starts.Count} start(s), windows {window}..{maxWindow} by {grow}, {threads} thread(s)"
        + (windowSeconds > 0 ? $", {windowSeconds} s" : "") + (windowGb > 0 ? $", {windowGb.ToString("0.##", inv)} GB" : "")
        + (windowSeconds > 0 || windowGb > 0 ? " per window (per ladder for the incremental one)" : "")
        + (storePruned ? ", the ladder stores pruned children" : ""));

    StreamWriter? log = null;
    if (logPath is not null)
    {
        bool fresh = !File.Exists(logPath) || new FileInfo(logPath).Length == 0;
        log = new StreamWriter(logPath, append: true) { AutoFlush = true };
        if (fresh)
        {
            log.WriteLine("level;start;window;mode;outcome;bound;shortcut;expanded;skipped;rescored;stored;seconds");
        }
    }

    var gate = new object();
    int mismatches = 0;
    void Done(LadderStart r)
    {
        lock (gate)
        {
            Console.WriteLine($"start {r.Start}:");
            for (int k = 0; k < Math.Max(r.Fresh.Count, r.Incremental.Count); k++)
            {
                LadderRung? f = k < r.Fresh.Count ? r.Fresh[k] : null;
                LadderRung? c = k < r.Incremental.Count ? r.Incremental[k] : null;
                bool comparable = f is not null && c is not null
                    && f.Outcome is WindowOutcome.NoShortcut or WindowOutcome.Shortcut
                    && c.Outcome is WindowOutcome.NoShortcut or WindowOutcome.Shortcut;
                bool same = !comparable
                    || (f!.Outcome == c!.Outcome && f.Bound == c.Bound && f.Shortcut?.Length == c.Shortcut?.Length);
                if (!same)
                {
                    mismatches++;
                }

                string Cell(LadderRung? x, bool incremental) => x is null ? "-" : string.Create(inv,
                    $"{KnownWindow.Name(x.Outcome),-12} {x.Bound,3} {x.Expanded,13:N0}{(incremental ? string.Create(inv, $" +{x.Skipped,11:N0} skip {x.Rescored,11:N0} rescore") : "")} {x.Seconds,7:F1} s");
                Console.WriteLine(string.Create(inv,
                    $"  {(f ?? c)!.Window,3}  fresh {Cell(f, false)}  | ladder {Cell(c, true)}{(same ? "" : "  MISMATCH")}"));
                foreach ((LadderRung? x, string mode) in new[] { (f, "fresh"), (c, "ladder") })
                {
                    if (x is not null)
                    {
                        log?.WriteLine(string.Create(inv,
                            $"{name};{r.Start};{x.Window};{mode};{KnownWindow.Name(x.Outcome)};{x.Bound};{x.Shortcut?.Length.ToString(inv) ?? ""};{x.Expanded};{x.Skipped};{x.Rescored};{x.Stored};{x.Seconds:F2}"));
                    }
                }
            }
        }
    }

    var sw = Stopwatch.StartNew();
    IReadOnlyList<LadderStart> results = new WindowOptimizer(level).Ladder(moves, starts, window, grow, maxWindow, options, Done, storePruned);
    log?.Dispose();

    // Totals over the rungs both modes reached, so a limit hit by one only does not skew them.
    long freshNodes = 0, ladderNodes = 0, rescored = 0, skipped = 0, freshStored = 0, ladderStored = 0;
    double freshSecs = 0, ladderSecs = 0;
    foreach (LadderStart r in results)
    {
        int both = Math.Min(r.Fresh.Count, r.Incremental.Count);
        for (int k = 0; k < both; k++)
        {
            freshNodes += r.Fresh[k].Expanded;
            freshSecs += r.Fresh[k].Seconds;
            ladderNodes += r.Incremental[k].Expanded;
            rescored += r.Incremental[k].Rescored;
            skipped += r.Incremental[k].Skipped;
            ladderSecs += r.Incremental[k].Seconds;
        }

        if (both > 0)
        {
            // The memory each needs: the largest table of the fresh windows, the ladder's last.
            freshStored += r.Fresh.Take(both).Max(x => (long)x.Stored);
            ladderStored += r.Incremental[both - 1].Stored;
        }
    }

    Console.WriteLine();
    Console.WriteLine(string.Create(inv,
        $"rungs reached by both: fresh {freshNodes:N0} expansions in {freshSecs:F1} s; ladder {ladderNodes:N0} expansions "
        + $"(+{skipped:N0} skipped, {rescored:N0} re-scored) in {ladderSecs:F1} s"));
    if (freshNodes > 0 && freshSecs > 0)
    {
        Console.WriteLine(string.Create(inv,
            $"ladder: {100.0 * ladderNodes / freshNodes:F0} % of the expansions, {100.0 * ladderSecs / freshSecs:F0} % of the time; "
            + $"tables {100.0 * ladderStored / Math.Max(1, freshStored):F0} % of the fresh windows' largest; "
            + $"{mismatches} mismatch(es); {sw.Elapsed.TotalSeconds:F0} s wall"));
    }

    return mismatches == 0 ? 0 : 1;
}

// ----------------------------------------------------------------- improve --

static int Improve(Corpus corpus, string[] argv)
{
    if (argv.Length < 2)
    {
        Console.Error.WriteLine("improve needs a level name");
        return 2;
    }

    var inv = System.Globalization.CultureInfo.InvariantCulture;
    string name = argv[1];
    int window = int.TryParse(TakeOption(argv, "--window"), out int w) ? w : 16;
    string? strideText = TakeOption(argv, "--stride");
    long nodeLimit = long.TryParse(TakeOption(argv, "--nodes"), out long n) ? (n <= 0 ? long.MaxValue : n) : 2_000_000;
    int threads = int.TryParse(TakeOption(argv, "--threads"), out int th) ? Math.Max(1, th) : Environment.ProcessorCount;
    int seconds = int.TryParse(TakeOption(argv, "--seconds"), out int sec) ? sec : 0;
    int windowSeconds = int.TryParse(TakeOption(argv, "--window-seconds"), out int wsec) ? Math.Max(0, wsec) : 0;
    double windowGb = double.TryParse(TakeOption(argv, "--window-memory"), System.Globalization.NumberStyles.Float, inv, out double gb) ? gb : 0;
    long windowBytes = windowGb > 0 ? (long)(windowGb * 1024 * 1024 * 1024) : long.MaxValue;
    int grow = int.TryParse(TakeOption(argv, "--grow"), out int g) ? Math.Max(0, g) : 0;

    // Growing by N, a stride of N loses nothing: every window of the last size
    // lies inside a window of this size that starts on the stride, so each
    // round covers all of the round before at a quarter of the windows (N = 4).
    int stride = int.TryParse(strideText, out int st) ? Math.Max(1, st) : grow > 0 ? grow : 1;
    int maxWindow = int.TryParse(TakeOption(argv, "--max-window"), out int mw) ? mw : int.MaxValue;
    string orderText = TakeOption(argv, "--order") ?? "sequential";
    string heuristicText = TakeOption(argv, "--heuristic") ?? "full";
    bool mergeIdentical = !argv.Contains("--no-merge");
    bool skipReversals = !argv.Contains("--no-skip-reversals");
    string? outPath = TakeOption(argv, "--out");
    string? windowLog = TakeOption(argv, "--window-log");
    string? statePath = TakeOption(argv, "--state");
    bool resume = argv.Contains("--resume");
    bool recordWindows = !argv.Contains("--no-window-record");
    bool quiet = argv.Contains("--quiet");
    bool perStart = grow > 0 && argv.Contains("--grow-per-start");
    var policy = new GrowPolicy();

    WindowOrder order;
    switch (orderText)
    {
        case "spread": order = WindowOrder.Spread; break;
        case "sequential": order = WindowOrder.Sequential; break;
        default:
            Console.Error.WriteLine($"--order must be spread or sequential (got '{orderText}')");
            return 2;
    }

    WindowHeuristic heuristic;
    switch (heuristicText)
    {
        case "full": heuristic = WindowHeuristic.WallsAndItems; break;
        case "walls": heuristic = WindowHeuristic.Walls; break;
        case "manhattan": heuristic = WindowHeuristic.Manhattan; break;
        default:
            Console.Error.WriteLine($"--heuristic must be full, walls or manhattan (got '{heuristicText}')");
            return 2;
    }

    Level level = corpus.LoadLevel(name);

    // --resume picks up the best solution a stopped run left in --out.
    string moves = TakeOption(argv, "--moves") ?? "";
    string source = moves.Length > 0 ? "--moves" : "legacy/solution";
    if (resume && outPath is not null && File.Exists(outPath)
        && ReadSavedMoves(File.ReadAllText(outPath)) is { Length: > 0 } saved
        && SolutionValidator.Validate(new Room(level), saved).Solved)
    {
        moves = saved;
        source = outPath;
    }
    else if (moves.Length == 0 && !corpus.TryReadSolution(name, out moves))
    {
        Console.Error.WriteLine($"no solution to improve for {name} - pass --moves");
        return 2;
    }

    Console.WriteLine(
        $"{name}: {moves.Length} moves (from {source}), window {window}" + (grow > 0 ? $" growing by {grow}" + (maxWindow < int.MaxValue ? $" up to {maxWindow}" : "") : "")
        + $", stride {stride}, {order.ToString().ToLowerInvariant()} order, {heuristicText} bound, {threads} thread(s)"
        + (nodeLimit < long.MaxValue ? $", {nodeLimit:N0} nodes" : "")
        + (windowSeconds > 0 ? $", {windowSeconds} s" : "")
        + (windowBytes < long.MaxValue ? $", {windowGb.ToString("0.##", inv)} GB" : "")
        + (nodeLimit < long.MaxValue || windowSeconds > 0 || windowBytes < long.MaxValue ? " per window" : "")
        + (seconds > 0 ? $", {seconds} s in all" : "")
        + (mergeIdentical ? "" : ", identical items kept apart")
        + (skipReversals ? "" : ", no reversal skip")
        + (perStart ? ", each start grows until it hits a limit" : ""));

    void Save(string improved)
    {
        // Only a replayed solution is ever written, so a run stopped at any
        // point leaves a valid file behind.
        if (outPath is not null && SolutionValidator.Validate(new Room(level), improved).Solved)
        {
            File.WriteAllText(outPath, $"\nsaved_moves = '{improved}'\n");
        }
    }

    // --state: every window searched, kept across runs. A window ruled out,
    // or stopped by a limit no smaller than the current one, is skipped when
    // the run is started again. A window the run itself stopped is not kept.
    var known = new Dictionary<string, KnownWindow>(StringComparer.Ordinal);
    StreamWriter? stateWriter = null;
    if (statePath is not null)
    {
        if (File.Exists(statePath))
        {
            foreach (string line in File.ReadLines(statePath).Skip(1))
            {
                if (KnownWindow.Parse(line) is { } k)
                {
                    known[k.Id] = k;
                }
            }

            Console.WriteLine($"{known.Count:N0} window(s) known from {statePath}");
        }

        bool fresh = !File.Exists(statePath) || new FileInfo(statePath).Length == 0;
        stateWriter = new StreamWriter(statePath, append: true) { AutoFlush = true };
        if (fresh)
        {
            stateWriter.WriteLine(KnownWindow.Header);
        }
    }

    // One line per window, to the console and appended to --window-log as CSV:
    // how far each search got, so the limits can be set from what the windows
    // actually needed. Workers report concurrently, so the writes are locked.
    StreamWriter? logWriter = null;
    if (windowLog is not null)
    {
        bool fresh = !File.Exists(windowLog) || new FileInfo(windowLog).Length == 0;
        logWriter = new StreamWriter(windowLog, append: true) { AutoFlush = true };
        if (fresh)
        {
            logWriter.WriteLine("level;window;pass;start;end;length;outcome;bound;nodes;stored;seconds");
        }
    }

    var logLock = new object();
    int size = window;
    bool limitedAtSize = false;
    var limitedWindows = new List<string>(); // this size's windows stopped by a per-window limit
    var outcomes = new Dictionary<string, string>(StringComparer.Ordinal); // this size's windows, by id, for window-results.csv
    int knownAtSize = 0;

    // [start..end] padded to the solution's length, so the columns after it line up.
    int digits = Math.Max(1, moves.Length.ToString(inv).Length);
    string Span(WindowReport r) => $"[{r.Start.ToString(inv).PadLeft(digits)}..{r.End.ToString(inv).PadLeft(digits)}]";

    bool IsKnownBarren(string id)
    {
        if (perStart && policy.Skips(id))
        {
            lock (logLock)
            {
                outcomes[id] = "blocked"; // its start hit a limit at a smaller size
            }

            return true;
        }

        if (!known.TryGetValue(id, out KnownWindow? k) || !k.Covers(nodeLimit, windowSeconds, windowBytes, heuristicText))
        {
            return false;
        }

        lock (logLock)
        {
            outcomes[id] = k.Outcome;
            knownAtSize++;
        }

        if (k.Outcome != "none")
        {
            if (perStart)
            {
                policy.Limited(id);
            }

            limitedAtSize = true; // a limit was hit at this size, in an earlier run
            lock (logLock)
            {
                limitedWindows.Add($"{k.Outcome} at bound {k.Bound}/{k.Length} (an earlier run)");
            }
        }

        return true;
    }

    void WindowDone(WindowReport r)
    {
        string outcome = KnownWindow.Name(r.Outcome);
        lock (logLock)
        {
            outcomes[r.Id] = outcome;
        }

        if (r.HitLimit)
        {
            limitedAtSize = true;
            if (perStart)
            {
                policy.Limited(r.Id);
            }

            string where = $"{Span(r)} {outcome} at bound {r.Bound}/{r.Length}";
            lock (logLock)
            {
                limitedWindows.Add(where);
                if (!quiet && grow > 0)
                {
                    Console.WriteLine(perStart
                        ? $"  window {where}: this start grows no further; the others go on"
                        : $"  window {where}: the other windows of size {size} still run; size {size + grow} will not start");
                }
            }
        }

        lock (logLock)
        {
            if (logWriter is not null)
            {
                Console.WriteLine(string.Create(inv,
                    $"    {Span(r)} {outcome,-12} bound {r.Bound,3}/{r.Length,-3} {r.Nodes,13:N0} nodes {r.Stored,13:N0} stored {r.Seconds,8:F1} s"));
                logWriter.WriteLine(string.Create(inv,
                    $"{name};{size};{r.Pass};{r.Start};{r.End};{r.Length};{outcome};{r.Bound};{r.Nodes};{r.Stored};{r.Seconds:F2}"));
            }

            if (stateWriter is not null && r.Outcome is not (WindowOutcome.Stopped or WindowOutcome.Shortcut))
            {
                var k = new KnownWindow(r.Id, outcome, r.Bound, r.Length, r.Nodes, nodeLimit, windowSeconds, windowBytes, heuristicText);
                known[r.Id] = k;
                stateWriter.WriteLine(k.ToLine());
            }
        }
    }

    // One row of window-results.csv per size: what this round found, and the
    // longest stretch it proved free of shortcuts. A resumed round counts the
    // windows its state file already knew, and adds to the time already spent.
    void RecordRound(OptimizeResult result, int from, TimeSpan spent)
    {
        int last = Math.Max(0, from - size);
        int expected = from < 2 ? 0 : (last / stride) + 1 + (last % stride != 0 ? 1 : 0);
        int ruledOut = outcomes.Values.Count(o => o == "none");
        int shortcuts = outcomes.Values.Count(o => o == "shortcut");
        int limited = outcomes.Values.Count(o => o is "node-limit" or "memory-limit" or "window-time" or "blocked");
        int stoppedCount = outcomes.Values.Count(o => o == "stopped");
        int notSearched = stoppedCount + (result.TimedOut ? Math.Max(0, expected - outcomes.Count) : 0);
        int? covered = notSearched == 0 && limited == 0 ? Math.Min(size - stride + 1, from) : null;

        long nodes = result.NodesExplored;
        double secs = spent.TotalSeconds;
        if (knownAtSize > 0
            && WindowResults.Load(corpus.DocsDir).FirstOrDefault(r => r.Key == (name, size, stride, heuristicText)) is { } before)
        {
            nodes += before.Nodes;
            secs += before.Seconds;
        }

        string? warning = WindowResults.Record(corpus, [new WindowRound(
            name, size, stride, heuristicText, from, result.Moves.Length,
            Math.Max(expected, outcomes.Count), ruledOut, shortcuts, limited, notSearched, covered,
            nodeLimit == long.MaxValue ? null : nodeLimit, windowSeconds > 0 ? windowSeconds : null,
            windowBytes == long.MaxValue ? null : windowGb,
            nodes, Math.Round(secs, 1), result.TimedOut ? "run time limit" : "",
            DateTime.UtcNow.ToString("yyyy-MM-dd"))]);
        if (warning is not null)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }
    }

    var optimizer = new WindowOptimizer(level);
    var sw = Stopwatch.StartNew();
    int original = moves.Length;
    long totalNodes = 0;
    int totalSearched = 0, totalImproved = 0, totalPasses = 0;
    bool timedOut = false;
    string stopReason = "";

    while (true)
    {
        TimeSpan? remaining = seconds > 0 ? TimeSpan.FromSeconds(seconds) - sw.Elapsed : null;
        if (remaining is { } left && left <= TimeSpan.Zero)
        {
            timedOut = true;
            break;
        }

        limitedAtSize = false;
        limitedWindows.Clear();
        outcomes.Clear();
        knownAtSize = 0;
        policy.NextSize();
        int sizeFrom = moves.Length;
        TimeSpan sizeStart = sw.Elapsed;
        if (grow > 0)
        {
            Console.WriteLine($"--- window {size} from {moves.Length} moves, {sw.Elapsed.TotalSeconds:F0} s so far");
        }

        OptimizeResult result = optimizer.Optimize(moves, new WindowOptions
        {
            Window = size,
            Stride = stride,
            NodeLimit = nodeLimit,
            Threads = threads,
            Order = order,
            Heuristic = heuristic,
            TimeLimit = remaining,
            WindowTimeLimit = windowSeconds > 0 ? TimeSpan.FromSeconds(windowSeconds) : null,
            WindowMemoryLimit = windowBytes < long.MaxValue ? windowBytes : null,
            IsKnownBarren = known.Count > 0 || stateWriter is not null || perStart ? IsKnownBarren : null,
            MergeIdenticalItems = mergeIdentical,
            SkipReversals = skipReversals,
            Progress = quiet ? null : Console.WriteLine,
            Improved = Save,
            WindowDone = WindowDone,
        });

        moves = result.Moves;
        if (recordWindows)
        {
            RecordRound(result, sizeFrom, sw.Elapsed - sizeStart);
        }

        totalNodes += result.NodesExplored;
        totalSearched += result.WindowsSearched;
        totalImproved += result.WindowsImproved;
        totalPasses += result.Passes;
        if (result.TimedOut)
        {
            timedOut = true;
            break;
        }

        if (grow == 0)
        {
            break;
        }

        if (perStart)
        {
            // Only when every window of this size was skipped for its start: the
            // ones that hit a limit now are skipped from the next size on.
            int listed = GrowPolicy.WindowCount(sizeFrom, size, stride);
            if (policy.BlockedAtSize >= listed)
            {
                stopReason = $"every start is blocked at size {size} ({policy.Blocked} start(s) hit a per-window limit)";
                break;
            }

            if (limitedAtSize && !quiet)
            {
                Console.WriteLine($"  {policy.Blocked} start(s) blocked so far; size {size + grow} skips them");
            }
        }
        else if (limitedAtSize)
        {
            stopReason = $"every window of size {size} was searched, {limitedWindows.Count} of them hit a per-window limit, "
                + $"so size {size + grow} was not started: {string.Join("; ", limitedWindows.Take(10))}"
                + (limitedWindows.Count > 10 ? $"; and {limitedWindows.Count - 10} more" : "");
            break;
        }

        if (size + grow > Math.Min(maxWindow, moves.Length))
        {
            stopReason = $"next window would exceed {Math.Min(maxWindow, moves.Length)}";
            break;
        }

        size += grow;
    }

    sw.Stop();
    logWriter?.Dispose();
    stateWriter?.Dispose();

    // Never trust a spliced solution on the optimiser's word - replay it.
    SolutionResult check = SolutionValidator.Validate(new Room(level), moves);
    if (!check.Solved)
    {
        Console.Error.WriteLine($"BUG: the improved solution does not solve the level ({check.Error})");
        return 3;
    }

    int saved2 = original - moves.Length;
    // results.csv records solutions, so a window run goes there only when it
    // shortened one; every round goes to window-results.csv instead. (Recording
    // a run that found nothing replaced the counts of the run that set the
    // level's bound - see docs/029.)
    if (saved2 > 0 && !argv.Contains("--no-record"))
    {
        // Window results are never proven optimal - they are the best the
        // decomposition found, not a bound.
        Record(corpus, name, new LevelResult
        {
            Moves = moves.Length,
            Proven = false,
            Method = "window",
            Expanded = totalNodes,
            Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
            Status = (saved2 > 0 ? $"improved by {saved2}" : "no improvement") + (timedOut ? ", time limit" : ""),
            Recorded = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        });
    }

    Console.WriteLine();
    Console.WriteLine($"{original} -> {moves.Length} moves ({saved2} saved) - verified");
    Console.WriteLine(
        $"{totalPasses} pass(es), {totalSearched:N0} window(s) searched, {totalImproved} improved, " +
        $"{totalNodes:N0} nodes in {sw.Elapsed.TotalSeconds:F1} s" + (timedOut ? " - time limit reached" : "")
        + (grow > 0 ? $"; last window {size}" + (stopReason.Length > 0 ? $" ({stopReason})" : "") : ""));

    if (saved2 > 0)
    {
        Console.WriteLine();
        Console.WriteLine(moves);
    }

    if (outPath is not null)
    {
        File.WriteAllText(outPath, $"\nsaved_moves = '{moves}'\n");
        Console.WriteLine($"written to {outPath}");
    }

    return 0;
}

/// <summary>The saved_moves string of a solution file, or null.</summary>
static string? ReadSavedMoves(string text)
{
    int a = text.IndexOf("saved_moves", StringComparison.Ordinal);
    int open = a < 0 ? -1 : text.IndexOf('\'', a);
    int close = open < 0 ? -1 : text.IndexOf('\'', open + 1);
    return close < 0 ? null : text[(open + 1)..close];
}

// ------------------------------------------------------------------ smooth --

// Reorders a solution's moves without making it longer (solver/docs/031): the game's
// original moves where they fit between shared states, elsewhere fewer fish switches.
static int Smooth(Corpus corpus, string[] argv)
{
    bool all = argv.Contains("--all");
    bool write = argv.Contains("--write");
    bool noReference = argv.Contains("--no-reference");
    string? moves = TakeOption(argv, "--moves");
    string? from = TakeOption(argv, "--from");
    string? referenceFile = TakeOption(argv, "--reference");
    string? outPath = TakeOption(argv, "--out");
    if (!all && argv.Length < 2)
    {
        Console.Error.WriteLine("smooth needs a level name, or --all");
        return 2;
    }

    if (all && (moves ?? from ?? referenceFile ?? outPath) is not null)
    {
        Console.Error.WriteLine("smooth --all takes no --moves/--from/--reference/--out");
        return 2;
    }

    // --all: the levels whose original moved to legacy/solution/old.
    string oldDir = Path.Combine(corpus.SolutionsDir, Corpus.OriginalSolutionsFolder);
    IReadOnlyList<string> levels = all
        ? corpus.LevelNames.Where(l => File.Exists(Path.Combine(oldDir, l + ".lua"))).ToList()
        : [argv[1]];
    string ownDir = Path.Combine(Path.GetDirectoryName(corpus.LevelsDir) ?? ".", "solutions");

    var inv = System.Globalization.CultureInfo.InvariantCulture;
    Console.WriteLine($"{"level",-12} {"moves",11} {"switches",11} {"from orig.",13} {"shared states",15} {"changes",8} {"s",6}");
    int failures = 0;
    foreach (string level in levels)
    {
        if (!corpus.HasLevel(level))
        {
            Console.WriteLine($"{level,-12} no exported level");
            failures++;
            continue;
        }

        string? input = moves;
        if (input is null && from is not null)
        {
            input = ReadSavedMoves(File.ReadAllText(from)) ?? throw new InvalidDataException($"{from}: no saved_moves");
        }

        if (input is null && !corpus.TryReadSolution(level, out input))
        {
            Console.WriteLine($"{level,-12} no solution");
            failures++;
            continue;
        }

        string? reference = null;
        if (referenceFile is not null)
        {
            reference = ReadSavedMoves(File.ReadAllText(referenceFile)) ?? throw new InvalidDataException($"{referenceFile}: no saved_moves");
        }
        else if (!noReference && corpus.TryReadOriginalSolution(level, out string original))
        {
            reference = original;
        }

        SmoothResult r = new SolutionSmoother(corpus.LoadLevel(level)).Smooth(input, reference);
        string Pct(int x) => r.Moves.Length == 0 ? "-" : (100.0 * x / r.Moves.Length).ToString("0", inv) + "%";
        Console.WriteLine(string.Create(inv,
            $"{level,-12} {r.InputLength,5}->{r.Moves.Length,-5} {r.SwitchesBefore,4} -> {r.SwitchesAfter,-4} " +
            $"{Pct(r.ReferenceMovesBefore),5} -> {Pct(r.ReferenceMovesAfter),-5} " +
            $"{r.SharedStatesBefore,6} -> {r.SharedStatesAfter,-6} {r.Accepted,8} {r.Seconds,6:0.0}"));

        if (outPath is not null)
        {
            File.WriteAllText(outPath, $"\nsaved_moves = '{r.Moves}'\n");
            Console.WriteLine($"  written to {outPath}");
        }

        if (write && r.Changed)
        {
            foreach (string dir in new[] { corpus.SolutionsDir, ownDir })
            {
                string path = Path.Combine(dir, level + ".lua");
                if (dir == corpus.SolutionsDir || File.Exists(path))
                {
                    File.WriteAllText(path, $"\nsaved_moves = '{r.Moves}'\n");
                }
            }

            Console.WriteLine($"  written to legacy/solution{(File.Exists(Path.Combine(ownDir, level + ".lua")) ? " and solver/solutions" : "")}");
        }

        if (r.Moves.Length < r.InputLength)
        {
            Console.WriteLine($"  {level}: {r.InputLength - r.Moves.Length} move(s) shorter - record it in results.csv");
        }
    }

    return failures == 0 ? 0 : 1;
}

// ------------------------------------------------------------------- bench --

static int Bench(Corpus corpus, string[] argv)
{
    if (argv.Contains("--suite"))
    {
        return BenchSuite(corpus, argv);
    }

    if (argv.Length < 2)
    {
        Console.Error.WriteLine("bench needs a level name, or --suite");
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

/// <summary>
/// Wall-clock benchmark of real searches, for comparing two builds of the
/// solver (before/after an optimisation, JIT vs AOT, runtime versions).
///
/// <para>The workloads are node-limited plain A* at weight 1, so every build
/// does exactly the same work - the expansion count is printed, and a mismatch
/// against the compared file means the search itself changed, not just its
/// speed. Time is the best of <c>--reps</c> runs, which filters out most of the
/// noise from whatever else the machine is doing.</para>
///
/// <para>Bytes per state is what the search's own structures hold at their
/// current capacity (<see cref="Solver.MemoryBytes"/>, growth headroom included)
/// divided by the states it stored - the number the memory work (review P1) is
/// meant to cut. Counted from array sizes, not the GC, so it is exact and
/// repeatable.</para>
/// </summary>
static int BenchSuite(Corpus corpus, string[] argv)
{
    int reps = int.TryParse(TakeOption(argv, "--reps"), out int rp) ? Math.Max(1, rp) : 3;
    int threads = int.TryParse(TakeOption(argv, "--threads"), out int bt) ? Math.Max(1, bt) : 1;
    string? outPath = TakeOption(argv, "--out");
    string? comparePath = TakeOption(argv, "--compare");
    bool quick = argv.Contains("--quick");
    bool detectStuck = !argv.Contains("--no-stuck");
    bool stuckDistances = !argv.Contains("--no-stuck-dist");
    bool mergeIdentical = !argv.Contains("--no-merge");
    bool skipReversals = !argv.Contains("--no-skip-reversals");

    (string Level, long Nodes)[] suite = quick
        ? [("start", 0), ("cannons", 0)]
        : [("start", 0), ("wc", 0), ("cannons", 0), ("wreck", 0), ("society", 1_500_000), ("library", 2_000_000)];

    string runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
                     // IsDynamicCodeSupported is false under plain `dotnet run` too once a
                     // project sets PublishAot; only an AOT image has no JIT at all.
                     + (System.Runtime.JitInfo.GetCompiledMethodCount() > 0 ? " JIT" : " AOT");
    runtime += $", {threads} thread{(threads == 1 ? "" : "s")}" + (detectStuck ? (stuckDistances ? "" : ", no stuck distances") : ", no stuck") + (mergeIdentical ? "" : ", no merge") + (skipReversals ? "" : ", no reversal skip");
    Console.WriteLine($"bench suite - {runtime}, {Environment.ProcessorCount} cores, best of {reps}");
    Console.WriteLine($"{"level",-10} {"expanded",12} {"stored",12} {"seconds",8} {"k exp/s",8} {"B/state",8} {"f",5}");

    var rows = new List<(string Level, long Expanded, int Stored, double Seconds, double BytesPerState, int F)>();
    foreach ((string name, long nodes) in suite)
    {
        Level level = corpus.LoadLevel(name);
        string? known = corpus.TryReadSolution(name, out string k) ? k : null;
        LevelReduction reduction = LevelReduction.Verified(level, known);
        var options = new SolveOptions { MaxNodes = nodes > 0 ? nodes : 20_000_000, Threads = threads, DetectStuck = detectStuck, StuckDistances = stuckDistances, MergeIdenticalItems = mergeIdentical, SkipReversals = skipReversals };

        double best = double.MaxValue;
        SolveResult? result = null;
        double bytesPerState = 0;
        for (int rep = 0; rep < reps; rep++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var solver = new Solver(level, reduction);
            SolveResult r = solver.Solve(options);
            if (r.Elapsed.TotalSeconds < best)
            {
                best = r.Elapsed.TotalSeconds;
                result = r;
                bytesPerState = r.StatesStored > 0 ? (double)solver.MemoryBytes / r.StatesStored : 0;
            }
        }

        // f: the proven bound reached - the solution length when solved. With
        // a node cap, how far the same budget got matters more than its rate.
        rows.Add((name, result!.Expanded, result.StatesStored, best, bytesPerState, result.DeepestF));
        Console.WriteLine(
            $"{name,-10} {result.Expanded,12:N0} {result.StatesStored,12:N0} {best,8:F2} " +
            $"{result.Expanded / best / 1000,8:F0} {bytesPerState,8:F0} {result.DeepestF,5}");
    }

    if (outPath is not null)
    {
        var text = new System.Text.StringBuilder($"# {runtime}\nlevel;expanded;stored;seconds;bytesPerState;f\n");
        foreach (var row in rows)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{row.Level};{row.Expanded};{row.Stored};{row.Seconds:F3};{row.BytesPerState:F1};{row.F}\n");
        }

        File.WriteAllText(outPath, text.ToString());
        Console.WriteLine($"written to {outPath}");
    }

    if (comparePath is not null)
    {
        var before = File.ReadAllLines(comparePath)
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("level;", StringComparison.Ordinal))
            .Select(l => l.Split(';'))
            .ToDictionary(
                p => p[0],
                p => (Expanded: long.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                      Seconds: double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture),
                      Bytes: double.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture),
                      F: p.Length > 5 ? int.Parse(p[5], System.Globalization.CultureInfo.InvariantCulture) : 0));

        Console.WriteLine();
        Console.WriteLine($"vs {Path.GetFileName(comparePath)} ({File.ReadLines(comparePath).First().TrimStart('#', ' ')})");
        // speed-up is per expansion; time is wall clock for the whole run, which
        // is the one that counts when the two searches expand different nodes.
        Console.WriteLine($"{"level",-10} {"speed-up",9} {"time",9} {"memory",9}  note");
        foreach (var row in rows)
        {
            if (!before.TryGetValue(row.Level, out var b))
            {
                continue;
            }

            string note = b.Expanded == row.Expanded ? "" : $"different search: {b.Expanded:N0} -> {row.Expanded:N0} expansions";
            if (b.F > 0 && b.F != row.F)
            {
                note += $", f {b.F} -> {row.F}";
            }

            double speedUp = (b.Seconds / b.Expanded) / (row.Seconds / row.Expanded);
            Console.WriteLine($"{row.Level,-10} {speedUp,8:F2}x {b.Seconds / row.Seconds,8:F2}x {(b.Bytes > 0 ? row.BytesPerState / b.Bytes : 0),8:F2}x  {note}");
        }
    }

    return 0;
}

// ----------------------------------------------------------------- options --

/// <summary>
/// Reads --separator: a literal character, a name, or "culture" for whatever the
/// local Windows list separator is (a semicolon in cs/de/fr, a comma in en) -
/// which is the setting Excel opens a CSV with. Null means "leave it alone".
/// </summary>
static char? ParseSeparator(string? text) => text?.ToLowerInvariant() switch
{
    null => null,
    "culture" or "windows" or "local" => ResultsFile.CultureSeparator(),
    "comma" or "," => ',',
    "semicolon" or ";" => ';',
    "tab" or "\t" => '\t',
    _ => null,
};

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
