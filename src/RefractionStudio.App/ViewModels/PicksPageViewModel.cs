using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefractionStudio.Analysis;
using RefractionStudio.Analysis.Quality;
using RefractionStudio.Analysis.TravelTimes;
using RefractionStudio.App.Controls;
using RefractionStudio.Core.Services;
using RefractionStudio.Core.Session;
using RefractionStudio.Domain;
using RefractionStudio.Reporting;

namespace RefractionStudio.App.ViewModels;

/// <summary>A row of the pick table (editable time, inclusion and comment).</summary>
public sealed partial class PickRow : ViewModelBase
{
    private readonly Action<PickRow> _commit;
    private bool _ready;

    public PickRow(Observation o, string shot, string status, Action<PickRow> commit)
    {
        Observation = o;
        Shot = shot;
        Status = status;
        _commit = commit;
        TimeMs = Math.Round(o.RawTime * 1000, 4);
        Included = o.Pick.Included;
        Comment = o.Pick.Comment ?? "";
        _ready = true;
    }

    public Observation Observation { get; }
    public PickKey Key => Observation.Key;
    public string Shot { get; }
    public int Channel => Observation.Channel;
    public double X => Math.Round(Observation.ReceiverX, 3);
    public double Offset => Math.Round(Observation.Offset, 3);
    public string Correction => double.IsNaN(Observation.ShotCorrection) ? "n/a" : (Observation.ShotCorrection * 1000).ToString("0.000", CultureInfo.InvariantCulture);
    public double CorrectedMs => Math.Round(Observation.CorrectedTime * 1000, 3);
    public int Layer => Observation.Layer;
    public string Confidence => Observation.Pick.Confidence is { } c ? $"{c.Level} ({c.Score:0.00})" : Observation.Pick.IsManual ? "manual" : "—";
    public string Origin => ReportTables.OriginText(Observation.Pick.Origin);
    public string Snr => Observation.Pick.Snr is { } s ? s.ToString("0.0", CultureInfo.InvariantCulture) : "—";
    /// <summary>QC status text (icon + words, not colour alone).</summary>
    public string Status { get; }

    [ObservableProperty] public partial double TimeMs { get; set; }
    [ObservableProperty] public partial bool Included { get; set; }
    [ObservableProperty] public partial string Comment { get; set; }

    partial void OnTimeMsChanged(double value)
    {
        if (_ready) _commit(this);
    }

    partial void OnIncludedChanged(bool value)
    {
        if (_ready) _commit(this);
    }

    partial void OnCommentChanged(string value)
    {
        if (_ready) _commit(this);
    }
}

/// <summary>First-break picking of the P or SH line(s).</summary>
public sealed partial class PicksPageViewModel : PageViewModel
{
    private readonly WaveType _wave;
    private bool _loading;

    public PicksPageViewModel(StudioContext ctx, WaveType wave) : base(ctx)
    {
        _wave = wave;
        Viewer = new GatherViewerViewModel(ctx, wave);
        Viewer.Refreshed += (_, _) =>
        {
            LoadPickerSettings();
            LoadTable();
        };
        Algorithms = wave == WaveType.SH
            ? [PickerAlgorithm.ShPolarityDivergence, PickerAlgorithm.Aic, PickerAlgorithm.StaLta, PickerAlgorithm.EnergyRatio]
            : [PickerAlgorithm.Aic, PickerAlgorithm.StaLta, PickerAlgorithm.EnergyRatio];
    }

    public override string Title => _wave == WaveType.P ? "P picks" : "S picks";
    public override WorkflowStep Step => _wave == WaveType.P ? WorkflowStep.PPicks : WorkflowStep.SPicks;
    public override string HelpTopic => _wave == WaveType.P ? "picking.aic" : "picking.sh";
    public override string GuidedText => _wave == WaveType.P
        ? "Run automatic picking, then review: click a trace to place or move its pick, drag a pick to adjust it, ← → nudge by one sample, Enter accepts a suggestion. Review low-confidence picks first (the reasons are in the inspector). Manual picks are never overwritten."
        : "SH gathers combined from left and right impacts are picked where the shear energy reverses with the impact direction. Use the overlay of the original traces to confirm the polarity reversal. Review low-confidence picks first.";

    public GatherViewerViewModel Viewer { get; }
    public IReadOnlyList<PickerAlgorithm> Algorithms { get; }
    public ObservableCollection<PickRow> Picks { get; } = [];

