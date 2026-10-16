using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RefractionStudio.Analysis;
using RefractionStudio.App.ViewModels;
using RefractionStudio.Core;
using RefractionStudio.Core.Services;
using RefractionStudio.Core.Session;
using RefractionStudio.Domain;
using RefractionStudio.Domain.Units;
using RefractionStudio.Infrastructure;
using RefractionStudio.Licensing;
using RefractionStudio.Persistence;

namespace RefractionStudio.App.Services;

/// <summary>Composition root.</summary>
public static class Composition
{
    /// <param name="paths">Application data locations.</param>
    /// <param name="overrides">Optional replacements of services (used by the UI tests).</param>
    public static IServiceProvider Build(AppPaths paths, Action<IServiceCollection>? overrides = null)
    {
        var services = new ServiceCollection();
        var settingsService = new SettingsService(new SettingsStore(paths.Settings));
        var loggerFactory = Logging.Create(paths, settingsService.Current.VerboseLogging);
        services.AddSingleton(paths);
        services.AddSingleton(settingsService);
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(_ => new AppCatalog(paths.Catalog));
        services.AddSingleton(sp => new ProjectWorkspace(
            new WorkspaceOptions(AppInfo.Version, settingsService.Current.UserName ?? Environment.UserName, paths.Recovery),
            logger: sp.GetRequiredService<ILogger<ProjectWorkspace>>()));
        services.AddSingleton<AnalysisCoordinator>();
        services.AddSingleton<SelectionState>();
        services.AddSingleton<BusyService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton(sp => LicenseService.Create(paths, settingsService, sp.GetRequiredService<ILogger<LicenseManager>>()));
        services.AddSingleton(sp =>
        {
            var http = new HttpClient { BaseAddress = new Uri(settingsService.Current.LicenseServer), Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"RefractionStudio/{AppInfo.Version}");
            return new UpdateService(http, paths, AppInfo.ReleasePublicKeys, sp.GetRequiredService<ILogger<UpdateService>>());
        });
        services.AddSingleton(sp => new CrashHandler(paths, () => sp.GetRequiredService<ProjectWorkspace>().Checkpoint(), sp.GetRequiredService<ILogger<CrashHandler>>()));
        services.AddSingleton<AutosaveService>();
        services.AddSingleton<MainWindowViewModel>();
        overrides?.Invoke(services);
        return services.BuildServiceProvider();
    }
}

/// <summary>User settings with change notification.</summary>
public sealed class SettingsService(SettingsStore store)
{
    public UserSettings Current { get; private set; } = store.Load();

    public event EventHandler<UserSettings>? Changed;

    public void Update(Func<UserSettings, UserSettings> update)
    {
        Current = update(Current);
        try
        {
            store.Save(Current);
        }
        catch (IOException)
        {
            // Settings are a convenience; a failed save is retried on the next change.
        }
        Changed?.Invoke(this, Current);
    }

    public UnitFormatter Units => new(Current.Units, Current.ModulusUnit);
}

/// <summary>What is selected across views (line, shot, trace).</summary>
public sealed partial class SelectionState : ObservableObject
{
    [ObservableProperty] public partial LineId? Line { get; set; }
    [ObservableProperty] public partial GatherId? Gather { get; set; }
    [ObservableProperty] public partial int? Channel { get; set; }

    public PickKey? Pick => Gather is { } g && Channel is { } c ? new PickKey(g, c) : null;

    public void Select(LineId line, GatherId? gather = null, int? channel = null)
    {
        Line = line;
        Gather = gather;
        Channel = channel;
    }
}

/// <summary>
/// Recomputes the analysis on a background thread whenever the project changes (debounced, cancelling
/// the previous run) and publishes the result on the UI thread.
/// </summary>
public sealed partial class AnalysisCoordinator : ObservableObject
{
    private readonly ProjectWorkspace _workspace;
    private readonly ILogger<AnalysisCoordinator> _log;
    private CancellationTokenSource? _cts;
    private ProjectSession? _session;

    public AnalysisCoordinator(ProjectWorkspace workspace, ILogger<AnalysisCoordinator> log)
    {
        _workspace = workspace;
        _log = log;
        workspace.ProjectChanged += (_, _) => Attach();
    }

    [ObservableProperty] public partial AnalysisResult? Latest { get; private set; }
    [ObservableProperty] public partial ImmutableArray<StepStatus> Steps { get; private set; } = [];
    [ObservableProperty] public partial bool IsComputing { get; private set; }

    /// <summary>Raised on the UI thread after each completed analysis.</summary>
    public event EventHandler<AnalysisResult>? Updated;

    /// <summary>Raised on the UI thread for every project state change (before analysis).</summary>
    public event EventHandler<StateChangedEventArgs>? StateChanged;

    private void Attach()
    {
        if (_session is not null) _session.StateChanged -= OnStateChanged;
        _session = _workspace.Session;
        Latest = null;
        if (_session is not null)
        {
            _session.StateChanged += OnStateChanged;
            Schedule(0);
        }
        else
        {
            Steps = [];
        }
    }

