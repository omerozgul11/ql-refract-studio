using RefractionStudio.Analysis.Layered;
using RefractionStudio.Analysis.Numerics;
using RefractionStudio.Analysis.Picking;
using RefractionStudio.Analysis.Tomography;
using RefractionStudio.Analysis.TravelTimes;
using RefractionStudio.Domain;
using RefractionStudio.SignalProcessing;

namespace RefractionStudio.Analysis.Quality;

/// <summary>Severity of a QC finding (shown with colour, icon and text).</summary>
public enum QcLevel
{
    /// <summary>Acceptable (green, check icon).</summary>
    Ok,

    /// <summary>Should be reviewed (yellow, warning icon).</summary>
    Review,

    /// <summary>Likely problem (red, error icon).</summary>
    Problem,
}

/// <summary>A QC finding.</summary>
/// <param name="Level">Severity.</param>
/// <param name="Code">Stable code, e.g. "reciprocity".</param>
/// <param name="Title">Short title.</param>
/// <param name="Message">Plain-language explanation and suggested action.</param>
/// <param name="Line">Line.</param>
/// <param name="Gather">Gather, if specific.</param>
/// <param name="Key">Pick, if specific.</param>
public sealed record QcFlag(QcLevel Level, string Code, string Title, string Message, LineId? Line = null, GatherId? Gather = null, PickKey? Key = null);

/// <summary>A reciprocal pair of arrivals: shot A recorded at B's position and vice versa.</summary>
public sealed record ReciprocalPair(GatherId A, GatherId B, double TimeAtB, double TimeAtA, PickKey KeyAtB, PickKey KeyAtA)
{
    public double Difference => TimeAtB - TimeAtA;
}

/// <summary>Automatic quality checks of picks, geometry and models.</summary>
public static class QualityControl
{
    /// <summary>Finds reciprocal pairs among shots that are (within tolerance) at receiver positions of each other.</summary>
    public static ImmutableArray<ReciprocalPair> ReciprocalPairs(LineObservations obs, double tolerance)
    {
        var pairs = ImmutableArray.CreateBuilder<ReciprocalPair>();
        var gathers = obs.Line.Gathers.Where(g => g.Included).ToList();
        var included = obs.Included.ToList();
        for (var i = 0; i < gathers.Count; i++)
        {
            for (var j = i + 1; j < gathers.Count; j++)
            {
                var a = gathers[i];
                var b = gathers[j];
                if (Math.Abs(a.ShotX - b.ShotX) <= tolerance) continue;
                var atB = included.Where(o => o.Gather == a.Id && Math.Abs(o.ReceiverX - b.ShotX) <= tolerance).OrderBy(o => Math.Abs(o.ReceiverX - b.ShotX)).FirstOrDefault();
                var atA = included.Where(o => o.Gather == b.Id && Math.Abs(o.ReceiverX - a.ShotX) <= tolerance).OrderBy(o => Math.Abs(o.ReceiverX - a.ShotX)).FirstOrDefault();
                if (atB is null || atA is null) continue;
                pairs.Add(new ReciprocalPair(a.Id, b.Id, atB.CorrectedTime, atA.CorrectedTime, atB.Key, atA.Key));
            }
        }
        return pairs.ToImmutable();
    }

