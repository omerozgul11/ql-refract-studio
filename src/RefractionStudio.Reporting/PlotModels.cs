using RefractionStudio.Analysis;
using RefractionStudio.Analysis.Engineering;
using RefractionStudio.Analysis.Layered;
using RefractionStudio.Analysis.Quality;
using RefractionStudio.Analysis.Tomography;
using RefractionStudio.Core.Data;
using RefractionStudio.Domain;
using RefractionStudio.SignalProcessing;
using RefractionStudio.Visualization;

namespace RefractionStudio.Reporting;

/// <summary>Which velocity model a section shows.</summary>
public enum SectionKind
{
    Tomography,
    Layered,
    RayCoverage,
    StartingModel,
}

/// <summary>Builds renderer models from analysis results (shared by the desktop views and reports).</summary>
public static class PlotModels
{
    private static MarkerState StateOf(IEnumerable<QcFlag> flags)
    {
        var worst = flags.Select(f => f.Level).DefaultIfEmpty(QcLevel.Ok).Max();
        return worst switch { QcLevel.Problem => MarkerState.Problem, QcLevel.Review => MarkerState.Review, _ => MarkerState.Normal };
    }

    public static PickStyle StyleOf(ArrivalOrigin o) => o switch
    {
        ArrivalOrigin.Manual => PickStyle.Manual,
        ArrivalOrigin.AcceptedSuggestion => PickStyle.Accepted,
        ArrivalOrigin.Correlation => PickStyle.Correlation,
        _ => PickStyle.Automatic,
    };

    /// <summary>Shot gather with picks, suggestions, segment fits and model predictions.</summary>
    public static GatherPlotModel Gather(ProcessedGather gather, LineInterpretation interp, LineAnalysis? analysis, int? selectedChannel = null, string? title = null)
    {
        var display = interp.Display;
        var flags = analysis?.Flags.Where(f => f.Gather == gather.Gather || f.Key?.Gather == gather.Gather).ToList() ?? [];
        var traces = gather.Traces.Select(t => new GatherPlotTrace(
            t.Channel, t.X, display.ShowProcessed ? t.Processed : t.Original, display.ShowProcessed ? t.SampleInterval : t.OriginalSampleInterval, t.StartTime,
            t.Excluded, t.Channel == selectedChannel, display.OverlayRaw && display.ShowProcessed ? t.Original : null,
            StateOf(flags.Where(f => f.Key?.Channel == t.Channel && f.Code is "dead-trace" or "noisy-trace" or "polarity")))).ToImmutableArray();
        var byChannel = gather.Traces.ToDictionary(t => t.Channel);
        var picks = ImmutableArray.CreateBuilder<GatherPlotPick>();
        var suggestions = ImmutableArray.CreateBuilder<GatherPlotPick>();
        foreach (var (key, pick) in interp.Picks.Where(p => p.Key.Gather == gather.Gather))
        {
            if (!byChannel.TryGetValue(key.Channel, out var t)) continue;
            var shift = display.ShowProcessed ? t.TotalShift : 0;
            var state = pick.Included ? StateOf(flags.Where(f => f.Key == key)) : MarkerState.Excluded;
            picks.Add(new GatherPlotPick(key.Channel, pick.Time + shift, StyleOf(pick.Origin), state));
            if (pick.Suggestion is { } s && pick.IsManual && Math.Abs(s.Time - pick.Time) > 1e-9)
                suggestions.Add(new GatherPlotPick(key.Channel, s.Time + shift, PickStyle.Suggestion, MarkerState.Normal));
        }
        var lines = ImmutableArray.CreateBuilder<GatherPlotLine>();
        if (analysis is not null)
        {
            var correction = gather.Correction;
            foreach (var seg in analysis.Segments.Where(s => s.Gather == gather.Gather && s.Fit is not null))
            {
                var pts = new List<(double, double)>();
                foreach (var off in new[] { seg.MinOffset, seg.MaxOffset })
                    pts.Add((gather.ShotX + seg.Side * off, seg.Fit!.Evaluate(off) - correction));
                lines.Add(new GatherPlotLine(pts, LayerColor(seg.Layer), $"Layer {seg.Layer}"));
            }
            if (analysis.Tomography is { } tomo)
            {
                var obs = analysis.Observations.ForGather(gather.Gather).ToDictionary(o => o.Key);
                var predicted = tomo.Residuals.Where(r => r.Key.Gather == gather.Gather && obs.ContainsKey(r.Key))
                    .Select(r => (X: obs[r.Key].ReceiverX, T: r.Predicted - correction - obs[r.Key].TimeShift)).OrderBy(p => p.X).ToList();
                foreach (var side in predicted.GroupBy(p => Math.Sign(p.X - gather.ShotX)))
                    lines.Add(new GatherPlotLine([.. side], PlotTheme.Light.Accent, "Tomography", Dashed: true));
            }
        }
        return new GatherPlotModel(title ?? gather.Label, gather.ShotX, traces, picks.ToImmutable(), suggestions.ToImmutable(), lines.ToImmutable(), display);
    }

