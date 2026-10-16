using RefractionStudio.Analysis.Layered;
using RefractionStudio.Analysis.Numerics;
using RefractionStudio.Analysis.TravelTimes;
using RefractionStudio.Domain;
using RefractionStudio.Synthetics;

namespace RefractionStudio.Analysis.Tests;

public class RegressionTests
{
    [Fact]
    public void Ols_recovers_an_exact_line_with_zero_errors()
    {
        var x = new[] { 1.0, 2, 3, 4, 5 };
        var fit = Regression.Fit(x, x.Select(v => 0.5 + 2 * v).ToList())!;
        Assert.Equal(2, fit.Slope, 12);
        Assert.Equal(0.5, fit.Intercept, 12);
        Assert.Equal(1, fit.RSquared, 12);
        Assert.Equal(0, fit.SlopeStdError, 9);
    }

    [Fact]
    public void Ols_standard_errors_match_hand_calculation()
    {
        // x = 1..4, y = 1, 3, 2, 5 → slope 1.1, intercept 0, SSE 2.7, s² = 1.35, Sxx = 5.
        var fit = Regression.Fit([1.0, 2, 3, 4], [1.0, 3, 2, 5])!;
        Assert.Equal(1.1, fit.Slope, 12);
        Assert.Equal(0.0, fit.Intercept, 12);
        Assert.Equal(Math.Sqrt(1.35 / 5), fit.SlopeStdError, 12);
        Assert.Equal(Math.Sqrt(1.35 * (1 / 4.0 + 6.25 / 5)), fit.InterceptStdError, 12);
    }

    [Fact]
    public void Huber_regression_resists_an_outlier()
    {
        var x = Enumerable.Range(0, 12).Select(i => (double)i).ToList();
        var y = x.Select(v => 1 + 0.5 * v).ToList();
        y[7] += 20;
        var ols = Regression.Fit(x, y, RegressionMode.Ordinary)!;
        var robust = Regression.Fit(x, y, RegressionMode.Robust)!;
        Assert.True(Math.Abs(ols.Slope - 0.5) > 0.1);
        Assert.Equal(0.5, robust.Slope, 2);
        Assert.True(robust.Weights[7] < 0.1);
    }
}

public class LayeredMethodTests
{
    private static readonly double[] EndShots = [-1, 47];
    private static readonly double[] FiveShots = [-24, -1, 23, 47, 70];

    private static TestLines.Built TwoLayer() => TestLines.Create(new HorizontalLayersModel([500, 2000], [5]), FiveShots);

    [Fact]
    public void Segment_assigner_recovers_the_crossover_of_a_two_layer_model()
    {
        var b = TwoLayer();
        var model = new HorizontalLayersModel([500, 2000], [5]);
        var crossover = model.InterceptTime(1) / (1 / 500.0 - 1 / 2000.0);
        foreach (var o in b.Observations.All)
            Assert.Equal(o.AbsOffset < crossover ? 1 : 2, o.Layer);
    }

    [Fact]
    public void Pooled_direct_wave_velocity_is_exact()
    {
        var v = LayeredCommon.PooledVelocity(TwoLayer().Observations.Included, 1)!.Value;
        Assert.Equal(500, v.Velocity, 6);
    }

    [Fact]
    public void Intercept_time_unreversed_gives_velocity_and_depth()
    {
        var r = new InterceptTimeMethod().Interpret(TwoLayer().Observations, new LayeredSettings(LayeredMethod.InterceptTime), 2);
        Assert.True(r.HasValue, r.Reason);
        Assert.Equal(2000, r.Value!.Layers[1].Velocity, 3);
        Assert.All(r.Value.Refractors[0].Points, p => Assert.Equal(5, p.Depth, 6));
        Assert.Contains(r.Value.Warnings, w => w.Contains("No reversed shot pair"));
    }

    [Fact]
    public void Plus_minus_and_grm_xy0_are_identical_and_exact_for_flat_layers()
    {
        var b = TwoLayer();
        var settings = new LayeredSettings(LayeredMethod.PlusMinus, b.At(-1).Id, b.At(47).Id);
        var pm = new PlusMinusMethod().Interpret(b.Observations, settings, 2);
        var grm = new GrmMethod().Interpret(b.Observations, settings with { Method = LayeredMethod.Grm, GrmXy = 0 }, 2);
        Assert.True(pm.HasValue, pm.Reason);
        Assert.True(grm.HasValue, grm.Reason);
        Assert.Equal(2000, pm.Value!.Layers[1].Velocity, 3);
        Assert.NotEmpty(pm.Value.Refractors[0].Points);
        Assert.All(pm.Value.Refractors[0].Points, p => Assert.Equal(5, p.Depth, 6));
        Assert.Equal(pm.Value.Refractors[0].Points.Select(p => p.Depth), grm.Value!.Refractors[0].Points.Select(p => p.Depth));
    }

