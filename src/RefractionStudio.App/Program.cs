using Avalonia;

namespace RefractionStudio.App;

internal static class Program
{
    /// <summary>Command-line arguments (a project path when opened from Explorer).</summary>
    public static string[] Arguments { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        Arguments = args;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
