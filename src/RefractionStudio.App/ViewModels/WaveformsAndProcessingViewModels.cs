using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefractionStudio.Analysis;
using RefractionStudio.App.Controls;
using RefractionStudio.Core.Data;
using RefractionStudio.Core.Services;
using RefractionStudio.Core.Session;
using RefractionStudio.Domain;
using RefractionStudio.Reporting;
using RefractionStudio.SignalProcessing;

namespace RefractionStudio.App.ViewModels;

/// <summary>Waveform inspection: gathers, trace properties and spectrum.</summary>
public sealed partial class WaveformsPageViewModel : PageViewModel
{
    public WaveformsPageViewModel(StudioContext ctx) : base(ctx)
    {
        Viewer = new GatherViewerViewModel(ctx, null);
        Viewer.Refreshed += (_, _) => UpdateSpectrum();
    }

    public override string Title => "Waveforms";
    public override string HelpTopic => "waveforms";
    public override WorkflowStep Step => WorkflowStep.Waveforms;
    public override string GuidedText => "Inspect each shot. Hover over a trace for its details; click it to edit its properties (gain, polarity, exclusion). Use the mouse wheel to zoom time (Ctrl: distance), drag with the right button to pan and double-click to fit.";

    public GatherViewerViewModel Viewer { get; }

    [ObservableProperty] public partial IPlotAdapter? Spectrum { get; private set; }
    [ObservableProperty] public partial bool ShowSpectrum { get; set; } = true;

    public override void Activate() => Viewer.Reload();
    public override void OnProjectChanged() => Viewer.Reload();
    public override void OnStateChanged(StateChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Previous.Survey, e.Current.Survey)) Viewer.Reload();
        else Viewer.Rebuild();
    }

    public override void OnAnalysisUpdated(AnalysisResult result) => Viewer.Rebuild();

    partial void OnShowSpectrumChanged(bool value) => UpdateSpectrum();

    private void UpdateSpectrum()
    {
        if (!ShowSpectrum || Viewer.Processed is not { } g || Viewer.Interpretation is not { } i)
        {
            Spectrum = null;
            return;
        }
        var model = PlotModels.Spectrum(g, i.Pipeline, i.Display.ShowProcessed);
        Spectrum = model is null ? null : new XyPlot(model);
    }
}

/// <summary>A parameter of a processing step in the editor.</summary>
public sealed partial class StepParameter(string key, string label, string value, string unit, string? tip) : ViewModelBase
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Unit { get; } = unit;
    public string? Tip { get; } = tip;
    [ObservableProperty] public partial string Value { get; set; } = value;
}

/// <summary>A processing step in the pipeline list.</summary>
public sealed record StepRow(ProcessingStep Step, int Index)
{
    public string Name => Step.DisplayName;
    public bool Enabled => Step.Enabled;
    public string Number => (Index + 1).ToString(CultureInfo.InvariantCulture);
}

/// <summary>A step type that can be added.</summary>
public sealed record StepTemplate(string Name, string HelpTopic, Func<ProcessingStep> Create);

/// <summary>A per-shot shot-time row.</summary>
public sealed partial class ShotTimeRow(ShotGather g, ShotTimeEntry? entry, Analysis.TravelTimes.ShotTimeInfo? info) : ViewModelBase
{
    public ShotGather Gather { get; } = g;
    public string Label => Gather.Label;
    public string ReferencePick => Entry?.ReferencePick is { } p ? $"{p.Time * 1000:0.000} ms ({(p.IsManual ? "manual" : "automatic")})" : "—";
    public string Correction => info?.Correction is { HasValue: true } c ? $"{c.Value * 1000:+0.000;-0.000;0.000} ms" : info?.Correction.Reason ?? "—";
    [ObservableProperty] public partial string Manual { get; set; } = entry?.ManualCorrection is { } m ? (m * 1000).ToString("0.###", CultureInfo.InvariantCulture) : "";
    [ObservableProperty] public partial string ReferenceMs { get; set; } = entry?.ReferencePick is { } r ? (r.Time * 1000).ToString("0.###", CultureInfo.InvariantCulture) : "";
    public ShotTimeEntry? Entry { get; } = entry;
}

