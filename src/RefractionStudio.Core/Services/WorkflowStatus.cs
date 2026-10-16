using RefractionStudio.Analysis;
using RefractionStudio.Analysis.Quality;
using RefractionStudio.Domain;
using RefractionStudio.Domain.Units;

namespace RefractionStudio.Core.Services;

/// <summary>Steps of the refraction workflow, in their natural order.</summary>
public enum WorkflowStep
{
    Project,
    Import,
    Geometry,
    Waveforms,
    Processing,
    PPicks,
    SPicks,
    QualityControl,
    VelocityModel,
    Layers,
    Engineering,
    Results,
    Report,
}

/// <summary>Status of a workflow step.</summary>
public enum StepState
{
    NotStarted,
    InProgress,
    Complete,
    NeedsAttention,
    NotApplicable,
}

/// <summary>Status of one step with a hint of what to do next.</summary>
public sealed record StepStatus(WorkflowStep Step, string Title, StepState State, string Hint);

/// <summary>Derives the workflow stepper from the project and the latest analysis. Steps are never enforced.</summary>
public static class WorkflowStatus
{
    public static ImmutableArray<StepStatus> Compute(ProjectState state, AnalysisResult? analysis)
    {
        var s = state.Survey;
        var list = ImmutableArray.CreateBuilder<StepStatus>();
        list.Add(new(WorkflowStep.Project, "Project", string.IsNullOrWhiteSpace(state.Metadata.Name) ? StepState.InProgress : StepState.Complete,
            string.IsNullOrWhiteSpace(state.Metadata.Client) ? "Add client and site details for the report." : "Project details entered."));

        list.Add(new(WorkflowStep.Import, "Import", s.Records.IsEmpty ? StepState.NotStarted : StepState.Complete,
            s.Records.IsEmpty ? "Import SEG-2 or SEG-Y shot records to begin." : $"{s.Records.Length} records in {s.Lines.Length} line(s)."));

        var gathers = s.Lines.SelectMany(l => l.Gathers).ToList();
        var missingShots = gathers.Count(g => !double.IsFinite(g.ShotX));
        list.Add(new(WorkflowStep.Geometry, "Geometry",
            gathers.Count == 0 ? StepState.NotStarted : missingShots > 0 ? StepState.NeedsAttention : StepState.Complete,
            gathers.Count == 0 ? "Geometry is set after import." : missingShots > 0 ? $"{missingShots} shot(s) need a position." : "Shot and receiver positions set."));

        list.Add(new(WorkflowStep.Waveforms, "Waveforms", gathers.Count == 0 ? StepState.NotStarted : StepState.Complete,
            gathers.Count == 0 ? "Import data to view shot gathers." : "Inspect gathers; exclude bad traces."));

        var processed = s.Lines.Any(l => state.Interpretation(l.Id).Pipeline.EnabledSteps.Any(st => st is not RemoveMeanStep));
        list.Add(new(WorkflowStep.Processing, "Processing", gathers.Count == 0 ? StepState.NotStarted : processed ? StepState.Complete : StepState.InProgress,
            processed ? "Processing pipeline configured." : "Optional: inspect the spectrum and add a band-pass filter."));

        foreach (var (wave, step, title) in new[] { (WaveType.P, WorkflowStep.PPicks, "P picks"), (WaveType.SH, WorkflowStep.SPicks, "S picks") })
        {
            var lines = s.Lines.Where(l => l.WaveType == wave).ToList();
            if (lines.Count == 0)
            {
                list.Add(new(step, title, StepState.NotApplicable, wave == WaveType.SH ? "No SH line imported (needed for Vs and Vs30)." : "No P line imported."));
                continue;
            }
            var receivers = lines.Sum(l => l.Gathers.Where(g => g.Included).Sum(g => l.Spread(g.SpreadId)?.ReceiverChannels.Count() ?? 0));
            var picks = lines.Sum(l => state.Interpretation(l.Id).Picks.Count(p => p.Value.Included && l.Gather(p.Key.Gather)?.Included == true));
            var unreviewed = lines.Sum(l => state.Interpretation(l.Id).Picks.Count(p => p.Value.Origin == ArrivalOrigin.Automatic));
            var stateOf = picks == 0 ? StepState.NotStarted : picks < 0.9 * receivers ? StepState.InProgress : unreviewed > 0 ? StepState.NeedsAttention : StepState.Complete;
            list.Add(new(step, title, stateOf, picks == 0 ? "Run automatic picking or pick manually." :
                unreviewed > 0 ? $"{unreviewed} automatic pick(s) to review." : $"{picks} of {receivers} traces picked."));
        }

        var problems = analysis?.Qc.Flags.Count(f => f.Level == QcLevel.Problem) ?? 0;
        var reviews = analysis?.Qc.Flags.Count(f => f.Level == QcLevel.Review) ?? 0;
        list.Add(new(WorkflowStep.QualityControl, "Quality control",
            analysis is null || analysis.Lines.All(l => l.Observations.All.IsEmpty) ? StepState.NotStarted : problems > 0 ? StepState.NeedsAttention : reviews > 0 ? StepState.InProgress : StepState.Complete,
            problems > 0 ? $"{problems} likely problem(s)." : reviews > 0 ? $"{reviews} item(s) to review." : "No issues found."));

        var models = analysis?.Lines.Count(l => l.Layered.HasValue || l.Tomography is not null) ?? 0;
        var stale = analysis?.Lines.Count(l => l.TomographyStale) ?? 0;
        list.Add(new(WorkflowStep.VelocityModel, "Velocity model", models == 0 ? StepState.NotStarted : stale > 0 ? StepState.NeedsAttention : StepState.Complete,
            models == 0 ? "Assign layers and choose a method, or run tomography." : stale > 0 ? "A tomogram is out of date; run it again." : "Velocity model available."));

        var edited = s.Lines.Any(l => state.Interpretation(l.Id).Segments.Gathers.Values.Any(g => g.UserEdited));
        var anyAssigned = s.Lines.Any(l => !state.Interpretation(l.Id).Segments.Gathers.IsEmpty);
        list.Add(new(WorkflowStep.Layers, "Layers", !anyAssigned ? StepState.NotStarted : edited ? StepState.Complete : StepState.InProgress,
            !anyAssigned ? "Suggest crossovers, then review them." : edited ? "Layer assignment reviewed." : "Crossovers are automatic suggestions; review them."));

        var eng = analysis?.Engineering;
        var engState = eng is null ? StepState.NotStarted
            : eng.Vs30.HasValue || eng.Profiles.HasValue ? StepState.Complete
            : s.Lines.Any(l => l.WaveType == WaveType.SH) ? StepState.InProgress : StepState.NotApplicable;
        list.Add(new(WorkflowStep.Engineering, "Engineering", engState,
            eng is null ? "Requires a velocity model." : eng.Vs30.HasValue ? "Vs30 computed." : eng.Vs30.Reason ?? ""));

        list.Add(new(WorkflowStep.Results, "Results", models == 0 ? StepState.NotStarted : StepState.InProgress, "Review results and compare scenarios."));
        list.Add(new(WorkflowStep.Report, "Report", models == 0 ? StepState.NotStarted : StepState.InProgress, "Generate the PDF report and data exports."));
        return list.ToImmutable();
    }
}