    [Fact]
    public void Grm_with_the_optimum_xy_is_exact_and_xy_analysis_suggests_it()
    {
        var b = TwoLayer();
        // Optimum XY = 2h·tan(i) = 2·5·500/√(2000²−500²) = 2.582 m; use the nearest half-spacing multiple (2 m).
        var optimum = GrmMethod.OptimumXyForSingleLayer(5, 500, 2000);
        Assert.Equal(2 * 5 * 500 / Math.Sqrt(2000 * 2000 - 500 * 500), optimum, 12);
        var settings = new LayeredSettings(LayeredMethod.Grm, b.At(-1).Id, b.At(47).Id, GrmXy: 2);
        var grm = new GrmMethod().Interpret(b.Observations, settings, 2);
        Assert.True(grm.HasValue, grm.Reason);
        Assert.Equal(2000, grm.Value!.Layers[1].Velocity, 3);
        Assert.All(grm.Value.Refractors[0].Points, p => Assert.Equal(5, p.Depth, 6));
        var (evaluations, suggested) = GrmMethod.AnalyzeXy(b.Observations, settings, 1, 8);
        Assert.NotEmpty(evaluations);
        Assert.NotNull(suggested);
        // Average-velocity check: at the optimum XY the formula returns V1 exactly.
        var timeDepth = 5 * Math.Sqrt(1 / (500.0 * 500) - 1 / (2000.0 * 2000));
        Assert.Equal(500, GrmMethod.AverageVelocityFromXy(2000, optimum, timeDepth), 9);
    }

    [Fact]
    public void Delay_time_inversion_is_exact_for_flat_layers()
    {
        var r = new DelayTimeMethod().Interpret(TwoLayer().Observations, new LayeredSettings(), 2);
        Assert.True(r.HasValue, r.Reason);
        Assert.Equal(2000, r.Value!.Layers[1].Velocity, 2);
        var points = r.Value.Refractors[0].Points;
        Assert.True(points.Length >= 10);
        Assert.All(points, p => Assert.Equal(5, p.Depth, 3));
        Assert.True(r.Value.Refractors[0].RmsResidual < 1e-6);
    }

    [Fact]
    public void Three_layer_model_is_recovered_by_layer_stripping()
    {
        var model = new HorizontalLayersModel([400, 1200, 3000], [3, 8]);
        var b = TestLines.Create(model, [-48, -1, 47, 95], channels: 48, spacing: 2, layerCount: 3);
        foreach (var method in new ILayeredMethod[] { new DelayTimeMethod(), new InterceptTimeMethod() })
        {
            var r = method.Interpret(b.Observations, new LayeredSettings(method.Method), 3);
            Assert.True(r.HasValue, $"{method.Method}: {r.Reason}");
            Assert.Equal(400, r.Value!.Layers[0].Velocity, 3);
            Assert.Equal(1200, r.Value.Layers[1].Velocity, 0.5);
            Assert.Equal(3000, r.Value.Layers[2].Velocity, 1.0);
            Assert.All(r.Value.Refractors[0].Points, p => Assert.Equal(3, p.Depth, 0.05));
            Assert.All(r.Value.Refractors[1].Points, p => Assert.Equal(11, p.Depth, 0.1));
        }
    }

    [Fact]
    public void Dipping_refractor_is_resolved_by_a_reversed_pair()
    {
        var dip = 5 * Math.PI / 180;
        var model = new DippingLayerModel(600, 2500, 4, dip);
        var b = TestLines.Create(model, [-1, 47], channels: 24, spacing: 2);
        var settings = new LayeredSettings(LayeredMethod.InterceptTime, b.At(-1).Id, b.At(47).Id);
        var r = new InterceptTimeMethod().Interpret(b.Observations, settings, 2);
        Assert.True(r.HasValue, r.Reason);
        Assert.Equal(2500, r.Value!.Layers[1].Velocity, 2500 * 1e-6);
        foreach (var p in r.Value.Refractors[0].Points)
            Assert.Equal(model.DepthAt(p.X), p.Depth, 1e-6);

        var pm = new PlusMinusMethod().Interpret(b.Observations, settings with { Method = LayeredMethod.PlusMinus }, 2);
        Assert.True(pm.HasValue, pm.Reason);
        Assert.Equal(2500, pm.Value!.Layers[1].Velocity, 2500 * 0.02);
        foreach (var p in pm.Value.Refractors[0].Points)
            Assert.Equal(model.DepthAt(p.X), p.Depth, model.DepthAt(p.X) * 0.05);
    }

    [Fact]
    public void Velocity_inversion_is_reported_not_hidden()
    {
        var b = TwoLayer();
        var settings = new LayeredSettings(FixedVelocities: [500, 400]);
        var r = new DelayTimeMethod().Interpret(b.Observations, settings, 2);
        Assert.False(r.HasValue);
        Assert.Contains("requires velocity to increase", r.Reason);
    }

    [Fact]
    public void Missing_reciprocal_pair_is_explained()
    {
        var r = new GrmMethod().Interpret(TwoLayer().Observations, new LayeredSettings(LayeredMethod.Grm), 2);
        Assert.False(r.HasValue);
        Assert.Contains("reversed shot pair", r.Reason);
    }
}
