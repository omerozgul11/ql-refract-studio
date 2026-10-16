using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RefractionStudio.App.Services;
using RefractionStudio.App.ViewModels;
using RefractionStudio.App.Views;
using RefractionStudio.Infrastructure;
using RefractionStudio.Visualization;
using SkiaSharp;

namespace RefractionStudio.App;

public partial class App : Application
{
    /// <summary>Environment variable that relocates the application data folder (support and tests).</summary>
    public const string DataDirectoryVariable = "REFRACTIONSTUDIO_DATA_DIR";

    /// <summary>Optional service replacements applied at start-up (used by the UI tests).</summary>
    public static Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>The service provider (composition root).</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        LoadPlotFont();
        Services = Composition.Build(new AppPaths(Environment.GetEnvironmentVariable(DataDirectoryVariable) is { Length: > 0 } dir ? dir : null), ConfigureServices);
        var settings = Services.GetRequiredService<SettingsService>();
        ApplyTheme(settings.Current.Theme);
        settings.Changed += (_, s) => ApplyTheme(s.Theme);

        var crash = Services.GetRequiredService<CrashHandler>();
        crash.Install();
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            crash.Handle(e.Exception);
            e.Handled = Services.GetRequiredService<MainWindowViewModel>().ReportUnexpectedError(e.Exception);
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            Services.GetRequiredService<DialogService>().Owner = window;
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Opened += async (_, _) => await vm.StartupAsync(Program.Arguments);
        }
        base.OnFrameworkInitializationCompleted();
    }

    public void ApplyTheme(ThemePreference theme)
    {
        RequestedThemeVariant = theme switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>Plots use the same typeface as the user interface (Barlow, the QuakeLogic text face, shipped with the application).</summary>
    private static void LoadPlotFont()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://RefractionStudio/Assets/Fonts/Barlow-Regular.ttf"));
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            if (SKTypeface.FromStream(ms) is { } tf) PlotTheme.Typeface = tf;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or UriFormatException)
        {
            // The system sans-serif typeface is used instead.
        }
    }
}
