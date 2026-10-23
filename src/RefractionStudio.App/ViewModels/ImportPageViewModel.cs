using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefractionStudio.App.Services;
using RefractionStudio.Core.Import;
using RefractionStudio.Core.Services;
using RefractionStudio.Domain;
using RefractionStudio.Formats;
using RefractionStudio.Persistence;

namespace RefractionStudio.App.ViewModels;

/// <summary>An import finding with an optional correction the user may approve.</summary>
public sealed partial class ImportIssueViewModel(ImportIssue issue) : ViewModelBase
{
    public ImportIssue Issue { get; } = issue;
    public string Severity => Issue.Severity.ToString();
    public string Icon => Issue.Severity switch { ImportSeverity.Error => "Icon.Error", ImportSeverity.Warning => "Icon.Warning", _ => "Icon.Info" };
    public string Message => Issue.Message;
    public string? Details => Issue.Details;
    public bool HasFix => Issue.Fix is not null;
    public string FixText => Issue.Fix?.Description ?? "";

    [ObservableProperty] public partial bool ApplyFix { get; set; }
}

/// <summary>A file in the import list.</summary>
public sealed record ImportFileRow(InspectedFile File)
{
    public string Name => File.FileName;
    public string Format => File.Decoded?.FormatDescription ?? "Not readable";
    public string Records => File.Decoded?.Records.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—";
    public string Traces => File.Decoded?.Records.Sum(r => r.Traces.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—";
    public string Source => File.Decoded?.Records.FirstOrDefault()?.SourceX is { } x ? x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " m" : "—";
    public string Recorded => File.Decoded?.Records.FirstOrDefault()?.AcquiredAt?.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "—";
    public bool Readable => File.Decoded is not null;
}

/// <summary>Import: file selection, preview, validation, options and import.</summary>
public sealed partial class ImportPageViewModel(StudioContext ctx) : PageViewModel(ctx)
{
    public override string Title => "Import";
    public override string HelpTopic => "import";
    public override WorkflowStep Step => WorkflowStep.Import;
    public override string GuidedText => "Add SEG-2 or SEG-Y shot records (drag files or folders here). Check the preview and the findings, approve any corrections, choose the line and wave type, then import. Original files are kept unchanged inside the project.";

    public ObservableCollection<ImportFileRow> Files { get; } = [];
    public ObservableCollection<ImportIssueViewModel> Issues { get; } = [];
    public ObservableCollection<SurveyLine> Lines { get; } = [];
    public ObservableCollection<SpreadProfile> Profiles { get; } = [];
    public IReadOnlyList<WaveType> WaveTypes { get; } = [WaveType.P, WaveType.SH];
    public IReadOnlyList<GatherGrouping> Groupings { get; } = Enum.GetValues<GatherGrouping>();

    [ObservableProperty] public partial string Summary { get; private set; } = "";
    [ObservableProperty] public partial bool HasFiles { get; private set; }
    [ObservableProperty] public partial bool NewLine { get; set; } = true;
    [ObservableProperty] public partial SurveyLine? TargetLine { get; set; }
    [ObservableProperty] public partial string LineName { get; set; } = "Line 1 — P";
    [ObservableProperty] public partial WaveType WaveType { get; set; } = WaveType.P;
    [ObservableProperty] public partial SpreadProfile? Profile { get; set; }
    [ObservableProperty] public partial decimal Spacing { get; set; } = 2;
    [ObservableProperty] public partial GatherGrouping Grouping { get; set; } = GatherGrouping.Separate;
    [ObservableProperty] public partial string ResultText { get; private set; } = "";
    [ObservableProperty] public partial int ErrorCount { get; private set; }
    [ObservableProperty] public partial int WarningCount { get; private set; }

    public string GroupingHelp => Grouping switch
    {
        GatherGrouping.Separate => "Each record becomes its own shot gather.",
        GatherGrouping.StackSamePosition => "Records recorded at the same shot position are averaged (vertical stack).",
        _ => "Records at the same position are split into left and right impacts by the sign of their correlation and combined as (left − right)/2. Verify in Geometry.",
    };

