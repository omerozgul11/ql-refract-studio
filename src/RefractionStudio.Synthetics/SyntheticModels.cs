using RefractionStudio.Analysis.Tomography;
using RefractionStudio.Domain;

namespace RefractionStudio.Synthetics;

/// <summary>An earth model with known first-arrival times.</summary>
public interface ISyntheticModel
{
    /// <summary>Description for validation reports.</summary>
    string Description { get; }

    /// <summary>First-arrival travel times from a source to receivers (s).</summary>
    double[] FirstArrivals((double X, double Z) source, IReadOnlyList<(double X, double Z)> receivers);

    /// <summary>True velocity at depth below ground at x.</summary>
    double VelocityAt(double x, double depth);
}

/// <summary>
/// Horizontal layers over a half-space, flat ground at elevation 0, surface sources and receivers.
/// Exact first arrivals: min(x/V₁, x/V_k + Σ_{j&lt;k} 2h_j√(1/V_j² − 1/V_k²)).
/// </summary>
public sealed class HorizontalLayersModel : ISyntheticModel
{
    public HorizontalLayersModel(IReadOnlyList<double> velocities, IReadOnlyList<double> thicknesses)
    {
        if (velocities.Count != thicknesses.Count + 1) throw new ArgumentException("Need one more velocity than thicknesses (half-space).");
        for (var i = 1; i < velocities.Count; i++)
            if (velocities[i] <= velocities[i - 1]) throw new ArgumentException("Velocities must increase with depth.");
        Velocities = velocities;
        Thicknesses = thicknesses;
    }

    public IReadOnlyList<double> Velocities { get; }

    public IReadOnlyList<double> Thicknesses { get; }

    public string Description => "Horizontal layers: " + string.Join(", ", Velocities.Select((v, i) => i < Thicknesses.Count ? $"{v:0} m/s × {Thicknesses[i]:0.##} m" : $"{v:0} m/s half-space"));

    public double InterceptTime(int refractorLayer)
    {
        double t = 0;
        var vk = Velocities[refractorLayer];
        for (var j = 0; j < refractorLayer; j++)
            t += 2 * Thicknesses[j] * Math.Sqrt(1 / (Velocities[j] * Velocities[j]) - 1 / (vk * vk));
        return t;
    }

    public double[] FirstArrivals((double X, double Z) source, IReadOnlyList<(double X, double Z)> receivers)
        => [.. receivers.Select(r =>
        {
            var x = Math.Abs(r.X - source.X);
            var t = x / Velocities[0];
            for (var k = 1; k < Velocities.Count; k++)
            {
                // A head wave exists only beyond its critical distance.
                var critical = 0.0;
                for (var j = 0; j < k; j++)
                {
                    var sin = Velocities[j] / Velocities[k];
                    critical += 2 * Thicknesses[j] * sin / Math.Sqrt(1 - sin * sin);
                }
                if (x >= critical) t = Math.Min(t, x / Velocities[k] + InterceptTime(k));
            }
            return t;
        })];

    public double VelocityAt(double x, double depth)
    {
        var top = 0.0;
        for (var i = 0; i < Thicknesses.Count; i++)
        {
            top += Thicknesses[i];
            if (depth < top) return Velocities[i];
        }
        return Velocities[^1];
    }
}

/// <summary>
/// One layer over a planar dipping half-space, flat ground (elevation 0).
/// With perpendicular depth h_s beneath the source and dip α (positive = interface deepens towards +x),
/// the head-wave time is t = (|x|/V₁)·sin(θc ± α) + 2h_s·cos θc / V₁ ("+" shooting down-dip).
/// </summary>
public sealed class DippingLayerModel : ISyntheticModel
{
    public DippingLayerModel(double v1, double v2, double verticalDepthAtZero, double dipRadians)
    {
        if (v2 <= v1) throw new ArgumentException("V2 must exceed V1.");
        V1 = v1; V2 = v2; DepthAtZero = verticalDepthAtZero; Dip = dipRadians;
    }

