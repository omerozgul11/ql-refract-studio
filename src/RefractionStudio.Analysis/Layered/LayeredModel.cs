using RefractionStudio.Analysis.TravelTimes;
using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.Layered;

/// <summary>Velocity of one layer of a layered interpretation.</summary>
/// <param name="Layer">1-based layer index (1 = top layer).</param>
/// <param name="Velocity">Velocity (m/s).</param>
/// <param name="StdError">Standard error (m/s), if estimable.</param>
/// <param name="Basis">How it was obtained, in words.</param>
public sealed record LayerVelocity(int Layer, double Velocity, double? StdError, string Basis);

/// <summary>Depth of a refractor beneath one position.</summary>
/// <param name="X">In-line position (m).</param>
/// <param name="Depth">Depth below ground (m).</param>
/// <param name="Elevation">Refractor elevation (m) = ground elevation − depth.</param>
/// <param name="DepthStdError">Standard error of the depth (m), if estimable.</param>
/// <param name="DelayTime">Delay (time-depth) used, seconds.</param>
/// <param name="Observations">Number of arrivals that constrain the point.</param>
/// <param name="Questionable">True when the point needed a clamp or extrapolation (see warnings).</param>
public sealed record InterfacePoint(double X, double Depth, double Elevation, double? DepthStdError, double DelayTime, int Observations, bool Questionable = false);

/// <summary>Refractor velocity over an in-line interval.</summary>
public sealed record VelocitySegment(double X0, double X1, double Velocity, double? StdError);

/// <summary>Result for one refractor (the top of layer <see cref="Refractor"/> + 1).</summary>
/// <param name="Refractor">1-based refractor index.</param>
/// <param name="Velocity">Representative refractor velocity (m/s).</param>
/// <param name="VelocityStdError">Standard error (m/s).</param>
/// <param name="LateralVelocity">Lateral velocity variation, if resolved.</param>
/// <param name="Points">Depths along the line.</param>
/// <param name="RmsResidual">RMS travel-time residual of the method's own fit (s), if applicable.</param>
/// <param name="Basis">Description of how it was derived.</param>
public sealed record RefractorResult(int Refractor, double Velocity, double? VelocityStdError, ImmutableArray<VelocitySegment> LateralVelocity, ImmutableArray<InterfacePoint> Points, double? RmsResidual, string Basis)
{
    /// <summary>Depth at <paramref name="x"/> by linear interpolation; null outside the covered range.</summary>
    public double? DepthAt(double x)
    {
        if (Points.IsDefaultOrEmpty) return null;
        if (Points.Length == 1) return Math.Abs(Points[0].X - x) < 1e-9 ? Points[0].Depth : null;
        if (x < Points[0].X - 1e-9 || x > Points[^1].X + 1e-9) return null;
        for (var i = 1; i < Points.Length; i++)
        {
            if (x <= Points[i].X + 1e-12)
            {
                var a = Points[i - 1];
                var b = Points[i];
                var span = b.X - a.X;
                return span <= 0 ? a.Depth : a.Depth + (b.Depth - a.Depth) * (x - a.X) / span;
            }
        }
        return Points[^1].Depth;
    }
}

/// <summary>Travel-time residual of an observation with respect to a model.</summary>
public sealed record ModelResidual(PickKey Key, double Observed, double Predicted)
{
    public double Residual => Observed - Predicted;
}

/// <summary>A layered interpretation of a line.</summary>
public sealed record LayeredModel(
    LayeredMethod Method,
    ImmutableArray<LayerVelocity> Layers,
    ImmutableArray<RefractorResult> Refractors,
    ImmutableArray<ModelResidual> Residuals,
    ImmutableArray<string> Warnings)
{
    public int LayerCount => Layers.Length;

    /// <summary>Velocity at depth <paramref name="depth"/> below ground at <paramref name="x"/>; null where a refractor depth is unknown.</summary>
    public double? VelocityAt(double x, double depth)
    {
        if (Layers.IsDefaultOrEmpty) return null;
        for (var k = 0; k < Refractors.Length; k++)
        {
            var d = Refractors[k].DepthAt(x);
            if (d is null) return null;
            if (depth < d) return Layers[k].Velocity;
        }
        return Layers[^1].Velocity;
    }

    /// <summary>Deepest refractor depth at x (the depth below which only the half-space velocity is assumed).</summary>
    public double? DeepestInterfaceAt(double x)
        => Refractors.IsDefaultOrEmpty ? null : Refractors[^1].DepthAt(x);
}

