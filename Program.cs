using Avalonia;
using UplinkReader.Ui;

namespace UplinkReader;

public static class Program
{
    // The app takes no launch parameters: it always starts the GUI.
    // Debug builds also log every frame and session event to standard
    // I/O (see ConsoleLog); Release builds are a plain GUI app.
    [STAThread]
    public static int Main() => BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
