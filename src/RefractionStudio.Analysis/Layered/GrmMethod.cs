using RefractionStudio.Analysis.Numerics;
using RefractionStudio.Analysis.TravelTimes;
using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.Layered;

/// <summary>Result of evaluating the GRM velocity-analysis function for one XY value.</summary>
/// <param name="Xy">XY distance (m).</param>
/// <param name="Velocity">Refractor velocity from the slope of the velocity-analysis function.</param>
/// <param name="VelocityAnalysisRms">RMS deviation of the velocity-analysis function from its regression line (s).</param>
/// <param name="Points">Number of G positions.</param>
public sealed record GrmXyEvaluation(double Xy, double Velocity, double VelocityAnalysisRms, int Points);

/// <summary>
/// Palmer's generalized reciprocal method (Palmer, D., 1980, The Generalized Reciprocal Method of
/// Seismic Refraction Interpretation, SEG). For a forward shot A (low x), reverse shot B (high x), and
/// receivers X = G − XY/2, Y = G + XY/2:
/// velocity-analysis function t_V = (t_AY − t_BX + t_AB)/2, whose slope is 1/V′;
/// time-depth t_G = [t_AY + t_BX − (t_AB + XY/V′)]/2; depth by layer stripping
/// t_G = Σ_j h_j √(1/V_j² − 1/V′²). With XY = 0 this is Hagedoorn's plus–minus method.
/// The reciprocal time t_AB is the arrival of the refractor at the opposite shot, measured or linearly
/// extended along the refracted segment by at most one geophone interval (flagged).
/// </summary>
public class GrmMethod : ILayeredMethod
{
    public virtual LayeredMethod Method => LayeredMethod.Grm;

    protected virtual double XyFor(LayeredSettings settings) => Math.Max(0, settings.GrmXy);

    protected virtual string MethodName => "GRM";