/// <summary>Comparison values of one scenario.</summary>
/// <summary>A compared quantity (SI value). <paramref name="Kind"/> lets the display convert units; <paramref name="X"/> is the position for per-position quantities.</summary>
public sealed record ScenarioValue(string Line, string Quantity, double? Value, string Note, QuantityKind? Kind = null, double? X = null);

public sealed record ScenarioSummary(ScenarioId Id, string Name, ImmutableArray<ScenarioValue> Values);

/// <summary>Compares analysis scenarios without duplicating raw data (each scenario is analysed in turn).</summary>
public static class ScenarioComparer
{
    public static ImmutableArray<ScenarioSummary> Compare(ProjectState state, RefractionAnalyzer analyzer, ITomographyResultSource results)
    {
        var list = ImmutableArray.CreateBuilder<ScenarioSummary>();
        foreach (var sc in state.Scenarios)
        {
            var view = state with { ActiveScenarioId = sc.Id };
            var analysis = analyzer.Analyze(view, results);
            var values = ImmutableArray.CreateBuilder<ScenarioValue>();
            foreach (var l in analysis.Lines)
            {
                var name = l.Line.Name;
                if (l.Layered.HasValue)
                {
                    var m = l.Layered.Value!;
                    foreach (var layer in m.Layers) values.Add(new(name, $"V{layer.Layer} ({m.Method})", layer.Velocity, layer.Basis, QuantityKind.Velocity));
                    foreach (var r in m.Refractors)
                        if (!r.Points.IsDefaultOrEmpty) values.Add(new(name, $"Refractor {r.Refractor} mean depth", r.Points.Average(p => p.Depth), $"{r.Points.Length} points", QuantityKind.Depth));
                    var res = m.Residuals.Select(x => x.Residual).ToList();
                    if (res.Count > 0) values.Add(new(name, "Layered RMS residual (ms)", Math.Sqrt(res.Average(v => v * v)) * 1000, ""));
                }
                else values.Add(new(name, "Layered model", null, l.Layered.Reason ?? ""));
                if (l.Tomography is { } t) values.Add(new(name, "Tomography RMS (ms)", t.FinalRms * 1000, l.TomographyStale ? "out of date" : ""));
            }
            var vsLabel = view.ActiveScenario.Settings.Vs30.Label;
            if (analysis.Engineering.Vs30.HasValue)
                foreach (var e in analysis.Engineering.Vs30.Value!)
                    values.Add(new(vsLabel, vsLabel, e.Vs30.HasValue ? e.Vs30.Value!.Velocity : null, e.Vs30.HasValue ? e.Vs30.Value!.Qualifier : e.Vs30.Reason ?? "",
                        QuantityKind.Velocity, e.X));
            list.Add(new ScenarioSummary(sc.Id, sc.Name, values.ToImmutable()));
        }
        return list.ToImmutable();
    }
}
