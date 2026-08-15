using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using FishFillets.Physics;

using Microsoft.Win32;

// FishFillets.Physics also defines an `Action` (the round pipeline's move/fall
// enum), so the delegate has to be named explicitly here.
using Action = System.Action;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace FishFillets.Editor;

/// <summary>
/// The editor window: a room view, a model list, and the handful of operations
/// that simplify a level for the solver.
///
/// Built in code rather than XAML - it is one window, and this keeps the layout
/// and the behaviour in one readable place.
/// </summary>
internal sealed class EditorWindow : Window
{
    /// <summary>
    /// What a click does. One mode at a time, because every one of these is
    /// destructive and a click that does the wrong thing is not undoable.
    /// </summary>
    private enum Tool
    {
        /// <summary>Click selects an item, drag moves it.</summary>
        Select,

        /// <summary>Click paints a wall cell.</summary>
        DrawWall,

        /// <summary>Click clears a wall cell.</summary>
        EraseWall,

        /// <summary>Click removes one cell from an item's shape.</summary>
        EraseTile,

        /// <summary>Click merges the item into the room shape.</summary>
        FreezeItem,
    }

    private readonly RoomView _view = new();
    private readonly ListBox _models = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _stats = new();
    private readonly TextBlock _hint = new();
    private readonly Dictionary<Tool, ToggleButton> _toolButtons = [];

    private LevelDocument? _document;
    private string _sourceLevel = "";
    private string _levelsDir = "";
    private Tool _tool = Tool.Select;
    private bool _dragging;
    private (int X, int Y) _dragCell;
    private bool _suppressListSync;

