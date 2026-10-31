using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AVAHexEdit.Core;

namespace AVAHexEdit;

/// <summary>
/// Two files side by side, scrolling together, with the bytes that differ marked in both and buttons to step from one
/// difference to the next. Neither can be edited here.
/// </summary>
public sealed class CompareWindow : Window
{
    public HexPane Left { get; } = new();
    public HexPane Right { get; } = new();
    public TextBlock Summary { get; } = new() { Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    public Comparison? Result { get; private set; }

    /// <summary>The comparison under way, for tests to wait on.</summary>
    public Task Comparing { get; }

    private readonly Document _a, _b;
    private int _current = -1;
    private bool _syncing;

    public CompareWindow(Document a, Document b, int bytesPerRow, bool hexOffsets, TextEncodingChoice encoding)
    {
        _a = a;
        _b = b;
        Title = $"{a.Name} and {b.Name} — AVAHexEdit";
        Width = 1500;
        Height = 760;
        Background = Look.Window;
        FontFeatures = Look.Plain;
        foreach (var (pane, doc) in new[] { (Left, a), (Right, b) })
        {
            pane.View.Document = doc;
            pane.View.ReadOnly = true;
            pane.View.BytesPerRow = bytesPerRow;
            pane.View.HexOffsets = hexOffsets;
            pane.View.Encoding = encoding;
        }
        // Scroll and move together.
        Left.View.ScrollChanged += () => Follow(Left.View, Right.View);
        Right.View.ScrollChanged += () => Follow(Right.View, Left.View);
        Left.View.CaretMoved += () => FollowCaret(Left.View, Right.View);
        Right.View.CaretMoved += () => FollowCaret(Right.View, Left.View);

        var previous = new Button { Content = "Previous difference" };
        var next = new Button { Content = "Next difference", Classes = { "accent" } };
        previous.Click += (_, _) => Step(-1);
        next.Click += (_, _) => Step(1);
        var bar = new DockPanel { Margin = new Thickness(10, 8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { previous, next } };
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(Summary);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*"), RowDefinitions = new RowDefinitions("Auto,*") };
        AddColumn(grid, 0, a, Left);
        AddColumn(grid, 2, b, Right);
        var root = new DockPanel();
        var top = new Border { Background = Look.Bar, Child = bar, BorderBrush = Look.Border, BorderThickness = new Thickness(0, 0, 0, 1) };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(grid);
        Content = root;

        Summary.Text = "Comparing…";
        Comparing = Compare();
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.F3) { Step((e.KeyModifiers & Avalonia.Input.KeyModifiers.Shift) != 0 ? -1 : 1); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Escape) Close();
        };
        Closed += (_, _) =>
        {
            _a.Dispose();
            _b.Dispose();
        };
    }

    private static void AddColumn(Grid grid, int column, Document doc, HexPane pane)
    {
        var name = new TextBlock
        {
            Text = $"{doc.Name}  ({ByteFormats.Size(doc.Length)})",
            Foreground = Look.Heading,
            Margin = new Thickness(10, 6),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (doc.Path != null) ToolTip.SetTip(name, doc.Path);
        Grid.SetColumn(name, column);
        Grid.SetColumn(pane, column);
        Grid.SetRow(pane, 1);
        grid.Children.Add(name);
        grid.Children.Add(pane);
    }

    private async Task Compare()
    {
        var result = await Task.Run(() => Comparison.Run(_a, _b));
        Result = result;
        Left.View.Differences = result.Differences;
        Right.View.Differences = result.Differences;
        Summary.Text = result.Summary;
        Summary.Foreground = result.Same ? Look.Good : Look.Text;
        if (result.Differences.Count > 0) Step(1);
    }

    /// <summary>Goes to the next (1) or previous (-1) difference, selecting it in both.</summary>
    public void Step(int direction)
    {
        if (Result is not { Differences.Count: > 0 } r) return;
        _current = Math.Clamp(_current + direction, 0, r.Differences.Count - 1);
        var d = r.Differences[_current];
        foreach (var v in new[] { Left.View, Right.View })
        {
            var length = Math.Max(0, Math.Min(d.Length, v.Length - d.Start));
            v.Select(Math.Min(d.Start, v.Length), length);
        }
        Summary.Text = $"{r.Summary}  Difference {_current + 1:N0} of {r.Differences.Count:N0}: {ByteFormats.Size(d.Length)} at 0x{d.Start:X}.";
    }

    private void Follow(HexView from, HexView to)
    {
        if (_syncing) return;
        _syncing = true;
        to.TopRow = from.TopRow;
        _syncing = false;
    }

    private void FollowCaret(HexView from, HexView to)
    {
        if (_syncing) return;
        _syncing = true;
        if (from.HasSelection) to.Select(Math.Min(from.Selection.Start, to.Length), Math.Max(0, Math.Min(from.Selection.Length, to.Length - from.Selection.Start)));
        else to.MoveTo(from.Caret);
        to.TopRow = from.TopRow;
        _syncing = false;
    }
}