/// <summary>Processing pipeline, spectrum and shot-time correction.</summary>
public sealed partial class ProcessingPageViewModel : PageViewModel
{
    public ProcessingPageViewModel(StudioContext ctx) : base(ctx)
    {
        Viewer = new GatherViewerViewModel(ctx, null);
        Viewer.Refreshed += (_, _) => LoadLine();
        Templates =
        [
            new("Remove DC offset (demean)", "processing.removeMean", () => new RemoveMeanStep(StepId.New(), true)),
            new("Linear detrend", "processing.detrend", () => new DetrendStep(StepId.New(), true)),
            new("Taper", "processing.taper", () => new TaperStep(StepId.New(), true, 0.05, TaperShape.Hann)),
            new("High-pass (Butterworth)", "processing.highPass", () => new HighPassStep(StepId.New(), true, 10, 2, false)),
            new("Low-pass (Butterworth)", "processing.lowPass", () => new LowPassStep(StepId.New(), true, 250, 4, true)),
            new("Band-pass (Butterworth)", "processing.bandPass", () => new BandPassStep(StepId.New(), true, 10, 250, 2, false)),
            new("Notch (power line)", "processing.notch", () => new NotchStep(StepId.New(), true, 50, 30, 3)),
            new("Normalize", "processing.normalize", () => new NormalizeStep(StepId.New(), true, NormalizeMode.Peak)),
            new("Trace balance", "processing.balance", () => new BalanceStep(StepId.New(), true, 0, 0.1)),
            new("AGC (display aid)", "processing.agc", () => new AgcStep(StepId.New(), true, 0.05)),
            new("Polarity reversal", "processing.polarity", () => new PolarityStep(StepId.New(), true)),
            new("Time shift", "processing.timeShift", () => new TimeShiftStep(StepId.New(), true, 0)),
            new("Resample", "processing.resample", () => new ResampleStep(StepId.New(), true, 0.00025)),
        ];
        SelectedTemplate = Templates[3];
    }

    public override string Title => "Processing";
    public override WorkflowStep Step => WorkflowStep.Processing;
    public override string HelpTopic => "processing.highPass";
    public override string GuidedText => "Processing is applied to copies of the traces in the order listed; the raw data never change. Recommended: remove the DC offset and, if surface waves obscure the first breaks, add a causal high-pass. Inspect the spectrum before choosing filters. Correct trigger-time errors in Shot time.";

    public GatherViewerViewModel Viewer { get; }
    public IReadOnlyList<StepTemplate> Templates { get; }
    public ObservableCollection<StepRow> Steps { get; } = [];
    public ObservableCollection<StepParameter> Parameters { get; } = [];
    public ObservableCollection<string> Advice { get; } = [];
    public ObservableCollection<ShotTimeRow> ShotTimes { get; } = [];
    public IReadOnlyList<ShotTimeMethod> ShotTimeMethods { get; } = Enum.GetValues<ShotTimeMethod>();

    [ObservableProperty] public partial StepTemplate? SelectedTemplate { get; set; }
    [ObservableProperty] public partial StepRow? SelectedStep { get; set; }
    [ObservableProperty] public partial string StepHelp { get; private set; } = "";
    [ObservableProperty] public partial IPlotAdapter? Spectrum { get; private set; }
    [ObservableProperty] public partial string SuggestedBand { get; private set; } = "";
    [ObservableProperty] public partial ShotTimeMethod ShotTimeMethod { get; set; }
    [ObservableProperty] public partial string ReferenceVelocity { get; set; } = "";
    [ObservableProperty] public partial bool HasLine { get; private set; }

    private (double Low, double High)? _suggested;
    private bool _loading;

