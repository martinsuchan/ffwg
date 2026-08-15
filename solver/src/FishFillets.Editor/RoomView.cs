using System.Globalization;
using System.Windows;
using System.Windows.Media;

using FishFillets.Physics;

namespace FishFillets.Editor;

/// <summary>
/// Draws the room grid and turns mouse positions into cells.
///
/// Custom-drawn rather than a Canvas of Rectangles: a room is up to 57x40 cells
/// with a thousand-cell wall shape, and one OnRender pass over the models is both
/// faster and far less code than keeping a few thousand elements in sync with an
/// edit.
/// </summary>
internal sealed class RoomView : FrameworkElement
{
    private static readonly Brush Background = new SolidColorBrush(Color.FromRgb(24, 32, 48));
    private static readonly Brush WallBrush = new SolidColorBrush(Color.FromRgb(70, 80, 96));
    private static readonly Brush GridPen = new SolidColorBrush(Color.FromRgb(38, 48, 66));
    private static readonly Pen Grid = new(GridPen, 1);
    private static readonly Pen SelectionPen = new(new SolidColorBrush(Colors.White), 2);
    private static readonly Pen FrozenHint = new(new SolidColorBrush(Color.FromRgb(120, 140, 170)), 1);

    private LevelDocument? _document;

    public LevelDocument? Document
    {
        get => _document;
        set
        {
            _document = value;
            InvalidateVisual();
        }
    }

    public int SelectedIndex { get; set; } = -1;

    /// <summary>Cell size in device pixels, recomputed to fit on every render.</summary>
    public double CellSize { get; private set; } = 12;

    public double OriginX { get; private set; }

    public double OriginY { get; private set; }

    /// <summary>Maps a mouse position to a grid cell, or (-1,-1) outside the room.</summary>
    public (int X, int Y) CellAt(Point point)
    {
        if (_document is null || CellSize <= 0)
        {
            return (-1, -1);
        }

        int x = (int)Math.Floor((point.X - OriginX) / CellSize);
        int y = (int)Math.Floor((point.Y - OriginY) / CellSize);
        return (uint)x < (uint)_document.Width && (uint)y < (uint)_document.Height ? (x, y) : (-1, -1);
    }

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(Background, null, new Rect(RenderSize));

        if (_document is null || _document.Width == 0 || _document.Height == 0)
        {
            DrawHint(context, "File > Open a level from solver/levels");
            return;
        }

        CellSize = Math.Max(2, Math.Min(RenderSize.Width / _document.Width, RenderSize.Height / _document.Height));
        OriginX = (RenderSize.Width - (CellSize * _document.Width)) / 2;
        OriginY = (RenderSize.Height - (CellSize * _document.Height)) / 2;

        DrawModels(context);
        DrawGrid(context);
        DrawSelection(context);
    }

    private void DrawModels(DrawingContext context)
    {
        LevelDocument document = _document!;

        for (int i = 0; i < document.Models.Count; i++)
        {
            ModelJson model = document.Models[i];
            Brush brush = i == document.WallIndex ? WallBrush : BrushFor(model.Kind);

            foreach ((int mx, int my) in LevelDocument.Marks(model.Shape))
            {
                int cx = model.X + mx, cy = model.Y + my;
                if ((uint)cx >= (uint)document.Width || (uint)cy >= (uint)document.Height)
                {
                    continue;
                }

                context.DrawRectangle(brush, null, CellRect(cx, cy));
            }
        }
    }

    private void DrawGrid(DrawingContext context)
    {
        if (CellSize < 8)
        {
            return; // too dense to be anything but noise
        }

        LevelDocument document = _document!;
        double right = OriginX + (CellSize * document.Width);
        double bottom = OriginY + (CellSize * document.Height);

        for (int x = 0; x <= document.Width; x++)
        {
            double px = OriginX + (x * CellSize);
            context.DrawLine(Grid, new Point(px, OriginY), new Point(px, bottom));
        }

        for (int y = 0; y <= document.Height; y++)
        {
            double py = OriginY + (y * CellSize);
            context.DrawLine(Grid, new Point(OriginX, py), new Point(right, py));
        }
    }

    private void DrawSelection(DrawingContext context)
    {
        LevelDocument document = _document!;
        if (SelectedIndex < 0 || SelectedIndex >= document.Models.Count)
        {
            return;
        }

        ModelJson model = document.Models[SelectedIndex];
        foreach ((int mx, int my) in LevelDocument.Marks(model.Shape))
        {
            context.DrawRectangle(null, SelectionPen, CellRect(model.X + mx, model.Y + my));
        }
    }

    private static void DrawHint(DrawingContext context, string text)
    {
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 16, Brushes.Gray, 1.0);
        context.DrawText(formatted, new Point(24, 24));
    }

    private Rect CellRect(int x, int y) =>
        new(OriginX + (x * CellSize), OriginY + (y * CellSize), CellSize, CellSize);

    /// <summary>
    /// Colour by what the model does to the search, not by what it looks like in
    /// the game: fish first, then how heavy it is, since weight is what decides
    /// whether it can be pushed at all.
    /// </summary>
    private static Brush BrushFor(string kind) => kind switch
    {
        "fish_small" => new SolidColorBrush(Color.FromRgb(255, 176, 76)),
        "fish_big" => new SolidColorBrush(Color.FromRgb(120, 200, 255)),
        "item_heavy" => new SolidColorBrush(Color.FromRgb(150, 96, 72)),
        "item_light" => new SolidColorBrush(Color.FromRgb(206, 168, 110)),
        "item_fixed" => new SolidColorBrush(Color.FromRgb(70, 80, 96)),
        _ when kind.StartsWith("fish_", StringComparison.Ordinal) => new SolidColorBrush(Color.FromRgb(180, 255, 180)),
        _ => new SolidColorBrush(Color.FromRgb(180, 120, 200)),
    };
}
