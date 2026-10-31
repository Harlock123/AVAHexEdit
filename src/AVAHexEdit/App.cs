using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using AVAHexEdit.Core;

namespace AVAHexEdit;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // AVAHexEdit [file]: opens the file.
            var file = desktop.Args is { Length: > 0 } args ? args[0] : null;
            desktop.MainWindow = new MainWindow(AppSettings.DefaultPath, Path.Combine(AppSettings.Folder, "templates"), file);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