    public static SkiaSharp.SKColor LayerColor(int layer) => PlotTheme.Light.SeriesColor(layer + 2);

    /// <summary>Travel-time plot of a line.</summary>
    public static TravelTimePlotModel TravelTimes(LineAnalysis a, PickKey? selected = null, bool showModel = true, string? title = null)
    {
        var shotIndex = a.Line.Gathers.OrderBy(g => g.ShotX).Select((g, i) => (g.Id, i)).ToDictionary(t => t.Id, t => t.i);
        var flagsByKey = a.Flags.Where(f => f.Key is not null).GroupBy(f => f.Key!.Value).ToDictionary(g => g.Key, g => StateOf(g));
        var points = a.Observations.All.Where(o => double.IsFinite(o.ReceiverX) && double.IsFinite(o.CorrectedTime)).Select(o => new TravelTimePoint(
            o.Key.ToString(), shotIndex.GetValueOrDefault(o.Gather), o.ReceiverX, o.CorrectedTime, o.Layer, o.Included,
            flagsByKey.GetValueOrDefault(o.Key, MarkerState.Normal), o.Key == selected)).ToImmutableArray();
        var curves = ImmutableArray.CreateBuilder<TravelTimeCurve>();
        foreach (var seg in a.Segments.Where(s => s.Fit is not null))
        {
            var g = a.Line.Gather(seg.Gather);
            if (g is null) continue;
            curves.Add(new TravelTimeCurve(shotIndex.GetValueOrDefault(seg.Gather),
                [(g.ShotX + seg.Side * seg.MinOffset, seg.Fit!.Evaluate(seg.MinOffset)), (g.ShotX + seg.Side * seg.MaxOffset, seg.Fit.Evaluate(seg.MaxOffset))], false));
        }
        if (showModel && a.Tomography is { } t)
        {
            var obs = a.Observations.All.ToDictionary(o => o.Key);
            foreach (var group in t.Residuals.Where(r => obs.ContainsKey(r.Key)).GroupBy(r => (r.Key.Gather, Math.Sign(obs[r.Key].Offset))))
            {
                var pts = group.Select(r => (obs[r.Key].ReceiverX, r.Predicted)).OrderBy(p => p.ReceiverX).ToList();
                curves.Add(new TravelTimeCurve(shotIndex.GetValueOrDefault(group.Key.Gather), pts, true, "Tomography"));
            }
        }
        var reciprocals = a.Reciprocals.Select(p =>
        {
            var gb = a.Line.Gather(p.B)!;
            return (gb.ShotX, p.TimeAtB, p.TimeAtA);
        }).ToImmutableArray();
        var shots = a.Line.Gathers.Where(g => double.IsFinite(g.ShotX)).Select(g => (shotIndex[g.Id], g.ShotX, g.Label)).ToImmutableArray();
        return new TravelTimePlotModel(title ?? $"Travel times — {a.Line.Name}", points, curves.ToImmutable(), shots, reciprocals);
    }