    partial void OnGroupingChanged(GatherGrouping value) => OnPropertyChanged(nameof(GroupingHelp));

    partial void OnWaveTypeChanged(WaveType value)
    {
        if (NewLine) LineName = $"Line {Lines.Count / 1 + 1} — {value}";
        Grouping = value == WaveType.SH ? GatherGrouping.ShOppositePairs : GatherGrouping.Separate;
    }

    public override void Activate()
    {
        Lines.Clear();
        foreach (var l in Context.State?.Survey.Lines ?? []) Lines.Add(l);
        if (NewLine) LineName = $"Line {Lines.Count + 1} — {WaveType}";
        _ = LoadProfilesAsync();
    }

    public override void OnProjectChanged()
    {
        Clear();
        Activate();
    }

    private async Task LoadProfilesAsync()
    {
        Profiles.Clear();
        foreach (var p in await Context.Catalog.SpreadProfilesAsync()) Profiles.Add(p);
    }

    [RelayCommand]
    public async Task BrowseFilesAsync()
    {
        var files = await Context.Dialogs.OpenFilesAsync("Select seismic records", true, DialogService.SeismicFilter, new FileFilter("All files", "*"));
        if (files.Count > 0) await AddPathsAsync(files);
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var folder = await Context.Dialogs.OpenFolderAsync("Select a folder of seismic records");
        if (folder is not null) await AddPathsAsync([folder]);
    }

    /// <summary>Adds files and folders (recursively, supported extensions only) and re-validates.</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var expanded = new List<string>();
        var extensions = Context.Workspace.Formats.AllExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                expanded.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Where(f => extensions.Contains(Path.GetExtension(f))).Order(StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(p)) expanded.Add(p);
        }
        var existing = Files.Select(f => f.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toRead = expanded.Where(p => existing.Add(p)).ToList();
        if (toRead.Count == 0) return;
        var result = await Context.Busy.RunAsync($"Reading {toRead.Count} file(s)…", (progress, ct) =>
            Context.Workspace.InspectAsync(toRead, new Progress<(int Done, int Total)>(p => progress.Report(((double)p.Done / p.Total, $"Reading files ({p.Done}/{p.Total})"))), ct));
        if (result.Files is null) return;
        foreach (var f in result.Files) Files.Add(new ImportFileRow(f));
        Revalidate();
    }

