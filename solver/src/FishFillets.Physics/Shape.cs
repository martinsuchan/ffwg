namespace FishFillets.Physics;

/// <summary>
/// A model's occupied cells, relative to its anchor. Port of
/// legacy/src/level/Shape.cpp (web/src/game/Shape.ts): parses the ASCII-art
/// strings addModel() takes in models.lua - "X" occupied, "." empty, "\n" next
/// row.
///
/// NOTE: <see cref="W"/>/<see cref="H"/> are the bounding box of the "X" marks
/// only (the largest column/row that actually holds an "X"), not the raw
/// string's row length - trailing "."-padding doesn't count. Matches the
/// original exactly (Shape::Shape only advances max_x/max_y on an 'X').
///
/// Marks are stored once per model at level build and never re-parsed, so this
/// is the only place shape strings are touched.
/// </summary>
public sealed class Shape
{
    /// <summary>Cell offsets from the anchor, packed as (dx, dy) pairs.</summary>
    public readonly sbyte[] MarkX;

    public readonly sbyte[] MarkY;

    public readonly int W;

    public readonly int H;

    public Shape(string shape)
    {
        int count = 0;
        foreach (char c in shape)
        {
            if (c == 'X') count++;
        }

        MarkX = new sbyte[count];
        MarkY = new sbyte[count];

        int x = 0, y = 0, n = 0, maxX = -1, maxY = -1;
        foreach (char c in shape)
        {
            switch (c)
            {
                case '\n':
                    y++;
                    x = 0;
                    break;
                case 'X':
                    if (x > sbyte.MaxValue || y > sbyte.MaxValue)
                    {
                        throw new InvalidDataException($"shape mark out of sbyte range at ({x},{y})");
                    }

                    MarkX[n] = (sbyte)x;
                    MarkY[n] = (sbyte)y;
                    n++;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                    x++;
                    break;
                case '.':
                    x++;
                    break;
                default:
                    throw new InvalidDataException($"bad shape char '{c}'");
            }
        }

        W = maxX + 1;
        H = maxY + 1;

        // The marks with no mark of their own directly below: the only cells
        // whose downward neighbour can be something other than this model.
        var marks = new HashSet<(int, int)>();
        for (int m = 0; m < count; m++)
        {
            marks.Add((MarkX[m], MarkY[m]));
        }

        var bottomX = new List<sbyte>();
        var bottomY = new List<sbyte>();
        for (int m = 0; m < count; m++)
        {
            if (!marks.Contains((MarkX[m], MarkY[m] + 1)))
            {
                bottomX.Add(MarkX[m]);
                bottomY.Add(MarkY[m]);
            }
        }

        BottomX = [.. bottomX];
        BottomY = [.. bottomY];
    }

    /// <summary>
    /// The marks with no mark directly below them (the shape's lower edge, per
    /// column). While every mark is inside the room, the cell below any other
    /// mark is this model's own, so a support test needs only these.
    /// </summary>
    public readonly sbyte[] BottomX;

    public readonly sbyte[] BottomY;

    public int MarkCount => MarkX.Length;
}
