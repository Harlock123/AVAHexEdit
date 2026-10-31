using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace AVAHexEdit;

/// <summary>A <see cref="HexView"/> with its scroll bar, kept in step with each other.</summary>
public sealed class HexPane : Grid
{
    public HexView View { get; } = new();
    public ScrollBar Scroll { get; } = new() { Orientation = Orientation.Vertical, SmallChange = 1, AllowAutoHide = false };
    private bool _syncing;

    public HexPane()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        Children.Add(View);
        SetColumn(Scroll, 1);
        Children.Add(Scroll);
        View.ScrollChanged += Sync;
        Scroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_syncing) View.TopRow = (long)Math.Round(Scroll.Value);
        };
    }

    private void Sync()
    {
        _syncing = true;
        Scroll.Maximum = View.MaxTop;
        Scroll.ViewportSize = View.VisibleRows;
        Scroll.LargeChange = Math.Max(1, View.VisibleRows - 1);
        Scroll.Value = View.TopRow;
        _syncing = false;
    }
}
