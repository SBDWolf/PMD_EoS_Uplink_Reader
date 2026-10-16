using Avalonia;
using UplinkReader.SelfTest;
using UplinkReader.Ui;

namespace UplinkReader;

public static class Program
{
    // `--selftest` runs the offline protocol tests and exits (no GUI);
    // otherwise the app starts the GUI. Debug builds also log every frame
    // and session event to standard I/O (see ConsoleLog).
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest"))
            return SelfTestRunner.Run();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
