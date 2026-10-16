using RefractionStudio.Domain;
using RefractionStudio.SignalProcessing;

namespace RefractionStudio.Analysis.Picking;

/// <summary>A processed trace of a gather as seen by the pickers.</summary>
/// <param name="Channel">Channel number.</param>
/// <param name="X">Receiver position (m).</param>
/// <param name="Samples">Processed samples.</param>
/// <param name="SampleInterval">Sample interval (s).</param>
/// <param name="StartTime">Display time of sample 0 (s).</param>
/// <param name="TimeOffset">Corrected time − display time for this trace (shot correction); used for velocity windows.</param>
/// <param name="Excluded">Excluded by the user.</param>
public sealed record GatherTrace(int Channel, double X, double[] Samples, double SampleInterval, double StartTime, double TimeOffset, bool Excluded)
{
    public double TimeOf(double index) => StartTime + index * SampleInterval;

    public double IndexOf(double time) => (time - StartTime) / SampleInterval;
}

/// <summary>The traces of one gather, with SH members when the gather combines opposite impacts.</summary>
/// <param name="Gather">Gather.</param>
/// <param name="ShotX">Shot position.</param>
/// <param name="Traces">Combined (picked) traces.</param>
/// <param name="ShMembers">For SH gathers: polarity-weighted traces of each member (S aligned), same channel order.</param>
public sealed record GatherData(GatherId Gather, double ShotX, ImmutableArray<GatherTrace> Traces, ImmutableArray<ImmutableArray<GatherTrace>> ShMembers);

/// <summary>An automatic pick with its explanation.</summary>
public sealed record AutoPick(int Channel, double Time, PickConfidence Confidence, double? Snr, string Algorithm);

/// <summary>Gather-level automatic picking with confidence estimation.</summary>
public interface IAutoPicker
{
    IReadOnlyList<AutoPick> PickGather(GatherData gather, PickerSettings settings, CancellationToken ct = default);
}

/// <summary>
/// Picks every included trace of a gather within a velocity window, then scores confidence from
/// signal-to-noise, onset sharpness, consistency with neighbouring traces and agreement between
/// independent pickers. With neighbour guidance, an inconsistent pick is re-picked near the local
/// trend of its neighbours and the reason is recorded. See docs/algorithms/picking.md.
/// </summary>
public sealed class AutoPicker : IAutoPicker
{
    private const double SnrWeight = 0.35, SharpnessWeight = 0.20, NeighbourWeight = 0.25, AgreementWeight = 0.20;

