using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefractionStudio.Analysis.Picking;
using RefractionStudio.Analysis.Quality;
using RefractionStudio.App.Controls;
using RefractionStudio.Core.Data;
using RefractionStudio.Core.Session;
using RefractionStudio.Domain;
using RefractionStudio.Reporting;
using RefractionStudio.Visualization;

namespace RefractionStudio.App.ViewModels;

/// <summary>Which samples are displayed.</summary>
public enum WaveformView
{
    Processed,
    Original,
    Overlay,
}

/// <summary>Shot-gather viewer shared by the Waveforms, Processing and Picks pages.</summary>
public sealed partial class GatherViewerViewModel : ViewModelBase
{
    private readonly StudioContext _ctx;
    private readonly WaveType? _wave;
    private ProcessedGather? _gather;
    private bool _loading;

    public GatherViewerViewModel(StudioContext ctx, WaveType? wave)
    {
        _ctx = ctx;
        _wave = wave;
    }

    public ObservableCollection<SurveyLine> Lines { get; } = [];
    public ObservableCollection<ShotGather> Gathers { get; } = [];
    public IReadOnlyList<TraceDisplayMode> Modes { get; } = Enum.GetValues<TraceDisplayMode>();
    public IReadOnlyList<DisplayScaling> Scalings { get; } = Enum.GetValues<DisplayScaling>();
    public IReadOnlyList<WaveformView> Views { get; } = Enum.GetValues<WaveformView>();

    [ObservableProperty] public partial SurveyLine? Line { get; set; }
    [ObservableProperty] public partial ShotGather? Gather { get; set; }
    [ObservableProperty] public partial IPlotAdapter? Plot { get; private set; }
    [ObservableProperty] public partial string HoverText { get; private set; } = "";
    [ObservableProperty] public partial string ViewLabel { get; private set; } = "";
    [ObservableProperty] public partial int? SelectedChannel { get; private set; }
    [ObservableProperty] public partial TraceDisplayMode Mode { get; set; }
    [ObservableProperty] public partial DisplayScaling Scaling { get; set; }
    [ObservableProperty] public partial double Gain { get; set; } = 1;
    [ObservableProperty] public partial double Clip { get; set; } = 1.5;
    [ObservableProperty] public partial string ReductionVelocity { get; set; } = "";
    [ObservableProperty] public partial WaveformView WaveformView { get; set; }
    [ObservableProperty] public partial string Problems { get; private set; } = "";
    [ObservableProperty] public partial bool HasGather { get; private set; }
    [ObservableProperty] public partial string? EmptyMessage { get; private set; }

    // Trace inspector.
    [ObservableProperty] public partial string TraceTitle { get; private set; } = "No trace selected";
    [ObservableProperty] public partial string TraceDetails { get; private set; } = "";
    [ObservableProperty] public partial double TraceGain { get; set; } = 1;
    [ObservableProperty] public partial bool TraceInverted { get; set; }
    [ObservableProperty] public partial bool TraceExcluded { get; set; }
    [ObservableProperty] public partial double TraceShiftMs { get; set; }
    [ObservableProperty] public partial string TraceNote { get; set; } = "";
    [ObservableProperty] public partial string PickText { get; private set; } = "";
    [ObservableProperty] public partial bool HasSelection { get; private set; }

    public ProcessedGather? Processed => _gather;

    /// <summary>Raised after the displayed gather or picks changed (the picks page refreshes its table).</summary>
    public event EventHandler? Refreshed;

    public LineInterpretation? Interpretation => Line is { } l ? _ctx.State?.Interpretation(l.Id) : null;

    public void Reload()
    {
        _loading = true;
        var state = _ctx.State;
        var lineId = Line?.Id ?? _ctx.Selection.Line;
        Lines.Clear();
        foreach (var l in state?.Survey.Lines.Where(l => _wave is null || l.WaveType == _wave) ?? []) Lines.Add(l);
        Line = Lines.FirstOrDefault(l => l.Id == lineId) ?? Lines.FirstOrDefault();
        _loading = false;
        LoadGathers();
    }

