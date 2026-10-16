using RefractionStudio.Analysis.Tomography;
using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.Tests;

/// <summary>Accuracy of the shortest-path ray tracer against analytic travel times.</summary>
public class ShortestPathSolverTests
{
    private static (ModelGrid Grid, double[] Slowness) Model(double length, double depth, double dx, double dz, Func<double, double> velocityAtDepth)
    {
        var grid = ModelGrid.Create(Topography.Flat, 0, length, dx, dz, depth);
        var s = new double[grid.CellCount];
        for (var c = 0; c < grid.Nx; c++)
            for (var r = 0; r < grid.Nz; r++)
                s[grid.Index(c, r)] = 1 / velocityAtDepth(grid.DepthOf(c, r));
        return (grid, s);
    }

    [Theory]
    [InlineData(3, 0.01)]
    [InlineData(5, 0.003)]
    public void Homogeneous_medium_matches_straight_rays(int secondary, double tolerance)
    {
        var (grid, s) = Model(100, 30, 1, 1, _ => 1500);
        var receivers = Enumerable.Range(1, 20).Select(i => (X: i * 5.0, Z: 0.0)).Concat([(X: 50.0, Z: -20.0), (X: 97.3, Z: -11.7)]).ToList();
        var res = new ShortestPathSolver(secondary).Solve(grid, s, (0.0, 0.0), receivers, traceRays: true);
        for (var i = 0; i < receivers.Count; i++)
        {
            var exact = Math.Sqrt(receivers[i].X * receivers[i].X + receivers[i].Z * receivers[i].Z) / 1500;
            Assert.InRange((res[i].Time - exact) / exact, -1e-12, tolerance); // network paths are never shorter than the true path
        }
    }

    [Fact]
    public void Error_over_all_ray_directions_stays_below_half_a_percent_with_five_secondary_nodes()
    {
        // The shortest-path network restricts ray directions, so the error depends on the angle of the ray to
        // the cell edges. The default (5 secondary nodes) is documented to stay below 0.5 % in every direction.
        var (grid, s) = Model(100, 90, 1, 1, _ => 1500);
        var receivers = Enumerable.Range(0, 90).Select(a => a * Math.PI / 180)
            .Select(a => (X: 80 * Math.Cos(a), Z: -80 * Math.Sin(a))).Where(p => p.X >= 0.5).ToList();
        var res = new ShortestPathSolver(5).Solve(grid, s, (0.0, 0.0), receivers, traceRays: false);
        var worst = receivers.Select((p, i) => (res[i].Time - Math.Sqrt(p.X * p.X + p.Z * p.Z) / 1500) / (Math.Sqrt(p.X * p.X + p.Z * p.Z) / 1500)).Max();
        Assert.InRange(worst, 0, 0.005);
        TestContext.Current.TestOutputHelper?.WriteLine($"Worst relative error over directions: {worst:P3}");
    }

    [Fact]
    public void Two_layer_head_wave_matches_the_intercept_formula()
    {
        const double v1 = 500, v2 = 2000, h = 5;
        // Square cells: thin cells cannot represent steep critical-angle legs (see docs/algorithms/tomography.md).
        var (grid, s) = Model(120, 20, 0.5, 0.5, d => d < h ? v1 : v2);
        var receivers = Enumerable.Range(1, 24).Select(i => (X: i * 5.0, Z: 0.0)).ToList();
        var res = new ShortestPathSolver(4).Solve(grid, s, (0.0, 0.0), receivers, traceRays: true);
        var ti = 2 * h * Math.Sqrt(1 / (v1 * v1) - 1 / (v2 * v2));
        for (var i = 0; i < receivers.Count; i++)
        {
            var x = receivers[i].X;
            var exact = Math.Min(x / v1, x / v2 + ti);
            Assert.InRange(res[i].Time - exact, -1e-9, 1e-4);
        }
        // A far-offset ray must reach the refractor.
        var ray = res[^1].Ray!;
        Assert.True(ray.Z.Min() <= -h + 1e-9);
        Assert.Equal(res[^1].Time, Enumerable.Range(0, ray.Cells.Length).Sum(k => ray.SegmentLength(k) * s[ray.Cells[k]]), 9);
    }

    [Fact]
    public void Linear_gradient_matches_the_analytic_solution()
    {
        // v(z) = v0 + g z; surface-to-surface first arrival t = (2/g)·asinh(g x / (2 v0)).
        const double v0 = 400, g = 40;
        var (grid, s) = Model(100, 40, 0.5, 0.25, d => v0 + g * d);
        var receivers = Enumerable.Range(1, 10).Select(i => (X: i * 10.0, Z: 0.0)).ToList();
        var res = new ShortestPathSolver(3).Solve(grid, s, (0.0, 0.0), receivers, traceRays: false);
        for (var i = 0; i < receivers.Count; i++)
        {
            var x = receivers[i].X;
            var exact = 2 / g * Math.Asinh(g * x / (2 * v0));
            Assert.InRange(Math.Abs(res[i].Time - exact) / exact, 0, 0.01);
        }
    }

    [Fact]
    public void Topography_deactivates_air_cells_and_rays_stay_in_the_ground()
    {
        var topo = Topography.Create([new(0, 10), new(50, 20), new(100, 10)]);
        var grid = ModelGrid.Create(topo, 0, 100, 1, 1, 20);
        var s = Enumerable.Range(0, grid.CellCount).Select(i => grid.Active[i] ? 1 / 1000.0 : double.NaN).ToArray();
        Assert.Contains(false, grid.Active);
        var res = new ShortestPathSolver(3).Solve(grid, s, (0.0, 10.0), [(100.0, 10.0)], traceRays: true);
        Assert.True(double.IsFinite(res[0].Time));
        // The ray cannot cut through the air above the hill; it is at least as long as the straight chord.
        Assert.True(res[0].Time >= 100 / 1000.0);
        foreach (var c in res[0].Ray!.Cells) Assert.True(grid.Active[c]);
    }
}