    public IReadOnlyList<AutoPick> PickGather(GatherData gather, PickerSettings settings, CancellationToken ct = default)
    {
        var shPicker = settings.Algorithm == PickerAlgorithm.ShPolarityDivergence;
        var picker = shPicker ? null : Pickers.For(settings.Algorithm);
        var raw = new List<(GatherTrace Trace, PickCandidate Candidate)>();
        foreach (var t in gather.Traces.Where(t => !t.Excluded).OrderBy(t => t.X))
        {
            ct.ThrowIfCancellationRequested();
            var (start, end) = Window(t, gather.ShotX, settings);
            if (end - start < 8 || Picking.OnsetWindow.IsFlat(t.Samples, start, end)) continue;
            PickCandidate? c;
            if (shPicker)
            {
                var members = gather.ShMembers.IsDefaultOrEmpty ? [] : gather.ShMembers.Select(m => m.FirstOrDefault(x => x.Channel == t.Channel)).Where(x => x is not null).ToList();
                c = members.Count >= 2 ? ShPolarityPicker.Pick(members[0]!.Samples, members[1]!.Samples, start, end, t.SampleInterval, settings) : null;
            }
            else
            {
                c = picker!.Pick(t.Samples, start, end, t.SampleInterval, settings);
            }
            if (c is not null) raw.Add((t, c));
        }

        // Neighbour guidance: only where the neighbours agree with one another, and only if the re-pick
        // lands on their trend; otherwise the original pick is kept (and its confidence will say why).
        var picks = raw.Select(r => (r.Trace, r.Candidate, Reasons: new List<string>())).ToList();
        if (settings.UseNeighbourGuidance && picks.Count >= 5 && !shPicker)
        {
            var original = picks.Select(p => (p.Trace.X, p.Trace.TimeOf(p.Candidate.Index))).ToList();
            for (var i = 0; i < picks.Count; i++)
            {
                var (trace, cand, reasons) = picks[i];
                var neighbourhood = Neighbourhood(original, i, gather.ShotX);
                if (neighbourhood is not { } nb || nb.Scatter > Tolerance(trace, nb.Predicted)) continue;
                var time = trace.TimeOf(cand.Index);
                var tol = Tolerance(trace, time);
                if (Math.Abs(time - nb.Predicted) <= 4 * tol) continue;
                var center = (int)Math.Round(trace.IndexOf(nb.Predicted));
                var half = (int)Math.Ceiling(3 * tol / trace.SampleInterval) + 4;
                var refined = AicPicker.PickInWindow(trace.Samples, Math.Max(0, center - 2 * half), Math.Min(trace.Samples.Length - 1, center + half));
                if (refined is not null && Math.Abs(trace.TimeOf(refined.Index) - nb.Predicted) <= 2 * tol)
                {
                    reasons.Add($"Re-picked on the trend of consistent neighbouring traces (first estimate was {(time - nb.Predicted) * 1000:+0.0;-0.0} ms from it).");
                    picks[i] = (trace, refined, reasons);
                }
            }
        }

        var results = new List<AutoPick>();
        var trend = picks.Select(p => (p.Trace.X, p.Trace.TimeOf(p.Candidate.Index))).ToList();
        for (var i = 0; i < picks.Count; i++)
        {
            var (trace, cand, reasons) = picks[i];
            var time = trace.TimeOf(cand.Index);
            var tol = Tolerance(trace, time);
            var factors = new List<ConfidenceFactor>();

            var snr = SignalToNoise.Measure(trace.Samples, cand.Index, trace.SampleInterval, Math.Max(5 * trace.SampleInterval, 3 * settings.ShortWindow), settings.LongWindow);
            if (snr is { } sn)
            {
                var score = Math.Clamp(Math.Log10(Math.Max(sn, 1)) / Math.Log10(30), 0, 1);
                factors.Add(new ConfidenceFactor("Signal-to-noise", score, SnrWeight, $"Amplitude SNR {sn:0.0} (30 or more scores 1)."));
                if (score < 0.5) reasons.Add($"Low signal-to-noise ratio ({sn:0.0}).");
            }
            else
            {
                factors.Add(new ConfidenceFactor("Signal-to-noise", 0.5, SnrWeight, "Not measurable: too little record before the arrival."));
                reasons.Add("Signal-to-noise could not be measured (arrival too close to the start of the record).");
            }

            factors.Add(new ConfidenceFactor("Onset sharpness", cand.Sharpness, SharpnessWeight, $"{(shPicker ? "Polarity divergence" : Pickers.For(settings.Algorithm).Name)}: {cand.Description}."));
            if (cand.Sharpness < 0.4) reasons.Add("The onset is weak or emergent.");

            var predicted = PredictFromNeighbours(trend, i, gather.ShotX);
            if (predicted is { } pr)
            {
                var dev = time - pr;
                var score = Math.Exp(-0.5 * (dev / tol) * (dev / tol));
                factors.Add(new ConfidenceFactor("Neighbour consistency", score, NeighbourWeight, $"{dev * 1000:+0.00;-0.00} ms from the trend of neighbouring traces (tolerance {tol * 1000:0.00} ms)."));
                if (score < 0.5) reasons.Add($"Inconsistent with neighbouring traces ({dev * 1000:+0.0;-0.0} ms).");
            }
            else
            {
                factors.Add(new ConfidenceFactor("Neighbour consistency", 0.5, NeighbourWeight, "Too few neighbouring picks to compare."));
            }

            if (!shPicker)
            {
                var (s, e) = Window(trace, gather.ShotX, settings);
                var others = Pickers.All.Where(p => p.Algorithm != settings.Algorithm)
                    .Select(p => p.Pick(trace.Samples, s, e, trace.SampleInterval, settings))
                    .Where(c => c is not null).Select(c => trace.TimeOf(c!.Index)).ToList();
                if (others.Count > 0)
                {
                    var score = others.Average(o => Math.Exp(-0.5 * ((o - time) / (2 * tol)) * ((o - time) / (2 * tol))));
                    var maxDiff = others.Max(o => Math.Abs(o - time));
                    factors.Add(new ConfidenceFactor("Picker agreement", score, AgreementWeight, $"Other pickers within {maxDiff * 1000:0.00} ms."));
                    if (score < 0.5) reasons.Add($"Independent pickers disagree by up to {maxDiff * 1000:0.0} ms.");
                }
            }
            else if (!gather.ShMembers.IsDefaultOrEmpty)
            {
                var members = gather.ShMembers.Select(m => m.FirstOrDefault(x => x.Channel == trace.Channel)).Where(x => x is not null).ToList();
                if (members.Count >= 2)
                {
                    var agreement = ShPolarityPicker.PolarityAgreement(members[0]!.Samples, members[1]!.Samples, cand.Index, trace.SampleInterval, settings);
                    var score = Math.Clamp((agreement + 1) / 2, 0, 1);
                    factors.Add(new ConfidenceFactor("Opposite-impact agreement", score, AgreementWeight, $"Correlation of the two impacts after polarity reversal: {agreement:0.00}."));
                    if (agreement < 0.5) reasons.Add("The two opposite impacts do not confirm a shear arrival (weak polarity reversal).");
                }
            }

            var totalWeight = factors.Sum(f => f.Weight);
            var combined = factors.Sum(f => f.Score * f.Weight) / totalWeight;
            var confidence = new PickConfidence(combined, PickConfidence.LevelFor(combined), [.. factors], [.. reasons]);
            results.Add(new AutoPick(trace.Channel, time, confidence, snr, shPicker ? "SH polarity divergence" : Pickers.For(settings.Algorithm).Name));
        }
        return results;
    }

