using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using FishFillets.Physics;
using FishFillets.Search;

namespace FishFillets.Editor;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(LevelJson))]
internal partial class EditorJsonContext : JsonSerializerContext
{
}

/// <summary>
/// A level open for editing: the raw JSON the solver reads, plus the operations
/// that simplify it.
///
/// <para>The point of the editor is to shrink the search space by hand where the
/// automatic analysis cannot prove a model immobile (see solver/docs/004 on why a
/// complete reducer is not feasible). A person can see at a glance that a crate is
/// scenery; proving it is the hard part.</para>
///
/// <para><b>Every operation here changes the physics</b>, so a solution found on
/// the edited room is not automatically legal in the original - freezing an item
/// stops it falling, deleting it removes something a fish might have stood on.
/// That is why <see cref="LevelJson.SourceLevel"/> is stamped on save and the
/// solver re-checks its answers against the real level.</para>
/// </summary>
internal sealed class LevelDocument
{
    private LevelDocument(LevelJson json, string name)
    {
        Json = json;
        Name = name;
        NormaliseWallAnchor();
    }

    public LevelJson Json { get; }

    public string Name { get; private set; }

    public int Width => Json.Width;

    public int Height => Json.Height;

    public IReadOnlyList<ModelJson> Models => Json.Models;

    /// <summary>
    /// The room's own shape - the model everything frozen gets merged into. Levels
    /// declare it first (<c>room = addModel("item_fixed", 0, 0, ...)</c>).
    /// </summary>
    public int WallIndex { get; private set; } = -1;

