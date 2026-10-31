using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(AVAHexEdit.Tests.TestAppBuilder))]

namespace AVAHexEdit.Tests;

public class TestAppBuilder
{
    // Skia rendering (not headless drawing) so frames can be captured as screenshots.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