    [ObservableProperty] public partial PickerAlgorithm Algorithm { get; set; }
    [ObservableProperty] public partial double ShortWindowMs { get; set; } = 2;
    [ObservableProperty] public partial double LongWindowMs { get; set; } = 20;
    [ObservableProperty] public partial double MinVelocity { get; set; } = 150;
    [ObservableProperty] public partial double MaxVelocity { get; set; } = 8000;
    [ObservableProperty] public partial double MarginMs { get; set; } = 10;
    [ObservableProperty] public partial bool NeighbourGuidance { get; set; } = true;
    [ObservableProperty] public partial bool SnapToOnset { get; set; }
    [ObservableProperty] public partial bool CurrentShotOnly { get; set; }
    [ObservableProperty] public partial PickRow? SelectedRow { get; set; }
    [ObservableProperty] public partial IPlotAdapter? TravelTimes { get; private set; }
    [ObservableProperty] public partial string Summary { get; private set; } = "";

    public string AlgorithmHelp => Infrastructure.HelpCatalog.Get(Algorithm switch
    {
        PickerAlgorithm.StaLta => "picking.staLta",
        PickerAlgorithm.EnergyRatio => "picking.energyRatio",
        PickerAlgorithm.ShPolarityDivergence => "picking.sh",
        _ => "picking.aic",
    }) is { } t ? $"{t.WhatItDoes} {t.WhenToUse}" : "";

    partial void OnAlgorithmChanged(PickerAlgorithm value) => OnPropertyChanged(nameof(AlgorithmHelp));

