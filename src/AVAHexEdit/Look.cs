using Avalonia.Media;

namespace AVAHexEdit;

/// <summary>The colours and fonts the window is drawn in: dark, with amber for the caret and what is selected.</summary>
internal static class Look
{
    public static readonly IBrush Window = Brush("#12151B");
    public static readonly IBrush Bar = Brush("#191D25");
    public static readonly IBrush Sheet = Brush("#0D1015");
    public static readonly IBrush Border = Brush("#2A303C");
    public static readonly IBrush Heading = Brush("#7E8899");
    public static readonly IBrush Text = Brush("#D8DEE8");
    public static readonly IBrush Faint = Brush("#5C6575");
    public static readonly IBrush Accent = Brush("#FFB627");
    public static readonly IBrush Error = Brush("#FF6B6B");
    public static readonly IBrush Good = Brush("#5EE3A1");

    // The bytes, by what they are.
    public static readonly IBrush Zero = Brush("#4A5260");
    public static readonly IBrush Printable = Brush("#D8DEE8");
    public static readonly IBrush Control = Brush("#6FA8DC");
    public static readonly IBrush High = Brush("#C39BD3");
    public static readonly IBrush Changed = Brush("#FF7B54");

    public static readonly IBrush Selection = Brush("#66FFB627");
    public static readonly IBrush Selected = Brush("#FFFFFF");
    public static readonly IBrush Caret = Brush("#FFB627");
    public static readonly IBrush CaretGhost = Brush("#55FFB627");
    public static readonly IBrush Difference = Brush("#66E0475B");
    public static readonly IBrush Gutter = Brush("#8A93A3");
    public static readonly IBrush RowStripe = Brush("#0AFFFFFF");

    /// <summary>The template's fields, tinted in turn so neighbours differ.</summary>
    public static readonly IBrush[] Fields =
    [
        Brush("#3A2F6FBF"), Brush("#3A3FAF7F"), Brush("#3AB0723A"), Brush("#3A9C4FA8"), Brush("#3A2FA3A8"), Brush("#3AA89A2F"),
    ];

    /// <summary>The field colours at full strength, for the tree's swatches.</summary>
    public static readonly IBrush[] FieldSwatches =
    [
        Brush("#2F6FBF"), Brush("#3FAF7F"), Brush("#B0723A"), Brush("#9C4FA8"), Brush("#2FA3A8"), Brush("#A89A2F"),
    ];

    /// <summary>A fixed-width face, with fallbacks on every platform.</summary>
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, Liberation Mono, monospace");

    /// <summary>
    /// Inter's contextual alternates turn the x in "0x40" into a multiplication sign; offsets read better without.
    /// </summary>
    public static readonly FontFeatureCollection Plain = FontFeatureCollection.Parse("-calt");

    private static SolidColorBrush Brush(string colour) => new(Color.Parse(colour));
}