    public override void Activate() => Viewer.Reload();
    public override void OnProjectChanged() => Viewer.Reload();
    public override void OnStateChanged(StateChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Previous.Survey, e.Current.Survey)) Viewer.Reload();
        else Viewer.Rebuild();
    }

    public override void OnAnalysisUpdated(AnalysisResult result) => LoadShotTimes();

    private LineId? LineId => Viewer.Line?.Id;

    private void LoadLine()
    {
        _loading = true;
        var interp = Viewer.Interpretation;
        HasLine = interp is not null;
        var selectedId = SelectedStep?.Step.Id;
        Steps.Clear();
        Advice.Clear();
        if (interp is not null)
        {
            for (var i = 0; i < interp.Pipeline.Steps.Length; i++) Steps.Add(new StepRow(interp.Pipeline.Steps[i], i));
            if (Viewer.Processed?.Traces.FirstOrDefault() is { } t)
                foreach (var p in PipelineRunner.Validate(interp.Pipeline, t.OriginalSampleInterval)) Advice.Add("✖ " + p);
            foreach (var a in PipelineRunner.Advice(interp.Pipeline)) Advice.Add("▲ " + a);
            ShotTimeMethod = interp.ShotTime.Method;
            ReferenceVelocity = interp.ShotTime.ReferenceVelocity?.ToString("0", CultureInfo.InvariantCulture) ?? "";
        }
        SelectedStep = Steps.FirstOrDefault(s => s.Step.Id == selectedId);
        _loading = false;
        LoadParameters();
        UpdateSpectrum();
        LoadShotTimes();
    }

    private void LoadShotTimes()
    {
        ShotTimes.Clear();
        if (Viewer.Line is not { } line || Viewer.Interpretation is not { } interp) return;
        var infos = Context.LineAnalysis(line.Id)?.Observations.ShotTimes.ToDictionary(s => s.Gather);
        foreach (var g in Context.State!.Survey.Line(line.Id)!.Gathers)
        {
            interp.ShotTimes.TryGetValue(g.Id, out var entry);
            ShotTimeRow row = new(g, entry, infos?.GetValueOrDefault(g.Id));
            ShotTimes.Add(row);
        }
    }

    private void UpdateSpectrum()
    {
        if (Viewer.Processed is not { } g || Viewer.Interpretation is not { } i)
        {
            Spectrum = null;
            return;
        }
        var model = PlotModels.Spectrum(g, i.Pipeline, processed: false);
        Spectrum = model is null ? null : new XyPlot(model with { Title = "Spectrum of the original traces, with the filter response" });
        var traces = g.Traces.Where(t => !t.Excluded).Select(t => t.Original).Where(s => s.Length >= 8).ToList();
        if (traces.Count > 0)
        {
            var dt = g.Traces[0].OriginalSampleInterval;
            var spec = SpectrumAnalysis.Average(traces, dt);
            _suggested = SpectrumAnalysis.SuggestBandPass(spec, dt);
            SuggestedBand = $"Dominant {spec.DominantFrequency:0} Hz; −20 dB band {spec.BandLow:0}–{spec.BandHigh:0} Hz. Suggested band-pass: {_suggested.Value.Low:0}–{_suggested.Value.High:0} Hz.";
        }
    }

    partial void OnSelectedStepChanged(StepRow? value)
    {
        if (!_loading) LoadParameters();
    }

    partial void OnSelectedTemplateChanged(StepTemplate? value)
    {
        if (value is not null && Infrastructure.HelpCatalog.Get(value.HelpTopic) is { } t)
            StepHelp = $"{t.Title}: {t.WhatItDoes}\nWhen to use: {t.WhenToUse}\nWhen not to: {t.WhenNotToUse}";
    }

    private void LoadParameters()
    {
        Parameters.Clear();
        if (SelectedStep?.Step is not { } step) return;
        string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        switch (step)
        {
            case TaperStep t:
                Parameters.Add(new("fraction", "Taper length", F(t.Fraction * 100), "% each end", "0–50 %"));
                Parameters.Add(new("shape", "Shape (Hann / Linear)", t.Shape.ToString(), "", null));
                break;
            case HighPassStep h:
                Parameters.Add(new("corner", "Corner frequency", F(h.CornerHz), "Hz", null));
                Parameters.Add(new("order", "Order", h.Order.ToString(CultureInfo.InvariantCulture), "", "1–10; higher = steeper"));
                Parameters.Add(new("zero", "Zero-phase (yes / no)", h.ZeroPhase ? "yes" : "no", "", "Zero-phase can make first breaks early"));
                break;
            case LowPassStep l:
                Parameters.Add(new("corner", "Corner frequency", F(l.CornerHz), "Hz", null));
                Parameters.Add(new("order", "Order", l.Order.ToString(CultureInfo.InvariantCulture), "", "1–10"));
                Parameters.Add(new("zero", "Zero-phase (yes / no)", l.ZeroPhase ? "yes" : "no", "", "Causal low-pass delays first breaks"));
                break;
            case BandPassStep b:
                Parameters.Add(new("low", "Low corner", F(b.LowHz), "Hz", null));
                Parameters.Add(new("high", "High corner", F(b.HighHz), "Hz", null));
                Parameters.Add(new("order", "Order", b.Order.ToString(CultureInfo.InvariantCulture), "", "1–10"));
                Parameters.Add(new("zero", "Zero-phase (yes / no)", b.ZeroPhase ? "yes" : "no", "", null));
                break;
            case NotchStep n:
                Parameters.Add(new("frequency", "Frequency", F(n.FrequencyHz), "Hz", "50 or 60 Hz power lines"));
                Parameters.Add(new("q", "Quality factor Q", F(n.Q), "", "Bandwidth = f / Q"));
                Parameters.Add(new("harmonics", "Harmonics", n.Harmonics.ToString(CultureInfo.InvariantCulture), "", "1 = fundamental only"));
                break;
            case NormalizeStep nm:
                Parameters.Add(new("mode", "Mode (Peak / Rms)", nm.Mode.ToString(), "", null));
                break;
            case BalanceStep bal:
                Parameters.Add(new("start", "Window start", F(bal.WindowStart * 1000), "ms", null));
                Parameters.Add(new("end", "Window end", F(bal.WindowEnd * 1000), "ms", null));
                break;
            case AgcStep a:
                Parameters.Add(new("window", "Window", F(a.WindowSeconds * 1000), "ms", null));
                break;
            case TimeShiftStep ts:
                Parameters.Add(new("shift", "Shift", F(ts.Seconds * 1000), "ms", "Positive = later"));
                break;
            case ResampleStep r:
                Parameters.Add(new("interval", "New sample interval", F(r.NewSampleInterval * 1000), "ms", null));
                break;
        }
    }

    [RelayCommand]
    private void AddStep()
    {
        if (LineId is not { } l || SelectedTemplate is null) return;
        var step = SelectedTemplate.Create();
        if (step is BandPassStep && _suggested is { } s) step = new BandPassStep(step.Id, true, Math.Round(s.Low, 1), Math.Round(s.High), 2, false);
        Context.Edit(Edits.AddStep(l, step));
    }

    [RelayCommand]
    private void UseSuggestedBand()
    {
        if (LineId is not { } l || _suggested is not { } s) return;
        Context.Edit(Edits.AddStep(l, new BandPassStep(StepId.New(), true, Math.Round(s.Low, 1), Math.Round(s.High), 2, false)));
    }

    [RelayCommand]
    private void ApplyParameters()
    {
        if (LineId is not { } l || SelectedStep?.Step is not { } step) return;
        double D(string key, double scale = 1)
        {
            var text = Parameters.First(p => p.Key == key).Value;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v * scale : throw new FormatException($"“{text}” is not a number.");
        }
        string S(string key) => Parameters.First(p => p.Key == key).Value.Trim();
        bool B(string key) => S(key).ToLowerInvariant() is "yes" or "true" or "y" or "1";
        try
        {
            ProcessingStep updated = step switch
            {
                TaperStep t => t with { Fraction = D("fraction", 0.01), Shape = Enum.Parse<TaperShape>(S("shape"), true) },
                HighPassStep h => h with { CornerHz = D("corner"), Order = (int)D("order"), ZeroPhase = B("zero") },
                LowPassStep lp => lp with { CornerHz = D("corner"), Order = (int)D("order"), ZeroPhase = B("zero") },
                BandPassStep b => b with { LowHz = D("low"), HighHz = D("high"), Order = (int)D("order"), ZeroPhase = B("zero") },
                NotchStep n => n with { FrequencyHz = D("frequency"), Q = D("q"), Harmonics = (int)D("harmonics") },
                NormalizeStep nm => nm with { Mode = Enum.Parse<NormalizeMode>(S("mode"), true) },
                BalanceStep bal => bal with { WindowStart = D("start", 0.001), WindowEnd = D("end", 0.001) },
                AgcStep a => a with { WindowSeconds = D("window", 0.001) },
                TimeShiftStep ts => ts with { Seconds = D("shift", 0.001) },
                ResampleStep r => r with { NewSampleInterval = D("interval", 0.001) },
                _ => step,
            };
            var dt = Viewer.Processed?.Traces.FirstOrDefault()?.OriginalSampleInterval ?? 0.001;
            var problems = PipelineRunner.Validate(new ProcessingPipeline([updated]), dt);
            if (!problems.IsEmpty)
            {
                _ = Context.Dialogs.ShowAsync("Invalid parameters", string.Join("\n", problems), Services.MessageKind.Warning);
                return;
            }
            Context.Edit(Edits.UpdateStep(l, updated));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _ = Context.Dialogs.ShowAsync("Invalid parameters", ex.Message, Services.MessageKind.Warning);
        }
    }

    [RelayCommand]
    private void ToggleStep(StepRow? row)
    {
        if (LineId is { } l && row is not null) Context.Edit(Edits.SetStepEnabled(l, row.Step, !row.Step.Enabled));
    }

    [RelayCommand]
    private void MoveUp(StepRow? row)
    {
        if (LineId is { } l && row is not null && row.Index > 0) Context.Edit(Edits.MoveStep(l, row.Step.Id, row.Index - 1, row.Name));
    }

    [RelayCommand]
    private void MoveDown(StepRow? row)
    {
        if (LineId is { } l && row is not null && row.Index < Steps.Count - 1) Context.Edit(Edits.MoveStep(l, row.Step.Id, row.Index + 1, row.Name));
    }

    [RelayCommand]
    private void RemoveStep(StepRow? row)
    {
        if (LineId is { } l && row is not null) Context.Edit(Edits.RemoveStep(l, row.Step.Id, row.Name));
    }

    [RelayCommand]
    private async Task CopyPipelineToAllLinesAsync()
    {
        if (LineId is not { } l || Viewer.Interpretation is not { } interp || Context.State is not { } state) return;
        var others = state.Survey.Lines.Where(x => x.Id != l).ToList();
        if (others.Count == 0) return;
        if (await Context.Dialogs.AskAsync("Copy processing", $"Replace the processing of {others.Count} other line(s) with this pipeline?", Services.MessageKind.Question, "Copy", "Cancel") != 0) return;
        using (Context.BeginGesture("Copy processing to all lines"))
        {
            foreach (var o in others)
                Context.Edit(Edits.SetPipeline(o.Id, new ProcessingPipeline([.. interp.Pipeline.Steps.Select(s => s with { Id = StepId.New() })]), $"Copy processing to {o.Name}"));
        }
    }

    // ───────────────────────────── Shot time ─────────────────────────────

    partial void OnShotTimeMethodChanged(ShotTimeMethod value) => ApplyShotTimeSettings();

    [RelayCommand]
    private void ApplyShotTimeSettings()
    {
        if (_loading || LineId is not { } l || Viewer.Interpretation is not { } interp) return;
        double? v = double.TryParse(ReferenceVelocity, NumberStyles.Float, CultureInfo.InvariantCulture, out var rv) && rv > 0 ? rv : null;
        var settings = new ShotTimeSettings(ShotTimeMethod, v);
        if (settings != interp.ShotTime) Context.Edit(Edits.SetShotTimeSettings(l, settings));
    }

    [RelayCommand]
    private void ApplyShotTimeRow(ShotTimeRow? row)
    {
        if (row is null || LineId is not { } l) return;
        double? manual = double.TryParse(row.Manual, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m / 1000 : null;
        ArrivalPick? reference = row.Entry?.ReferencePick;
        if (double.TryParse(row.ReferenceMs, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && Math.Abs(r / 1000 - (reference?.Time ?? double.NaN)) > 1e-9)
            reference = new ArrivalPick(r / 1000, ArrivalOrigin.Manual);
        else if (string.IsNullOrWhiteSpace(row.ReferenceMs)) reference = null;
        Context.Edit(Edits.SetShotTimeEntry(l, row.Gather.Id, new ShotTimeEntry(manual, reference, row.Entry?.Note), $"Shot time of {row.Label}"));
    }

    [RelayCommand]
    private void AutoPickReferences()
    {
        if (LineId is not { } l || !Context.License.Status.CanEdit) return;
        var n = Context.Workspace.AutoPickReferences(l, Viewer.Interpretation?.Picker ?? new PickerSettings());
        if (n == 0) _ = Context.Dialogs.ShowAsync("No reference geophone", "No channel has the role “Reference” in this line's spread, or no onset was found. Set the channel role in Geometry.", Services.MessageKind.Information);
    }
}