    public static LevelDocument Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        LevelJson json = JsonSerializer.Deserialize(stream, EditorJsonContext.Default.LevelJson)
                         ?? throw new InvalidDataException($"empty level file: {path}");
        return new LevelDocument(json, Path.GetFileNameWithoutExtension(path));
    }

    public void SaveAs(string path, string sourceLevel)
    {
        Json.Name = Path.GetFileNameWithoutExtension(path);
        Json.SourceLevel = sourceLevel;
        Name = Json.Name;

        using FileStream stream = File.Create(path);
        JsonSerializer.Serialize(stream, Json, EditorJsonContext.Default.LevelJson);
    }

    // ------------------------------------------------------------ operations --

    /// <summary>Which model covers this cell, topmost first (the room shape last).</summary>
    public int ModelAt(int x, int y)
    {
        for (int i = Json.Models.Count - 1; i >= 0; i--)
        {
            if (i != WallIndex && Covers(Json.Models[i], x, y))
            {
                return i;
            }
        }

        return WallIndex >= 0 && Covers(Json.Models[WallIndex], x, y) ? WallIndex : -1;
    }

    public bool Covers(ModelJson model, int x, int y)
    {
        foreach ((int mx, int my) in Marks(model.Shape))
        {
            if (model.X + mx == x && model.Y + my == y)
            {
                return true;
            }
        }

        return false;
    }

    public void Move(int index, int dx, int dy)
    {
        if (index < 0 || index == WallIndex)
        {
            return;
        }

        Json.Models[index].X += dx;
        Json.Models[index].Y += dy;
    }

    /// <summary>
    /// Merges a model's cells into the room's shape and removes it - the main
    /// simplification. The cells stay solid, so the room looks the same, but the
    /// model is gone from the state space entirely.
    /// </summary>
    public void FreezeToWall(int index)
    {
        if (index < 0 || index == WallIndex || WallIndex < 0)
        {
            return;
        }

        ModelJson model = Json.Models[index];
        HashSet<(int, int)> wall = [.. Marks(Json.Models[WallIndex].Shape)];
        foreach ((int mx, int my) in Marks(model.Shape))
        {
            wall.Add((model.X + mx, model.Y + my));
        }

        Json.Models[WallIndex].Shape = Serialise(wall);
        Json.Models.RemoveAt(index);
        if (index < WallIndex)
        {
            WallIndex--;
        }
    }

    public void Delete(int index)
    {
        if (index < 0 || index == WallIndex)
        {
            return;
        }

        Json.Models.RemoveAt(index);
        if (index < WallIndex)
        {
            WallIndex--;
        }
    }

    /// <summary>
    /// Removes a single cell from a model's shape, for trimming an item down
    /// rather than losing it altogether - a four-cell crate cut to one cell still
    /// falls and still blocks, but occupies far less of the room.
    ///
    /// <para>The anchor deliberately stays where it is. Marks are relative to it,
    /// so leaving it alone keeps every remaining cell at the same absolute
    /// position; <see cref="Serialise"/> pads the gap with '.' instead.</para>
    /// </summary>
    /// <returns>True if that was the last cell and the model was removed.</returns>
    public bool RemoveTile(int index, int x, int y)
    {
        if (index < 0 || index == WallIndex || index >= Json.Models.Count)
        {
            return false;
        }

        ModelJson model = Json.Models[index];
        HashSet<(int, int)> marks = [.. Marks(model.Shape)];
        if (!marks.Remove((x - model.X, y - model.Y)))
        {
            return false;
        }

        if (marks.Count == 0)
        {
            Delete(index);
            return true;
        }

        model.Shape = Serialise(marks);
        return false;
    }

    /// <summary>Adds or removes a single wall cell, for drawing barriers by hand.</summary>
    public void PaintWall(int x, int y, bool solid)
    {
        if (WallIndex < 0 || (uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return;
        }

        HashSet<(int, int)> wall = [.. Marks(Json.Models[WallIndex].Shape)];
        if (solid)
        {
            wall.Add((x, y));
        }
        else
        {
            wall.Remove((x, y));
        }

        Json.Models[WallIndex].Shape = Serialise(wall);
    }

    // ------------------------------------------------------------ inspection --

    /// <summary>
    /// Builds the level through the real engine, which is the only trustworthy
    /// check: overlapping models throw at construction, exactly as they would when
    /// the solver loads the file.
    /// </summary>
    public string? Validate(out int mobileModels, out int frozenModels)
    {
        mobileModels = 0;
        frozenModels = 0;
        try
        {
            Level level = Level.Build(Json);
            _ = new Room(level);

            LevelReduction reduction = LevelReduction.Compute(level);
            mobileModels = reduction.MobileModels.Length;
            frozenModels = reduction.FrozenModels.Length;
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static string Describe(ModelJson model) =>
        $"{model.Kind} at ({model.X},{model.Y})" +
        (model.Goal is "goal_no" or "" ? "" : $"  [{model.Goal}]");

    // ------------------------------------------------------------ shape bits --

    public static IEnumerable<(int X, int Y)> Marks(string shape)
    {
        int x = 0, y = 0;
        foreach (char c in shape)
        {
            switch (c)
            {
                case '\n':
                    y++;
                    x = 0;
                    break;
                case 'X':
                    yield return (x, y);
                    x++;
                    break;
                default:
                    x++;
                    break;
            }
        }
    }

    private static string Serialise(HashSet<(int X, int Y)> marks)
    {
        if (marks.Count == 0)
        {
            return "\n";
        }

        int maxX = marks.Max(m => m.X);
        int maxY = marks.Max(m => m.Y);

        var text = new StringBuilder();
        for (int y = 0; y <= maxY; y++)
        {
            for (int x = 0; x <= maxX; x++)
            {
                text.Append(marks.Contains((x, y)) ? 'X' : '.');
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Pins the room shape's anchor at (0,0) so painted cells map straight to
    /// marks. Levels already declare it there, but a shifted anchor would make
    /// every edit off-by-something, and negative marks cannot be written at all.
    /// </summary>
    private void NormaliseWallAnchor()
    {
        WallIndex = Json.Models.FindIndex(m => m.Kind == "item_fixed");
        if (WallIndex < 0)
        {
            return;
        }

        ModelJson wall = Json.Models[WallIndex];
        if (wall.X == 0 && wall.Y == 0)
        {
            return;
        }

        HashSet<(int, int)> marks = [];
        foreach ((int mx, int my) in Marks(wall.Shape))
        {
            marks.Add((wall.X + mx, wall.Y + my));
        }

        wall.X = 0;
        wall.Y = 0;
        wall.Shape = Serialise(marks);
    }
}
