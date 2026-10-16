using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RefractionStudio.App.Services;
using RefractionStudio.Core;
using RefractionStudio.Core.Services;
using RefractionStudio.Infrastructure;
using RefractionStudio.Licensing;
using RefractionStudio.Persistence;

namespace RefractionStudio.App.ViewModels;

/// <summary>A navigation entry with its workflow status.</summary>
public sealed partial class NavItemViewModel(PageViewModel page, string iconKey) : ViewModelBase
{
    public PageViewModel Page { get; } = page;
    public string Title => Page.Title;
    public string IconKey { get; } = iconKey;

    [ObservableProperty] public partial StepState State { get; set; } = StepState.NotStarted;
    [ObservableProperty] public partial string Hint { get; set; } = "";

    /// <summary>Status glyph: ✓ complete, ● in progress, ! attention, ○ not started, — not applicable.</summary>
    public string StateGlyph => State switch
    {
        StepState.Complete => "✓",
        StepState.InProgress => "●",
        StepState.NeedsAttention => "!",
        StepState.NotApplicable => "—",
        _ => "○",
    };

    public string StateText => State switch
    {
        StepState.Complete => "Complete",
        StepState.InProgress => "In progress",
        StepState.NeedsAttention => "Needs attention",
        StepState.NotApplicable => "Not applicable",
        _ => "Not started",
    };

    partial void OnStateChanged(StepState value)
    {
        OnPropertyChanged(nameof(StateGlyph));
        OnPropertyChanged(nameof(StateText));
    }
}

/// <summary>An entry of the command palette.</summary>
public sealed record PaletteCommand(string Title, string Category, string? Shortcut, Func<Task> Execute);

