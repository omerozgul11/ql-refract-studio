using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using RefractionStudio.App.ViewModels;
using RefractionStudio.App.Views;

namespace RefractionStudio.App.Tests;

/// <summary>Collects binding and layout problems reported by Avalonia.</summary>
public sealed class CollectingLogSink : ILogSink
{
    public static readonly CollectingLogSink Instance = new();

    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages) return [.. _messages];
        }
    }

    /// <summary>
    /// Messages other than null propagation along a binding path ("Value is null"), which Avalonia reports
    /// while templates are created before they are attached, or when nothing is selected.
    /// </summary>
    public IReadOnlyList<string> Problems => [.. Messages.Where(m => !m.EndsWith("Value is null.", StringComparison.Ordinal))];

    public void Clear()
    {
        lock (_messages) _messages.Clear();
    }

    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area is LogArea.Binding or LogArea.Property or LogArea.Layout or LogArea.Visual;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Add(level, area, source, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) => Add(level, area, source, messageTemplate, propertyValues);

    private void Add(LogEventLevel level, string area, object? source, string template, object?[] values)
    {
        var text = template;
        foreach (var v in values)
        {
            var start = text.IndexOf('{');
            var end = start < 0 ? -1 : text.IndexOf('}', start);
            if (end < 0) break;
            text = text[..start] + v + text[(end + 1)..];
        }
        lock (_messages) _messages.Add($"[{level} {area}] {source?.GetType().Name}: {text}");
    }
}

/// <summary>Helpers to drive the main window in the headless platform.</summary>
public static class UiHarness
{
    /// <summary>Messages shown to the user during the test (instead of message windows).</summary>
    public static List<RefractionStudio.App.Services.DialogMessage> Messages { get; } = [];

    /// <summary>Chooses the button for a message; by default the last one (cancel / OK).</summary>
    public static Func<RefractionStudio.App.Services.DialogMessage, int>? Answer { get; set; }

    public static string ScreenshotDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "screenshots");

    public static (MainWindow Window, MainWindowViewModel Vm) CreateWindow(double width = 1440, double height = 900)
    {
        Logger.Sink = CollectingLogSink.Instance;
        var vm = RefractionStudio.App.App.Services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        var dialogs = RefractionStudio.App.App.Services.GetRequiredService<RefractionStudio.App.Services.DialogService>();
        dialogs.Owner = window;
        dialogs.MessageHandler = m =>
        {
            lock (Messages) Messages.Add(m);
            return Task.FromResult(Answer?.Invoke(m) ?? m.Buttons.Count - 1);
        };
        window.Show();
        Pump();
        return (window, vm);
    }

    /// <summary>Runs queued dispatcher work and a render tick.</summary>
    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds = 120, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting for " + (what ?? "a condition") + ". Messages: " + string.Join(" | ", Messages.Select(m => $"{m.Title}: {m.Message} {m.Details}")));
            Pump();
            await Task.Delay(25);
        }
        Pump();
    }

    /// <summary>Renders the window and saves the frame as PNG; returns the path.</summary>
    public static string Screenshot(Window window, string name)
    {
        Directory.CreateDirectory(ScreenshotDirectory);
        Pump();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered.");
        var path = Path.Combine(ScreenshotDirectory, name + ".png");
        frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        return path;
    }

    public static IEnumerable<T> Find<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    /// <summary>Opens a private copy of the sample project and waits for the analysis.</summary>
    public static async Task OpenSampleAsync(MainWindowViewModel vm)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Samples", "Refraction-Studio-Sample.rfsx");
        var copy = Path.Combine(TestApp.DataDirectory, $"sample-{Guid.NewGuid():N}.rfsx");
        File.Copy(source, copy);
        Messages.Clear();
        await vm.OpenPathAsync(copy);
        Assert.True(Messages.Count == 0, "Unexpected messages: " + string.Join(" | ", Messages.Select(m => $"{m.Title}: {m.Message} {m.Details}")));
        var analysis = RefractionStudio.App.App.Services.GetRequiredService<RefractionStudio.App.Services.AnalysisCoordinator>();
        await WaitUntilAsync(() => vm.HasProject && analysis.Latest is not null && !analysis.IsComputing, what: "the analysis of the sample project");
    }
}
