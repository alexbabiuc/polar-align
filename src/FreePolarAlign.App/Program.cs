using Avalonia;

namespace FreePolarAlign.App;

internal static class Program
{
    // Avalonia's own convention: keep this method free of anything that
    // touches Avalonia types before BuildAvaloniaApp runs, so the designer and
    // headless test hosts (see FreePolarAlign.Tests.App) can call
    // BuildAvaloniaApp() without side effects.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