    public double V1 { get; }
    public double V2 { get; }
    public double DepthAtZero { get; }
    public double Dip { get; }

    public string Description => $"Dipping refractor: {V1:0} m/s over {V2:0} m/s, vertical depth {DepthAtZero:0.##} m at x = 0, dip {Dip * 180 / Math.PI:0.##}°";

    /// <summary>Vertical depth of the interface at x.</summary>
    public double DepthAt(double x) => DepthAtZero + x * Math.Tan(Dip);

    public double[] FirstArrivals((double X, double Z) source, IReadOnlyList<(double X, double Z)> receivers)
    {
        var critical = Math.Asin(V1 / V2);
        var hPerp = DepthAt(source.X) * Math.Cos(Dip);
        return [.. receivers.Select(r =>
        {
            var dx = r.X - source.X;
            var x = Math.Abs(dx);
            var direct = x / V1;
            var alpha = dx >= 0 ? Dip : -Dip; // shooting towards +x is down-dip for a positive dip
            var hReceiver = DepthAt(r.X) * Math.Cos(Dip);
            // Critical distance along the surface for this geometry (both legs must be real).
            var legs = hPerp * Math.Tan(critical) + hReceiver * Math.Tan(critical);
            if (x * Math.Cos(alpha) < legs) return direct;
            var head = x / V1 * Math.Sin(critical + alpha) + 2 * hPerp * Math.Cos(critical) / V1;
            return Math.Min(direct, head);
        })];
    }

    public double VelocityAt(double x, double depth) => depth < DepthAt(x) ? V1 : V2;
}

/// <summary>
/// An arbitrary velocity function v(x, depth) with topography; first arrivals from a fine-grid
/// shortest-path solution on square cells of the given size with 6 secondary nodes. Used where no
/// closed form exists; its accuracy is established by the solver's own analytic tests.
/// </summary>
public sealed class GridSyntheticModel : ISyntheticModel
{
    private readonly ModelGrid _grid;
    private readonly double[] _slowness;
    private readonly ShortestPathSolver _solver = new(6);

    public GridSyntheticModel(Func<double, double, double> velocity, Topography topography, double xMin, double xMax, double maxDepth, double cellSize, string description)
    {
        _velocity = velocity;
        Description = description;
        _grid = ModelGrid.Create(topography, xMin, xMax, cellSize, cellSize, maxDepth);
        _slowness = new double[_grid.CellCount];
        for (var c = 0; c < _grid.Nx; c++)
            for (var r = 0; r < _grid.Nz; r++)
            {
                var i = _grid.Index(c, r);
                _slowness[i] = _grid.Active[i] ? 1 / velocity(_grid.CenterX(c), _grid.DepthOf(c, r)) : double.NaN;
            }
    }

    private readonly Func<double, double, double> _velocity;

    public string Description { get; }

    public double[] FirstArrivals((double X, double Z) source, IReadOnlyList<(double X, double Z)> receivers)
        => [.. _solver.Solve(_grid, _slowness, source, receivers, traceRays: false).Select(r => r.Time)];

    public double VelocityAt(double x, double depth) => _velocity(x, depth);
}

/// <summary>Linear velocity gradient v = v₀ + g·depth with flat ground; t = (2/g)·asinh(g·x / 2v₀).</summary>
public sealed class GradientModel(double v0, double gradient) : ISyntheticModel
{
    public double V0 { get; } = v0;
    public double Gradient { get; } = gradient;

    public string Description => $"Linear gradient: {V0:0} m/s at the surface + {Gradient:0.##} (m/s)/m";

    public double[] FirstArrivals((double X, double Z) source, IReadOnlyList<(double X, double Z)> receivers)
        => [.. receivers.Select(r => 2 / Gradient * Math.Asinh(Gradient * Math.Abs(r.X - source.X) / (2 * V0)))];

    public double VelocityAt(double x, double depth) => V0 + Gradient * depth;
}
