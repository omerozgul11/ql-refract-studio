using RefractionStudio.Analysis.Numerics;
using RefractionStudio.Analysis.TravelTimes;
using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.Layered;

/// <summary>
/// Least-squares delay-time (time-term) method (Scheidegger &amp; Willmore 1957; Willmore &amp; Bancroft
/// 1960; Barry 1967). Each head-wave arrival of refractor k is modelled as
/// t = a_s + a_r + Σ_m s_m·L_m, where a_s and a_r are the delay times at the source and receiver,
/// s_m the refractor slowness in lateral segment m and L_m the part of the source–receiver interval
/// lying in segment m. Sources and receivers within the coincidence tolerance share one delay;
/// a surface source between receivers is tied to the interpolated receiver delay (a soft constraint
/// with weight 10), which separates source and receiver delays. Adjacent slownesses are tied by
/// <see cref="LayeredSettings.VelocitySmoothing"/>. Depths follow by layer stripping:
/// a_k = Σ_{j≤k} h_j √(1/V_j² − 1/V_{k+1}²).
/// </summary>
public sealed class DelayTimeMethod : ILayeredMethod
{
    private const double SourceTieWeight = 10.0;
    private const double Damping = 1e-6;

    public LayeredMethod Method => LayeredMethod.DelayTime;

    public Computed<LayeredModel> Interpret(LineObservations observations, LayeredSettings settings, int layerCount)
    {
        var obs = observations.Included.ToList();
        var warnings = ImmutableArray.CreateBuilder<string>();
        var v1 = LayeredCommon.TopLayerVelocity(obs, settings);
        if (!v1.HasValue) return Computed<LayeredModel>.Unavailable(v1.Reason!);
        var layers = new List<LayerVelocity> { v1.Value! };
        var refractors = new List<RefractorResult>();
        var residuals = ImmutableArray.CreateBuilder<ModelResidual>();
        var tolerance = settings.CoincidenceTolerance ?? observations.TypicalSpacing / 4;

        for (var k = 1; k < layerCount; k++)
        {
            var layer = k + 1;
            var data = obs.Where(o => o.Layer == layer && o.AbsOffset > 0).ToList();
            var solved = Solve(observations, data, settings, tolerance, LayeredCommon.Fixed(settings, layer));
            if (!solved.HasValue) return Computed<LayeredModel>.Unavailable($"Refractor {k}: {solved.Reason}");
            var s = solved.Value!;
            foreach (var n in s.Notes) warnings.Add($"Refractor {k}: {n}");
            layers.Add(new LayerVelocity(layer, s.Velocity, s.VelocityStdError, s.VelocityBasis));
            var velocities = layers.Select(l => l.Velocity).ToList();
            if (LayeredCommon.VelocityInversion(velocities) is { } inversion) return Computed<LayeredModel>.Unavailable(inversion);

            var points = ImmutableArray.CreateBuilder<InterfacePoint>();
            var extrapolated = false;
            foreach (var node in s.Nodes.Where(n => n.IsSurface).OrderBy(n => n.X))
            {
                double[] upper = [];
                if (k > 1)
                {
                    var u = LayeredCommon.UpperThicknesses(refractors, node.X);
                    if (u is null) continue;
                    upper = u.Value.Thicknesses;
                    extrapolated |= u.Value.Extrapolated;
                }
                var h = LayeredCommon.ThicknessFromDelay(node.Delay, upper, velocities, k);
                var questionable = false;
                if (!(h >= 0))
                {
                    warnings.Add($"Refractor {k} at x = {node.X:0.##} m: delay time {node.Delay * 1000:0.00} ms gives a negative thickness ({h:0.##} m); set to 0.");
                    h = 0;
                    questionable = true;
                }
                var depth = upper.Sum() + h;
                var q = LayeredCommon.VerticalSlowness(velocities[k - 1], velocities[k]);
                points.Add(new InterfacePoint(node.X, depth, LayeredCommon.Ground(observations, node.X) - depth,
                    node.DelayStdError is { } e && q > 0 ? e / q : null, node.Delay, node.Count, questionable));
            }
            if (extrapolated) warnings.Add($"Refractor {k}: shallower layer thicknesses were held constant beyond their measured range.");
            refractors.Add(new RefractorResult(k, s.Velocity, s.VelocityStdError, s.Lateral, points.ToImmutable(), s.Rms, s.Basis));
            residuals.AddRange(s.Residuals);
        }
        return Computed<LayeredModel>.Ok(new LayeredModel(Method, [.. layers], [.. refractors], residuals.ToImmutable(), warnings.ToImmutable()));
    }