    private void OnStateChanged(object? sender, StateChangedEventArgs e)
    {
        // The user interface is only updated on its own thread.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnStateChanged(sender, e));
            return;
        }
        StateChanged?.Invoke(this, e);
        Schedule(120);
    }

    /// <summary>Requests a new analysis now.</summary>
    public void Refresh() => Schedule(0);

    private void Schedule(int delayMs)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var session = _session;
        if (session is null) return;
        IsComputing = true;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0) await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
                var state = session.State;
                var result = _workspace.Analyzer.Analyze(state, _workspace.Results, cts.Token);
                var steps = WorkflowStatus.Compute(state, result);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested || !ReferenceEquals(_session, session)) return;
                    Latest = result;
                    Steps = steps;
                    IsComputing = false;
                    Updated?.Invoke(this, result);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or Persistence.ProjectFileException or Formats.SeismicFormatException)
            {
                _log.LogError(ex, "Analysis failed");
                await Dispatcher.UIThread.InvokeAsync(() => IsComputing = false);
            }
        });
    }
}

/// <summary>Runs long operations with progress and cancellation, shown in the status bar.</summary>
public sealed partial class BusyService : ObservableObject
{
    private CancellationTokenSource? _cts;

    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string Message { get; private set; } = "";
    [ObservableProperty] public partial double? Progress { get; private set; }
    [ObservableProperty] public partial bool CanCancel { get; private set; }

    /// <summary>True while no fraction is known (an indeterminate bar is shown).</summary>
    public bool IsIndeterminate => Progress is null;

    /// <summary>Progress in percent for the status bar.</summary>
    public double Percent => (Progress ?? 0) * 100;

    partial void OnProgressChanged(double? value)
    {
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(Percent));
    }

    public async Task<T?> RunAsync<T>(string message, Func<IProgress<(double Fraction, string Stage)>, CancellationToken, Task<T>> work, bool cancellable = true)
    {
        if (IsBusy) return default;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        CanCancel = cancellable;
        Message = message;
        Progress = null;
        var progress = new Progress<(double Fraction, string Stage)>(p =>
        {
            Progress = double.IsNaN(p.Fraction) ? null : Math.Clamp(p.Fraction, 0, 1);
            if (!string.IsNullOrEmpty(p.Stage)) Message = p.Stage;
        });
        try
        {
            return await work(progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return default;
        }
        finally
        {
            IsBusy = false;
            CanCancel = false;
            Progress = null;
            Message = "";
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    public void Cancel() => _cts?.Cancel();
}

/// <summary>Writes recovery checkpoints periodically while there are unsaved changes.</summary>
public sealed class AutosaveService
{
    private readonly DispatcherTimer _timer;

    public AutosaveService(ProjectWorkspace workspace, SettingsService settings, ILogger<AutosaveService> log)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Math.Max(1, settings.Current.AutosaveMinutes)) };
        _timer.Tick += (_, _) =>
        {
            try
            {
                if (workspace.Checkpoint()) log.LogDebug("Recovery checkpoint written");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Recovery checkpoint failed");
            }
        };
        settings.Changed += (_, s) => _timer.Interval = TimeSpan.FromMinutes(Math.Max(1, s.AutosaveMinutes));
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();
}

/// <summary>Licence status for the user interface.</summary>
public sealed partial class LicenseService : ObservableObject
{
    public LicenseService(LicenseManager manager)
    {
        Manager = manager;
        Status = Manager.Evaluate();
        Manager.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() => Status = s);
    }

    /// <summary>Creates the licence service for this computer with the production licence server and keys.</summary>
    public static LicenseService Create(AppPaths paths, SettingsService settings, ILogger<LicenseManager> log)
    {
        DeviceInfo device;
        try
        {
            device = new DeviceFingerprint(AppInfo.FingerprintSalt).Compute(settings.Current.SendMachineName);
        }
        catch (InvalidOperationException)
        {
            // No stable identifier could be read: use a per-installation identifier (licensing then requires online checks).
            var id = Path.Combine(paths.Root, "installation-id");
            if (!File.Exists(id)) File.WriteAllText(id, Guid.NewGuid().ToString("N"));
            device = new DeviceFingerprint(AppInfo.FingerprintSalt, new FixedIdentifierSource(new MachineIdentifier("installation", File.ReadAllText(id)))).Compute(settings.Current.SendMachineName);
        }
        var http = new HttpClient { BaseAddress = new Uri(settings.Current.LicenseServer), Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RefractionStudio/{AppInfo.Version}");
        return new LicenseService(new LicenseManager(new LicensingOptions(AppInfo.ProductCode, AppInfo.ProductName, AppInfo.Version, AppInfo.LicensePublicKeys),
            new HttpLicenseApi(http), new FileLicenseStore(paths.License, device.Fingerprint), device, logger: log));
    }

    public LicenseManager Manager { get; }

    [ObservableProperty] public partial LicenseStatus Status { get; private set; }

    public void Reevaluate() => Status = Manager.Evaluate();
}
