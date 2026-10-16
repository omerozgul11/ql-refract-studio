using RefractionStudio.Domain.Units;

namespace RefractionStudio.Domain;

/// <summary>Descriptive project information.</summary>
public sealed record ProjectMetadata(
    string Name,
    string? ProjectNumber = null,
    string? Client = null,
    string? ClientContact = null,
    string? SiteName = null,
    string? SiteAddress = null,
    string? Description = null,
    DateOnly? SurveyDate = null,
    string? FieldCrew = null,
    string? Instrument = null,
    string? Geophones = null,
    string? Source = null,
    string? Conditions = null,
    DateTimeOffset CreatedAt = default,
    string CreatedWithVersion = "",
    UnitSystem DisplayUnits = UnitSystem.SI,
    ModulusDisplayUnit ModulusUnit = ModulusDisplayUnit.MPa);

/// <summary>Company branding printed on reports.</summary>
public sealed record CompanyBranding(
    string CompanyName = "",
    string? Address = null,
    string? Phone = null,
    string? Email = null,
    string? Website = null,
    string? Footer = null,
    string? EngineerName = null,
    string? Registration = null,
    string? LogoPngBase64 = null);

/// <summary>Optional report sections (cover, methodology, appendices…).</summary>
public enum ReportSection
{
    Cover,
    ProjectInformation,
    SurveyConfiguration,
    Geometry,
    ProcessingMethodology,
    QualityControl,
    ShotGathers,
    TravelTimes,
    LayeredModel,
    Tomography,
    VelocityProfiles,
    EngineeringParameters,
    Vs30,
    ProcessingNotes,
    Warnings,
    Conclusions,
    PickTableAppendix,
    HistoryAppendix,
}

/// <summary>Report content and options stored with the project.</summary>
public sealed record ReportSettings(
    CompanyBranding Branding,
    string Title,
    string? Subtitle,
    string? ReportNumber,
    string? PreparedBy,
    string? CheckedBy,
    string? ProcessingNotes,
    string? Conclusions,
    ImmutableArray<ReportSection> Sections,
    bool IncludeAllGathers,
    UnitSystem? UnitsOverride)
{
    public static ReportSettings Default { get; } = new(
        new CompanyBranding(),
        "Seismic Refraction Survey Report",
        null, null, null, null, null, null,
        [.. Enum.GetValues<ReportSection>()],
        false,
        null);
}

/// <summary>Header of a named project snapshot (content stored separately).</summary>
public sealed record SnapshotHeader(SnapshotId Id, string Name, DateTimeOffset CreatedAt, string? Note);

/// <summary>Kind of audited operation.</summary>
public enum HistoryKind
{
    Project,
    Import,
    Geometry,
    Processing,
    Picking,
    Interpretation,
    Inversion,
    Report,
    Undo,
    Redo,
    Scenario,
}

/// <summary>An append-only audit record of a meaningful change.</summary>
/// <param name="Timestamp">UTC time of the change.</param>
/// <param name="Kind">Category.</param>
/// <param name="Operation">Machine-readable operation name, e.g. "pick.move".</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="Parameters">Parameters of the change.</param>
/// <param name="User">User name of the editor.</param>
/// <param name="AppVersion">Application version.</param>
/// <param name="Scenario">Scenario that was active.</param>
public sealed record HistoryEntry(
    DateTimeOffset Timestamp,
    HistoryKind Kind,
    string Operation,
    string Description,
    ImmutableDictionary<string, string> Parameters,
    string User,
    string AppVersion,
    ScenarioId? Scenario);

/// <summary>The complete editable state of a project. Immutable; edits produce new states.</summary>
public sealed record ProjectState(
    int SchemaVersion,
    ProjectMetadata Metadata,
    Survey Survey,
    ImmutableArray<AnalysisScenario> Scenarios,
    ScenarioId ActiveScenarioId,
    ReportSettings Report,
    ImmutableArray<SnapshotHeader> Snapshots)
{
    /// <summary>Current schema version written by this build.</summary>
    public const int CurrentSchemaVersion = 1;

    public AnalysisScenario ActiveScenario
    {
        get
        {
            foreach (var s in Scenarios)
                if (s.Id == ActiveScenarioId) return s;
            return Scenarios[0];
        }
    }

    public AnalysisScenario? Scenario(ScenarioId id)
    {
        foreach (var s in Scenarios)
            if (s.Id == id) return s;
        return null;
    }

    /// <summary>Interpretation of a line in the active scenario.</summary>
    public LineInterpretation Interpretation(LineId line) => ActiveScenario.LineOrDefault(line);

    /// <summary>Creates a new empty project.</summary>
    public static ProjectState CreateNew(string name, string appVersion, DateTimeOffset now)
    {
        var scenario = new AnalysisScenario(ScenarioId.New(), "Base interpretation", null, now,
            ImmutableDictionary<LineId, LineInterpretation>.Empty, ScenarioSettings.Default);
        return new ProjectState(
            CurrentSchemaVersion,
            new ProjectMetadata(name, CreatedAt: now, CreatedWithVersion: appVersion),
            Survey.Empty,
            [scenario],
            scenario.Id,
            ReportSettings.Default,
            []);
    }

    /// <summary>Returns a copy with the active scenario replaced by <paramref name="update"/>(active).</summary>
    public ProjectState WithActiveScenario(Func<AnalysisScenario, AnalysisScenario> update)
    {
        var active = ActiveScenario;
        var updated = update(active);
        return this with { Scenarios = Scenarios.Replace(active, updated) };
    }

    /// <summary>Returns a copy with the interpretation of <paramref name="line"/> in the active scenario updated.</summary>
    public ProjectState WithLineInterpretation(LineId line, Func<LineInterpretation, LineInterpretation> update)
        => WithActiveScenario(s => s with { Lines = s.Lines.SetItem(line, update(s.LineOrDefault(line))) });

    /// <summary>Returns a copy with a survey line replaced.</summary>
    public ProjectState WithLine(SurveyLine line)
    {
        var lines = Survey.Lines;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Id == line.Id)
                return this with { Survey = Survey with { Lines = lines.SetItem(i, line) } };
        }
        throw new InvalidOperationException($"Line {line.Id} does not exist.");
    }
}