    /// <summary>Velocity section from the tomogram or the layered model.</summary>
    public static SectionPlotModel? Section(LineAnalysis a, SectionKind kind, ScenarioSettings settings, Colormap? map = null, bool rays = false, double ve = 1, string? title = null)
    {
        var line = a.Line;
        var topo = Topography(line);
        var receivers = line.Spreads.SelectMany(s => s.ReceiverChannels.Select(c => s.PositionOf(c.Channel)))
            .Where(x => x is not null).Select(x => x!.Value).Distinct().Order().Select(x => (x, line.Topography.ElevationAt(x))).ToImmutableArray();
        var shots = line.Gathers.Where(g => g.Included && double.IsFinite(g.ShotX)).Select(g => (g.ShotX, line.ShotGroundElevation(g))).ToImmutableArray();
        var interfaces = ImmutableArray.CreateBuilder<SectionLine>();
        if (a.Layered.HasValue)
        {
            foreach (var r in a.Layered.Value!.Refractors)
                interfaces.Add(new SectionLine($"Refractor {r.Refractor} ({r.Velocity:0} m/s)", [.. r.Points.Select(p => (p.X, p.Elevation))], LayerColor(r.Refractor + 1), Dashed: kind != SectionKind.Layered));
        }
        var waveLabel = line.WaveType == WaveType.P ? "Vp (m/s)" : "Vs (m/s)";
        var contours = settings.IsolineVelocities.IsDefaultOrEmpty ? (ImmutableArray<double>)[] : settings.IsolineVelocities;
        switch (kind)
        {
            case SectionKind.Tomography or SectionKind.StartingModel or SectionKind.RayCoverage:
            {
                if (a.Tomography is not { } t) return null;
                var model = kind == SectionKind.StartingModel ? t.StartingModel : t.Model;
                var g = model.Grid;
                var constrained = Enumerable.Range(0, g.CellCount).Select(i => kind == SectionKind.StartingModel || t.IsConstrained(i)).ToArray();
                double[] values;
                string label;
                Colormap cmap;
                if (kind == SectionKind.RayCoverage)
                {
                    values = [.. t.Coverage.Select((c, i) => g.Active[i] && c > 0 ? Math.Log10(c) : double.NaN)];
                    label = "Ray length per cell (log₁₀ m)";
                    cmap = Colormap.Cividis;
                }
                else
                {
                    values = [.. model.Velocity];
                    label = waveLabel;
                    cmap = map ?? Colormap.Viridis;
                }
                var finite = values.Where((v, i) => double.IsFinite(v) && constrained[i]).ToList();
                var (lo, hi) = finite.Count > 0 ? (finite.Min(), finite.Max()) : (0.0, 1.0);
                if (kind != SectionKind.RayCoverage && contours.IsDefaultOrEmpty)
                    contours = [.. Ticks.Values(lo, hi, 6).Where(v => v > lo && v < hi)];
                var grid = new SectionGrid(g.X0, g.Dx, g.Nx, g.ZTop, g.Dz, g.Nz, values, constrained, [.. g.Ground]);
                var rayLines = rays ? t.Rays.Select(r => (IReadOnlyList<(double, double)>)[.. r.X.Zip(r.Z)]).ToImmutableArray() : [];
                var tTitle = kind switch { SectionKind.RayCoverage => "Ray coverage", SectionKind.StartingModel => "Starting model", _ => "Tomographic velocity model" };
                return new SectionPlotModel(title ?? $"{tTitle} — {line.Name}", label, grid, cmap, lo, hi, kind == SectionKind.RayCoverage ? [] : contours,
                    topo, interfaces.ToImmutable(), rayLines, receivers, shots, [], ve);
            }
            default:
            {
                if (!a.Layered.HasValue) return null;
                var m = a.Layered.Value!;
                var xs = receivers.Select(r => r.Item1).Concat(shots.Select(s => s.Item1)).ToList();
                if (xs.Count == 0) return null;
                var depth = Math.Max(5, m.Refractors.SelectMany(r => r.Points).Select(p => p.Depth).DefaultIfEmpty(10).Max() * 1.4);
                var spacing = line.Spreads.Select(s => s.Spacing).DefaultIfEmpty(1).Min();
                var grid = ModelGrid.Create(line.Topography, xs.Min(), xs.Max(), spacing / 2, spacing / 2, depth);
                var raster = TomographyEngine.Rasterize(m, grid);
                var vals = raster.Velocity.ToArray();
                var lo = m.Layers.Min(l => l.Velocity);
                var hi = m.Layers.Max(l => l.Velocity);
                var labels = ImmutableArray.CreateBuilder<(double, double, string)>();
                var mid = 0.5 * (xs.Min() + xs.Max());
                var top = 0.0;
                for (var k = 0; k < m.Layers.Length; k++)
                {
                    var bottom = k < m.Refractors.Length ? m.Refractors[k].DepthAt(mid) ?? m.Refractors[k].Points.Select(p => p.Depth).DefaultIfEmpty(top + 2).Average() : top + 4;
                    var z = line.Topography.ElevationAt(mid) - 0.5 * (top + bottom);
                    labels.Add((mid, z, $"{m.Layers[k].Velocity:0} m/s"));
                    top = bottom;
                }
                var sg = new SectionGrid(grid.X0, grid.Dx, grid.Nx, grid.ZTop, grid.Dz, grid.Nz, vals, null, [.. grid.Ground]);
                return new SectionPlotModel(title ?? $"Layered model — {line.Name}", waveLabel, sg, map ?? Colormap.Viridis, lo, hi, [],
                    topo, interfaces.ToImmutable(), [], receivers, shots, labels.ToImmutable(), ve);
            }
        }
    }