    /// <summary>Search window in samples from the apparent-velocity bounds (in corrected time).</summary>
    public static (int Start, int End) Window(GatherTrace t, double shotX, PickerSettings s)
    {
        var offset = Math.Abs(t.X - shotX);
        var tMin = s.MaxApparentVelocity is { } vmax && vmax > 0 ? offset / vmax - s.WindowMargin : double.NegativeInfinity;
        var tMax = s.MinApparentVelocity is { } vmin && vmin > 0 ? offset / vmin + s.WindowMargin : double.PositiveInfinity;
        // Corrected = display + offset  →  display = corrected − offset.
        var start = double.IsNegativeInfinity(tMin) ? 0 : (int)Math.Floor(t.IndexOf(tMin - t.TimeOffset));
        var end = double.IsPositiveInfinity(tMax) ? t.Samples.Length - 1 : (int)Math.Ceiling(t.IndexOf(tMax - t.TimeOffset));
        // Never start before the trigger (record time 0) unless the window says so explicitly.
        start = Math.Max(start, Math.Max(0, (int)Math.Floor(t.IndexOf(-t.TimeOffset)) - 2));
        return (Math.Clamp(start, 0, t.Samples.Length - 1), Math.Clamp(end, 0, t.Samples.Length - 1));
    }

    private static double Tolerance(GatherTrace t, double time) => Math.Max(3 * t.SampleInterval, 0.03 * Math.Abs(time + t.TimeOffset));