    public Computed<LayeredModel> Interpret(LineObservations observations, LayeredSettings settings, int layerCount)
    {
        var line = observations.Line;
        if (settings.ForwardShot is not { } fId || settings.ReverseShot is not { } rId)
            return Computed<LayeredModel>.Unavailable($"{MethodName} needs a reversed shot pair: select a forward shot (low x) and a reverse shot (high x).");
        var fwd = line.Gather(fId);
        var rev = line.Gather(rId);
        if (fwd is null || rev is null) return Computed<LayeredModel>.Unavailable("The selected reversed shot pair no longer exists on this line.");
        if (fwd.ShotX >= rev.ShotX) return Computed<LayeredModel>.Unavailable("The forward shot must be at a lower x than the reverse shot.");

        var obs = observations.Included.ToList();
        var warnings = ImmutableArray.CreateBuilder<string>();
        var v1 = LayeredCommon.TopLayerVelocity(obs, settings);
        if (!v1.HasValue) return Computed<LayeredModel>.Unavailable(v1.Reason!);
        var layers = new List<LayerVelocity> { v1.Value! };
        var refractors = new List<RefractorResult>();
        var residuals = ImmutableArray.CreateBuilder<ModelResidual>();
        var xy = XyFor(settings);
        var spacing = observations.TypicalSpacing;
        Lazy<Computed<LayeredModel>> interceptFallback = new(() => new InterceptTimeMethod().Interpret(observations, settings, layerCount));

        for (var k = 1; k < layerCount; k++)
        {
            var layer = k + 1;
            var eval = Evaluate(obs, fwd, rev, layer, xy, spacing, LayeredCommon.Fixed(settings, layer), settings.MinusTimeWindow);
            if (!eval.HasValue)
            {
                var fb = interceptFallback.Value;
                if (fb.HasValue && fb.Value!.Refractors.Length >= k && !fb.Value.Refractors[k - 1].Points.IsDefaultOrEmpty)
                {
                    warnings.Add($"Refractor {k}: {eval.Reason} The intercept-time solution beneath the shots is used for this refractor.");
                    layers.Add(fb.Value.Layers[k]);
                    refractors.Add(fb.Value.Refractors[k - 1]);
                    continue;
                }
                return Computed<LayeredModel>.Unavailable($"Refractor {k}: {eval.Reason}");
            }
            var e = eval.Value!;
            foreach (var note in e.Notes) warnings.Add($"Refractor {k}: {note}");
            layers.Add(new LayerVelocity(layer, e.Velocity, e.VelocityStdError, e.VelocityBasis));
            var velocities = layers.Select(l => l.Velocity).ToList();
            if (LayeredCommon.VelocityInversion(velocities) is { } inversion) return Computed<LayeredModel>.Unavailable(inversion);

            var points = ImmutableArray.CreateBuilder<InterfacePoint>();
            var extrapolatedUpper = false;
            foreach (var (g, timeDepth, count) in e.TimeDepths)
            {
                double[] upper = [];
                if (k > 1)
                {
                    var u = LayeredCommon.UpperThicknesses(refractors, g);
                    if (u is null) continue;
                    upper = u.Value.Thicknesses;
                    extrapolatedUpper |= u.Value.Extrapolated;
                }
                var h = LayeredCommon.ThicknessFromDelay(timeDepth, upper, velocities, k);
                var questionable = false;
                if (!(h >= 0))
                {
                    warnings.Add($"Refractor {k} at x = {g:0.##} m: negative thickness ({h:0.##} m) set to 0; check picks near this position.");
                    h = 0;
                    questionable = true;
                }
                var depth = upper.Sum() + h;
                var sd = e.TimeDepthStdError is { } se ? se / LayeredCommon.VerticalSlowness(velocities[k - 1], velocities[k]) : (double?)null;
                points.Add(new InterfacePoint(g, depth, LayeredCommon.Ground(observations, g) - depth, sd, timeDepth, count, questionable));
            }
            if (extrapolatedUpper) warnings.Add($"Refractor {k}: shallower layer thicknesses were held constant beyond their measured range.");
            refractors.Add(new RefractorResult(k, e.Velocity, e.VelocityStdError, e.Lateral, points.ToImmutable(), e.VelocityAnalysisRms, e.Basis));
        }
        return Computed<LayeredModel>.Ok(new LayeredModel(Method, [.. layers], [.. refractors], residuals.ToImmutable(), warnings.ToImmutable()));
    }

    private sealed record Evaluation(
        double Velocity,
        double? VelocityStdError,
        string VelocityBasis,
        ImmutableArray<(double G, double TimeDepth, int Count)> TimeDepths,
        double? TimeDepthStdError,
        ImmutableArray<VelocitySegment> Lateral,
        double VelocityAnalysisRms,
        string Basis,
        ImmutableArray<string> Notes);