    partial void OnLineChanged(SurveyLine? value)
    {
        if (_loading) return;
        if (value is not null) _ctx.Selection.Line = value.Id;
        LoadGathers();
    }

    partial void OnGatherChanged(ShotGather? value)
    {
        if (_loading) return;
        if (value is not null) _ctx.Selection.Gather = value.Id;
        SelectedChannel = null;
        Rebuild();
    }

    private void LoadGathers()
    {
        _loading = true;
        var gatherId = Gather?.Id ?? _ctx.Selection.Gather;
        Gathers.Clear();
        if (Line is { } l)
        {
            Line = _ctx.State?.Survey.Line(l.Id) ?? l;
            foreach (var g in Line.Gathers.OrderBy(g => double.IsFinite(g.ShotX) ? g.ShotX : double.MaxValue)) Gathers.Add(g);
        }
        Gather = Gathers.FirstOrDefault(g => g.Id == gatherId) ?? Gathers.FirstOrDefault();
        if (Interpretation is { } i)
        {
            Mode = i.Display.Mode;
            Scaling = i.Display.Scaling;
            Gain = i.Display.Gain;
            Clip = i.Display.Clip;
            ReductionVelocity = i.Display.ReductionVelocity?.ToString("0", CultureInfo.InvariantCulture) ?? "";
            WaveformView = !i.Display.ShowProcessed ? WaveformView.Original : i.Display.OverlayRaw ? WaveformView.Overlay : WaveformView.Processed;
        }
        _loading = false;
        Rebuild();
    }

