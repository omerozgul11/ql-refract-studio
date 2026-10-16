using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefractionStudio.App.Services;
using RefractionStudio.Core.Services;
using RefractionStudio.Core.Session;
using RefractionStudio.Domain;
using RefractionStudio.Reporting;

namespace RefractionStudio.App.ViewModels;

/// <summary>Project details, scenarios, snapshots and file information.</summary>
public sealed partial class ProjectPageViewModel(StudioContext ctx) : PageViewModel(ctx)
{
    public override string Title => "Project";
    public override WorkflowStep Step => WorkflowStep.Project;
    public override string HelpTopic => "workflow";
    public override string GuidedText => "Describe the project and site. These details appear on the report cover and in the audit record. You can return here at any time.";

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string? ProjectNumber { get; set; }
    [ObservableProperty] public partial string? Client { get; set; }
    [ObservableProperty] public partial string? ClientContact { get; set; }
    [ObservableProperty] public partial string? SiteName { get; set; }
    [ObservableProperty] public partial string? SiteAddress { get; set; }
    [ObservableProperty] public partial string? Description { get; set; }
    [ObservableProperty] public partial DateTimeOffset? SurveyDate { get; set; }
    [ObservableProperty] public partial string? FieldCrew { get; set; }
    [ObservableProperty] public partial string? Instrument { get; set; }
    [ObservableProperty] public partial string? Geophones { get; set; }
    [ObservableProperty] public partial string? Source { get; set; }
    [ObservableProperty] public partial string? Conditions { get; set; }
    [ObservableProperty] public partial bool IsModified { get; set; }
    [ObservableProperty] public partial string FileInfo { get; private set; } = "";
    [ObservableProperty] public partial AnalysisScenario? SelectedScenario { get; set; }
    [ObservableProperty] public partial string NewScenarioName { get; set; } = "";
    [ObservableProperty] public partial string SnapshotName { get; set; } = "";

    public ObservableCollection<AnalysisScenario> Scenarios { get; } = [];
    public ObservableCollection<SnapshotHeader> Snapshots { get; } = [];
    public ObservableCollection<HistoryEntry> History { get; } = [];

    private bool _loading;

    public override void OnProjectChanged() => Load();

    public override void Activate() => Load();

