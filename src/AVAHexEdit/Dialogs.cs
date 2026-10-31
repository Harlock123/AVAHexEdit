using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AVAHexEdit;

/// <summary>A small question with buttons, such as whether to save changes before closing.</summary>
internal sealed class AskDialog : Window
{
    private AskDialog(string title, string message, string[] buttons)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Look.Window;
        FontFeatures = Look.Plain;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i], MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == 0) b.Classes.Add("accent");
            b.Click += (_, _) => Close(index);
            row.Children.Add(b);
        }
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, FontSize = 14 },
                row,
            },
        };
    }

    /// <summary>Asks, and returns the index of the button pressed, or the last one (cancel) if the window is closed.</summary>
    public static async Task<int> Show(Window owner, string title, string message, params string[] buttons)
    {
        var result = await new AskDialog(title, message, buttons).ShowDialog<int?>(owner);
        return result ?? buttons.Length - 1;
    }
}
