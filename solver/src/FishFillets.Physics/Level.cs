namespace FishFillets.Physics;

/// <summary>
/// An immutable, parsed level: shapes, weights, goals and fish control symbols,
/// built once from a <see cref="LevelJson"/> and shared by every
/// <see cref="Room"/> (and every search thread) that plays it.
///
/// Absorbs legacy/src/level/ModelFactory.cpp (web/src/game/ModelFactory.ts) -
/// the kind -&gt; (weight, power, alive) table, the output_* plug items, and the
/// per-fish move symbols.
/// </summary>
public sealed class Level
{
    /// <summary>
    /// legacy ModelFactory::createUnit()'s recorded-move symbols: lowercase for
    /// fish_small, uppercase for fish_big. See docs/021 - this is also the flat
    /// string format legacy/solution/*.lua's saved_moves uses.
    /// </summary>
    private const string SmallFishSymbols = "udlr";

    private const string BigFishSymbols = "UDLR";

    public string Name { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Real models, in the order the level declared them (index 0 is the room's wall shape).</summary>
    public ModelDef[] Models { get; }

    public UnitDef[] Units { get; }

    /// <summary>
    /// Index of the shared border model. Unlike the browser port (which keeps a
    /// separate Cube with index -1), the border lives at the end of the model
    /// arrays here so every recursive rule can walk into it without a special
    /// case; loops over "the room's models" run 0..<see cref="ModelCount"/>-1 and
    /// so skip it, exactly like the original's Room::m_models.
    /// </summary>
    public int BorderIndex => Models.Length - 1;

    /// <summary>Real models only (the border excluded).</summary>
    public int ModelCount => Models.Length - 1;

    private Level(string name, int width, int height, ModelDef[] models, UnitDef[] units)
    {
        Name = name;
        Width = width;
        Height = height;
        Models = models;
        Units = units;
    }

    public static Level Build(LevelJson json)
    {
        var models = new ModelDef[json.Models.Count + 1];
        var units = new List<UnitDef>(4);

        for (int i = 0; i < json.Models.Count; i++)
        {
            ModelJson m = json.Models[i];
            models[i] = CreateModel(m);
            if (TryCreateUnit(i, m.Kind, out UnitDef unit))
            {
                units.Add(unit);
            }
        }

        // legacy ModelFactory::createBorder() - a single FIXED, not-alive cell
        // parked out of bounds at (-1,-1). Every out-of-bounds probe resolves to
        // it ("border is as wall"), and its own probes resolve back to itself,
        // which is what makes getPlacedResist's "skip myself" rule terminate the
        // recursion there.
        models[^1] = new ModelDef
        {
            Kind = "border",
            Shape = new Shape("X\n"),
            Weight = Weight.Fixed,
            Power = Weight.None,
            IsAlive = false,
            Goal = GoalKind.No,
            IsLeft = true,
            X = -1,
            Y = -1,
        };

        return new Level(json.Name, json.Width, json.Height, models, units.ToArray());
    }

    private static ModelDef CreateModel(ModelJson m)
    {
        var shape = new Shape(m.Shape);
        GoalKind goal = ParseGoal(m.Goal);

        if (m.Kind.StartsWith("output_", StringComparison.Ordinal))
        {
            // legacy ModelFactory::createOutputItem: a FIXED cube with a one-way
            // exit direction. windoze's spuntik is the only one in the game.
            return new ModelDef
            {
                Kind = m.Kind,
                Shape = shape,
                Weight = Weight.Fixed,
                Power = Weight.None,
                IsAlive = false,
                Goal = goal,
                IsLeft = m.IsLeft,
                X = (short)m.X,
                Y = (short)m.Y,
                OutDir = ParseOutDir(m.Kind),
                OutCapacity = 2,
            };
        }

        (Weight weight, Weight power, bool alive) = CreateParams(m.Kind);
        return new ModelDef
        {
            Kind = m.Kind,
            Shape = shape,
            Weight = weight,
            Power = power,
            IsAlive = alive,
            Goal = goal,
            IsLeft = m.IsLeft,
            X = (short)m.X,
            Y = (short)m.Y,
        };
    }

    /// <summary>Port of legacy ModelFactory::createParams().</summary>
    private static (Weight Weight, Weight Power, bool Alive) CreateParams(string kind) => kind switch
    {
        "fish_small" => (Weight.Light, Weight.Light, true),
        "fish_big" => (Weight.Light, Weight.Heavy, true),
        "item_light" => (Weight.Light, Weight.None, false),
        "item_heavy" => (Weight.Heavy, Weight.None, false),
        "item_fixed" => (Weight.Fixed, Weight.None, false),
        // windoze's "old couple" - a second pair of fish in the bonus window.
        _ when kind.StartsWith("fish_extra", StringComparison.Ordinal) => (Weight.Light, Weight.Light, true),
        _ when kind.StartsWith("fish_EXTRA", StringComparison.Ordinal) => (Weight.Light, Weight.Heavy, true),
        _ => throw new InvalidDataException($"unknown model kind: {kind}"),
    };

    private static Dir ParseOutDir(string kind) => kind switch
    {
        "output_left" => Dir.Left,
        "output_right" => Dir.Right,
        "output_up" => Dir.Up,
        "output_down" => Dir.Down,
        _ => throw new InvalidDataException($"unknown output dir: {kind}"),
    };

    private static GoalKind ParseGoal(string goal) => goal switch
    {
        "goal_out" => GoalKind.Out,
        "goal_escape" => GoalKind.Escape,
        "goal_alive" => GoalKind.Alive,
        _ => GoalKind.No,
    };

    private static bool TryCreateUnit(int index, string kind, out UnitDef unit)
    {
        string symbols;
        if (kind is "fish_small")
        {
            symbols = SmallFishSymbols;
        }
        else if (kind is "fish_big")
        {
            symbols = BigFishSymbols;
        }
        else if (kind.StartsWith("fish_extra", StringComparison.Ordinal)
                 || kind.StartsWith("fish_EXTRA", StringComparison.Ordinal))
        {
            // legacy ModelFactory::parseExtraControlSym: the four symbols are
            // encoded in the kind string itself ("fish_extra-UDLR").
            const int prefixLen = 11; // "fish_extra-"
            if (kind.Length != prefixLen + 4)
            {
                throw new InvalidDataException($"extra fish kind must specify 4 control symbols: {kind}");
            }

            symbols = kind[prefixLen..];
        }
        else
        {
            unit = default;
            return false;
        }

        unit = new UnitDef
        {
            Model = index,
            Up = symbols[0],
            Down = symbols[1],
            Left = symbols[2],
            Right = symbols[3],
        };
        return true;
    }
}