    private Computed<Evaluation> Evaluate(List<Observation> obs, ShotGather fwd, ShotGather rev, int layer, double xy, double spacing, double? fixedVelocity, int window)
    {
        var fObs = obs.Where(o => o.Gather == fwd.Id && o.Layer == layer && o.Offset > 0).ToList();
        var rObs = obs.Where(o => o.Gather == rev.Id && o.Layer == layer && o.Offset < 0).ToList();
        if (fObs.Count < 2 || rObs.Count < 2)
            return Computed<Evaluation>.Unavailable($"layer {layer} arrivals are needed on both the forward shot (≥ 2, towards increasing x) and the reverse shot (≥ 2, towards decreasing x).");
        var notes = ImmutableArray.CreateBuilder<string>();

        // Reciprocal time t_AB.
        var tab1 = LayeredCommon.ArrivalAt(fObs, rev.ShotX, spacing);
        var tab2 = LayeredCommon.ArrivalAt(rObs, fwd.ShotX, spacing);
        double tab;
        if (tab1 is { } a && tab2 is { } b)
        {
            tab = 0.5 * (a.Time + b.Time);
            if (Math.Abs(a.Time - b.Time) > Math.Max(0.002, 0.03 * tab))
                notes.Add($"the two reciprocal-time estimates differ by {Math.Abs(a.Time - b.Time) * 1000:0.0} ms (forward {a.Time * 1000:0.0} ms, reverse {b.Time * 1000:0.0} ms); their mean was used.");
            if (a.Extrapolated || b.Extrapolated) notes.Add("the reciprocal time was extended linearly along the refracted segment by up to one geophone interval.");
        }
        else if ((tab1 ?? tab2) is { } one)
        {
            tab = one.Time;
            notes.Add($"the reciprocal time ({tab * 1000:0.0} ms) is measured on one shot only{(one.Extrapolated ? ", extended by up to one geophone interval" : "")}.");
        }
        else
        {
            return Computed<Evaluation>.Unavailable($"the reciprocal time is not measured: layer {layer} arrivals of each shot must reach (within one geophone interval) the position of the other shot.");
        }

        // Velocity-analysis and time-depth functions at receiver positions between the shots.
        var gs = fObs.Concat(rObs).Select(o => o.ReceiverX).Where(x => x > fwd.ShotX && x < rev.ShotX).Distinct().Order().ToList();
        var rows = new List<(double G, double TAY, double TBX, int Count)>();
        foreach (var g in gs)
        {
            var tay = LayeredCommon.ArrivalAt(fObs, g + xy / 2, 0);
            var tbx = LayeredCommon.ArrivalAt(rObs, g - xy / 2, 0);
            if (tay is null || tbx is null) continue;
            var count = fObs.Count(o => Math.Abs(o.ReceiverX - (g + xy / 2)) <= spacing) + rObs.Count(o => Math.Abs(o.ReceiverX - (g - xy / 2)) <= spacing);
            rows.Add((g, tay.Value.Time, tbx.Value.Time, count));
        }
        if (rows.Count < 2)
            return Computed<Evaluation>.Unavailable($"fewer than two positions are covered by layer {layer} arrivals of both shots{(xy > 0 ? $" for XY = {xy:0.##} m" : "")}.");

        var tv = rows.Select(r => (r.TAY - r.TBX + tab) / 2).ToList();
        var fit = Regression.Fit(rows.Select(r => r.G).ToList(), tv);
        double velocity;
        double? velocityError = null;
        string velocityBasis;
        if (fixedVelocity is { } fv)
        {
            velocity = fv;
            velocityBasis = "Fixed by the user";
        }
        else
        {
            if (fit is null || !(fit.Slope > 0))
                return Computed<Evaluation>.Unavailable("the velocity-analysis function does not increase with position; the arrivals are inconsistent with a refractor.");
            velocity = 1 / fit.Slope;
            if (!double.IsNaN(fit.SlopeStdError)) velocityError = fit.SlopeStdError / (fit.Slope * fit.Slope);
            velocityBasis = $"{MethodName} velocity analysis over {rows.Count} positions (XY = {xy:0.##} m)";
        }
        var vaRms = fit?.RmsResidual ?? double.NaN;

        var timeDepths = rows.Select(r => (r.G, (r.TAY + r.TBX - (tab + xy / velocity)) / 2, r.Count)).ToImmutableArray();

        // Lateral velocity from a sliding window (LayeredSettings.MinusTimeWindow stations, at least 3)
        // over the velocity-analysis function.
        var lateral = ImmutableArray.CreateBuilder<VelocitySegment>();
        window = Math.Max(3, window);
        if (rows.Count >= window && fixedVelocity is null)
        {
            for (var i = 0; i + window <= rows.Count; i++)
            {
                var wf = Regression.Fit(rows.Skip(i).Take(window).Select(r => r.G).ToList(), tv.Skip(i).Take(window).ToList());
                if (wf is { Slope: > 0 })
                    lateral.Add(new VelocitySegment(rows[i].G, rows[i + window - 1].G, 1 / wf.Slope,
                        double.IsNaN(wf.SlopeStdError) ? null : wf.SlopeStdError / (wf.Slope * wf.Slope)));
            }
        }

        // Scatter of the time-depths about a smooth curve is not an error estimate; the regression
        // scatter of the velocity-analysis function is used as an indicative time uncertainty.
        double? tdError = double.IsFinite(vaRms) ? vaRms : null;
        var basis = $"{MethodName}, XY = {xy:0.##} m, reciprocal time {tab * 1000:0.0} ms, {rows.Count} positions";
        return Computed<Evaluation>.Ok(new Evaluation(velocity, velocityError, velocityBasis, timeDepths, tdError, lateral.ToImmutable(), vaRms, basis, notes.ToImmutable()));
    }

    /// <summary>
    /// Evaluates the velocity-analysis function for XY = 0, ½·spacing, … up to <paramref name="maxXy"/>.
    /// Palmer's criterion: the optimum XY gives the simplest (most linear) velocity-analysis function.
    /// Returns the evaluations and the suggested XY (minimum RMS deviation); a suggestion only.
    /// </summary>
    public static (ImmutableArray<GrmXyEvaluation> Evaluations, double? SuggestedXy) AnalyzeXy(LineObservations observations, LayeredSettings settings, int refractor, double maxXy)
    {
        var line = observations.Line;
        if (settings.ForwardShot is not { } f || settings.ReverseShot is not { } r) return ([], null);
        var fwd = line.Gather(f);
        var rev = line.Gather(r);
        if (fwd is null || rev is null || fwd.ShotX >= rev.ShotX) return ([], null);
        var spacing = observations.TypicalSpacing;
        var obs = observations.Included.ToList();
        var list = ImmutableArray.CreateBuilder<GrmXyEvaluation>();
        var method = new GrmMethod();
        for (var xy = 0.0; xy <= maxXy + 1e-9; xy += spacing / 2)
        {
            var e = method.Evaluate(obs, fwd, rev, refractor + 1, xy, spacing, null, settings.MinusTimeWindow);
            if (e.HasValue && double.IsFinite(e.Value!.VelocityAnalysisRms))
                list.Add(new GrmXyEvaluation(xy, e.Value.Velocity, e.Value.VelocityAnalysisRms, e.Value.TimeDepths.Length));
        }
        var candidates = list.Where(e => e.Points >= 3).ToList();
        double? suggested = candidates.Count > 0 ? candidates.OrderBy(e => e.VelocityAnalysisRms).First().Xy : null;
        return (list.ToImmutable(), suggested);
    }

    /// <summary>
    /// Average overburden velocity implied by an XY value for a single overburden layer
    /// (Palmer 1980): V̄ = √(V′²·XY / (XY + 2·V′·t_G)).
    /// </summary>
    public static double AverageVelocityFromXy(double refractorVelocity, double xy, double timeDepth)
        => Math.Sqrt(refractorVelocity * refractorVelocity * xy / (xy + 2 * refractorVelocity * timeDepth));

    /// <summary>
    /// XY implied by a single-layer model: XY = 2h·tan(i), sin(i) = V₁/V₂.
    /// </summary>
    public static double OptimumXyForSingleLayer(double depth, double v1, double v2)
        => 2 * depth * v1 / Math.Sqrt(v2 * v2 - v1 * v1);
}

/// <summary>Hagedoorn's plus–minus method (Hagedoorn, J.G., 1959, Geophysical Prospecting 7, 158–182): the GRM with XY = 0.</summary>
public sealed class PlusMinusMethod : GrmMethod
{
    public override LayeredMethod Method => LayeredMethod.PlusMinus;

    protected override double XyFor(LayeredSettings settings) => 0;

    protected override string MethodName => "Plus–minus";
}