    /// <summary>Prediction at pick i from up to two neighbours on each side (same side of the shot) and the RMS scatter of those neighbours about their line.</summary>
    private static (double Predicted, double Scatter)? Neighbourhood(IReadOnlyList<(double X, double Time)> picks, int i, double shotX)
    {
        var side = Math.Sign(picks[i].X - shotX);
        var neighbours = new List<(double X, double T)>();
        for (var d = 1; d <= 2; d++)
        {
            if (i - d >= 0 && Math.Sign(picks[i - d].X - shotX) == side) neighbours.Add(picks[i - d]);
            if (i + d < picks.Count && Math.Sign(picks[i + d].X - shotX) == side) neighbours.Add(picks[i + d]);
        }
        if (neighbours.Count < 3) return null;
        var fit = Numerics.Regression.Fit(neighbours.Select(n => n.X).ToList(), neighbours.Select(n => n.T).ToList());
        return fit is null ? null : (fit.Evaluate(picks[i].X), fit.RmsResidual);
    }

    /// <summary>Time predicted at pick i from up to two neighbours on each side (same side of the shot), by a local line.</summary>
    private static double? PredictFromNeighbours(IReadOnlyList<(double X, double Time)> picks, int i, double shotX)
    {
        var side = Math.Sign(picks[i].X - shotX);
        var neighbours = new List<(double X, double T)>();
        for (var d = 1; d <= 2; d++)
        {
            if (i - d >= 0 && Math.Sign(picks[i - d].X - shotX) == side) neighbours.Add(picks[i - d]);
            if (i + d < picks.Count && Math.Sign(picks[i + d].X - shotX) == side) neighbours.Add(picks[i + d]);
        }
        if (neighbours.Count < 2) return null;
        var fit = Numerics.Regression.Fit(neighbours.Select(n => n.X).ToList(), neighbours.Select(n => n.T).ToList());
        return fit?.Evaluate(picks[i].X);
    }
}

/// <summary>
/// SH first-break picking on opposite impacts. With S aligned in both polarity-weighted members a and b,
/// the shear energy is in D = (a + b)/2 and energy that does not reverse with the impact (P, noise) in
/// C = (a − b)/2. An STA/LTA energy trigger on D is accepted only where the characteristic function
/// D²/(D² + C²) (smoothed over the short window) averages at least 0.6 over two short windows, i.e. where
/// the energy reverses with the impact direction; the onset is refined by AIC on D.
/// </summary>
public static class ShPolarityPicker
{
    public static PickCandidate? Pick(double[] a, double[] b, int start, int end, double sampleInterval, PickerSettings settings)
    {
        var n = Math.Min(a.Length, b.Length);
        end = Math.Min(end, n - 1);
        if (end - start < 8) return null;
        var d = new double[n];
        var c = new double[n];
        for (var i = 0; i < n; i++) { d[i] = 0.5 * (a[i] + b[i]); c[i] = 0.5 * (a[i] - b[i]); }
        var w = Math.Max(2, (int)Math.Round(settings.ShortWindow / sampleInterval));
        var pd = new double[n + 1];
        var pc = new double[n + 1];
        for (var i = 0; i < n; i++) { pd[i + 1] = pd[i] + d[i] * d[i]; pc[i + 1] = pc[i] + c[i] * c[i]; }
        var floor = 1e-6 * (pd[n] + pc[n]) / n + 1e-300;
        double Cf(int i)
        {
            var lo = Math.Max(0, i - w / 2);
            var hi = Math.Min(n, i + w / 2 + 1);
            var ed = pd[hi] - pd[lo];
            var ec = pc[hi] - pc[lo];
            return ed / (ed + ec + floor * (hi - lo));
        }
        // Energy trigger on D (P cancels there), accepted only where the polarity-divergence ratio
        // confirms shear energy; otherwise search on after it.
        var searchFrom = start;
        var onset = -1.0;
        while (searchFrom < end - w)
        {
            var (trigger, _, _, _) = OnsetWindow.Trigger(d, searchFrom, end, sampleInterval, settings);
            if (trigger < 0) break;
            var confirm = Enumerable.Range(trigger, Math.Min(2 * w, n - trigger)).Select(Cf).DefaultIfEmpty(0).Average();
            if (confirm >= 0.6)
            {
                var nl = Math.Max(w + 1, (int)Math.Round(settings.LongWindow / sampleInterval));
                var aic = AicPicker.PickInWindow(d, Math.Max(searchFrom, trigger - nl), Math.Min(end, trigger + 2 * w));
                onset = aic?.Index ?? trigger;
                break;
            }
            searchFrom = trigger + w;
        }
        if (onset < 0) return null;
        var after = Enumerable.Range((int)onset, Math.Min(4 * w, n - (int)onset)).Select(Cf).DefaultIfEmpty(0).Average();
        return new PickCandidate(onset, Math.Clamp(after, 0, 1), $"shear energy fraction {after:0.00} after the onset");
    }