    /// <summary>Pick and travel-time checks of one line.</summary>
    public static ImmutableArray<QcFlag> CheckLine(LineObservations obs, QcThresholds th, ImmutableArray<SegmentFit> fits,
        ImmutableArray<ReciprocalPair> reciprocals, Computed<LayeredModel>? layered, TomographyResult? tomography, bool tomographyStale)
    {
        var flags = ImmutableArray.CreateBuilder<QcFlag>();
        var lineId = obs.Line.Id;
        var label = new Dictionary<GatherId, string>(obs.Line.Gathers.Select(g => KeyValuePair.Create(g.Id, g.Label)));
        string Where(Observation o) => $"shot {label.GetValueOrDefault(o.Gather, "?")}, channel {o.Channel} (x = {o.ReceiverX:0.##} m)";

        if (obs.All.IsEmpty)
        {
            flags.Add(new QcFlag(QcLevel.Problem, "no-picks", "No picks", "No first arrivals have been picked on this line.", lineId));
            return flags.ToImmutable();
        }

        // Shot-time corrections that could not be computed.
        foreach (var st in obs.ShotTimes.Where(s => !s.Correction.HasValue))
            flags.Add(new QcFlag(QcLevel.Problem, "shot-time", "Shot time not corrected", st.Correction.Reason!, lineId, st.Gather));

        // Per-pick checks.
        foreach (var o in obs.Included)
        {
            var p = o.Pick;
            if (p.Snr is { } snr && snr < th.LowSnr)
                flags.Add(new QcFlag(QcLevel.Review, "low-snr", "Low signal-to-noise", $"{Where(o)}: SNR {snr:0.0} is below {th.LowSnr:0.#}. Check the pick or apply filtering.", lineId, o.Gather, o.Key));
            if (p.Origin == ArrivalOrigin.Automatic && p.Confidence is { } c)
            {
                if (c.Level == ConfidenceLevel.Low)
                    flags.Add(new QcFlag(QcLevel.Review, "low-confidence", "Low-confidence automatic pick",
                        $"{Where(o)}: confidence {c.Score:0.00}. {string.Join(" ", c.Reasons)}", lineId, o.Gather, o.Key));
                var sharp = c.Factors.FirstOrDefault(f => f.Name == "Onset sharpness");
                if (sharp is not null && sharp.Score < 0.3)
                    flags.Add(new QcFlag(QcLevel.Review, "weak-onset", "Weak onset", $"{Where(o)}: the onset is emergent; the pick may be late.", lineId, o.Gather, o.Key));
            }
            // Shear-wave velocities overlap the speed of sound, so the air-wave test is only meaningful on P lines.
            if (obs.Line.WaveType == WaveType.P && o.AbsOffset > 0 && o.CorrectedTime > 0)
            {
                var apparent = o.AbsOffset / o.CorrectedTime;
                if (apparent >= th.AirWaveVelocityMin && apparent <= th.AirWaveVelocityMax && o.AbsOffset > 5)
                    flags.Add(new QcFlag(QcLevel.Review, "air-wave", "Possible air wave",
                        $"{Where(o)}: the pick implies {apparent:0} m/s from the shot, the speed of sound in air. Make sure the first ground arrival was picked.", lineId, o.Gather, o.Key));
            }
            if (o.CorrectedTime <= 0 && o.AbsOffset > 0)
                flags.Add(new QcFlag(QcLevel.Problem, "non-positive-time", "Arrival at or before the shot",
                    $"{Where(o)}: corrected time {o.CorrectedTime * 1000:0.00} ms is not positive. Check the trigger delay and the shot-time correction.", lineId, o.Gather, o.Key));
        }

        // Monotonicity and adjacent-pick velocity jumps per shot side.
        foreach (var group in obs.Included.GroupBy(o => (o.Gather, o.Side)))
        {
            var sorted = group.OrderBy(o => o.AbsOffset).ToList();
            for (var i = 1; i < sorted.Count; i++)
            {
                var a = sorted[i - 1];
                var b = sorted[i];
                if (b.CorrectedTime < a.CorrectedTime - th.TimeReversalTolerance)
                    flags.Add(new QcFlag(QcLevel.Review, "time-reversal", "Arrival time decreases with offset",
                        $"{Where(b)} arrives {(a.CorrectedTime - b.CorrectedTime) * 1000:0.0} ms earlier than the nearer receiver at x = {a.ReceiverX:0.##} m. Review the picks or the geometry.", lineId, b.Gather, b.Key));
            }
        }

        // Segment residuals ("unusual velocity jump" / inconsistent adjacent picks).
        foreach (var f in fits.Where(f => f.Fit is not null && f.Count >= 4))
        {
            var members = obs.Included.Where(o => o.Gather == f.Gather && o.Side == f.Side && o.Layer == f.Layer && o.AbsOffset > 0).ToList();
            var residuals = members.Select(o => o.CorrectedTime - f.Fit!.Evaluate(o.AbsOffset)).ToArray();
            // A pick is anomalous when it departs from the line by more than the absolute threshold and by
            // more than 3 robust standard deviations of its segment (curved branches are not flagged wholesale).
            var robustSigma = residuals.Length >= 4 ? Statistics.RobustSigma(residuals) : 0;
            for (var k = 0; k < members.Count; k++)
            {
                var o = members[k];
                var r = residuals[k];
                if (Math.Abs(r) > th.MaxResidualSeconds && Math.Abs(r) > 3 * robustSigma)
                    flags.Add(new QcFlag(Math.Abs(r) > 2 * th.MaxResidualSeconds ? QcLevel.Problem : QcLevel.Review, "segment-residual", "Inconsistent with its segment",
                        $"{Where(o)} is {r * 1000:+0.0;-0.0} ms from the straight-line fit of layer {o.Layer} arrivals. It may be mis-picked or assigned to the wrong layer.", lineId, o.Gather, o.Key));
            }
        }

        // Trigger timing: direct arrivals should extrapolate to ~0 at zero offset.
        foreach (var f in fits.Where(f => f.Layer == 1 && f.Fit is not null && f.Count >= 3))
        {
            var gather = obs.Line.Gather(f.Gather);
            if (gather is null || gather.ShotDepth > 0) continue;
            if (Math.Abs(f.InterceptTime) > th.TriggerInterceptTolerance)
                flags.Add(new QcFlag(QcLevel.Review, "trigger", "Possible trigger timing problem",
                    $"Direct arrivals of shot {gather.Label} ({(f.Side < 0 ? "towards decreasing x" : "towards increasing x")}) extrapolate to {f.InterceptTime * 1000:+0.0;-0.0} ms at zero offset. " +
                    "This can indicate a trigger delay; a shot-time correction may be needed (not applied automatically).", lineId, f.Gather));
        }

        // Reciprocity.
        foreach (var pair in reciprocals)
        {
            var mean = 0.5 * (pair.TimeAtA + pair.TimeAtB);
            var tol = Math.Max(th.ReciprocalToleranceAbsolute, th.ReciprocalToleranceRelative * mean);
            var diff = Math.Abs(pair.Difference);
            if (diff > tol)
                flags.Add(new QcFlag(diff > 2 * tol ? QcLevel.Problem : QcLevel.Review, "reciprocity", "Reciprocal times disagree",
                    $"Shots {label.GetValueOrDefault(pair.A)} and {label.GetValueOrDefault(pair.B)} should have equal reciprocal times but differ by {diff * 1000:0.0} ms (tolerance {tol * 1000:0.0} ms). " +
                    "Check those picks and the shot-time corrections.", lineId, pair.A, pair.KeyAtB));
        }

        // SH confirmation.
        if (obs.Line.WaveType == WaveType.SH)
        {
            foreach (var g in obs.Line.Gathers.Where(g => g.Included))
            {
                var kinds = g.Members.Select(m => m.Kind).Distinct().ToList();
                if (!(g.Combine == CombineMode.Stack && kinds.Contains(SourceKind.ShLeft) && kinds.Contains(SourceKind.ShRight)))
                    flags.Add(new QcFlag(QcLevel.Review, "sh-unconfirmed", "SH arrival not confirmed by opposite impacts",
                        $"Shot {g.Label} is not combined from left and right impacts, so the shear arrival is not confirmed by a polarity reversal.", lineId, g.Id));
            }
        }

        // Missing picks on receiver channels.
        foreach (var g in obs.Line.Gathers.Where(g => g.Included))
        {
            var spread = obs.Line.Spread(g.SpreadId);
            if (spread is null) continue;
            var picked = obs.ForGather(g.Id).Select(o => o.Channel).ToHashSet();
            var missing = spread.ReceiverChannels.Where(c => !picked.Contains(c.Channel) && !obs.Interpretation.AdjustmentOf(new PickKey(g.Id, c.Channel)).Excluded).Select(c => c.Channel).ToList();
            if (missing.Count > 0)
                flags.Add(new QcFlag(QcLevel.Review, "missing-picks", "Unpicked traces",
                    $"Shot {g.Label}: channel(s) {string.Join(", ", missing)} have no pick and are not excluded.", lineId, g.Id));
        }

        // Models.
        if (layered is { HasValue: false } lm)
            flags.Add(new QcFlag(QcLevel.Problem, "layered", "Layered interpretation not available", lm.Reason!, lineId));
        if (layered is { HasValue: true } ok)
            foreach (var w in ok.Value!.Warnings) flags.Add(new QcFlag(QcLevel.Review, "layered-warning", "Layered interpretation note", w, lineId));
        if (tomography is not null)
        {
            if (tomographyStale)
                flags.Add(new QcFlag(QcLevel.Review, "tomography-stale", "Tomography is out of date",
                    "Picks, geometry or settings changed after the tomogram was computed. Run the inversion again before reporting.", lineId));
            if (tomography.FinalChi2 > 3)
                flags.Add(new QcFlag(QcLevel.Review, "tomography-misfit", "Tomography does not fit the picks",
                    $"Final χ² = {tomography.FinalChi2:0.0} (RMS {tomography.FinalRms * 1000:0.00} ms). Review outliers or the pick uncertainty settings.", lineId));
            foreach (var r in tomography.Residuals.Where(r => Math.Abs(r.Residual) > th.MaxResidualSeconds))
            {
                var o = obs.All.FirstOrDefault(x => x.Key == r.Key);
                if (o is null) continue;
                flags.Add(new QcFlag(Math.Abs(r.Residual) > 2 * th.MaxResidualSeconds ? QcLevel.Problem : QcLevel.Review, "tomography-residual", "Large model residual",
                    $"{Where(o)}: observed − modelled = {r.Residual * 1000:+0.0;-0.0} ms in the tomogram.", lineId, o.Gather, o.Key));
            }
        }
        return flags.ToImmutable();
    }