    /// <summary>Property section (Poisson's ratio, moduli…) from paired P/SH tomograms.</summary>
    public static SectionPlotModel PropertySection(PropertySections s, string property, SurveyLine line, Domain.Units.UnitFormatter u, string? title = null)
    {
        var (values, label, map) = property switch
        {
            "Poisson" => (s.Poisson, "Poisson's ratio", Colormap.Viridis),
            "VpVs" => (s.VpVs, "Vp/Vs", Colormap.Viridis),
            "G" => (s.ShearModulus.Select(v => u.ToDisplay(v, Domain.Units.QuantityKind.Modulus)).ToArray(), $"G ({u.Symbol(Domain.Units.QuantityKind.Modulus)})", Colormap.Cividis),
            "E" => (s.YoungsModulus.Select(v => u.ToDisplay(v, Domain.Units.QuantityKind.Modulus)).ToArray(), $"E ({u.Symbol(Domain.Units.QuantityKind.Modulus)})", Colormap.Cividis),
            "K" => (s.BulkModulus.Select(v => u.ToDisplay(v, Domain.Units.QuantityKind.Modulus)).ToArray(), $"K ({u.Symbol(Domain.Units.QuantityKind.Modulus)})", Colormap.Cividis),
            _ => (s.Density.Select(v => u.ToDisplay(v, Domain.Units.QuantityKind.Density)).ToArray(), $"Density ({u.Symbol(Domain.Units.QuantityKind.Density)})", Colormap.Cividis),
        };
        var g = s.Grid;
        var finite = values.Where(double.IsFinite).ToList();
        var (lo, hi) = finite.Count > 0 ? (finite.Min(), finite.Max()) : (0.0, 1.0);
        var grid = new SectionGrid(g.X0, g.Dx, g.Nx, g.ZTop, g.Dz, g.Nz, values, null, [.. g.Ground]);
        var name = title ?? $"{label}{(s.DensityEstimated && property is "G" or "E" or "K" or "Density" ? " — ESTIMATED density" : "")}";
        return new SectionPlotModel(name, label, grid, map, lo, hi, [], Topography(line), [], [], [], [], [], 1);
    }