/// <summary>A layered interpretation method.</summary>
public interface ILayeredMethod
{
    LayeredMethod Method { get; }

    /// <summary>Interprets the observations; returns the reason when it cannot.</summary>
    Computed<LayeredModel> Interpret(LineObservations observations, LayeredSettings settings, int layerCount);
}

/// <summary>Helpers shared by the layered methods.</summary>
public static class LayeredCommon
{
    /// <summary>
    /// Pooled within-group slope of corrected time against absolute offset for the arrivals of
    /// <paramref name="layer"/>, grouping by (shot, side) so that each group has its own intercept
    /// (absorbing shot-time and near-source delays). Returns apparent velocity and its standard error.
    /// </summary>
    public static (double Velocity, double StdError, int Count)? PooledVelocity(IEnumerable<Observation> obs, int layer)
    {
        var groups = obs.Where(o => o.Layer == layer && o.AbsOffset > 0).GroupBy(o => (o.Gather, o.Side)).Where(g => g.Count() >= 2).ToList();
        double sxx = 0, sxy = 0;
        var n = 0;
        foreach (var g in groups)
        {
            var mx = g.Average(o => o.AbsOffset);
            var my = g.Average(o => o.CorrectedTime);
            foreach (var o in g)
            {
                sxx += (o.AbsOffset - mx) * (o.AbsOffset - mx);
                sxy += (o.AbsOffset - mx) * (o.CorrectedTime - my);
                n++;
            }
        }
        if (n < 3 || sxx <= 0) return null;
        var slope = sxy / sxx;
        if (slope <= 0) return null;
        double sse = 0;
        foreach (var g in groups)
        {
            var mx = g.Average(o => o.AbsOffset);
            var my = g.Average(o => o.CorrectedTime);
            foreach (var o in g)
            {
                var r = o.CorrectedTime - my - slope * (o.AbsOffset - mx);
                sse += r * r;
            }
        }
        var dof = n - groups.Count - 1;
        var seSlope = dof > 0 ? Math.Sqrt(sse / dof / sxx) : double.NaN;
        return (1 / slope, double.IsNaN(seSlope) ? double.NaN : seSlope / (slope * slope), n);
    }

    /// <summary>Velocity of layer 1 (fixed by the user, or pooled from direct arrivals).</summary>
    public static Computed<LayerVelocity> TopLayerVelocity(IEnumerable<Observation> obs, LayeredSettings settings)
    {
        if (Fixed(settings, 1) is { } v) return Computed<LayerVelocity>.Ok(new LayerVelocity(1, v, null, "Fixed by the user"));
        var pooled = PooledVelocity(obs, 1);
        return pooled is { } p
            ? Computed<LayerVelocity>.Ok(new LayerVelocity(1, p.Velocity, double.IsNaN(p.StdError) ? null : p.StdError, $"Direct arrivals ({p.Count} picks, pooled slope)"))
            : Computed<LayerVelocity>.Unavailable("The velocity of the top layer cannot be estimated: at least three direct arrivals (layer 1) on one side of a shot are needed, or fix the velocity.");
    }

    public static double? Fixed(LayeredSettings settings, int layer)
        => !settings.FixedVelocities.IsDefaultOrEmpty && settings.FixedVelocities.Length >= layer && settings.FixedVelocities[layer - 1] is { } v && v > 0 ? v : null;

    /// <summary>q_jk = √(1/V_j² − 1/V_k²): vertical slowness of layer j for a ray critically refracted at the top of layer k.</summary>
    public static double VerticalSlowness(double vj, double vk) => Math.Sqrt(Math.Max(0, 1 / (vj * vj) - 1 / (vk * vk)));