    /// <summary>
    /// Trace-level suggestions from waveforms: dead traces, abnormally noisy traces and probable
    /// reversed polarity (negative correlation with both neighbours around the first break).
    /// </summary>
    public static ImmutableArray<QcFlag> CheckTraces(LineId line, GatherData gather, IReadOnlyDictionary<int, double>? pickTimes)
    {
        var flags = ImmutableArray.CreateBuilder<QcFlag>();
        var traces = gather.Traces.Where(t => !t.Excluded).OrderBy(t => t.X).ToList();
        if (traces.Count == 0) return flags.ToImmutable();
        var rms = traces.Select(t => Statistics.Rms(t.Samples)).ToList();
        for (var i = 0; i < traces.Count; i++)
        {
            var key = new PickKey(gather.Gather, traces[i].Channel);
            if (rms[i] == 0)
            {
                flags.Add(new QcFlag(QcLevel.Problem, "dead-trace", "Dead trace", $"Channel {traces[i].Channel} is flat. Exclude it (suggestion).", line, gather.Gather, key));
                continue;
            }
            // Compared with its neighbours along the spread (up to two each side), not with the whole
            // gather: traces near the shot are legitimately much stronger (geometric spreading).
            var neighbours = Enumerable.Range(i - 2, 5).Where(j => j != i && j >= 0 && j < traces.Count && rms[j] > 0).Select(j => rms[j]).ToArray();
            if (neighbours.Length < 2) continue;
            var local = Statistics.Median(neighbours);
            if (local > 0 && rms[i] > 10 * local)
                flags.Add(new QcFlag(QcLevel.Review, "noisy-trace", "Possible bad trace",
                    $"Channel {traces[i].Channel} is {rms[i] / local:0}× stronger than its neighbouring traces; it may be noisy or clipped.", line, gather.Gather, key));
        }
        if (pickTimes is null) return flags.ToImmutable();
        for (var i = 1; i + 1 < traces.Count; i++)
        {
            if (!pickTimes.TryGetValue(traces[i].Channel, out var t)) continue;
            double? Corr(GatherTrace a, GatherTrace b, double ta, double tb)
            {
                var w = (int)Math.Round(0.01 / a.SampleInterval);
                var ia = (int)Math.Round(a.IndexOf(ta));
                var ib = (int)Math.Round(b.IndexOf(tb));
                if (ia < 0 || ib < 0 || ia + w > a.Samples.Length || ib + w > b.Samples.Length || w < 4) return null;
                return Correlation.Pearson(a.Samples.AsSpan(ia, w), b.Samples.AsSpan(ib, w));
            }
            var left = traces[i - 1];
            var right = traces[i + 1];
            if (!pickTimes.TryGetValue(left.Channel, out var tl) || !pickTimes.TryGetValue(right.Channel, out var tr)) continue;
            var cl = Corr(traces[i], left, t, tl);
            var cr = Corr(traces[i], right, t, tr);
            var crossNeighbours = Corr(left, right, tl, tr);
            if (cl < -0.5 && cr < -0.5 && crossNeighbours > 0.5)
                flags.Add(new QcFlag(QcLevel.Review, "polarity", "Possible polarity reversal",
                    $"Channel {traces[i].Channel} is anti-correlated with both neighbours ({cl:0.00}, {cr:0.00}) after its first break. The geophone may be wired in reverse; reverse its polarity if confirmed.", line, gather.Gather, new PickKey(gather.Gather, traces[i].Channel)));
        }
        return flags.ToImmutable();
    }
}