/// <summary>Main window: toolbar, navigation, current page, inspector, status bar and the start page.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly StudioContext _ctx;
    private readonly UpdateService _updates;
    private readonly CrashHandler _crash;
    private readonly AutosaveService _autosave;
    private readonly ILogger<MainWindowViewModel> _log;
    private readonly List<PageViewModel> _pages;

    public MainWindowViewModel(ProjectWorkspace workspace, AnalysisCoordinator analysis, SelectionState selection, DialogService dialogs, BusyService busy,
        SettingsService settings, LicenseService license, AppCatalog catalog, AppPaths paths, UpdateService updates, CrashHandler crash, AutosaveService autosave,
        ILogger<MainWindowViewModel> log)
    {
        _ctx = new StudioContext(workspace, analysis, selection, dialogs, busy, settings, license, catalog, paths) { Navigate = NavigateTo };
        _updates = updates;
        _crash = crash;
        _autosave = autosave;
        _log = log;
        Busy = busy;
        License = license;
        Start = new StartPageViewModel(_ctx, this, updates);
        Project = new ProjectPageViewModel(_ctx);
        Import = new ImportPageViewModel(_ctx);
        _pages =
        [
            Project, Import, new GeometryPageViewModel(_ctx), new WaveformsPageViewModel(_ctx), new ProcessingPageViewModel(_ctx),
            new PicksPageViewModel(_ctx, Domain.WaveType.P), new PicksPageViewModel(_ctx, Domain.WaveType.SH), new QcPageViewModel(_ctx),
            new VelocityModelPageViewModel(_ctx), new EngineeringPageViewModel(_ctx), new ResultsPageViewModel(_ctx), new ReportPageViewModel(_ctx),
        ];
        var icons = new Dictionary<WorkflowStep, string>
        {
            [WorkflowStep.Project] = "Icon.Project", [WorkflowStep.Import] = "Icon.Import", [WorkflowStep.Geometry] = "Icon.Geometry",
            [WorkflowStep.Waveforms] = "Icon.Waveform", [WorkflowStep.Processing] = "Icon.Filter", [WorkflowStep.PPicks] = "Icon.Pick",
            [WorkflowStep.SPicks] = "Icon.Pick", [WorkflowStep.QualityControl] = "Icon.Qc", [WorkflowStep.VelocityModel] = "Icon.Section",
            [WorkflowStep.Engineering] = "Icon.Engineering", [WorkflowStep.Results] = "Icon.Results", [WorkflowStep.Report] = "Icon.Report",
        };
        foreach (var p in _pages) NavItems.Add(new NavItemViewModel(p, icons.GetValueOrDefault(p.Step, "Icon.Info")));
        Settings = new SettingsViewModel(_ctx, updates);
        Help = new HelpViewModel();
        LicensePanel = new LicenseViewModel(_ctx);

        workspace.ProjectChanged += (_, _) =>
        {
            HasProject = workspace.Session is not null;
            foreach (var p in _pages) p.OnProjectChanged();
            RefreshTitle();
            if (HasProject && SelectedNav is null) SelectedNav = NavItems[0];
            if (!HasProject) _ = Start.RefreshAsync();
        };
        analysis.StateChanged += (_, e) =>
        {
            foreach (var p in _pages) p.OnStateChanged(e);
            RefreshTitle();
            RefreshCommands();
        };
        analysis.Updated += (_, result) =>
        {
            foreach (var p in _pages) p.OnAnalysisUpdated(result);
            foreach (var n in NavItems)
            {
                var s = analysis.Steps.FirstOrDefault(x => x.Step == n.Page.Step);
                if (s is null) continue;
                n.State = s.State;
                n.Hint = s.Hint;
            }
        };
        license.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LicenseService.Status)) RefreshLicense();
        };
        settings.Changed += (_, _) =>
        {
            foreach (var p in _pages) p.OnSettingsChanged();
            OnPropertyChanged(nameof(IsExpert));
        };
        RefreshLicense();
    }

    public BusyService Busy { get; }
    public LicenseService License { get; }
    public StartPageViewModel Start { get; }
    public ProjectPageViewModel Project { get; }
    public ImportPageViewModel Import { get; }
    public SettingsViewModel Settings { get; }
    public HelpViewModel Help { get; }
    public LicenseViewModel LicensePanel { get; }
    public ObservableCollection<NavItemViewModel> NavItems { get; } = [];
    public string Version => $"Version {AppInfo.Version}";

    [ObservableProperty] public partial bool HasProject { get; private set; }
    [ObservableProperty] public partial NavItemViewModel? SelectedNav { get; set; }
    [ObservableProperty] public partial ViewModelBase? CurrentPage { get; private set; }
    [ObservableProperty] public partial string WindowTitle { get; private set; } = AppInfo.ProductName;
    [ObservableProperty] public partial string ProjectTitle { get; private set; } = "";
    [ObservableProperty] public partial string SaveStatus { get; private set; } = "";
    [ObservableProperty] public partial bool IsDirty { get; private set; }
    [ObservableProperty] public partial string LicenseTitle { get; private set; } = "";
    [ObservableProperty] public partial string LicenseShort { get; private set; } = "";
    [ObservableProperty] public partial string LicenseDetail { get; private set; } = "";
    [ObservableProperty] public partial bool ShowLicenseBanner { get; private set; }
    [ObservableProperty] public partial bool LicenseBannerIsWarning { get; private set; }
    [ObservableProperty] public partial string StatusMessage { get; set; } = "Ready";
    [ObservableProperty] public partial bool IsPaletteOpen { get; set; }
    [ObservableProperty] public partial string PaletteQuery { get; set; } = "";
    [ObservableProperty] public partial bool IsSettingsOpen { get; set; }
    [ObservableProperty] public partial bool IsHelpOpen { get; set; }
    [ObservableProperty] public partial bool IsLicenseOpen { get; set; }
    [ObservableProperty] public partial bool IsInspectorVisible { get; set; } = true;
    public ObservableCollection<PaletteCommand> PaletteResults { get; } = [];
    [ObservableProperty] public partial PaletteCommand? SelectedPaletteCommand { get; set; }

    public bool IsExpert
    {
        get => _ctx.IsExpert;
        set => _ctx.Settings.Update(s => s with { Mode = value ? UserMode.Expert : UserMode.Guided });
    }

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        CurrentPage = value?.Page;
        value?.Page.Activate();
    }

    partial void OnPaletteQueryChanged(string value) => FillPalette();

    private void NavigateTo(WorkflowStep step)
    {
        var item = NavItems.FirstOrDefault(n => n.Page.Step == step);
        if (item is not null) SelectedNav = item;
    }

    private void RefreshTitle()
    {
        var ws = _ctx.Workspace;
        if (ws.Session is null)
        {
            WindowTitle = AppInfo.ProductName;
            ProjectTitle = "";
            SaveStatus = "";
            IsDirty = false;
            return;
        }
        IsDirty = ws.Session.IsDirty;
        ProjectTitle = ws.State.Metadata.Name;
        SaveStatus = ws.FilePath is null ? "Not saved yet" : IsDirty ? "Unsaved changes" : "All changes saved";
        WindowTitle = $"{(IsDirty ? "• " : "")}{ProjectTitle} — {AppInfo.ProductName}";
    }

    private void RefreshLicense()
    {
        var s = License.Status;
        LicenseTitle = s.Title;
        LicenseShort = s.State switch
        {
            LicenseState.Licensed => "Licensed",
            LicenseState.LicenseOfflineGrace => "Licensed (offline)",
            LicenseState.TrialActive or LicenseState.TrialExpiring => s.DaysRemaining is { } d ? $"Trial: {d} day{(d == 1 ? "" : "s")}" : "Trial",
            LicenseState.TrialExpired => "Trial ended",
            LicenseState.LicenseExpired => "Licence expired",
            LicenseState.LicenseRevoked => "Licence revoked",
            LicenseState.ActivationLimitReached => "Activation limit",
            LicenseState.NetworkValidationRequired => "Check licence online",
            _ => "Not activated",
        };
        LicenseDetail = s.LastVerifiedText(DateTimeOffset.UtcNow) is { } v && s.State is LicenseState.Licensed or LicenseState.LicenseOfflineGrace ? $"{s.Message} {v}" : s.Message;
        ShowLicenseBanner = s.State is not (LicenseState.Licensed or LicenseState.TrialActive);
        LicenseBannerIsWarning = s.State is not (LicenseState.TrialActive or LicenseState.Licensed or LicenseState.LicenseOfflineGrace);
        if (s.State == LicenseState.TrialActive) ShowLicenseBanner = true;
    }

    private void RefreshCommands()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoTip));
        OnPropertyChanged(nameof(RedoTip));
    }

    public string UndoTip => _ctx.Workspace.Session?.UndoDescription is { } d ? $"Undo: {d} (Ctrl+Z)" : "Nothing to undo";
    public string RedoTip => _ctx.Workspace.Session?.RedoDescription is { } d ? $"Redo: {d} (Ctrl+Y)" : "Nothing to redo";

    // ───────────────────────────── Startup ─────────────────────────────

    public async Task StartupAsync(string[] args)
    {
        _autosave.Start();
        await Start.RefreshAsync();
        foreach (var (path, report) in _crash.PendingReports())
        {
            _crash.Acknowledge(path);
            _log.LogInformation("Previous session ended unexpectedly at {Time}", report.Time);
        }
        var recoverable = _ctx.Workspace.Recovery.Recoverable();
        foreach (var info in recoverable)
        {
            var choice = await _ctx.Dialogs.AskAsync("Recover unsaved project",
                $"Refraction Studio recovered an unsaved project: “{info.ProjectName}” (last checkpoint {info.CheckpointAt.ToLocalTime():g}).",
                MessageKind.Question, "Recover", "Discard", "Later");
            if (choice == 0)
            {
                await RunGuardedAsync("Recovering project", () =>
                {
                    _ctx.Workspace.Recover(info);
                    StatusMessage = "Project recovered. Save it to keep the changes.";
                    return Task.CompletedTask;
                });
                break;
            }
            if (choice == 1) _ctx.Workspace.Recovery.Discard(info.SessionId);
        }
        if (_ctx.Workspace.Session is null && args.FirstOrDefault(a => a.EndsWith(ProjectContainer.Extension, StringComparison.OrdinalIgnoreCase)) is { } file && File.Exists(file))
            await OpenPathAsync(file);
        if (License.Status.State == LicenseState.NotActivated) IsLicenseOpen = true;
        else _ = RefreshLicenseOnlineAsync();
        // NotifyOnly: report an available update (start page and status bar); Automatic: also offer to
        // download and install it (the user confirms). Manual: only when asked.
        if (_ctx.Settings.Current.Updates != UpdateMode.Manual) _ = Start.CheckForUpdatesAsync(silent: _ctx.Settings.Current.Updates == UpdateMode.NotifyOnly);
    }

    /// <summary>Called when the window closes after the user confirmed: stops autosave and releases the project.</summary>
    public void Shutdown()
    {
        _autosave.Stop();
        _ctx.Workspace.Close();
    }

    private async Task RefreshLicenseOnlineAsync()
    {
        try
        {
            await License.Manager.RefreshAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogInformation("Licence refresh skipped: {Message}", ex.Message);
        }
    }

    /// <summary>Handles an unexpected UI exception: logs it and shows a message; returns true to keep running.</summary>
    public bool ReportUnexpectedError(Exception ex)
    {
        _ = _ctx.Dialogs.ShowAsync("Something went wrong",
            "An unexpected error occurred. Your work is kept in a recovery checkpoint. If the problem persists, export a diagnostic package from Settings → Advanced and contact support.",
            MessageKind.Error, Logging.Scrub(ex.ToString()));
        return true;
    }

    /// <summary>Runs an action, turning expected errors into friendly messages with details.</summary>
    public async Task<bool> RunGuardedAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (ProjectFileException ex)
        {
            await _ctx.Dialogs.ShowAsync(what, ex.Message, MessageKind.Error, ex.TechnicalDetail);
        }
        catch (Formats.SeismicFormatException ex)
        {
            await _ctx.Dialogs.ShowAsync(what, ex.Message, MessageKind.Error, ex.TechnicalDetail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            await _ctx.Dialogs.ShowAsync(what, ex.Message, MessageKind.Error, ex.GetType().Name);
        }
        return false;
    }

    // ───────────────────────────── File commands ─────────────────────────────

    /// <summary>Asks to save unsaved changes. Returns false if the user cancelled.</summary>
    public async Task<bool> ConfirmCloseProjectAsync()
    {
        var session = _ctx.Workspace.Session;
        if (session is null || !session.IsDirty) return true;
        var choice = await _ctx.Dialogs.AskAsync("Unsaved changes", $"Save changes to “{_ctx.Workspace.State.Metadata.Name}” before closing it?", MessageKind.Question, "Save", "Don't save", "Cancel");
        if (choice == 0) return await SaveInternalAsync(saveAs: false);
        if (choice == 1)
        {
            return true;
        }
        return false;
    }

    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (!await ConfirmCloseProjectAsync()) return;
        _ctx.Workspace.Close();
        _ctx.Workspace.NewProject("New refraction project");
        var m = _ctx.Workspace.State.Metadata;
        _ctx.Workspace.Session!.Execute(Core.Session.Edits.SetMetadata(m with { DisplayUnits = _ctx.Settings.Current.Units, ModulusUnit = _ctx.Settings.Current.ModulusUnit }));
        _ctx.Workspace.Session.MarkSaved();
        _ctx.Workspace.Session.MarkDirty();
        NavigateTo(WorkflowStep.Project);
        StatusMessage = "New project created. Enter the project details, then import data.";
    }

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        var files = await _ctx.Dialogs.OpenFilesAsync("Open project", false, DialogService.ProjectFilter);
        if (files.Count == 1) await OpenPathAsync(files[0]);
    }

    public async Task OpenPathAsync(string path)
    {
        if (!await ConfirmCloseProjectAsync()) return;
        await RunGuardedAsync("Could not open the project", async () =>
        {
            _ctx.Workspace.Close();
            var loaded = await Busy.RunAsync("Opening project…", (_, _) => Task.Run(() => ProjectWorkspace.Load(path)), cancellable: false);
            if (loaded is null) return;
            _ctx.Workspace.Attach(loaded);
            await _ctx.Catalog.AddRecentAsync(path, _ctx.Workspace.State.Metadata.Name, DateTimeOffset.UtcNow);
            foreach (var note in loaded.MigrationNotes) StatusMessage = note;
            NavigateTo(WorkflowStep.Project);
            StatusMessage = $"Opened {Path.GetFileName(path)}.";
        });
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private Task SaveAsync() => SaveInternalAsync(saveAs: false);

    [RelayCommand(CanExecute = nameof(HasProject))]
    private Task SaveAsAsync() => SaveInternalAsync(saveAs: true);

    private async Task<bool> SaveInternalAsync(bool saveAs)
    {
        var ws = _ctx.Workspace;
        if (ws.Session is null) return false;
        var path = ws.FilePath;
        if (saveAs || path is null)
        {
            Directory.CreateDirectory(AppPaths.DefaultProjectFolder);
            path = await _ctx.Dialogs.SaveFileAsync("Save project", SafeName(ws.State.Metadata.Name) + ProjectContainer.Extension, "rfsx", DialogService.ProjectFilter);
            if (path is null) return false;
        }
        var ok = await RunGuardedAsync("Could not save the project", async () =>
        {
            await Busy.RunAsync("Saving…", (_, _) => Task.Run(() => { ws.Save(path); return true; }), cancellable: false);
            await _ctx.Catalog.AddRecentAsync(path, ws.State.Metadata.Name, DateTimeOffset.UtcNow);
        });
        RefreshTitle();
        if (ok) StatusMessage = $"Saved {Path.GetFileName(path)}.";
        return ok;
    }

    private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => _ctx.Workspace.Session?.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => _ctx.Workspace.Session?.Redo();

    private bool CanUndo() => _ctx.Workspace.Session?.CanUndo == true;

    private bool CanRedo() => _ctx.Workspace.Session?.CanRedo == true;

    partial void OnHasProjectChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        ReportCommand.NotifyCanExecuteChanged();
        RefreshCommands();
    }

    [RelayCommand]
    private async Task ImportDataAsync()
    {
        if (_ctx.Workspace.Session is null) await NewProjectAsync();
        if (_ctx.Workspace.Session is null) return;
        NavigateTo(WorkflowStep.Import);
        await Import.BrowseFilesAsync();
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void Export() => NavigateTo(WorkflowStep.Results);

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void Report() => NavigateTo(WorkflowStep.Report);

    [RelayCommand]
    private void OpenSettings()
    {
        Settings.Load();
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void OpenHelp()
    {
        Help.ShowTopic(CurrentPage is PageViewModel p ? p.HelpTopic : "workflow");
        IsHelpOpen = true;
    }

    [RelayCommand]
    private void OpenLicense() => IsLicenseOpen = true;

    [RelayCommand]
    private void CloseOverlays()
    {
        IsPaletteOpen = false;
        IsSettingsOpen = false;
        IsHelpOpen = false;
        IsLicenseOpen = false;
    }

    [RelayCommand]
    private void ToggleInspector() => IsInspectorVisible = !IsInspectorVisible;

    [RelayCommand]
    private void GoHome()
    {
        SelectedNav = null;
        CurrentPage = null;
        _ = Start.RefreshAsync();
    }

    // ───────────────────────────── Command palette ─────────────────────────────

    [RelayCommand]
    private void OpenPalette()
    {
        PaletteQuery = "";
        FillPalette();
        IsPaletteOpen = true;
    }

    private IEnumerable<PaletteCommand> AllCommands()
    {
        yield return new("New project", "File", "Ctrl+N", () => NewProjectCommand.ExecuteAsync(null));
        yield return new("Open project…", "File", "Ctrl+O", () => OpenProjectCommand.ExecuteAsync(null));
        yield return new("Save", "File", "Ctrl+S", () => SaveCommand.ExecuteAsync(null));
        yield return new("Save as…", "File", "Ctrl+Shift+S", () => SaveAsCommand.ExecuteAsync(null));
        yield return new("Import seismic data…", "File", "Ctrl+I", () => ImportDataCommand.ExecuteAsync(null));
        yield return new("Undo", "Edit", "Ctrl+Z", () => { Undo(); return Task.CompletedTask; });
        yield return new("Redo", "Edit", "Ctrl+Y", () => { Redo(); return Task.CompletedTask; });
        yield return new("Generate report", "Report", "Ctrl+R", () => { Report(); return Task.CompletedTask; });
        yield return new("Settings", "Application", null, () => { OpenSettings(); return Task.CompletedTask; });
        yield return new("Licence", "Application", null, () => { OpenLicense(); return Task.CompletedTask; });
        yield return new("Help and user guide", "Help", "F1", () => { OpenHelp(); return Task.CompletedTask; });
        yield return new("Explore sample project", "Help", null, () => Start.OpenSampleCommand.ExecuteAsync(null));
        yield return new(IsExpert ? "Switch to Guided mode" : "Switch to Expert mode", "View", null, () => { IsExpert = !IsExpert; return Task.CompletedTask; });
        yield return new("Toggle inspector panel", "View", null, () => { ToggleInspector(); return Task.CompletedTask; });
        foreach (var n in NavItems) yield return new($"Go to {n.Title}", "Navigate", null, () => { SelectedNav = n; return Task.CompletedTask; });
        foreach (var t in HelpCatalog.All) yield return new($"Help: {t.Title}", "Help", null, () => { Help.ShowTopic(t.Id); IsHelpOpen = true; return Task.CompletedTask; });
    }

    private void FillPalette()
    {
        PaletteResults.Clear();
        var terms = PaletteQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var c in AllCommands().Where(c => terms.All(t => c.Title.Contains(t, StringComparison.OrdinalIgnoreCase) || c.Category.Contains(t, StringComparison.OrdinalIgnoreCase))).Take(14))
            PaletteResults.Add(c);
        SelectedPaletteCommand = PaletteResults.FirstOrDefault();
    }

    [RelayCommand]
    private async Task RunPaletteCommandAsync(PaletteCommand? command)
    {
        command ??= PaletteResults.FirstOrDefault();
        if (command is null) return;
        IsPaletteOpen = false;
        await command.Execute();
    }
}