    public override void Activate() => Viewer.Reload();
    public override void OnProjectChanged() => Viewer.Reload();
    public override void OnStateChanged(StateChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Previous.Survey, e.Current.Survey)) Viewer.Reload();
        else Viewer.Rebuild();
    }

    public override void OnAnalysisUpdated(AnalysisResult result)
    {
        Viewer.Rebuild();
    }

    partial void OnCurrentShotOnlyChanged(bool value) => LoadTable();

    private void LoadPickerSettings()
    {
        if (Viewer.Interpretation is not { } i) return;
        _loading = true;
        var p = i.Picker;
        Algorithm = Algorithms.Contains(p.Algorithm) ? p.Algorithm : Algorithms[0];
        ShortWindowMs = p.ShortWindow * 1000;
        LongWindowMs = p.LongWindow * 1000;
        MinVelocity = p.MinApparentVelocity ?? 0;
        MaxVelocity = p.MaxApparentVelocity ?? 0;
        MarginMs = p.WindowMargin * 1000;
        NeighbourGuidance = p.UseNeighbourGuidance;
        _loading = false;
    }

    private PickerSettings CurrentSettings() => new(Algorithm, Math.Max(0.0002, ShortWindowMs / 1000), Math.Max(ShortWindowMs / 1000 * 2, LongWindowMs / 1000),
        MinVelocity > 0 ? MinVelocity : null, MaxVelocity > 0 ? MaxVelocity : null, Math.Max(0, MarginMs / 1000), NeighbourGuidance);

    private void LoadTable()
    {
        Picks.Clear();
        var line = Viewer.Line;
        var analysis = Context.LineAnalysis(line?.Id);
        if (line is null || analysis is null)
        {
            TravelTimes = null;
            Summary = "";
            return;
        }
        var labels = analysis.Line.Gathers.ToDictionary(g => g.Id, g => g.Label);
        var flags = analysis.Flags.Where(f => f.Key is not null).GroupBy(f => f.Key!.Value).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var o in analysis.Observations.All.Where(o => !CurrentShotOnly || o.Gather == Viewer.Gather?.Id).OrderBy(o => o.ShotX).ThenBy(o => o.ReceiverX))
        {
            var f = flags.GetValueOrDefault(o.Key);
            var status = f is null ? "✓ OK" : f.Any(x => x.Level == QcLevel.Problem) ? "✖ " + f.First(x => x.Level == QcLevel.Problem).Title : "▲ " + f[0].Title;
            Picks.Add(new PickRow(o, labels.GetValueOrDefault(o.Gather, ""), status, Commit));
        }
        var all = analysis.Observations.All;
        Summary = $"{all.Length} picks on {line.Name}: {all.Count(o => o.Pick.IsManual)} manual or accepted, {all.Count(o => o.Pick.Origin == ArrivalOrigin.Automatic)} automatic to review, " +
                  $"{all.Count(o => o.Pick.Confidence?.Level == ConfidenceLevel.Low && o.Pick.Origin == ArrivalOrigin.Automatic)} low-confidence, {all.Count(o => !o.Included)} excluded.";
        _loading = true;
        SelectedRow = Picks.FirstOrDefault(r => r.Key.Gather == Viewer.Gather?.Id && r.Channel == Viewer.SelectedChannel);
        _loading = false;
        var model = PlotModels.TravelTimes(analysis, Viewer.Gather is { } g && Viewer.SelectedChannel is { } ch ? new PickKey(g.Id, ch) : null, showModel: false);
        TravelTimes = new TravelTimePlot(model with { ShowLegend = false }, []);
    }

    private void Commit(PickRow row)
    {
        if (Viewer.Line is not { } line) return;
        var pick = Context.State?.Interpretation(line.Id).PickOf(row.Key);
        if (pick is null) return;
        if (Math.Abs(pick.Time * 1000 - row.TimeMs) > 1e-6) Context.Edit(Edits.SetManualPick(line.Id, row.Key, row.TimeMs / 1000));
        else if (pick.Included != row.Included) Context.Edit(Edits.SetPickIncluded(line.Id, row.Key, row.Included));
        else if ((pick.Comment ?? "") != row.Comment) Context.Edit(Edits.SetPickComment(line.Id, row.Key, string.IsNullOrWhiteSpace(row.Comment) ? null : row.Comment));
    }

    partial void OnSelectedRowChanged(PickRow? value)
    {
        if (_loading || value is null || Viewer.Line is null) return;
        Context.Selection.Gather = value.Key.Gather;
        if (Viewer.Gather?.Id != value.Key.Gather) Viewer.Gather = Viewer.Gathers.FirstOrDefault(g => g.Id == value.Key.Gather);
        Viewer.SelectChannel(value.Channel);
    }

    /// <summary>Selects the trace of a clicked travel-time point.</summary>
    public void SelectPoint(string id)
    {
        var key = PickKey.Parse(id);
        if (Viewer.Gather?.Id != key.Gather) Viewer.Gather = Viewer.Gathers.FirstOrDefault(g => g.Id == key.Gather);
        Viewer.SelectChannel(key.Channel);
    }

    [RelayCommand]
    private async Task AutoPickAsync()
    {
        if (Viewer.Line is not { } line) return;
        if (!Context.License.Status.CanEdit)
        {
            await Context.Dialogs.ShowAsync("Picking not available", Context.License.Status.Message, Services.MessageKind.Warning);
            return;
        }
        var settings = CurrentSettings();
        IReadOnlyCollection<GatherId>? only = CurrentShotOnly && Viewer.Gather is { } g ? [g.Id] : null;
        var count = await Context.Busy.RunAsync("Picking first breaks…", (progress, ct) =>
            Context.Workspace.AutoPickAsync(line.Id, settings, only, new Progress<(int Done, int Total)>(p => progress.Report(((double)p.Done / p.Total, $"Picking shot {p.Done} of {p.Total}"))), ct));
        Summary = $"Automatic picking finished: {count} trace(s) picked. Manual picks were kept; their automatic suggestions were updated.";
    }

    [RelayCommand]
    private void SavePickerSettings()
    {
        if (Viewer.Line is { } line && !_loading) Context.Edit(Edits.SetPickerSettings(line.Id, CurrentSettings()));
    }

    [RelayCommand]
    private void AcceptHighConfidence()
    {
        if (Viewer.Line is not { } line || Context.State is not { } state) return;
        var keys = state.Interpretation(line.Id).Picks
            .Where(p => p.Value.Origin == ArrivalOrigin.Automatic && p.Value.Confidence?.Level == ConfidenceLevel.High && (!CurrentShotOnly || p.Key.Gather == Viewer.Gather?.Id))
            .Select(p => p.Key).ToList();
        if (keys.Count > 0) Context.Edit(Edits.AcceptPicks(line.Id, keys));
    }

    [RelayCommand]
    private void AcceptShot()
    {
        if (Viewer.Line is not { } line || Viewer.Gather is not { } g || Context.State is not { } state) return;
        var keys = state.Interpretation(line.Id).Picks.Where(p => p.Key.Gather == g.Id && p.Value.Origin == ArrivalOrigin.Automatic).Select(p => p.Key).ToList();
        if (keys.Count > 0) Context.Edit(Edits.AcceptPicks(line.Id, keys));
    }

    [RelayCommand]
    private async Task DeleteShotPicksAsync()
    {
        if (Viewer.Line is not { } line || Viewer.Gather is not { } g || Context.State is not { } state) return;
        var keys = state.Interpretation(line.Id).Picks.Keys.Where(k => k.Gather == g.Id).ToList();
        if (keys.Count == 0) return;
        if (await Context.Dialogs.AskAsync("Delete picks", $"Delete all {keys.Count} picks of shot {g.Label}, including manual picks? This can be undone.", Services.MessageKind.Question, "Delete", "Cancel") == 0)
            Context.Edit(Edits.DeletePicks(line.Id, keys, $"Delete picks of shot {g.Label}"));
    }

    [RelayCommand]
    private void FocusTraceOnTable(PickRow? row) => SelectedRow = row;
}