    private sealed class Node
    {
        public required int Index;
        public required double X;
        public required bool IsSurface;
        public double Delay;
        public double? DelayStdError;
        public int Count;
        public bool HasReceiver;
    }

    private sealed record Solution(
        double Velocity,
        double? VelocityStdError,
        string VelocityBasis,
        ImmutableArray<VelocitySegment> Lateral,
        IReadOnlyList<Node> Nodes,
        double Rms,
        string Basis,
        ImmutableArray<ModelResidual> Residuals,
        ImmutableArray<string> Notes);

    private static Computed<Solution> Solve(LineObservations observations, List<Observation> data, LayeredSettings settings, double tolerance, double? fixedVelocity)
    {
        if (data.Count < 4) return Computed<Solution>.Unavailable($"only {data.Count} head-wave arrivals are assigned to it; at least 4 are needed.");
        var notes = ImmutableArray.CreateBuilder<string>();

        // Delay nodes: receivers clustered by position; surface sources share a coincident receiver node.
        var nodes = new List<Node>();
        Node NodeAt(double x, bool surface, bool receiver)
        {
            if (surface)
            {
                foreach (var n in nodes)
                {
                    if (n.IsSurface && Math.Abs(n.X - x) <= tolerance)
                    {
                        n.HasReceiver |= receiver;
                        return n;
                    }
                }
            }
            var node = new Node { Index = nodes.Count, X = x, IsSurface = surface, HasReceiver = receiver };
            nodes.Add(node);
            return node;
        }
        foreach (var o in data.OrderBy(o => o.ReceiverX)) NodeAt(o.ReceiverX, surface: true, receiver: true);
        var sourceNode = new Dictionary<GatherId, Node>();
        foreach (var g in data.Select(o => o.Gather).Distinct())
        {
            var first = data.First(o => o.Gather == g);
            sourceNode[g] = NodeAt(first.ShotX, surface: first.ShotDepth <= 0, receiver: false);
            if (first.ShotDepth > 0) notes.Add($"the buried shot at x = {first.ShotX:0.##} m has its own delay (not converted to depth).");
        }

        var segments = Math.Max(1, fixedVelocity is null ? settings.RefractorVelocitySegments : 1);
        var xMin = Math.Min(data.Min(o => o.ShotX), data.Min(o => o.ReceiverX));
        var xMax = Math.Max(data.Max(o => o.ShotX), data.Max(o => o.ReceiverX));
        var width = (xMax - xMin) / segments;
        if (!(width > 0)) return Computed<Solution>.Unavailable("all arrivals are at one position.");

        var nNodes = nodes.Count;
        var nUnknowns = nNodes + (fixedVelocity is null ? segments : 0);
        var rows = new List<(int[] Columns, double[] Values, double Rhs, double Weight)>();
        foreach (var o in data)
        {
            var cols = new List<int>();
            var vals = new List<double>();
            var sNode = sourceNode[o.Gather];
            var rNode = NodeAt(o.ReceiverX, true, true);
            if (sNode == rNode) continue; // zero-length path
            cols.Add(sNode.Index); vals.Add(1);
            cols.Add(rNode.Index); vals.Add(1);
            var a = Math.Min(o.ShotX, o.ReceiverX);
            var b = Math.Max(o.ShotX, o.ReceiverX);
            var rhs = o.CorrectedTime;
            if (fixedVelocity is { } fv)
            {
                rhs -= (b - a) / fv;
            }
            else
            {
                for (var m = 0; m < segments; m++)
                {
                    var s0 = xMin + m * width;
                    var s1 = m == segments - 1 ? xMax : s0 + width;
                    var len = Math.Max(0, Math.Min(b, s1) - Math.Max(a, s0));
                    if (len > 0) { cols.Add(nNodes + m); vals.Add(len); }
                }
            }
            rows.Add(([.. cols], [.. vals], rhs, 1.0));
            sNode.Count++;
            rNode.Count++;
        }
        if (rows.Count < nUnknowns / 2.0)
            notes.Add("there are few arrivals relative to the number of unknown delays; delays are poorly constrained.");

        // Tie surface sources that are not on a receiver node to interpolated receiver delays.
        var receiverNodes = nodes.Where(n => n.HasReceiver).OrderBy(n => n.X).ToList();
        var tied = 0;
        foreach (var s in sourceNode.Values.Distinct().Where(n => n.IsSurface && !n.HasReceiver))
        {
            var left = receiverNodes.LastOrDefault(n => n.X < s.X);
            var right = receiverNodes.FirstOrDefault(n => n.X > s.X);
            if (left is null || right is null) continue;
            var w = (s.X - left.X) / (right.X - left.X);
            rows.Add(([s.Index, left.Index, right.Index], [1, -(1 - w), -w], 0, SourceTieWeight));
            tied++;
        }
        var shared = sourceNode.Values.Distinct().Count(n => n.HasReceiver);
        if (shared + tied == 0)
            notes.Add("no shot lies within the receiver spread, so source and receiver delays cannot be separated reliably; depths beneath receivers are uncertain.");

        // Slowness smoothing between adjacent segments (in units of time over the mean segment width).
        if (fixedVelocity is null && segments > 1 && settings.VelocitySmoothing > 0)
        {
            for (var m = 0; m + 1 < segments; m++)
                rows.Add(([nNodes + m, nNodes + m + 1], [width, -width], 0, settings.VelocitySmoothing));
        }
        // Tiny damping keeps the system positive definite; its bias is below 1e-6 of a delay.
        for (var i = 0; i < nNodes; i++) rows.Add(([i], [1.0], 0, Damping));

        var solution = DenseSolver.LeastSquares(rows, nUnknowns);
        if (solution is null) return Computed<Solution>.Unavailable("the delay-time equations are singular; check that arrivals from several shots are assigned to this refractor.");
        var (x, cov) = solution.Value;

        // Residuals and error scale from data rows only.
        var residuals = ImmutableArray.CreateBuilder<ModelResidual>();
        double sse = 0;
        var nData = 0;
        foreach (var o in data)
        {
            var sNode = sourceNode[o.Gather];
            var rNode = NodeAt(o.ReceiverX, true, true);
            if (sNode == rNode) continue;
            var a = Math.Min(o.ShotX, o.ReceiverX);
            var b = Math.Max(o.ShotX, o.ReceiverX);
            var predicted = x[sNode.Index] + x[rNode.Index];
            if (fixedVelocity is { } fv) predicted += (b - a) / fv;
            else
            {
                for (var m = 0; m < segments; m++)
                {
                    var s0 = xMin + m * width;
                    var s1 = m == segments - 1 ? xMax : s0 + width;
                    predicted += x[nNodes + m] * Math.Max(0, Math.Min(b, s1) - Math.Max(a, s0));
                }
            }
            residuals.Add(new ModelResidual(o.Key, o.CorrectedTime, predicted));
            sse += (o.CorrectedTime - predicted) * (o.CorrectedTime - predicted);
            nData++;
        }
        var dof = nData - nUnknowns;
        var sigma2 = dof > 0 ? sse / dof : double.NaN;
        if (dof <= 0) notes.Add("there are no redundant arrivals, so uncertainties cannot be estimated.");
        foreach (var n in nodes)
        {
            n.Delay = x[n.Index];
            n.DelayStdError = double.IsNaN(sigma2) ? null : Math.Sqrt(sigma2 * cov[n.Index]);
        }

        double velocity;
        double? velocityError = null;
        var lateral = ImmutableArray.CreateBuilder<VelocitySegment>();
        if (fixedVelocity is { } fixedV)
        {
            velocity = fixedV;
        }
        else
        {
            for (var m = 0; m < segments; m++)
            {
                var s = x[nNodes + m];
                if (!(s > 0)) return Computed<Solution>.Unavailable($"the solved refractor slowness of segment {m + 1} is not positive; the arrivals are inconsistent with a refractor.");
                double? err = double.IsNaN(sigma2) ? null : Math.Sqrt(sigma2 * cov[nNodes + m]) / (s * s);
                var s0 = xMin + m * width;
                lateral.Add(new VelocitySegment(s0, m == segments - 1 ? xMax : s0 + width, 1 / s, err));
            }
            // Representative velocity: harmonic mean weighted by segment width (equal widths).
            velocity = segments / lateral.Sum(l => 1 / l.Velocity);
            velocityError = segments == 1 ? lateral[0].StdError : null;
        }
        var rms = nData > 0 ? Math.Sqrt(sse / nData) : double.NaN;
        var basis = $"Delay-time least squares: {nData} arrivals, {nodes.Count(n => n.IsSurface)} delay nodes, {segments} velocity segment(s), RMS {rms * 1000:0.00} ms";
        return Computed<Solution>.Ok(new Solution(velocity, velocityError, fixedVelocity is null ? "Delay-time least squares" : "Fixed by the user",
            segments > 1 ? lateral.ToImmutable() : [], nodes, rms, basis, residuals.ToImmutable(), notes.ToImmutable()));
    }
}