    /// <summary>Zero-lag correlation of the two polarity-weighted members over 4 short windows after the pick (+1 = confirmed shear).</summary>
    public static double PolarityAgreement(double[] a, double[] b, double pickIndex, double sampleInterval, PickerSettings settings)
    {
        var w = Math.Max(4, (int)Math.Round(4 * settings.ShortWindow / sampleInterval));
        var p = Math.Clamp((int)Math.Round(pickIndex), 0, Math.Min(a.Length, b.Length) - 1);
        var len = Math.Min(w, Math.Min(a.Length, b.Length) - p);
        return len < 3 ? 0 : Correlation.Pearson(a.AsSpan(p, len), b.AsSpan(p, len));
    }
}

/// <summary>Result of transferring a pick between traces by cross-correlation.</summary>
/// <param name="Time">Transferred time on the target trace (display time).</param>
/// <param name="Coefficient">Correlation coefficient at the best lag.</param>
/// <param name="LagSeconds">Time shift target − source.</param>
/// <param name="Curve">Correlation curve (lag in s, coefficient).</param>
public sealed record CorrelationTransfer(double Time, double Coefficient, double LagSeconds, ImmutableArray<(double Lag, double Coefficient)> Curve);

/// <summary>Cross-correlation tools for relative arrival times.</summary>
public interface ICrossCorrelationAnalyzer
{
    CorrelationTransfer? Transfer(GatherTrace source, double sourceTime, GatherTrace target, double windowBefore, double windowAfter, double maxLag);
}

/// <inheritdoc />
public sealed class CrossCorrelationAnalyzer : ICrossCorrelationAnalyzer
{
    /// <summary>
    /// Correlates a window around the source pick (from <paramref name="windowBefore"/> before to
    /// <paramref name="windowAfter"/> after) with the target trace over ±<paramref name="maxLag"/> and
    /// returns the transferred time. Both traces must have the same sample interval.
    /// </summary>
    public CorrelationTransfer? Transfer(GatherTrace source, double sourceTime, GatherTrace target, double windowBefore, double windowAfter, double maxLag)
    {
        if (Math.Abs(source.SampleInterval - target.SampleInterval) > 1e-12) return null;
        var dt = source.SampleInterval;
        var start = (int)Math.Round(source.IndexOf(sourceTime - windowBefore));
        var length = (int)Math.Round((windowBefore + windowAfter) / dt);
        var lagSamples = (int)Math.Round(maxLag / dt);
        if (start < 0 || start + length >= source.Samples.Length || length < 4) return null;
        // Align the target time axis to the source axis.
        var axisShift = (int)Math.Round((target.StartTime - source.StartTime) / dt);
        var aligned = new double[source.Samples.Length];
        for (var i = 0; i < aligned.Length; i++)
        {
            var j = i - axisShift;
            if (j >= 0 && j < target.Samples.Length) aligned[i] = target.Samples[j];
        }
        var r = Correlation.Normalized(source.Samples, aligned, start, length, lagSamples);
        var lag = r.BestLag * dt;
        var curve = r.Lags.Select((l, k) => (l * dt, r.Coefficients[k])).ToImmutableArray();
        return new CorrelationTransfer(sourceTime + lag, r.BestCoefficient, lag, curve);
    }
}
