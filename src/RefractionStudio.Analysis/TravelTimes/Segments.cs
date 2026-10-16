using RefractionStudio.Analysis.Numerics;
using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.TravelTimes;

/// <summary>A straight-line fit to the arrivals of one layer on one side of one shot.</summary>
/// <param name="Gather">Shot.</param>
/// <param name="Side">−1 (decreasing x) or +1.</param>
/// <param name="Layer">Layer (1 = direct wave).</param>
/// <param name="Fit">Fit of corrected time against absolute offset, or null with fewer than 2 points.</param>
/// <param name="Count">Number of arrivals.</param>
/// <param name="MinOffset">Smallest absolute offset of the arrivals.</param>
/// <param name="MaxOffset">Largest absolute offset of the arrivals.</param>
public sealed record SegmentFit(GatherId Gather, int Side, int Layer, LineFit? Fit, int Count, double MinOffset, double MaxOffset)
{
    /// <summary>Apparent velocity 1/slope (NaN if the slope is not positive).</summary>
    public double ApparentVelocity => Fit is { Slope: > 0 } f ? 1 / f.Slope : double.NaN;

    /// <summary>Zero-offset intercept time.</summary>
    public double InterceptTime => Fit?.Intercept ?? double.NaN;
}

/// <summary>Assigns arrivals to layers and fits segments.</summary>
public interface ISegmentAssigner
{
    /// <summary>Suggests crossover offsets for one side of one shot.</summary>
    ImmutableArray<double> SuggestCrossovers(IReadOnlyList<Observation> sideObservations, int layerCount);

    /// <summary>Suggests crossovers for every shot whose segments the user has not edited.</summary>
    SegmentAssignment Suggest(LineObservations observations, int layerCount);
}

/// <summary>
/// Automatic layer assignment (a suggestion the user confirms or edits). Each shot side is segmented
/// by least-squares regression with the number of segments chosen by BIC (segments ≥ 2 arrivals,
/// velocities increasing with offset). Reference layer slownesses come from sides that start with a
/// direct wave (segments through the origin). Each side's segments are then mapped onto the
/// reference layers by a monotone alignment that lets a curved branch span several segments of one
/// layer and lets shallow layers be absent (off-end shots). See docs/algorithms/layer-assignment.md.
/// </summary>
public sealed class SegmentAssigner : ISegmentAssigner
{
    public SegmentAssignment Suggest(LineObservations observations, int layerCount)
    {
        var current = observations.Interpretation.Segments;
        var sides = new List<(GatherId Gather, int Side, (double X, double T)[] Points, List<int> Breaks, List<double> Slopes)>();
        foreach (var gather in observations.Line.Gathers)
        {
            if (current.Gathers.TryGetValue(gather.Id, out var existing) && existing.UserEdited) continue;
            var obs = observations.ForGather(gather.Id).Where(o => o.Included && o.AbsOffset > 0).ToList();
            foreach (var side in new[] { -1, 1 })
            {
                var pts = obs.Where(o => o.Side == side).OrderBy(o => o.AbsOffset).Select(o => (o.AbsOffset, o.CorrectedTime)).ToArray();
                var (breaks, slopes, _) = Segment(pts, layerCount);
                sides.Add((gather.Id, side, pts, breaks, slopes));
            }
        }
        var reference = InitialSlownesses(sides.Select(s => s.Points).ToList(), layerCount);

        var gathers = current.Gathers.ToBuilder();
        foreach (var group in sides.GroupBy(s => s.Gather))
        {
            ImmutableArray<double> left = [], right = [];
            foreach (var (_, side, pts, breaks, slopes) in group)
            {
                if (pts.Length == 0) continue;
                var mapping = reference is null || slopes.Count == 0 ? [.. Enumerable.Range(1, slopes.Count)] : MapSegments(slopes, reference);
                var bounds = new List<int> { 0 };
                bounds.AddRange(breaks);
                bounds.Add(pts.Length);
                var layers = new int[pts.Length];
                for (var sIdx = 0; sIdx < Math.Max(1, slopes.Count); sIdx++)
                    for (var i = bounds[sIdx]; i < bounds[Math.Min(sIdx + 1, bounds.Count - 1)]; i++) layers[i] = slopes.Count == 0 ? 1 : mapping[sIdx];
                if (slopes.Count == 0) Array.Fill(layers, 1);
                var arr = Crossovers(pts, layers, layerCount);
                if (side < 0) left = arr; else right = arr;
            }
            gathers[group.Key] = new GatherSegments(left, right, false);
        }
        return current with { LayerCount = layerCount, Gathers = gathers.ToImmutable() };
    }