    private void Revalidate()
    {
        var files = Files.Select(f => f.File).ToList();
        var existing = Context.State?.Survey.SourceFiles.Select(f => f.Sha256).ToHashSet() ?? [];
        Issues.Clear();
        foreach (var i in ImportInspector.Validate(files, existing).OrderByDescending(i => i.Severity))
            Issues.Add(new ImportIssueViewModel(i) { ApplyFix = i.Severity == ImportSeverity.Error && i.Fix is not null });
        var s = ImportInspector.Summarize(files);
        string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        Summary = $"{s.FileCount} file(s), {s.ReadableFiles} readable — {s.RecordCount} record(s), {s.TraceCount} traces. " +
                  $"Sample interval: {string.Join(", ", s.SampleIntervals.Select(d => F(d * 1000) + " ms"))}. " +
                  $"Samples per trace: {string.Join(", ", s.SampleCounts)}. Channels per record: {string.Join(", ", s.ChannelCounts)}." +
                  (s.FirstAcquisition is { } a ? $" Recorded {a:yyyy-MM-dd HH:mm}" + (s.LastAcquisition is { } b && $"{b:yyyy-MM-dd HH:mm}" != $"{a:yyyy-MM-dd HH:mm}" ? $" – {b:yyyy-MM-dd HH:mm}." : ".") : "");
        HasFiles = Files.Count > 0;
        ErrorCount = Issues.Count(i => i.Issue.Severity == ImportSeverity.Error);
        WarningCount = Issues.Count(i => i.Issue.Severity == ImportSeverity.Warning);
        ImportCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveFile(ImportFileRow? row)
    {
        if (row is null) return;
        Files.Remove(row);
        Revalidate();
    }

    [RelayCommand]
    private void Clear()
    {
        Files.Clear();
        Issues.Clear();
        Summary = "";
        HasFiles = false;
        ResultText = "";
        ImportCommand.NotifyCanExecuteChanged();
    }

    private bool CanImport() => Files.Any(f => f.Readable);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (!Context.License.Status.CanEdit)
        {
            await Context.Dialogs.ShowAsync("Import not available", Context.License.Status.Message, MessageKind.Warning);
            return;
        }
        var unresolvedErrors = Issues.Where(i => i.Issue.Severity == ImportSeverity.Error && !(i.HasFix && i.ApplyFix)).ToList();
        if (unresolvedErrors.Count > 0)
        {
            var choice = await Context.Dialogs.AskAsync("Unresolved errors",
                $"{unresolvedErrors.Count} error(s) have no approved correction. Files that cannot be read are never imported. Continue with the readable files?",
                MessageKind.Warning, "Continue", "Cancel");
            if (choice != 0) return;
        }
        var skipFiles = Issues.Where(i => i.ApplyFix && i.Issue.Fix?.Kind == ImportFixKind.SkipFile).Select(i => i.Issue.Fix!.FileName).ToHashSet();
        var skipTraces = Issues.Where(i => i.ApplyFix && i.Issue.Fix is { Kind: ImportFixKind.SkipTrace, Channel: not null })
            .Select(i => (i.Issue.Fix!.FileName, i.Issue.Fix.Channel!.Value)).ToHashSet();
        var options = new ImportOptions(NewLine ? null : TargetLine?.Id, string.IsNullOrWhiteSpace(LineName) ? $"Line {Lines.Count + 1}" : LineName.Trim(),
            WaveType, Profile, (double)Spacing, Grouping, skipFiles, skipTraces);
        var files = Files.Select(f => f.File).ToList();
        if (Context.State is { } current && Core.Import.ImportService.Importable(current, files, skipFiles).Count == 0)
        {
            await Context.Dialogs.ShowAsync("Nothing to import",
                "All of the selected files are already in this project, were skipped, or could not be read. Choose other files, or clear the list.",
                MessageKind.Information);
            return;
        }
        var plan = await Context.Busy.RunAsync("Importing…", (_, _) => Task.Run(() => Context.Workspace.PrepareImport(files, options)), cancellable: false);
        if (plan is null) return;
        Context.Workspace.ApplyImport(plan);
        // New lines get the user's default processing pipeline.
        // New lines get the user's default processing and picker (SH lines: the SH polarity-divergence picker).
        if (NewLine && Context.State is not null)
        {
            var session = Context.Workspace.Session!;
            var pipeline = Context.Settings.Current.DefaultPipeline;
            var picker = WaveType == WaveType.SH
                ? new PickerSettings(PickerAlgorithm.ShPolarityDivergence, ShortWindow: 0.004, LongWindow: 0.04, MinApparentVelocity: 100)
                : Context.Settings.Current.DefaultPicker is { Algorithm: not PickerAlgorithm.ShPolarityDivergence } p ? p : new PickerSettings();
            using (session.BeginGesture("Defaults for the new line"))
            {
                session.Execute(Core.Session.Edits.SetPipeline(plan.Line, pipeline with { Steps = [.. pipeline.Steps.Select(s => s with { Id = StepId.New() })] }, "Default processing for the new line"));
                session.Execute(Core.Session.Edits.SetPickerSettings(plan.Line, picker));
            }
        }
        Context.Selection.Select(plan.Line, Context.State?.Survey.Line(plan.Line)?.Gathers.FirstOrDefault()?.Id);
        ResultText = $"Imported {plan.RecordCount} record(s) as {plan.GatherCount} shot gather(s)." + (plan.Notes.IsEmpty ? "" : "\n• " + string.Join("\n• ", plan.Notes));
        Files.Clear();
        Issues.Clear();
        HasFiles = false;
        Activate();
        ImportCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void GoToGeometry() => Context.Navigate?.Invoke(WorkflowStep.Geometry);
}
