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
    private readonly RoomView _view = new();
    private readonly ListBox _models = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _stats = new();
    private readonly ToggleButton _paintWalls = new() { Content = "_Draw walls" };

    private LevelDocument? _document;
    private string _sourceLevel = "";
    private string _levelsDir = "";
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
        _view.MouseMove += OnViewMouseMove;
        _view.MouseLeftButtonUp += (_, _) => _dragging = false;
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
        bar.Items.Add(_paintWalls);
        bar.Items.Add(new TextBlock
        {
            Text = "  click a cell to add wall, right-click to erase   |   arrows move the selected item",
            Foreground = Brushes.Silver,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return bar;
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(2, 0, 2, 0) };
        button.Click += (_, _) => action();
        return button;
    }

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

        if (_paintWalls.IsChecked == true)
        {
            Apply(d => d.PaintWall(x, y, solid: true));
            _dragging = true;
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

        if (e.RightButton == MouseButtonState.Pressed && _paintWalls.IsChecked == true)
        {
            Apply(d => d.PaintWall(x, y, solid: false));
            return;
        }

        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (_paintWalls.IsChecked == true)
        {
            Apply(d => d.PaintWall(x, y, solid: true));
            return;
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

        if (_paintWalls.IsChecked != true && _view.SelectedIndex >= 0)
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
                Select(-1);
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
            _status.Text =
                $"selected [{_view.SelectedIndex}] {LevelDocument.Describe(_document.Models[_view.SelectedIndex])}" +
                "    F = freeze into wall, Delete = remove, arrows = move";
            _status.Foreground = Brushes.Gainsboro;
        }
        else
        {
            _status.Text = "click an item to select it";
            _status.Foreground = Brushes.Gainsboro;
        }
    }

    private void Report(string message)
    {
        _status.Text = message;
        _status.Foreground = Brushes.Gainsboro;
    }
}