    /// <summary>
    /// Maps the segments of one side onto layers (1-based): a non-decreasing sequence minimising
    /// Σ |ln(slope_i / reference_layer)|. Consecutive segments may share a layer (a curved branch split by
    /// the segmentation) and shallow layers may be absent (off-end shots).
    /// </summary>
    private static int[] MapSegments(IReadOnlyList<double> slopes, IReadOnlyList<double> reference)
    {
        var m = slopes.Count;
        var l = reference.Count;
        var cost = new double[m, l];
        var from = new int[m, l];
        double C(int i, int k) => slopes[i] > 0 ? Math.Abs(Math.Log(slopes[i] / reference[k])) : 10;
        for (var k = 0; k < l; k++) cost[0, k] = C(0, k);
        for (var i = 1; i < m; i++)
        {
            for (var k = 0; k < l; k++)
            {
                var best = double.PositiveInfinity;
                var arg = 0;
                for (var kp = 0; kp <= k; kp++)
                {
                    if (cost[i - 1, kp] < best) { best = cost[i - 1, kp]; arg = kp; }
                }
                cost[i, k] = best + C(i, k);
                from[i, k] = arg;
            }
        }
        var last = 0;
        for (var k = 1; k < l; k++) if (cost[m - 1, k] < cost[m - 1, last]) last = k;
        var result = new int[m];
        for (var i = m - 1; i >= 0; i--)
        {
            result[i] = last + 1;
            last = from[i, last];
        }
        return result;
    }

    /// <summary>
    /// Initial layer slownesses. Layer 1 (direct wave): median slope of first segments that pass near
    /// the origin (|intercept| below a quarter of their first arrival time). Deeper layers: median slope of
    /// segment k on the sides that start with a direct segment and show k or more segments. Layers that
    /// remain undetermined come from a weighted 1-D k-means of all segment slopes in log-slowness.
    /// </summary>
    private static double[]? InitialSlownesses(IReadOnlyList<(double X, double T)[]> sides, int layerCount)
    {
        var segmented = new List<(List<double> Slopes, List<double> Intercepts, double FirstTime, List<int> Counts)>();
        foreach (var pts in sides)
        {
            if (pts.Length < 2) continue;
            var (breaks, slopes, intercepts) = Segment(pts, layerCount);
            var bounds = new List<int> { 0 };
            bounds.AddRange(breaks);
            bounds.Add(pts.Length);
            segmented.Add((slopes, intercepts, pts[0].T, [.. Enumerable.Range(0, slopes.Count).Select(i => bounds[i + 1] - bounds[i])]));
        }
        if (segmented.Count == 0) return null;
        var direct = segmented.Where(s => s.Slopes[0] > 0 && Math.Abs(s.Intercepts[0]) < Math.Max(0.002, 0.25 * s.FirstTime)).ToList();
        var result = new double?[layerCount];
        if (direct.Count > 0)
        {
            for (var k = 0; k < layerCount; k++)
            {
                var values = direct.Where(s => s.Slopes.Count > k && s.Slopes[k] > 0).Select(s => s.Slopes[k]).Order().ToList();
                if (values.Count > 0) result[k] = values[values.Count / 2];
            }
        }
        if (result.Any(r => r is null))
        {
            var candidates = segmented.SelectMany(s => s.Slopes.Where(v => v > 0).Select(v => Math.Log(v))).ToList();
            var fill = KMeans(candidates, layerCount);
            for (var k = 0; k < layerCount; k++) result[k] ??= fill[k];
        }
        var r2 = result.Select(v => v!.Value).ToArray();
        for (var k = 1; k < layerCount; k++) r2[k] = Math.Min(r2[k], r2[k - 1] * 0.98);
        return r2;
    }