    public EditorWindow()
    {
        Title = "Fish Fillets level editor";
        Width = 1200;
        Height = 800;
        Background = new SolidColorBrush(Color.FromRgb(32, 36, 44));

        var root = new DockPanel();
        root.Children.Add(BuildToolbar());
        DockPanel.SetDock(root.Children[^1], Dock.Top);

        var statusBar = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8, 4, 8, 6) };
        _status.Foreground = Brushes.Gainsboro;
        _stats.Foreground = new SolidColorBrush(Color.FromRgb(150, 190, 150));
        statusBar.Children.Add(_stats);
        statusBar.Children.Add(_status);
        root.Children.Add(statusBar);
        DockPanel.SetDock(statusBar, Dock.Bottom);

        _models.Width = 300;
        _models.Background = new SolidColorBrush(Color.FromRgb(24, 28, 36));
        _models.Foreground = Brushes.Gainsboro;
        _models.SelectionChanged += OnListSelectionChanged;
        root.Children.Add(_models);
        DockPanel.SetDock(_models, Dock.Right);

        root.Children.Add(_view);

        Content = root;

        _view.MouseLeftButtonDown += OnViewMouseDown;
        _view.MouseRightButtonDown += OnViewRightMouseDown;
        _view.MouseMove += OnViewMouseMove;
        _view.MouseLeftButtonUp += (_, _) => _dragging = false;
        _view.MouseRightButtonUp += (_, _) => _dragging = false;
        _view.Focusable = true;
        PreviewKeyDown += OnKeyDown;

        _levelsDir = FindLevelsDir();
        Report("Open a level to begin. Freezing an item into the wall is the main way to shrink the search.");
    }

    private UIElement BuildToolbar()
    {
        var bar = new ToolBar();

        bar.Items.Add(Button("_Open…", Open));
        bar.Items.Add(Button("Save _as…", SaveAs));
        bar.Items.Add(new Separator());
        bar.Items.Add(Button("_Freeze into wall", () => Apply(d => d.FreezeToWall(_view.SelectedIndex))));
        bar.Items.Add(Button("_Delete item", () => Apply(d => d.Delete(_view.SelectedIndex))));
        bar.Items.Add(new Separator());
        // Alt-D is already "Delete item", so walls take W.
        bar.Items.Add(ToolButton("Draw _walls", Tool.DrawWall));
        bar.Items.Add(ToolButton("_Erase walls", Tool.EraseWall));
        bar.Items.Add(ToolButton("Erase _tile", Tool.EraseTile));
        bar.Items.Add(ToolButton("Freeze on _click", Tool.FreezeItem));
        bar.Items.Add(new Separator());

        _hint.Foreground = Brushes.Silver;
        _hint.VerticalAlignment = VerticalAlignment.Center;
        bar.Items.Add(_hint);

        return bar;
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(2, 0, 2, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>A tool toggle: checking one turns the others off, unchecking returns to selecting.</summary>
    private ToggleButton ToolButton(string text, Tool tool)
    {
        var button = new ToggleButton
        {
            Content = text,
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(2, 0, 2, 0),
        };

        button.Click += (_, _) => SetTool(button.IsChecked == true ? tool : Tool.Select);
        _toolButtons[tool] = button;
        return button;
    }

    private void SetTool(Tool tool)
    {
        _tool = tool;
        _dragging = false;

        foreach ((Tool which, ToggleButton button) in _toolButtons)
        {
            button.IsChecked = which == tool;
        }

        Refresh();
    }

    private static string NameOf(Tool tool) => tool switch
    {
        Tool.DrawWall => "drawing walls",
        Tool.EraseWall => "erasing walls",
        Tool.EraseTile => "erasing item cells",
        Tool.FreezeItem => "freezing items on click",
        _ => "selecting",
    };

    private string HintFor(Tool tool) => tool switch
    {
        Tool.DrawWall => "  click or drag to add wall   |   right-click erases",
        Tool.EraseWall => "  click or drag to clear wall cells",
        Tool.EraseTile => "  click a cell of the selected item to cut it away"
                          + "   |   with nothing selected, click an item to start on it",
        Tool.FreezeItem => "  click any item to merge it into the wall",
        _ => "  click an item to select it   |   arrows or drag move it   |   F freezes, Delete removes",
    };

    // ---------------------------------------------------------------- files --

    private void Open()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Level JSON (*.json)|*.json",
            InitialDirectory = Directory.Exists(_levelsDir) ? _levelsDir : null,
            Title = "Open a level",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _document = LevelDocument.Load(dialog.FileName);
            _levelsDir = Path.GetDirectoryName(dialog.FileName) ?? _levelsDir;

            // Keep pointing at the ORIGINAL level even when a simplified file is
            // reopened and simplified further, so the solver still knows what to
            // re-verify against.
            _sourceLevel = _document.Json.SourceLevel ?? _document.Name;

            _view.Document = _document;
            _view.SelectedIndex = -1;
            RefreshList();
            Refresh();
        }
        catch (Exception ex)
        {
            Report($"could not open: {ex.Message}");
        }
    }

    private void SaveAs()
    {
        if (_document is null)
        {
            return;
        }

        string? error = _document.Validate(out _, out _);
        if (error is not null
            && MessageBox.Show(
                this,
                $"This level does not build:\n\n{error}\n\nSave anyway?",
                "Invalid level",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Level JSON (*.json)|*.json",
            InitialDirectory = Directory.Exists(_levelsDir) ? _levelsDir : null,
            FileName = $"{_sourceLevel}-simple.json",
            Title = "Save the simplified level",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _document.SaveAs(dialog.FileName, _sourceLevel);
            Report(
                $"saved {Path.GetFileName(dialog.FileName)} (from {_sourceLevel}).  " +
                $"Solve it with:  ffsolve solve {_document.Name}");
        }
        catch (Exception ex)
        {
            Report($"could not save: {ex.Message}");
        }
    }

    /// <summary>Finds solver/levels relative to the running executable.</summary>
    private static string FindLevelsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "solver", "levels");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return "";
    }

    // ----------------------------------------------------------- interaction --

    private void OnViewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        _view.Focus();
        (int x, int y) = _view.CellAt(e.GetPosition(_view));
        if (x < 0)
        {
            return;
        }

        switch (_tool)
        {
            case Tool.DrawWall:
                Apply(d => d.PaintWall(x, y, solid: true));
                _dragging = true;
                return;

            case Tool.EraseWall:
                Apply(d => d.PaintWall(x, y, solid: false));
                _dragging = true;
                return;

            case Tool.EraseTile:
                EraseTileAt(x, y);
                _dragging = true;
                return;

            case Tool.FreezeItem:
                int item = _document.ModelAt(x, y);
                if (item >= 0 && item != _document.WallIndex)
                {
                    Apply(d => d.FreezeToWall(item));
                }

                return;
        }

        int index = _document.ModelAt(x, y);
        if (index >= 0 && index != _document.WallIndex)
        {
            Select(index);
            _dragging = true;
            _dragCell = (x, y);
        }
        else
        {
            Select(-1);
        }
    }

    /// <summary>Right-click erases wall while drawing, so the two are one tool.</summary>
    private void OnViewRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_document is null || _tool != Tool.DrawWall)
        {
            return;
        }

        _view.Focus();
        (int x, int y) = _view.CellAt(e.GetPosition(_view));
        if (x < 0)
        {
            return;
        }

        Apply(d => d.PaintWall(x, y, solid: false));
        _dragging = true;
    }

    /// <summary>
    /// Cuts one cell out of an item. The selection scopes it: with an item
    /// selected only that item is cut, so a click that strays onto a neighbour
    /// does nothing instead of quietly damaging it. With nothing selected, the
    /// first click picks up whatever it lands on.
    /// </summary>
    private void EraseTileAt(int x, int y)
    {
        if (_document is null)
        {
            return;
        }

        int index = _view.SelectedIndex;
        if (index < 0 || index >= _document.Models.Count || index == _document.WallIndex)
        {
            index = _document.ModelAt(x, y);
            if (index < 0 || index == _document.WallIndex)
            {
                return;
            }

            Select(index);
        }
        else if (!_document.Covers(_document.Models[index], x, y))
        {
            return;
        }

        Apply(d => d.RemoveTile(index, x, y));
    }

    private void OnViewMouseMove(object sender, MouseEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        (int x, int y) = _view.CellAt(e.GetPosition(_view));
        if (x < 0)
        {
            return;
        }

        if (e.RightButton == MouseButtonState.Pressed && _tool == Tool.DrawWall)
        {
            Apply(d => d.PaintWall(x, y, solid: false));
            return;
        }

        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        switch (_tool)
        {
            case Tool.DrawWall:
                Apply(d => d.PaintWall(x, y, solid: true));
                return;

            case Tool.EraseWall:
                Apply(d => d.PaintWall(x, y, solid: false));
                return;

            case Tool.EraseTile:
                EraseTileAt(x, y);
                return;

            case Tool.FreezeItem:
                return; // freezing is a click, never a sweep
        }

        // Dragging moves by whole cells, tracking how far the pointer has come
        // since the last step rather than snapping the model under the cursor.
        int dx = x - _dragCell.X, dy = y - _dragCell.Y;
        if (dx == 0 && dy == 0)
        {
            return;
        }

        _dragCell = (x, y);
        Apply(d => d.Move(_view.SelectedIndex, dx, dy));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        if (_tool == Tool.Select && _view.SelectedIndex >= 0)
        {
            (int dx, int dy) = e.Key switch
            {
                Key.Left => (-1, 0),
                Key.Right => (1, 0),
                Key.Up => (0, -1),
                Key.Down => (0, 1),
                _ => (0, 0),
            };

            if (dx != 0 || dy != 0)
            {
                Apply(d => d.Move(_view.SelectedIndex, dx, dy));
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.F:
                Apply(d => d.FreezeToWall(_view.SelectedIndex));
                e.Handled = true;
                break;
            case Key.Delete:
                Apply(d => d.Delete(_view.SelectedIndex));
                e.Handled = true;
                break;
            case Key.Escape:
                // One step back at a time: leave the tool first, then the
                // selection, so Escape never does two things at once.
                if (_tool != Tool.Select)
                {
                    SetTool(Tool.Select);
                }
                else
                {
                    Select(-1);
                }

                e.Handled = true;
                break;
        }
    }

    // --------------------------------------------------------------- plumbing --

    private void Apply(Action<LevelDocument> edit)
    {
        if (_document is null)
        {
            return;
        }

        int before = _document.Models.Count;
        edit(_document);
        if (_document.Models.Count != before)
        {
            _view.SelectedIndex = -1;
            RefreshList();
        }

        Refresh();
    }

    private void Select(int index)
    {
        _view.SelectedIndex = index;
        _suppressListSync = true;
        _models.SelectedIndex = index;
        _suppressListSync = false;
        Refresh();
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressListSync)
        {
            _view.SelectedIndex = _models.SelectedIndex;
            Refresh();
        }
    }

    private void RefreshList()
    {
        _models.Items.Clear();
        if (_document is null)
        {
            return;
        }

        for (int i = 0; i < _document.Models.Count; i++)
        {
            string tag = i == _document.WallIndex ? "  (room shape)" : "";
            _models.Items.Add($"{i,3}  {LevelDocument.Describe(_document.Models[i])}{tag}");
        }
    }

    private void Refresh()
    {
        _view.InvalidateVisual();
        if (_document is null)
        {
            return;
        }

        _hint.Text = HintFor(_tool);

        string? error = _document.Validate(out int mobile, out int frozen);

        // The mobile count is the number that matters: it is exactly what goes
        // into the solver's state key, so watching it drop is watching the search
        // space shrink.
        _stats.Text =
            $"{_document.Name}   {_document.Width}x{_document.Height}   " +
            $"{_document.Models.Count} models   mobile {mobile}   frozen by analysis {frozen}";

        if (error is not null)
        {
            _status.Text = $"INVALID: {error}";
            _status.Foreground = new SolidColorBrush(Color.FromRgb(255, 140, 140));
        }
        else if (_view.SelectedIndex >= 0 && _view.SelectedIndex < _document.Models.Count)
        {
            ModelJson selected = _document.Models[_view.SelectedIndex];
            int tiles = LevelDocument.Marks(selected.Shape).Count();
            _status.Text =
                $"selected [{_view.SelectedIndex}] {LevelDocument.Describe(selected)}, {tiles} cells" +
                "    F = freeze into wall, Delete = remove, arrows = move, Esc = deselect";
            _status.Foreground = Brushes.Gainsboro;
        }
        else
        {
            _status.Text = _tool == Tool.Select
                ? "click an item to select it"
                : $"{NameOf(_tool)} - Esc leaves the tool";
            _status.Foreground = Brushes.Gainsboro;
        }
    }

    private void Report(string message)
    {
        _status.Text = message;
        _status.Foreground = Brushes.Gainsboro;
    }
}
