using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefractionStudio.Analysis;
using RefractionStudio.Analysis.Layered;
using RefractionStudio.Analysis.Quality;
using RefractionStudio.Analysis.Tomography;
using RefractionStudio.Analysis.TravelTimes;
using RefractionStudio.App.Controls;
using RefractionStudio.Core.Services;
using RefractionStudio.Core.Session;
using RefractionStudio.Domain;
using RefractionStudio.Reporting;
using RefractionStudio.Visualization;

namespace RefractionStudio.App.ViewModels;

/// <summary>A QC finding row (icon + level text + message).</summary>
public sealed record QcRow(QcFlag Flag, string LineName)
{
    public string Icon => Flag.Level switch { QcLevel.Problem => "Icon.Error", QcLevel.Review => "Icon.Warning", _ => "Icon.Check" };
    public string Level => ReportTables.LevelText(Flag.Level);
    public string Title => Flag.Title;
    public string Message => Flag.Message;
}

/// <summary>A QC card with its status icon and text.</summary>
public sealed record QcCardRow(QcCard Card)
{
    public string Icon => Card.Level switch { QcLevel.Problem => "Icon.Error", QcLevel.Review => "Icon.Warning", _ => "Icon.Check" };
    public string LevelText => ReportTables.LevelText(Card.Level);
    public string Classes => Card.Level switch { QcLevel.Problem => "problem", QcLevel.Review => "review", _ => "ok" };
}

/// <summary>Quality-control dashboard.</summary>
public sealed partial class QcPageViewModel(StudioContext ctx) : PageViewModel(ctx)
{
    public override string Title => "Quality control";
    public override WorkflowStep Step => WorkflowStep.QualityControl;
    public override string HelpTopic => "qc.overview";
    public override string GuidedText => "Automatic checks of picks, timing, geometry and models. Each finding explains what was found and what to check; click a finding to go to the trace or shot. The summary is an aid to review and does not imply compliance with any standard.";

    public ObservableCollection<QcCardRow> Cards { get; } = [];
    public ObservableCollection<QcRow> Findings { get; } = [];
    public IReadOnlyList<string> Filters { get; } = ["All findings", "Likely problems", "To review"];

    [ObservableProperty] public partial string Verdict { get; private set; } = "Incomplete";
    [ObservableProperty] public partial string VerdictReasons { get; private set; } = "";
    [ObservableProperty] public partial string VerdictClass { get; private set; } = "review";
    [ObservableProperty] public partial string VerdictIcon { get; private set; } = "Icon.Warning";
    [ObservableProperty] public partial string Filter { get; set; } = "All findings";
    [ObservableProperty] public partial QcRow? Selected { get; set; }

    public override void Activate()
    {
        if (Context.Result is { } r) Load(r);
    }

    public override void OnAnalysisUpdated(AnalysisResult result) => Load(result);

    partial void OnFilterChanged(string value)
    {
        if (Context.Result is { } r) Load(r);
    }

    private void Load(AnalysisResult r)
    {
        Verdict = ReportGenerator.VerdictText(r.Qc.Verdict);
        VerdictReasons = string.Join(" ", r.Qc.Reasons);
        VerdictClass = r.Qc.Verdict switch { QualityVerdict.Good => "ok", QualityVerdict.ReviewRecommended => "review", _ => "problem" };
        VerdictIcon = r.Qc.Verdict switch { QualityVerdict.Good => "Icon.Check", QualityVerdict.ReviewRecommended => "Icon.Warning", _ => "Icon.Info" };
        Cards.Clear();
        foreach (var c in r.Qc.Cards) Cards.Add(new QcCardRow(c));
        Findings.Clear();
        var names = r.Lines.ToDictionary(l => l.Line.Id, l => l.Line.Name);
        foreach (var f in r.Qc.Flags.Where(f => Filter switch { "Likely problems" => f.Level == QcLevel.Problem, "To review" => f.Level == QcLevel.Review, _ => true })
                     .OrderByDescending(f => f.Level).Take(1500))
            Findings.Add(new QcRow(f, f.Line is { } l ? names.GetValueOrDefault(l, "") : ""));
    }