    /// <summary>1-D k-means in log-slowness; returns slownesses in decreasing order.</summary>
    private static double[] KMeans(IReadOnlyList<double> logSlowness, int k)
    {
        if (logSlowness.Count == 0) return [.. Enumerable.Range(0, k).Select(i => 1.0 / (500 * (i + 1)))];
        var lo = logSlowness.Min();
        var hi = logSlowness.Max();
        var centres = Enumerable.Range(0, k).Select(i => k == 1 ? hi : hi - (hi - lo) * i / (k - 1.0)).ToArray();
        for (var iter = 0; iter < 50; iter++)
        {
            var sums = new double[k];
            var counts = new int[k];
            foreach (var v in logSlowness)
            {
                var best = 0;
                for (var j = 1; j < k; j++) if (Math.Abs(v - centres[j]) < Math.Abs(v - centres[best])) best = j;
                sums[best] += v;
                counts[best]++;
            }
            var moved = false;
            for (var j = 0; j < k; j++)
            {
                if (counts[j] == 0) continue;
                var c = sums[j] / counts[j];
                moved |= Math.Abs(c - centres[j]) > 1e-9;
                centres[j] = c;
            }
            if (!moved) break;
        }
        return [.. centres.OrderByDescending(c => c).Select(Math.Exp)];
    }

    /// <summary>Crossover offsets reproducing a monotone layer sequence (0 = layer absent at small offsets).</summary>
    private static ImmutableArray<double> Crossovers((double X, double T)[] pts, int[] layers, int layerCount)
    {
        if (pts.Length == 0) return [];
        var crossovers = new List<double>();
        for (var k = 1; k < layers[0]; k++) crossovers.Add(0);
        for (var i = 1; i < pts.Length; i++)
        {
            var boundary = 0.5 * (pts[i - 1].X + pts[i].X);
            for (var k = layers[i - 1]; k < layers[i]; k++) crossovers.Add(boundary);
        }
        return [.. crossovers.Take(layerCount - 1)];
    }

    public ImmutableArray<double> SuggestCrossovers(IReadOnlyList<Observation> sideObservations, int layerCount)
    {
        var pts = sideObservations.OrderBy(o => o.AbsOffset).Select(o => (o.AbsOffset, o.CorrectedTime)).ToList();
        var (breaks, _, _) = Segment(pts, layerCount);
        return [.. breaks.Select(b => 0.5 * (pts[b - 1].Item1 + pts[b].Item1))];
    }