    /// <summary>Rebuilds the plot model from the current state (cheap: processed gathers are cached).</summary>
    public void Rebuild()
    {
        var state = _ctx.State;
        if (state is null || Line is null || Gather is null)
        {
            Plot = null;
            _gather = null;
            HasGather = false;
            EmptyMessage = state is null ? "No project is open." : Lines.Count == 0
                ? (_wave is null ? "No survey loaded. Import SEG-2 or SEG-Y data to begin." : $"No {_wave} line in this project. Import {_wave} records to begin.")
                : "This line has no shots.";
            Refreshed?.Invoke(this, EventArgs.Empty);
            return;
        }
        try
        {
            _gather = _ctx.Workspace.Gather(Line.Id, Gather.Id);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or Formats.SeismicFormatException or Persistence.ProjectFileException)
        {
            Plot = null;
            HasGather = false;
            EmptyMessage = "The traces of this shot could not be read: " + ex.Message;
            return;
        }
        HasGather = true;
        EmptyMessage = null;
        var interp = state.Interpretation(Line.Id);
        var model = PlotModels.Gather(_gather, interp, _ctx.LineAnalysis(Line.Id), SelectedChannel,
            $"{Line.Name} · shot {Gather.Label}{(Gather.Included ? "" : " (excluded)")}");
        Plot = new GatherPlot(model);
        ViewLabel = interp.Display.ShowProcessed
            ? interp.Display.OverlayRaw ? "Processed (black) with original overlaid (blue)" : $"Processed: {(interp.Pipeline.EnabledSteps.Any() ? string.Join(" → ", interp.Pipeline.EnabledSteps.Select(s => s.DisplayName)) : "no steps")}"
            : "Original (unprocessed) traces";
        if (interp.Display.ReductionVelocity is { } rv) ViewLabel += $" · reduced at {rv:0} m/s";
        Problems = string.Join(" ", _gather.Problems);
        UpdateInspector();
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    // ───────────────────────────── Display settings ─────────────────────────────

    partial void OnModeChanged(TraceDisplayMode value) => SaveDisplay();
    partial void OnScalingChanged(DisplayScaling value) => SaveDisplay();
    partial void OnGainChanged(double value) => SaveDisplay();
    partial void OnClipChanged(double value) => SaveDisplay();
    partial void OnReductionVelocityChanged(string value) => SaveDisplay();
    partial void OnWaveformViewChanged(WaveformView value) => SaveDisplay();

    private void SaveDisplay()
    {
        if (_loading || Line is null || Interpretation is not { } i) return;
        double? rv = double.TryParse(ReductionVelocity, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
        var display = i.Display with
        {
            Mode = Mode,
            Scaling = Scaling,
            Gain = Math.Clamp(Gain, 0.05, 50),
            Clip = Math.Clamp(Clip, 0.2, 20),
            ReductionVelocity = rv,
            ShowProcessed = WaveformView != WaveformView.Original,
            OverlayRaw = WaveformView == WaveformView.Overlay,
        };
        if (display != i.Display) _ctx.Workspace.Session?.Execute(Edits.SetDisplay(Line.Id, display));
    }

    // ───────────────────────────── Navigation ─────────────────────────────

    [RelayCommand]
    public void NextGather() => Step(1);

    [RelayCommand]
    public void PreviousGather() => Step(-1);

    private void Step(int d)
    {
        if (Gather is null || Gathers.Count == 0) return;
        var i = Gathers.IndexOf(Gathers.First(g => g.Id == Gather.Id));
        Gather = Gathers[Math.Clamp(i + d, 0, Gathers.Count - 1)];
    }

    public void SelectNextTrace(int d)
    {
        if (_gather is null || _gather.Traces.IsEmpty) return;
        var ordered = _gather.Traces.OrderBy(t => t.X).ToList();
        var idx = SelectedChannel is { } c ? ordered.FindIndex(t => t.Channel == c) : -1;
        SelectChannel(ordered[Math.Clamp(idx + d, 0, ordered.Count - 1)].Channel);
    }

    public void SelectChannel(int? channel)
    {
        SelectedChannel = channel;
        _ctx.Selection.Channel = channel;
        Rebuild();
    }

    // ───────────────────────────── Hover and inspector ─────────────────────────────

    public void Hover(GatherPlotTrace? trace, double displayTime)
    {
        if (trace is null || _gather is null || Line is null || Gather is null)
        {
            HoverText = "";
            return;
        }
        var pt = _gather.Traces.FirstOrDefault(t => t.Channel == trace.Channel);
        if (pt is null) return;
        var idx = (int)Math.Round((displayTime - pt.StartTime) / trace.SampleInterval);
        var amp = idx >= 0 && idx < trace.Samples.Length ? trace.Samples[idx] : double.NaN;
        var interp = _ctx.State!.Interpretation(Line.Id);
        var key = new PickKey(Gather.Id, trace.Channel);
        var pick = interp.PickOf(key);
        var flags = _ctx.LineAnalysis(Line.Id)?.Flags.Where(f => f.Key == key).ToList() ?? [];
        var u = _ctx.Units;
        HoverText = $"Channel {trace.Channel} · x {u.Format(trace.X, Domain.Units.QuantityKind.Length)} · offset {u.Format(Math.Abs(trace.X - Gather.ShotX), Domain.Units.QuantityKind.Length)} · " +
                    $"t {displayTime * 1000:0.00} ms · amplitude {amp:G4} · Δt {trace.SampleInterval * 1e6:0.#} µs" +
                    (pick is not null ? $" · pick {pick.Time * 1000:0.00} ms ({ReportTables.OriginText(pick.Origin)})" : " · no pick") +
                    (pick?.Snr is { } snr ? $" · SNR {snr:0.0}" : "") +
                    (flags.Count > 0 ? $" · QC: {string.Join("; ", flags.Select(f => f.Title))}" : " · QC: OK");
    }

    private void UpdateInspector()
    {
        _loading = true;
        HasSelection = SelectedChannel is not null && Gather is not null && Line is not null;
        if (!HasSelection || _gather is null)
        {
            TraceTitle = "No trace selected";
            TraceDetails = "Click a trace to see and edit its properties.";
            PickText = "";
            _loading = false;
            return;
        }
        var key = new PickKey(Gather!.Id, SelectedChannel!.Value);
        var interp = _ctx.State!.Interpretation(Line!.Id);
        var a = interp.AdjustmentOf(key);
        var t = _gather.Traces.Concat(_gather.ReferenceTraces).FirstOrDefault(x => x.Channel == key.Channel);
        var u = _ctx.Units;
        TraceTitle = $"Channel {key.Channel}" + (t?.Role == ChannelRole.Reference ? " (reference geophone)" : "");
        TraceDetails = t is null ? "" :
            $"Position {u.Format(t.X, Domain.Units.QuantityKind.Length)}, offset {u.Format(Math.Abs(t.X - Gather.ShotX), Domain.Units.QuantityKind.Length)}\n" +
            $"Component: {(Line.WaveType == WaveType.P ? "vertical (P)" : "horizontal transverse (SH)")}\n" +
            $"Samples {t.Original.Length} at {t.OriginalSampleInterval * 1000:0.###} ms, first sample {t.StartTime * 1000:0.###} ms";
        TraceGain = a.Gain;
        TraceInverted = a.InvertPolarity;
        TraceExcluded = a.Excluded;
        TraceShiftMs = a.TimeShift * 1000;
        TraceNote = a.Note ?? "";
        var pick = interp.PickOf(key);
        var flags = _ctx.LineAnalysis(Line.Id)?.Flags.Where(f => f.Key == key).ToList() ?? [];
        PickText = pick is null ? "No pick." :
            $"Pick {pick.Time * 1000:0.00} ms — {ReportTables.OriginText(pick.Origin)}" +
            (pick.Confidence is { } c ? $"\nConfidence {c.Level} ({c.Score:0.00})" + (c.Reasons.IsDefaultOrEmpty ? "" : ": " + string.Join(" ", c.Reasons)) : "") +
            (pick.Snr is { } s ? $"\nSNR {s:0.0}" : "") +
            (pick.Suggestion is { } sug && pick.IsManual ? $"\nAutomatic suggestion {sug.Time * 1000:0.00} ms ({sug.Algorithm})" : "") +
            (pick.Included ? "" : "\nExcluded from analysis") +
            (flags.Count > 0 ? "\n" + string.Join("\n", flags.Select(f => $"{(f.Level == QcLevel.Problem ? "✖" : "▲")} {f.Title}: {f.Message}")) : "");
        _loading = false;
    }

    [RelayCommand]
    private void ApplyTrace()
    {
        if (!HasSelection || Line is null || Gather is null || SelectedChannel is null) return;
        var adj = new TraceAdjustment(Math.Clamp(TraceGain, 0.01, 100), TraceInverted, TraceExcluded, TraceShiftMs / 1000, string.IsNullOrWhiteSpace(TraceNote) ? null : TraceNote.Trim());
        _ctx.Edit(Edits.SetTraceAdjustment(Line.Id, new PickKey(Gather.Id, SelectedChannel.Value), adj,
            $"Trace {SelectedChannel}: gain {adj.Gain:0.##}{(adj.InvertPolarity ? ", reversed" : "")}{(adj.Excluded ? ", excluded" : "")}{(adj.TimeShift != 0 ? $", shift {TraceShiftMs:0.##} ms" : "")}"));
    }

    [RelayCommand]
    private void ToggleExcludeTrace()
    {
        TraceExcluded = !TraceExcluded;
        ApplyTrace();
    }

    // ───────────────────────────── Picking ─────────────────────────────

    private PickKey? KeyOf(int channel) => Gather is { } g ? new PickKey(g.Id, channel) : null;

    private ProcessedTrace? TraceOf(int channel) => _gather?.Traces.FirstOrDefault(t => t.Channel == channel);

    /// <summary>Places a manual pick at a display time (converted to record time). Optional snap to the nearby onset.</summary>
    public void PlacePick(int channel, double displayTime, bool snap)
    {
        if (Line is null || TraceOf(channel) is not { } t || KeyOf(channel) is not { } key) return;
        if (snap) displayTime = Snap(t, displayTime);
        _ctx.Edit(Edits.SetManualPick(Line.Id, key, t.ToRecordTime(displayTime)));
        SelectChannel(channel);
    }

    /// <summary>The AIC onset within ±4 ms of the given time (the "snap suggestion").</summary>
    public static double Snap(ProcessedTrace t, double displayTime)
    {
        var dt = t.SampleInterval;
        var center = (int)Math.Round((displayTime - t.StartTime) / dt);
        var half = Math.Max(8, (int)Math.Round(0.004 / dt));
        var cand = AicPicker.PickInWindow(t.Processed, Math.Max(0, center - half), Math.Min(t.Processed.Length - 1, center + half));
        return cand is null ? displayTime : t.StartTime + cand.Index * dt;
    }

    public IDisposable? BeginDrag(int channel) => _ctx.BeginGesture($"Drag pick on channel {channel}");

    public void DragPick(int channel, double displayTime)
    {
        if (!_ctx.License.Status.CanEdit) return;
        if (Line is null || TraceOf(channel) is not { } t || KeyOf(channel) is not { } key) return;
        _ctx.Workspace.Session?.Execute(Edits.SetManualPick(Line.Id, key, t.ToRecordTime(displayTime)));
    }

    /// <summary>Moves the selected pick by a number of samples (keyboard).</summary>
    public void NudgePick(int samples)
    {
        if (Line is null || SelectedChannel is not { } ch || TraceOf(ch) is not { } t || KeyOf(ch) is not { } key) return;
        var pick = _ctx.State?.Interpretation(Line.Id).PickOf(key);
        if (pick is null) return;
        _ctx.Edit(Edits.SetManualPick(Line.Id, key, pick.Time + samples * t.SampleInterval));
    }

    [RelayCommand]
    public void DeleteSelectedPick()
    {
        if (Line is null || SelectedChannel is not { } ch || KeyOf(ch) is not { } key) return;
        if (_ctx.State?.Interpretation(Line.Id).PickOf(key) is not null) _ctx.Edit(Edits.DeletePick(Line.Id, key));
    }

    /// <summary>Accepts the automatic pick (or restores the automatic suggestion of a manual pick) of the selected trace.</summary>
    [RelayCommand]
    public void AcceptSelected()
    {
        if (Line is null || SelectedChannel is not { } ch || KeyOf(ch) is not { } key) return;
        var pick = _ctx.State?.Interpretation(Line.Id).PickOf(key);
        if (pick is null) return;
        if (pick.Origin == ArrivalOrigin.Automatic) _ctx.Edit(Edits.AcceptPicks(Line.Id, [key]));
        else if (pick.Suggestion is not null) _ctx.Edit(Edits.RestoreAutomaticPick(Line.Id, key));
    }

    [RelayCommand]
    public void RestoreAutomatic()
    {
        if (Line is null || SelectedChannel is not { } ch || KeyOf(ch) is not { } key) return;
        _ctx.Edit(Edits.RestoreAutomaticPick(Line.Id, key));
    }

    [RelayCommand]
    public void TogglePickIncluded()
    {
        if (Line is null || SelectedChannel is not { } ch || KeyOf(ch) is not { } key) return;
        if (_ctx.State?.Interpretation(Line.Id).PickOf(key) is { } p) _ctx.Edit(Edits.SetPickIncluded(Line.Id, key, !p.Included));
    }

    [RelayCommand]
    public void PropagateFromSelected()
    {
        if (Line is null || Gather is null || SelectedChannel is not { } ch) return;
        if (_ctx.State?.Interpretation(Line.Id).PickOf(new PickKey(Gather.Id, ch)) is null)
        {
            _ = _ctx.Dialogs.ShowAsync("Pick the reference trace first", "Place or accept a pick on the selected trace; it is then transferred to its neighbours.", Services.MessageKind.Information);
            return;
        }
        if (!_ctx.License.Status.CanEdit) return;
        var n = _ctx.Workspace.PropagatePick(Line.Id, Gather.Id, ch);
        HoverText = n == 0 ? "No neighbouring trace correlated well enough to transfer the pick." : $"Pick transferred to {n} trace(s) by cross-correlation.";
    }
}