    /// <summary>Shows the trace, shot or model a finding refers to.</summary>
    [RelayCommand]
    private void GoTo(QcRow? value)
    {
        value ??= Selected;
        if (value?.Flag is not { } f || f.Line is not { } line) return;
        var wave = Context.Line(line)?.WaveType;
        Context.Selection.Select(line, f.Gather ?? f.Key?.Gather, f.Key?.Channel);
        if (f.Key is not null) Context.Navigate?.Invoke(wave == WaveType.SH ? WorkflowStep.SPicks : WorkflowStep.PPicks);
        else if (f.Code.StartsWith("tomography", StringComparison.Ordinal) || f.Code.StartsWith("layered", StringComparison.Ordinal)) Context.Navigate?.Invoke(WorkflowStep.VelocityModel);
        else if (f.Code is "shot-time") Context.Navigate?.Invoke(WorkflowStep.Processing);
        else Context.Navigate?.Invoke(WorkflowStep.Waveforms);
    }
}

/// <summary>Which section is displayed on the velocity-model page.</summary>
public enum SectionChoice
{
    Layered,
    Tomography,
    RayCoverage,
    StartingModel,
}

/// <summary>Layer assignment, layered methods and tomography.</summary>
public sealed partial class VelocityModelPageViewModel(StudioContext ctx) : PageViewModel(ctx)
{
    public override string Title => "Velocity model";
    public override WorkflowStep Step => WorkflowStep.VelocityModel;
    public override string HelpTopic => "layers.assignment";
    public override string GuidedText => "1 · Choose the number of layers and press Suggest; check the crossovers on the travel-time plot (drag the dashed handles of the selected shot). 2 · Choose a method — delay-time is recommended when a shot lies inside the spread. 3 · Run tomography for a gradational model and compare.";

