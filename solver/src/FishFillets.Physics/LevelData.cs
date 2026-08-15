using System.Text.Json;
using System.Text.Json.Serialization;

namespace FishFillets.Physics;

/// <summary>
/// One level's physics geometry, as produced by web/tools/export-levels.mjs
/// (run via scripts\export-levels.ps1) into solver/levels/&lt;name&gt;.json.
///
/// This is the whole input to the solver: no pictures, no animation frames, no
/// dialogs, no waves. Everything here comes out of the real, unmodified legacy
/// Lua (models.lua + code.lua) via the browser port's loader, so goal
/// reassignment on the nine world-final levels (docs/008) is already applied.
/// </summary>
public sealed class LevelJson
{
    public string Name { get; set; } = "";

    public int Width { get; set; }

    public int Height { get; set; }

    public List<ModelJson> Models { get; set; } = [];

    /// <summary>
    /// The level this one was derived from, if it was hand-simplified in the
    /// editor. Absent for exported levels.
    ///
    /// This matters for more than provenance. Simplifying a level - freezing an
    /// item into the wall, deleting it, moving it - <b>changes the physics</b>, so
    /// a solution found on the simplified room is not automatically legal in the
    /// real one. A frozen item cannot fall, for instance, so a solution that
    /// relies on it staying put may fall apart when it is a light item again.
    /// Recording the origin lets the solver re-check its answer against the real
    /// level automatically rather than relying on the user to remember.
    /// </summary>
    public string? SourceLevel { get; set; }
}

/// <summary>One model as declared by the level's addModel() call.</summary>
public sealed class ModelJson
{
    /// <summary>"fish_small" | "fish_big" | "item_light" | "item_heavy" | "item_fixed" | "output_*" | "fish_extra-wxyz".</summary>
    public string Kind { get; set; } = "";

    public int X { get; set; }

    public int Y { get; set; }

    /// <summary>ASCII-art occupancy ("X" occupied, "." empty, "\n" next row) - see <see cref="Shape"/>.</summary>
    public string Shape { get; set; } = "";

    /// <summary>"goal_no" | "goal_out" | "goal_escape" | "goal_alive".</summary>
    public string Goal { get; set; } = "";

    /// <summary>Facing, from the level's addFishAnim(LOOK_LEFT/LOOK_RIGHT) - see docs/023.</summary>
    public bool IsLeft { get; set; }
}

/// <summary>
/// Source-generated JSON contract. Native AOT trims the reflection-based
/// serializer away, so every type crossing the JSON boundary must be declared
/// here - the IsAotCompatible analyzer enforces it at build time.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LevelJson))]
[JsonSerializable(typeof(List<string>))]
public partial class LevelJsonContext : JsonSerializerContext
{
}

public static class LevelLoader
{
    public static LevelJson LoadFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, LevelJsonContext.Default.LevelJson)
               ?? throw new InvalidDataException($"empty level file: {path}");
    }

    /// <summary>Level codenames listed in the export's index.json, in export order.</summary>
    public static List<string> LoadIndex(string levelsDir)
    {
        string path = Path.Combine(levelsDir, "index.json");
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, LevelJsonContext.Default.ListString)
               ?? throw new InvalidDataException($"empty index: {path}");
    }
}