    /// <summary>Velocity profiles at one position (step plot, depth down).</summary>
    public static XyPlotModel Profiles(double x, IReadOnlyList<(string Name, Computed<VelocityProfile> Profile)> profiles, Domain.Units.UnitFormatter u, double maxDepth)
    {
        var series = ImmutableArray.CreateBuilder<XySeries>();
        var markers = ImmutableArray.CreateBuilder<XyMarker>();
        foreach (var (name, p) in profiles)
        {
            if (!p.HasValue) continue;
            var pts = new List<(double, double)>();
            foreach (var i in p.Value!.Intervals)
            {
                var bottom = double.IsFinite(i.Bottom) ? i.Bottom : maxDepth;
                pts.Add((u.ToDisplay(i.Velocity, Domain.Units.QuantityKind.Velocity), u.ToDisplay(i.Top, Domain.Units.QuantityKind.Depth)));
                pts.Add((u.ToDisplay(i.Velocity, Domain.Units.QuantityKind.Velocity), u.ToDisplay(Math.Min(bottom, maxDepth), Domain.Units.QuantityKind.Depth)));
                if (i.Top > maxDepth) break;
            }
            series.Add(new XySeries(name, pts, SeriesStyle.Line));
            markers.Add(new XyMarker(false, u.ToDisplay(p.Value.ConstrainedDepth, Domain.Units.QuantityKind.Depth), $"{name}: constrained to {u.Format(p.Value.ConstrainedDepth, Domain.Units.QuantityKind.Depth, 1)}"));
        }
        return new XyPlotModel($"Velocity profile at x = {u.Format(x, Domain.Units.QuantityKind.Length, 1)}", $"Velocity ({u.Symbol(Domain.Units.QuantityKind.Velocity)})",
            $"Depth ({u.Symbol(Domain.Units.QuantityKind.Depth)})", series.ToImmutable(), markers.ToImmutable(), YDown: true, YMin: 0, YMax: u.ToDisplay(maxDepth, Domain.Units.QuantityKind.Depth), XAxisOnTop: true);
    }

    /// <summary>Average amplitude spectrum of a gather with the magnitude response of the pipeline filters.</summary>
    public static XyPlotModel? Spectrum(ProcessedGather g, ProcessingPipeline pipeline, bool processed)
    {
        var traces = g.Traces.Where(t => !t.Excluded).Select(t => processed ? t.Processed : t.Original).Where(s => s.Length >= 8).ToList();
        if (traces.Count == 0) return null;
        var dt = processed ? g.Traces[0].SampleInterval : g.Traces[0].OriginalSampleInterval;
        var spec = SpectrumAnalysis.Average(traces, dt);
        var max = spec.Amplitudes.Skip(1).DefaultIfEmpty(1).Max();
        var nyquist = 0.5 / dt;
        var series = ImmutableArray.CreateBuilder<XySeries>();
        series.Add(new XySeries(processed ? "Processed" : "Original", [.. spec.Frequencies.Zip(spec.Amplitudes).Skip(1).Select(p => (p.First, max > 0 ? p.Second / max : 0))], SeriesStyle.Area));
        var response = FilterResponse(pipeline, 1 / dt, spec.Frequencies);
        if (response is not null) series.Add(new XySeries("Filter response", [.. spec.Frequencies.Zip(response).Skip(1)], SeriesStyle.Line, PlotTheme.Light.Problem, Dashed: true));
        var (lo, hi) = SpectrumAnalysis.SuggestBandPass(spec, dt);
        return new XyPlotModel($"Amplitude spectrum — {g.Label}", "Frequency (Hz)", "Relative amplitude", series.ToImmutable(),
        [
            new XyMarker(true, spec.DominantFrequency, $"Dominant {spec.DominantFrequency:0} Hz"),
            new XyMarker(true, spec.BandLow, $"−{spec.BandLevelDb:0} dB {spec.BandLow:0} Hz"),
            new XyMarker(true, spec.BandHigh, $"{spec.BandHigh:0} Hz"),
        ], XMin: 0, XMax: Math.Min(nyquist, Math.Max(spec.BandHigh * 3, hi * 1.5)), YMin: 0, YMax: 1.05);
    }