    public ObservableCollection<SurveyLine> Lines { get; } = [];
    public ObservableCollection<ShotGather> Shots { get; } = [];
    public ObservableCollection<LayerVelocity> Layers { get; } = [];
    public ObservableCollection<string> LayerTexts { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public IReadOnlyList<LayeredMethod> Methods { get; } = Enum.GetValues<LayeredMethod>();
    public IReadOnlyList<RegressionMode> Regressions { get; } = Enum.GetValues<RegressionMode>();
    public IReadOnlyList<SectionChoice> Sections { get; } = Enum.GetValues<SectionChoice>();
    public IReadOnlyList<StartingModelKind> StartingModels { get; } = Enum.GetValues<StartingModelKind>();
    public IReadOnlyList<int> LayerCounts { get; } = [2, 3, 4];

    [ObservableProperty] public partial SurveyLine? Line { get; set; }
    [ObservableProperty] public partial ShotGather? SelectedShot { get; set; }
    [ObservableProperty] public partial int LayerCount { get; set; } = 2;
    [ObservableProperty] public partial LayeredMethod Method { get; set; }
    [ObservableProperty] public partial ShotGather? ForwardShot { get; set; }
    [ObservableProperty] public partial ShotGather? ReverseShot { get; set; }
    [ObservableProperty] public partial double GrmXy { get; set; }
    [ObservableProperty] public partial int VelocitySegments { get; set; } = 1;
    [ObservableProperty] public partial double VelocitySmoothing { get; set; } = 1;
    [ObservableProperty] public partial string FixedVelocities { get; set; } = "";
    [ObservableProperty] public partial RegressionMode Regression { get; set; }
    [ObservableProperty] public partial IPlotAdapter? TravelTimes { get; private set; }
    [ObservableProperty] public partial IPlotAdapter? Section { get; private set; }
    [ObservableProperty] public partial IPlotAdapter? SecondaryPlot { get; private set; }
    [ObservableProperty] public partial SectionChoice SectionChoice { get; set; } = SectionChoice.Layered;
    [ObservableProperty] public partial bool ShowRays { get; set; }
    [ObservableProperty] public partial double VerticalExaggeration { get; set; } = 2;
    [ObservableProperty] public partial string LayeredStatus { get; private set; } = "";
    [ObservableProperty] public partial string TomographyStatus { get; private set; } = "";
    [ObservableProperty] public partial string ForwardCheck { get; private set; } = "";
    [ObservableProperty] public partial string SelectedPointText { get; private set; } = "";
    [ObservableProperty] public partial string? SelectedPointId { get; private set; }
    [ObservableProperty] public partial bool HasLine { get; private set; }

    // Tomography settings.
    [ObservableProperty] public partial double CellSize { get; set; } = 1;
    [ObservableProperty] public partial double MaxDepth { get; set; } = 20;
    [ObservableProperty] public partial bool AutoGrid { get; set; } = true;
    [ObservableProperty] public partial StartingModelKind StartingModel { get; set; }
    [ObservableProperty] public partial double Smoothing { get; set; } = 30;
    [ObservableProperty] public partial double SmoothingRatio { get; set; } = 2;
    [ObservableProperty] public partial int Iterations { get; set; } = 10;
    [ObservableProperty] public partial double PickErrorMs { get; set; } = 0.5;
    [ObservableProperty] public partial double PickErrorPercent { get; set; } = 2;
    [ObservableProperty] public partial bool Robust { get; set; } = true;
    [ObservableProperty] public partial double MinVelocity { get; set; } = 100;
    [ObservableProperty] public partial double MaxVelocity { get; set; } = 7000;

    private bool _loading;
    private TravelTimePlot? _ttPlot;

    public override void Activate() => Reload();
    public override void OnProjectChanged() => Reload();

    public override void OnStateChanged(StateChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Previous.Survey, e.Current.Survey)) Reload();
    }

    public override void OnAnalysisUpdated(AnalysisResult result) => Refresh();

    private void Reload()
    {
        _loading = true;
        var id = Line?.Id ?? Context.Selection.Line;
        Lines.Clear();
        foreach (var l in Context.State?.Survey.Lines ?? []) Lines.Add(l);
        Line = Lines.FirstOrDefault(l => l.Id == id) ?? Lines.FirstOrDefault();
        HasLine = Line is not null;
        _loading = false;
        LoadSettings();
        Refresh();
    }

    partial void OnLineChanged(SurveyLine? value)
    {
        if (_loading) return;
        if (value is not null) Context.Selection.Line = value.Id;
        LoadSettings();
        Refresh();
    }

    partial void OnSelectedShotChanged(ShotGather? value)
    {
        if (_loading) return;
        if (value is not null) Context.Selection.Gather = value.Id;
        Refresh();
    }

    partial void OnSectionChoiceChanged(SectionChoice value) => Refresh();
    partial void OnShowRaysChanged(bool value) => Refresh();
    partial void OnVerticalExaggerationChanged(double value) => Refresh();

    private void LoadSettings()
    {
        if (Line is null || Context.State is not { } state) return;
        _loading = true;
        var interp = state.Interpretation(Line.Id);
        var line = state.Survey.Line(Line.Id)!;
        Shots.Clear();
        foreach (var g in line.Gathers.Where(g => double.IsFinite(g.ShotX)).OrderBy(g => g.ShotX)) Shots.Add(g);
        SelectedShot = Shots.FirstOrDefault(s => s.Id == Context.Selection.Gather) ?? Shots.FirstOrDefault();
        var ls = interp.Layered;
        LayerCount = Math.Clamp(interp.Segments.LayerCount, 2, 4);
        Method = ls.Method;
        ForwardShot = Shots.FirstOrDefault(s => s.Id == ls.ForwardShot);
        ReverseShot = Shots.FirstOrDefault(s => s.Id == ls.ReverseShot);
        GrmXy = ls.GrmXy;
        VelocitySegments = ls.RefractorVelocitySegments;
        VelocitySmoothing = ls.VelocitySmoothing;
        FixedVelocities = ls.FixedVelocities.IsDefaultOrEmpty ? "" : string.Join(", ", ls.FixedVelocities.Select(v => v?.ToString("0", CultureInfo.InvariantCulture) ?? "-"));
        Regression = ls.RegressionMode;
        var t = interp.Tomography;
        AutoGrid = t.CellWidth is null && t.MaxDepth is null;
        var spacing = line.Spreads.Select(s => s.Spacing).DefaultIfEmpty(2).Min();
        CellSize = t.CellWidth ?? spacing / 2;
        MaxDepth = t.MaxDepth ?? 20;
        StartingModel = t.StartingModel;
        Smoothing = t.Smoothing;
        SmoothingRatio = t.HorizontalSmoothingRatio;
        Iterations = t.MaxIterations;
        PickErrorMs = t.PickErrorAbsolute * 1000;
        PickErrorPercent = t.PickErrorRelative * 100;
        Robust = t.RobustDataWeighting;
        MinVelocity = t.MinVelocity;
        MaxVelocity = t.MaxVelocity;
        _loading = false;
    }

    private void Refresh()
    {
        Layers.Clear();
        LayerTexts.Clear();
        Warnings.Clear();
        var a = Context.LineAnalysis(Line?.Id);
        if (a is null || Context.State is not { } state)
        {
            TravelTimes = null;
            Section = null;
            LayeredStatus = Line is null ? "Import data to build a velocity model." : "";
            return;
        }
        var interp = state.Interpretation(a.Line.Id);
        // Travel-time plot with crossover handles of the selected shot.
        var shotIndex = a.Line.Gathers.OrderBy(g => g.ShotX).Select((g, i) => (g.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var handles = new List<CrossoverHandle>();
        if (SelectedShot is { } shot && interp.Segments.Gathers.TryGetValue(shot.Id, out var seg))
        {
            for (var k = 0; k < seg.Left.Length; k++) if (seg.Left[k] > 0) handles.Add(new CrossoverHandle(shot.Id, -1, k, shot.ShotX - seg.Left[k], shotIndex.GetValueOrDefault(shot.Id)));
            for (var k = 0; k < seg.Right.Length; k++) if (seg.Right[k] > 0) handles.Add(new CrossoverHandle(shot.Id, 1, k, shot.ShotX + seg.Right[k], shotIndex.GetValueOrDefault(shot.Id)));
        }
        var selectedKey = SelectedPointId is { } id ? PickKey.Parse(id) : (PickKey?)null;
        _ttPlot = new TravelTimePlot(PlotModels.TravelTimes(a, selectedKey), handles);
        TravelTimes = _ttPlot;

        if (a.Layered.HasValue)
        {
            var m = a.Layered.Value!;
            foreach (var l in m.Layers)
            {
                Layers.Add(l);
                LayerTexts.Add($"V{l.Layer} = {Context.Units.Format(l.Velocity, Domain.Units.QuantityKind.Velocity)}" +
                               (l.StdError is { } se ? $" ± {Context.Units.Format(se, Domain.Units.QuantityKind.Velocity)}" : "") + $"   {l.Basis}");
            }
            foreach (var w in m.Warnings) Warnings.Add(w);
            LayeredStatus = $"{ReportTables.MethodName(m.Method)}: " + string.Join(" · ", m.Refractors.Where(r => !r.Points.IsDefaultOrEmpty)
                .Select(r => $"refractor {r.Refractor} depth {Context.Units.Format(r.Points.Min(p => p.Depth), Domain.Units.QuantityKind.Depth, 1)}–{Context.Units.Format(r.Points.Max(p => p.Depth), Domain.Units.QuantityKind.Depth, 1)}"));
        }
        else
        {
            LayeredStatus = "Not available: " + a.Layered.Reason;
        }
        TomographyStatus = a.Tomography is { } t
            ? $"{t.Iterations.Length - 1} iterations, RMS {t.FinalRms * 1000:0.00} ms, χ² {t.FinalChi2:0.00}. {t.StopReason}" + (a.TomographyStale ? "  ▲ OUT OF DATE — run again." : "") +
              (t.Notes.IsDefaultOrEmpty ? "" : "\n" + string.Join("\n", t.Notes))
            : "Not computed. Set the options and press Run tomography.";
        var choice = SectionChoice;
        var kind = choice switch { SectionChoice.Tomography => SectionKind.Tomography, SectionChoice.RayCoverage => SectionKind.RayCoverage, SectionChoice.StartingModel => SectionKind.StartingModel, _ => SectionKind.Layered };
        var model = PlotModels.Section(a, kind, state.ActiveScenario.Settings, Visualization.Colormap.ByName(Context.Settings.Current.Colormap), ShowRays, Math.Clamp(VerticalExaggeration, 0.5, 10));
        Section = model is null ? null : new SectionPlot(model);
        SecondaryPlot = a.Tomography is { } tomo && choice != SectionChoice.Layered ? new XyPlot(PlotModels.Convergence(tomo)) : SecondaryPlot is XyPlot { Model.Title: var title } xy && title?.StartsWith("GRM", StringComparison.Ordinal) == true ? xy : null;
    }

    // ───────────────────────────── Layer assignment ─────────────────────────────

    [RelayCommand]
    private void SuggestLayers()
    {
        if (Line is null || Context.State is not { } state) return;
        var interp = state.Interpretation(Line.Id);
        var cleared = interp with { Segments = interp.Segments with { LayerCount = LayerCount, Gathers = interp.Segments.Gathers.Where(g => g.Value.UserEdited).ToImmutableDictionary() } };
        var obs = ObservationBuilder.Build(state.Survey.Line(Line.Id)!, cleared);
        var suggestion = new SegmentAssigner().Suggest(obs, LayerCount);
        Context.Edit(Edits.SetSegments(Line.Id, suggestion, $"Suggest layer assignment ({LayerCount} layers)"));
    }

    [RelayCommand]
    private async Task ResetLayersAsync()
    {
        if (Line is null || Context.State is not { } state) return;
        if (await Context.Dialogs.AskAsync("Reset layer assignment", "Discard all edited crossovers and layer overrides of this line and suggest a new assignment?", Services.MessageKind.Question, "Reset", "Cancel") != 0) return;
        var interp = state.Interpretation(Line.Id);
        var cleared = interp with { Segments = new SegmentAssignment(LayerCount, ImmutableDictionary<GatherId, GatherSegments>.Empty, ImmutableDictionary<PickKey, int>.Empty) };
        var suggestion = new SegmentAssigner().Suggest(ObservationBuilder.Build(state.Survey.Line(Line.Id)!, cleared), LayerCount);
        Context.Edit(Edits.SetSegments(Line.Id, suggestion, $"Reset layer assignment ({LayerCount} layers)"));
    }

    /// <summary>Moves a crossover handle to a new position (called during a drag gesture).</summary>
    public void MoveCrossover(CrossoverHandle handle, double x)
    {
        if (Line is null || Context.State is not { } state || !Context.License.Status.CanEdit) return;
        var gather = state.Survey.Line(Line.Id)?.Gather(handle.Gather);
        if (gather is null) return;
        var interp = state.Interpretation(Line.Id);
        if (!interp.Segments.Gathers.TryGetValue(handle.Gather, out var seg)) return;
        var offset = Math.Max(0.01, handle.Side < 0 ? gather.ShotX - x : x - gather.ShotX);
        var list = (handle.Side < 0 ? seg.Left : seg.Right).ToArray();
        if (handle.Index >= list.Length) return;
        list[handle.Index] = offset;
        // Keep crossovers ordered.
        for (var k = handle.Index - 1; k >= 0; k--) list[k] = Math.Min(list[k], list[k + 1]);
        for (var k = handle.Index + 1; k < list.Length; k++) list[k] = Math.Max(list[k], list[k - 1]);
        var updated = handle.Side < 0 ? seg with { Left = [.. list] } : seg with { Right = [.. list] };
        Context.Workspace.Session?.Execute(Edits.SetGatherCrossovers(Line.Id, handle.Gather, updated));
    }

    public IDisposable? BeginCrossoverDrag() => Context.BeginGesture("Move crossover");

    public TravelTimePlot? TravelTimePlot => _ttPlot;

    public void SelectPoint(TravelTimePoint? point)
    {
        SelectedPointId = point?.Id;
        if (point is null || Line is null)
        {
            SelectedPointText = "";
            Refresh();
            return;
        }
        var key = PickKey.Parse(point.Id);
        Context.Selection.Select(Line.Id, key.Gather, key.Channel);
        var shot = Shots.FirstOrDefault(s => s.Id == key.Gather);
        if (shot is not null && SelectedShot?.Id != shot.Id)
        {
            _loading = true;
            SelectedShot = shot;
            _loading = false;
        }
        var o = Context.LineAnalysis(Line.Id)?.Observations.All.FirstOrDefault(x => x.Key == key);
        SelectedPointText = o is null ? "" : $"Shot {shot?.Label}, channel {o.Channel}: x {o.ReceiverX:0.##} m, offset {o.Offset:0.##} m, t {o.CorrectedTime * 1000:0.00} ms, layer {o.Layer}" +
                                             (o.Included ? "" : " (excluded)");
        Refresh();
    }

    [RelayCommand]
    private void AssignSelectedToLayer(string? layer)
    {
        if (Line is null || SelectedPointId is null || !int.TryParse(layer, out var l)) return;
        Context.Edit(Edits.SetPickLayer(Line.Id, PickKey.Parse(SelectedPointId), l));
    }

    [RelayCommand]
    private void ClearLayerOverride()
    {
        if (Line is null || SelectedPointId is null) return;
        Context.Edit(Edits.SetPickLayer(Line.Id, PickKey.Parse(SelectedPointId), null));
    }

    [RelayCommand]
    private void ToggleSelectedIncluded()
    {
        if (Line is null || SelectedPointId is null || Context.State is not { } state) return;
        var key = PickKey.Parse(SelectedPointId);
        if (state.Interpretation(Line.Id).PickOf(key) is { } p) Context.Edit(Edits.SetPickIncluded(Line.Id, key, !p.Included));
    }

    [RelayCommand]
    private void OpenSelectedTrace()
    {
        if (Line is null || SelectedPointId is null) return;
        Context.Navigate?.Invoke(Line.WaveType == WaveType.SH ? WorkflowStep.SPicks : WorkflowStep.PPicks);
    }

    // ───────────────────────────── Layered method ─────────────────────────────

    [RelayCommand]
    private void ApplyMethod()
    {
        if (Line is null) return;
        ImmutableArray<double?> fixedV = [];
        if (!string.IsNullOrWhiteSpace(FixedVelocities))
        {
            var parts = FixedVelocities.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
            var list = new List<double?>();
            foreach (var p in parts)
            {
                if (p is "-" or "?") list.Add(null);
                else if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0) list.Add(v);
                else
                {
                    _ = Context.Dialogs.ShowAsync("Fixed velocities", "Enter velocities in m/s separated by commas, using “-” for layers to estimate (e.g. “450, -, -”).", Services.MessageKind.Warning);
                    return;
                }
            }
            fixedV = [.. list];
        }
        var settings = new LayeredSettings(Method, ForwardShot?.Id, ReverseShot?.Id, Math.Max(0, GrmXy), Math.Clamp(VelocitySegments, 1, 20), Math.Max(0, VelocitySmoothing),
            null, fixedV, 5, Regression);
        using (Context.BeginGesture("Layered interpretation settings"))
        {
            if (Context.State?.Interpretation(Line.Id).Segments.LayerCount != LayerCount)
            {
                var interp = Context.State!.Interpretation(Line.Id);
                Context.Edit(Edits.SetSegments(Line.Id, interp.Segments with { LayerCount = LayerCount }, $"{LayerCount} layers"));
            }
            Context.Edit(Edits.SetLayeredSettings(Line.Id, settings));
        }
    }

    [RelayCommand]
    private void AnalyzeXy()
    {
        if (Line is null || Context.LineAnalysis(Line.Id) is not { } a) return;
        var settings = new LayeredSettings(LayeredMethod.Grm, ForwardShot?.Id, ReverseShot?.Id);
        var spacing = a.Observations.TypicalSpacing;
        var (evaluations, suggested) = GrmMethod.AnalyzeXy(a.Observations, settings, 1, spacing * 4);
        if (evaluations.IsEmpty)
        {
            _ = Context.Dialogs.ShowAsync("XY analysis not possible", "Select a forward and a reverse shot whose refracted arrivals overlap.", Services.MessageKind.Information);
            return;
        }
        SecondaryPlot = new XyPlot(PlotModels.GrmXy(evaluations, suggested));
        if (suggested is { } s) GrmXy = s;
    }

    [RelayCommand]
    private void ForwardModelCheck()
    {
        if (Line is null || Context.LineAnalysis(Line.Id) is not { Layered.HasValue: true } a) return;
        var obs = a.Observations.Included.ToList();
        if (obs.Count == 0) return;
        var xs = obs.Select(o => o.ShotX).Concat(obs.Select(o => o.ReceiverX)).ToList();
        var spacing = a.Observations.TypicalSpacing;
        var depth = Math.Max(5, a.Layered.Value!.Refractors.SelectMany(r => r.Points).Select(p => p.Depth).DefaultIfEmpty(10).Max() * 1.5);
        var grid = ModelGrid.Create(a.Line.Topography, xs.Min(), xs.Max(), spacing / 2, spacing / 2, depth);
        var residuals = TomographyEngine.ForwardModel(TomographyEngine.Rasterize(a.Layered.Value, grid), obs, 4);
        var rms = residuals.Length == 0 ? double.NaN : Math.Sqrt(residuals.Average(r => r.Residual * r.Residual));
        var worst = residuals.OrderByDescending(r => Math.Abs(r.Residual)).Take(3).Select(r => $"{r.Residual * 1000:+0.0;-0.0} ms").ToList();
        ForwardCheck = $"Travel times computed through the layered model reproduce the picks with an RMS of {rms * 1000:0.00} ms ({residuals.Length} picks). Largest residuals: {string.Join(", ", worst)}.";
    }

    // ───────────────────────────── Tomography ─────────────────────────────

    private TomographySettings TomographySettingsFromForm() => new(
        AutoGrid ? null : Math.Max(0.1, CellSize), AutoGrid ? null : Math.Max(0.1, CellSize), AutoGrid ? null : Math.Max(1, MaxDepth), StartingModel, null, null,
        Math.Max(0, Smoothing), 0.8, Math.Max(0.1, SmoothingRatio), Math.Clamp(Iterations, 1, 50), Math.Max(10, MinVelocity), Math.Max(MinVelocity + 10, MaxVelocity), 5,
        Math.Max(0.00001, PickErrorMs / 1000), Math.Max(0, PickErrorPercent / 100), 0, 1, Robust);

    [RelayCommand]
    private async Task RunTomographyAsync()
    {
        if (Line is null) return;
        if (!Context.License.Status.CanEdit)
        {
            await Context.Dialogs.ShowAsync("Inversion not available", Context.License.Status.Message, Services.MessageKind.Warning);
            return;
        }
        var settings = TomographySettingsFromForm();
        var lineId = Line.Id;
        var result = await Context.Busy.RunAsync("Running tomography…", (progress, ct) =>
            Context.Workspace.RunTomographyAsync(lineId, settings, new Progress<TomographyProgress>(p =>
                progress.Report(((double)p.Iteration / Math.Max(1, p.MaxIterations), $"Tomography — iteration {p.Iteration}/{p.MaxIterations}: {p.Stage}" +
                    (p.RmsSeconds is { } r ? $", RMS {r * 1000:0.00} ms" : "")))), ct));
        if (result is null) TomographyStatus = "Cancelled.";
        else if (!result.HasValue) await Context.Dialogs.ShowAsync("Tomography not possible", result.Reason!, Services.MessageKind.Warning);
        else SectionChoice = SectionChoice.Tomography;
    }
}