    public override void OnStateChanged(StateChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Previous.Metadata, e.Current.Metadata) || e.Kind != StateChangeKind.Edit) LoadMetadata();
        LoadLists();
    }

    private void Load()
    {
        LoadMetadata();
        LoadLists();
    }

    private void LoadMetadata()
    {
        var s = Context.State;
        if (s is null) return;
        _loading = true;
        var m = s.Metadata;
        Name = m.Name;
        ProjectNumber = m.ProjectNumber;
        Client = m.Client;
        ClientContact = m.ClientContact;
        SiteName = m.SiteName;
        SiteAddress = m.SiteAddress;
        Description = m.Description;
        SurveyDate = m.SurveyDate is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue)) : null;
        FieldCrew = m.FieldCrew;
        Instrument = m.Instrument;
        Geophones = m.Geophones;
        Source = m.Source;
        Conditions = m.Conditions;
        IsModified = false;
        _loading = false;
    }

    private void LoadLists()
    {
        var s = Context.State;
        if (s is null) return;
        Scenarios.Clear();
        foreach (var sc in s.Scenarios) Scenarios.Add(sc);
        SelectedScenario = s.ActiveScenario;
        Snapshots.Clear();
        foreach (var sn in s.Snapshots.OrderByDescending(x => x.CreatedAt)) Snapshots.Add(sn);
        History.Clear();
        foreach (var h in Context.Workspace.Session!.History.Reverse().Take(200)) History.Add(h);
        var ws = Context.Workspace;
        var bytes = s.Survey.SourceFiles.Sum(f => f.Length);
        FileInfo = (ws.FilePath is null ? "Not saved yet." : $"File: {ws.FilePath}") +
                   $"\nRaw data: {s.Survey.SourceFiles.Length} file(s), {bytes / 1048576.0:0.0} MB, {s.Survey.Records.Length} record(s), {s.Survey.Lines.Length} line(s)." +
                   $"\nCreated {s.Metadata.CreatedAt.ToLocalTime():g} with version {s.Metadata.CreatedWithVersion}.";
    }

    partial void OnNameChanged(string value) => Touch();
    partial void OnProjectNumberChanged(string? value) => Touch();
    partial void OnClientChanged(string? value) => Touch();
    partial void OnClientContactChanged(string? value) => Touch();
    partial void OnSiteNameChanged(string? value) => Touch();
    partial void OnSiteAddressChanged(string? value) => Touch();
    partial void OnDescriptionChanged(string? value) => Touch();
    partial void OnSurveyDateChanged(DateTimeOffset? value) => Touch();
    partial void OnFieldCrewChanged(string? value) => Touch();
    partial void OnInstrumentChanged(string? value) => Touch();
    partial void OnGeophonesChanged(string? value) => Touch();
    partial void OnSourceChanged(string? value) => Touch();
    partial void OnConditionsChanged(string? value) => Touch();

    private void Touch()
    {
        if (!_loading) IsModified = true;
    }

    [RelayCommand]
    private void ApplyMetadata()
    {
        var s = Context.State;
        if (s is null) return;
        if (string.IsNullOrWhiteSpace(Name))
        {
            _ = Context.Dialogs.ShowAsync("Project name required", "Enter a project name.", MessageKind.Warning);
            return;
        }
        static string? T(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var m = s.Metadata with
        {
            Name = Name.Trim(),
            ProjectNumber = T(ProjectNumber),
            Client = T(Client),
            ClientContact = T(ClientContact),
            SiteName = T(SiteName),
            SiteAddress = T(SiteAddress),
            Description = T(Description),
            SurveyDate = SurveyDate is { } d ? DateOnly.FromDateTime(d.Date) : null,
            FieldCrew = T(FieldCrew),
            Instrument = T(Instrument),
            Geophones = T(Geophones),
            Source = T(Source),
            Conditions = T(Conditions),
        };
        if (Context.Edit(Edits.SetMetadata(m))) IsModified = false;
    }

    [RelayCommand]
    private void RevertMetadata() => LoadMetadata();

    [RelayCommand]
    private void DuplicateScenario()
    {
        var name = string.IsNullOrWhiteSpace(NewScenarioName) ? $"Scenario {Scenarios.Count + 1}" : NewScenarioName.Trim();
        if (Context.Edit(Edits.DuplicateScenario(name, DateTimeOffset.UtcNow))) NewScenarioName = "";
    }

    [RelayCommand]
    private void SwitchScenario(AnalysisScenario? scenario)
    {
        if (scenario is not null && scenario.Id != Context.State?.ActiveScenarioId) Context.Edit(Edits.SwitchScenario(scenario.Id, scenario.Name));
    }

    [RelayCommand]
    private async Task DeleteScenarioAsync(AnalysisScenario? scenario)
    {
        if (scenario is null || Scenarios.Count <= 1) return;
        if (await Context.Dialogs.AskAsync("Delete scenario", $"Delete the scenario “{scenario.Name}”? Raw data are not affected; you can undo this.", MessageKind.Question, "Delete", "Cancel") == 0)
            Context.Edit(Edits.DeleteScenario(scenario.Id, scenario.Name));
    }

    [RelayCommand]
    private void CreateSnapshot()
    {
        var name = string.IsNullOrWhiteSpace(SnapshotName) ? $"Snapshot {DateTime.Now:g}" : SnapshotName.Trim();
        if (!Context.License.Status.CanEdit) return;
        Context.Workspace.Session?.CreateSnapshot(name, null);
        SnapshotName = "";
    }

    [RelayCommand]
    private async Task RestoreSnapshotAsync(SnapshotHeader? snapshot)
    {
        if (snapshot is null || !Context.License.Status.CanEdit) return;
        if (await Context.Dialogs.AskAsync("Restore snapshot", $"Restore “{snapshot.Name}”? The current state can be recovered with Undo.", MessageKind.Question, "Restore", "Cancel") == 0)
            Context.Workspace.Session?.RestoreSnapshot(snapshot.Id);
    }

    [RelayCommand]
    private async Task ArchiveAsync()
    {
        var ws = Context.Workspace;
        if (ws.FilePath is null)
        {
            await Context.Dialogs.ShowAsync("Save first", "Save the project before creating an archive.", MessageKind.Information);
            return;
        }
        var path = await Context.Dialogs.SaveFileAsync("Archive project", Path.GetFileNameWithoutExtension(ws.FilePath) + "-archive.zip", "zip", new FileFilter("Zip archive", "*.zip"));
        if (path is null) return;
        var analysis = ws.Analyze();
        var audit = AuditExport.Create(ws.State, analysis, ws.Session!.History, Infrastructure.AppInfo.Version, Context.Workspace.Options.UserName, DateTimeOffset.UtcNow);
        ws.Archive(path, audit);
        await Context.Dialogs.ShowAsync("Archive created", $"The project and its audit record were written to {path}.");
    }

    [RelayCommand]
    private async Task DuplicateProjectAsync()
    {
        var ws = Context.Workspace;
        if (ws.Session is null) return;
        var path = await Context.Dialogs.SaveFileAsync("Save a copy of the project", ws.State.Metadata.Name + " (copy).rfsx", "rfsx", DialogService.ProjectFilter);
        if (path is null) return;
        Persistence.ProjectContainer.Save(path, ws.Document(), ws.Raw, Infrastructure.AppInfo.Version);
        await Context.Dialogs.ShowAsync("Copy saved", $"A copy was saved to {path}. You are still working on the original.");
    }
}
