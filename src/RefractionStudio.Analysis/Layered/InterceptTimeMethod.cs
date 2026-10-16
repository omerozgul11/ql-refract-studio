using RefractionStudio.Analysis.TravelTimes;
using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.Layered;

/// <summary>
/// Slope–intercept interpretation (e.g. Redpath 1973; Haeni 1988, USGS TWRI 2-D2).
/// Planar layers. For a refractor at the top of layer k+1 the intercept time is
/// T_k = 2 Σ_{j≤k} h_j √(1/V_j² − 1/V_{k+1}²), solved for the thicknesses by layer stripping.
/// With a reversed shot pair, a two-layer dipping refractor is solved exactly from the down- and
/// up-dip apparent velocities; deeper refractors use the harmonic-mean velocity 2V_dV_u/(V_d+V_u)
/// (small-dip approximation). Without a reversed pair, apparent velocities are used and dip is not
/// resolved. Depths are reported beneath the shot points.
/// </summary>
public sealed class InterceptTimeMethod : ILayeredMethod
{
    public LayeredMethod Method => LayeredMethod.InterceptTime;

    public Computed<LayeredModel> Interpret(LineObservations observations, LayeredSettings settings, int layerCount)
    {
        var obs = observations.Included.ToList();
        var warnings = ImmutableArray.CreateBuilder<string>();
        var v1 = LayeredCommon.TopLayerVelocity(obs, settings);
        if (!v1.HasValue) return Computed<LayeredModel>.Unavailable(v1.Reason!);
        var layers = new List<LayerVelocity> { v1.Value! };
        var fits = SegmentAssigner.FitAll(observations, settings.RegressionMode);

        ShotGather? fwd = settings.ForwardShot is { } f ? observations.Line.Gather(f) : null;
        ShotGather? rev = settings.ReverseShot is { } r ? observations.Line.Gather(r) : null;
        var reversed = fwd is not null && rev is not null && fwd.ShotX < rev.ShotX;
        if (settings.ForwardShot is not null && !reversed)
            warnings.Add("The selected reversed shot pair is incomplete or not ordered forward (low x) → reverse (high x); apparent velocities were used.");
        if (!reversed)
            warnings.Add("No reversed shot pair: refractor velocities are apparent velocities and dip is not resolved (horizontal layers assumed).");

        var dipAngles = new Dictionary<int, double>();
        for (var k = 1; k < layerCount; k++)
        {
            var layer = k + 1;
            if (LayeredCommon.Fixed(settings, layer) is { } fixedV)
            {
                layers.Add(new LayerVelocity(layer, fixedV, null, "Fixed by the user"));
                continue;
            }
            if (reversed)
            {
                var down = fits.FirstOrDefault(s => s.Gather == fwd!.Id && s.Side == 1 && s.Layer == layer);
                var up = fits.FirstOrDefault(s => s.Gather == rev!.Id && s.Side == -1 && s.Layer == layer);
                if (down?.Fit is null || up?.Fit is null || double.IsNaN(down.ApparentVelocity) || double.IsNaN(up.ApparentVelocity))
                    return Computed<LayeredModel>.Unavailable($"Layer {layer} arrivals are missing on the forward shot (towards increasing x) or the reverse shot (towards decreasing x); at least two arrivals are needed on each.");
                var vd = down.ApparentVelocity;
                var vu = up.ApparentVelocity;
                if (k == 1)
                {
                    var vTop = layers[0].Velocity;
                    if (vTop >= vd || vTop >= vu)
                        return Computed<LayeredModel>.Unavailable("An apparent refractor velocity is not higher than the top-layer velocity, so the dip formulas do not apply. Check the layer assignment.");
                    var a = Math.Asin(vTop / vd);
                    var b = Math.Asin(vTop / vu);
                    var critical = 0.5 * (a + b);
                    var dip = 0.5 * (a - b);
                    dipAngles[k] = dip;
                    layers.Add(new LayerVelocity(layer, vTop / Math.Sin(critical), null,
                        $"Reversed pair, exact dipping-layer solution (apparent {vd:0} / {vu:0} m/s, dip {dip * 180 / Math.PI:0.0}°)"));
                }
                else
                {
                    layers.Add(new LayerVelocity(layer, 2 * vd * vu / (vd + vu), null,
                        $"Reversed pair, harmonic mean of apparent velocities {vd:0} / {vu:0} m/s (small-dip approximation)"));
                }
            }
            else
            {
                var pooled = LayeredCommon.PooledVelocity(obs, layer);
                if (pooled is null)
                    return Computed<LayeredModel>.Unavailable($"Layer {layer} has too few arrivals for a velocity (at least three on one side of a shot are needed).");
                layers.Add(new LayerVelocity(layer, pooled.Value.Velocity, double.IsNaN(pooled.Value.StdError) ? null : pooled.Value.StdError,
                    $"Apparent velocity, pooled over {pooled.Value.Count} arrivals"));
            }
        }

        var velocities = layers.Select(l => l.Velocity).ToList();
        if (LayeredCommon.VelocityInversion(velocities) is { } inversion) return Computed<LayeredModel>.Unavailable(inversion);

        // Depth beneath each shot from its intercepts, both sides averaged where available.
        var shots = reversed ? [fwd!, rev!] : observations.Line.Gathers.Where(g => g.Included).ToList();
        var refractors = new List<RefractorResult>();
        var pointsPerRefractor = Enumerable.Range(0, layerCount - 1).Select(_ => new List<InterfacePoint>()).ToList();
        foreach (var shot in shots.OrderBy(s => s.ShotX))
        {
            var thicknesses = new List<double>();
            for (var k = 1; k < layerCount; k++)
            {
                IEnumerable<SegmentFit> sides = fits.Where(s => s.Gather == shot.Id && s.Layer == k + 1 && s.Fit is not null);
                if (reversed) sides = sides.Where(s => s.Side == (shot.Id == fwd!.Id ? 1 : -1));
                var intercepts = sides.Select(s => s.InterceptTime).Where(double.IsFinite).ToList();
                if (intercepts.Count == 0) break;
                var delay = intercepts.Average() / 2;
                double h;
                if (reversed && k == 1 && dipAngles.TryGetValue(1, out var dip))
                {
                    // Perpendicular depth h = V1·t_i / (2 cos θc); vertical depth below the shot = h / cos α.
                    var critical = Math.Asin(velocities[0] / velocities[1]);
                    var perpendicular = velocities[0] * 2 * delay / (2 * Math.Cos(critical));
                    h = perpendicular / Math.Cos(dip);
                }
                else
                {
                    h = LayeredCommon.ThicknessFromDelay(delay, thicknesses, velocities, k);
                }
                var questionable = false;
                if (!(h >= 0))
                {
                    warnings.Add($"Shot at x = {shot.ShotX:0.##} m: refractor {k} intercept implies a negative thickness ({h:0.##} m); set to 0. Check picks and layer assignment.");
                    h = 0;
                    questionable = true;
                }
                thicknesses.Add(h);
                var depth = thicknesses.Sum();
                var ground = observations.Line.ShotGroundElevation(shot);
                var count = obs.Count(o => o.Gather == shot.Id && o.Layer == k + 1);
                pointsPerRefractor[k - 1].Add(new InterfacePoint(shot.ShotX, depth, ground - depth, null, delay, count, questionable));
            }
        }
        if (shots.Any(s => s.ShotDepth > 0))
            warnings.Add("Some shots are buried; intercept depths beneath them are relative to the source depth and were not corrected.");

        for (var k = 1; k < layerCount; k++)
        {
            var pts = pointsPerRefractor[k - 1].OrderBy(p => p.X).ToImmutableArray();
            if (pts.IsEmpty) warnings.Add($"No shot has a measurable intercept for refractor {k}.");
            refractors.Add(new RefractorResult(k, velocities[k], layers[k].StdError, [], pts, null, "Intercept times beneath shot points"));
        }

        var residuals = fits.Where(f => f.Fit is not null)
            .SelectMany(f => obs.Where(o => o.Gather == f.Gather && o.Side == f.Side && o.Layer == f.Layer && o.AbsOffset > 0)
                .Select(o => new ModelResidual(o.Key, o.CorrectedTime, f.Fit!.Evaluate(o.AbsOffset))))
            .ToImmutableArray();

        return Computed<LayeredModel>.Ok(new LayeredModel(Method, [.. layers], [.. refractors], residuals, warnings.ToImmutable()));
    }
}