    /// <summary>Combined magnitude response of the enabled filters (zero-phase steps squared), or null.</summary>
    public static double[]? FilterResponse(ProcessingPipeline pipeline, double sampleRate, double[] frequencies)
    {
        var filters = new List<(SosFilter Filter, bool ZeroPhase)>();
        foreach (var s in pipeline.EnabledSteps)
        {
            try
            {
                switch (s)
                {
                    case HighPassStep h: filters.Add((IirDesign.Butterworth(FilterType.HighPass, h.Order, h.CornerHz, sampleRate), h.ZeroPhase)); break;
                    case LowPassStep l: filters.Add((IirDesign.Butterworth(FilterType.LowPass, l.Order, l.CornerHz, sampleRate), l.ZeroPhase)); break;
                    case BandPassStep b: filters.Add((IirDesign.BandPass(b.Order, b.LowHz, b.HighHz, sampleRate), b.ZeroPhase)); break;
                    case NotchStep n: filters.Add((new SosFilter([IirDesign.Notch(n.FrequencyHz, n.Q, sampleRate)]), true)); break;
                }
            }
            catch (ArgumentException)
            {
                // Invalid steps are reported by the pipeline validation and skipped here.
            }
        }
        if (filters.Count == 0) return null;
        return [.. frequencies.Select(f => filters.Aggregate(1.0, (acc, x) => acc * Math.Pow(x.Filter.Magnitude(f, sampleRate), x.ZeroPhase ? 2 : 1)))];
    }

    /// <summary>Convergence of a tomographic inversion.</summary>
    public static XyPlotModel Convergence(TomographyResult t) => new("Inversion convergence", "Iteration", "RMS residual (ms)",
        [new XySeries("RMS (ms)", [.. t.Iterations.Select(i => ((double)i.Iteration, i.RmsSeconds * 1000))], SeriesStyle.LinePoints)], [], YMin: 0);

    /// <summary>GRM velocity-analysis quality versus XY.</summary>
    public static XyPlotModel GrmXy(ImmutableArray<GrmXyEvaluation> evaluations, double? suggested) => new("GRM: velocity-analysis function scatter versus XY", "XY (m)", "RMS deviation (ms)",
        [new XySeries("RMS deviation", [.. evaluations.Select(e => (e.Xy, e.VelocityAnalysisRms * 1000))], SeriesStyle.LinePoints)],
        suggested is { } s ? [new XyMarker(true, s, $"Suggested XY {s:0.##} m")] : [], YMin: 0);

    public static SpreadPlotModel Spread(SurveyLine line, GatherId? selected, IReadOnlyDictionary<int, MarkerState>? channelStates = null)
    {
        var receivers = line.Spreads.SelectMany(s => s.ReceiverChannels.Select(c => (c.Channel, X: s.PositionOf(c.Channel))))
            .Where(r => r.X is not null).GroupBy(r => r.X!.Value).Select(g => (g.First().Channel, g.Key, line.Topography.ElevationAt(g.Key),
                channelStates?.GetValueOrDefault(g.First().Channel, MarkerState.Normal) ?? MarkerState.Normal)).OrderBy(r => r.Key).ToImmutableArray();
        var shots = line.Gathers.Select(g => (g.Label, g.ShotX, double.IsFinite(g.ShotX) ? line.ShotGroundElevation(g) : 0, g.ShotDepth, g.Id == selected, g.Included)).ToImmutableArray();
        var refs = line.Spreads.SelectMany(s => s.Channels.Where(c => c.Role == ChannelRole.Reference && c.XOverride is not null)
            .Select(c => (c.XOverride!.Value, line.Topography.ElevationAt(c.XOverride.Value)))).ToImmutableArray();
        return new SpreadPlotModel($"Line geometry — {line.Name}", Topography(line), receivers, shots, refs, 5);
    }

    private static ImmutableArray<(double X, double Z)> Topography(SurveyLine line)
    {
        if (!line.Topography.IsFlat) return [.. line.Topography.Points.Select(p => (p.X, p.Elevation))];
        var xs = line.Spreads.SelectMany(s => s.ReceiverChannels.Select(c => s.PositionOf(c.Channel))).Where(x => x is not null).Select(x => x!.Value)
            .Concat(line.Gathers.Where(g => double.IsFinite(g.ShotX)).Select(g => g.ShotX)).ToList();
        return xs.Count == 0 ? [] : [(xs.Min(), 0.0), (xs.Max(), 0.0)];
    }
}