    /// <summary>Best segmentation of one side: break indices, segment slopes and intercepts.</summary>
    private static (List<int> Breaks, List<double> Slopes, List<double> Intercepts) Segment(IReadOnlyList<(double X, double T)> points, int layerCount)
    {
        var pts = points.ToArray();
        var n = pts.Length;
        if (n < 2) return ([], [], []);
        var maxBreaks = Math.Min(layerCount - 1, n / 2 - 1);

        // Prefix sums for O(1) segment least squares.
        var sx = new double[n + 1]; var sy = new double[n + 1]; var sxx = new double[n + 1]; var sxy = new double[n + 1]; var syy = new double[n + 1];
        for (var i = 0; i < n; i++)
        {
            sx[i + 1] = sx[i] + pts[i].X; sy[i + 1] = sy[i] + pts[i].T;
            sxx[i + 1] = sxx[i] + pts[i].X * pts[i].X; sxy[i + 1] = sxy[i] + pts[i].X * pts[i].T; syy[i + 1] = syy[i] + pts[i].T * pts[i].T;
        }
        (double Sse, double Slope, double Intercept) Seg(int a, int b) // points a..b-1
        {
            var m = b - a;
            var mx = (sx[b] - sx[a]) / m; var my = (sy[b] - sy[a]) / m;
            var vxx = (sxx[b] - sxx[a]) - m * mx * mx;
            var vxy = (sxy[b] - sxy[a]) - m * mx * my;
            var vyy = (syy[b] - syy[a]) - m * my * my;
            if (vxx <= 1e-12) return (double.PositiveInfinity, double.NaN, double.NaN);
            var slope = vxy / vxx;
            return (Math.Max(0, vyy - slope * vxy), slope, my - slope * mx);
        }

        var bestScore = double.PositiveInfinity;
        int[] best = [];
        var full = Seg(0, n);
        if (full.Slope > 0) bestScore = Bic(full.Sse, n, 1);

        void Search(int start, int remaining, List<int> breaks, double sse, double prevSlope)
        {
            if (remaining == 0)
            {
                var last = Seg(start, n);
                if (n - start < 2 || !(last.Slope > 0) || !(last.Slope < prevSlope)) return;
                var score = Bic(sse + last.Sse, n, breaks.Count + 1);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = [.. breaks];
                }
                return;
            }
            for (var b = start + 2; b <= n - 2 * remaining; b++)
            {
                var s = Seg(start, b);
                if (!(s.Slope > 0) || !(s.Slope < prevSlope)) continue;
                breaks.Add(b);
                Search(b, remaining - 1, breaks, sse + s.Sse, s.Slope);
                breaks.RemoveAt(breaks.Count - 1);
            }
        }

        for (var k = 1; k <= maxBreaks; k++) Search(0, k, [], 0, double.PositiveInfinity);
        var bounds = new List<int> { 0 };
        bounds.AddRange(best);
        bounds.Add(n);
        var slopes = new List<double>();
        var intercepts = new List<double>();
        for (var i = 0; i + 1 < bounds.Count; i++)
        {
            var s = Seg(bounds[i], bounds[i + 1]);
            slopes.Add(s.Slope);
            intercepts.Add(s.Intercept);
        }
        return ([.. best], slopes, intercepts);
    }

    /// <summary>Timing precision floor (s): misfits below it are treated as zero (no picks are this precise).</summary>
    private const double TimingFloor = 1e-5;

    // Gaussian log-likelihood BIC with 2 parameters per segment; the misfit is floored at the timing
    // precision so that rounding noise in exact data cannot justify extra segments.
    private static double Bic(double sse, int n, int segments)
        => n * Math.Log(Math.Max(sse / n, TimingFloor * TimingFloor)) + 2 * segments * Math.Log(n);

    /// <summary>Fits every (shot, side, layer) segment of the included observations.</summary>
    public static ImmutableArray<SegmentFit> FitAll(LineObservations observations, RegressionMode mode)
    {
        var fits = ImmutableArray.CreateBuilder<SegmentFit>();
        foreach (var group in observations.Included.GroupBy(o => (o.Gather, o.Side, o.Layer)))
        {
            var pts = group.Where(o => o.AbsOffset > 0).OrderBy(o => o.AbsOffset).ToList();
            if (pts.Count == 0) continue;
            var fit = pts.Count >= 2 ? Regression.Fit(pts.Select(p => p.AbsOffset).ToList(), pts.Select(p => p.CorrectedTime).ToList(), mode) : null;
            fits.Add(new SegmentFit(group.Key.Gather, group.Key.Side, group.Key.Layer, fit, pts.Count, pts[0].AbsOffset, pts[^1].AbsOffset));
        }
        return fits.ToImmutable();
    }
}