    /// <summary>
    /// Layer stripping. Given the delay (time-depth) <paramref name="delay"/> of refractor k (top of
    /// layer k+1) and thicknesses h_1..h_{k−1} above it, returns h_k from
    /// delay = Σ_{j≤k} h_j √(1/V_j² − 1/V_{k+1}²). The two-way intercept time equals 2·delay for planar layers.
    /// </summary>
    public static double ThicknessFromDelay(double delay, IReadOnlyList<double> upperThicknesses, IReadOnlyList<double> velocities, int k)
    {
        var vBelow = velocities[k]; // V_{k+1} (0-based index k)
        var remaining = delay;
        for (var j = 0; j < k - 1; j++) remaining -= upperThicknesses[j] * VerticalSlowness(velocities[j], vBelow);
        var q = VerticalSlowness(velocities[k - 1], vBelow);
        return q > 0 ? remaining / q : double.NaN;
    }

    /// <summary>Checks that velocities increase with depth; returns a message when they do not.</summary>
    public static string? VelocityInversion(IReadOnlyList<double> velocities)
    {
        for (var i = 1; i < velocities.Count; i++)
        {
            if (!(velocities[i] > velocities[i - 1]))
                return $"Layer {i + 1} velocity ({velocities[i]:0} m/s) is not higher than layer {i} ({velocities[i - 1]:0} m/s). " +
                       "The refraction method requires velocity to increase with depth; check the layer assignment of the arrivals.";
        }
        return null;
    }

    /// <summary>Upper-layer thicknesses at x from already computed refractors (cumulative depths differenced).</summary>
    public static (double[] Thicknesses, bool Extrapolated)? UpperThicknesses(IReadOnlyList<RefractorResult> upper, double x)
    {
        var h = new double[upper.Count];
        var previous = 0.0;
        var extrapolated = false;
        for (var j = 0; j < upper.Count; j++)
        {
            var pts = upper[j].Points;
            if (pts.IsDefaultOrEmpty) return null;
            var d = upper[j].DepthAt(x);
            if (d is null)
            {
                d = x < pts[0].X ? pts[0].Depth : pts[^1].Depth;
                extrapolated = true;
            }
            h[j] = Math.Max(0, d.Value - previous);
            previous = Math.Max(previous, d.Value);
        }
        return (h, extrapolated);
    }

    /// <summary>
    /// Arrival time of one layer in a gather at position <paramref name="x"/>: linear
    /// interpolation between included arrivals; within <paramref name="maxExtrapolation"/> metres beyond the
    /// first/last arrival, linear extrapolation along the end segment (flagged).
    /// </summary>
    public static (double Time, bool Extrapolated)? ArrivalAt(IReadOnlyList<Observation> gatherLayerObs, double x, double maxExtrapolation)
    {
        var pts = gatherLayerObs.OrderBy(o => o.ReceiverX).ToList();
        if (pts.Count == 0) return null;
        if (pts.Count == 1) return Math.Abs(pts[0].ReceiverX - x) < 1e-6 ? (pts[0].CorrectedTime, false) : null;
        if (x >= pts[0].ReceiverX - 1e-9 && x <= pts[^1].ReceiverX + 1e-9)
        {
            for (var i = 1; i < pts.Count; i++)
            {
                if (x <= pts[i].ReceiverX + 1e-9)
                {
                    var a = pts[i - 1]; var b = pts[i];
                    var span = b.ReceiverX - a.ReceiverX;
                    var t = span <= 0 ? a.CorrectedTime : a.CorrectedTime + (b.CorrectedTime - a.CorrectedTime) * (x - a.ReceiverX) / span;
                    return (t, false);
                }
            }
        }
        if (x < pts[0].ReceiverX)
        {
            if (pts[0].ReceiverX - x > maxExtrapolation + 1e-9) return null;
            var a = pts[0]; var b = pts[1];
            return (a.CorrectedTime + (b.CorrectedTime - a.CorrectedTime) * (x - a.ReceiverX) / (b.ReceiverX - a.ReceiverX), true);
        }
        if (x - pts[^1].ReceiverX > maxExtrapolation + 1e-9) return null;
        var c = pts[^2]; var d = pts[^1];
        return (d.CorrectedTime + (d.CorrectedTime - c.CorrectedTime) * (x - d.ReceiverX) / (d.ReceiverX - c.ReceiverX), true);
    }

    /// <summary>Elevation of the ground at x on the line.</summary>
    public static double Ground(LineObservations obs, double x) => obs.Line.Topography.ElevationAt(x);
}
