using CommunityToolkit.Mvvm.ComponentModel;
using RefractionStudio.Analysis;
using RefractionStudio.App.Services;
using RefractionStudio.Core;
using RefractionStudio.Core.Services;
using RefractionStudio.Core.Session;
using RefractionStudio.Domain;
using RefractionStudio.Domain.Units;
using RefractionStudio.Infrastructure;
using RefractionStudio.Persistence;

namespace RefractionStudio.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
}

/// <summary>Services shared by all pages.</summary>
public sealed class StudioContext(
    ProjectWorkspace workspace,
    AnalysisCoordinator analysis,
    SelectionState selection,
    DialogService dialogs,
    BusyService busy,
    SettingsService settings,
    LicenseService license,
    AppCatalog catalog,
    AppPaths paths)
{
    public ProjectWorkspace Workspace { get; } = workspace;
    public AnalysisCoordinator Analysis { get; } = analysis;
    public SelectionState Selection { get; } = selection;
    public DialogService Dialogs { get; } = dialogs;
    public BusyService Busy { get; } = busy;
    public SettingsService Settings { get; } = settings;
    public LicenseService License { get; } = license;
    public AppCatalog Catalog { get; } = catalog;
    public AppPaths Paths { get; } = paths;

    /// <summary>Navigation callback set by the main window.</summary>
    public Action<WorkflowStep>? Navigate { get; set; }

    public ProjectState? State => Workspace.Session?.State;

    public AnalysisResult? Result => Analysis.Latest;

    public UnitFormatter Units => Settings.Units;

    public bool IsExpert => Settings.Current.Mode == UserMode.Expert;

    /// <summary>
    /// Executes an edit if the licence permits editing. A licence problem never blocks viewing; it only
    /// blocks changes, and explains why.
    /// </summary>
    public bool Edit(ProjectEdit edit)
    {
        var session = Workspace.Session;
        if (session is null) return false;
        if (!License.Status.CanEdit)
        {
            _ = Dialogs.ShowAsync("Editing is not available", License.Status.Message + " Projects can still be opened, viewed and exported.", MessageKind.Warning);
            return false;
        }
        session.Execute(edit);
        return true;
    }

    /// <summary>Starts a gesture (one undo step for a drag).</summary>
    public IDisposable? BeginGesture(string description)
        => Workspace.Session is { } s && License.Status.CanEdit ? s.BeginGesture(description) : null;

    public SurveyLine? Line(LineId? id) => id is { } l ? State?.Survey.Line(l) : null;

    public LineAnalysis? LineAnalysis(LineId? id) => id is { } l ? Result?.Line(l) : null;
}

/// <summary>Base of the workspace pages.</summary>
public abstract partial class PageViewModel(StudioContext context) : ViewModelBase
{
    protected StudioContext Context { get; } = context;

    public abstract string Title { get; }

    public abstract WorkflowStep Step { get; }

    /// <summary>Context-help topic of the page (F1).</summary>
    public virtual string HelpTopic => "workflow";

    /// <summary>Short explanation shown at the top of the page in Guided mode.</summary>
    public virtual string GuidedText => "";

    public bool ShowGuidance => !Context.IsExpert && GuidedText.Length > 0;

    public bool IsExpert => Context.IsExpert;

    /// <summary>Called when the page becomes visible.</summary>
    public virtual void Activate()
    {
    }

    /// <summary>Called after a project was created, opened or closed.</summary>
    public virtual void OnProjectChanged()
    {
    }

    /// <summary>Called on every project state change (UI thread).</summary>
    public virtual void OnStateChanged(StateChangedEventArgs e)
    {
    }

    /// <summary>Called when a new analysis result is available (UI thread).</summary>
    public virtual void OnAnalysisUpdated(AnalysisResult result)
    {
    }

    /// <summary>Called when the Guided/Expert mode or units change.</summary>
    public virtual void OnSettingsChanged()
    {
        OnPropertyChanged(nameof(ShowGuidance));
        OnPropertyChanged(nameof(IsExpert));
    }
}
