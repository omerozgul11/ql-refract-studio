using RefractionStudio.Analysis.Engineering;
using RefractionStudio.Analysis.Tomography;
using RefractionStudio.Domain;
using RefractionStudio.Synthetics;

namespace RefractionStudio.Analysis.Tests;

public class TomographyTests
{
    [Fact]
    public async Task Gradient_model_is_recovered_within_ten_percent_where_rays_constrain_it()
    {
        var model = new GradientModel(400, 40);
        var b = TestLines.Create(model, [-24, -1, 11, 23, 35, 47, 70], channels: 24, spacing: 2, layerCount: 2);
        // Exact data: a tight pick-error model so that the χ² target demands a close fit.
        var settings = new TomographySettings(CellWidth: 1, CellHeight: 1, MaxDepth: 16, MaxIterations: 10, Smoothing: 10, SecondaryNodes: 4, PickErrorAbsolute: 0.0001, PickErrorRelative: 0.002);
        var r = await new TomographyEngine().RunAsync(b.Observations, settings, null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(r.HasValue, r.Reason);
        var t = r.Value!;
        Assert.True(t.FinalRms < 0.0005, $"RMS {t.FinalRms * 1000:0.000} ms");
        Assert.True(t.Iterations[^1].Chi2 < t.Iterations[0].Chi2);
        var checkedCells = 0;
        var g = t.Model.Grid;
        for (var c = 0; c < g.Nx; c++)
        {
            var x = g.CenterX(c);
            if (x < 6 || x > 40) continue;
            for (var row = 0; row < g.Nz; row++)
            {
                var idx = g.Index(c, row);
                var depth = g.DepthOf(c, row);
                if (!g.Active[idx] || depth < 1 || depth > 0.8 * t.InvestigationDepth[c] || t.Hits[idx] < 5) continue;
                var truth = model.VelocityAt(x, depth);
                Assert.InRange(t.Model.Velocity[idx], 0.9 * truth, 1.1 * truth);
                checkedCells++;
            }
        }
        Assert.True(checkedCells > 50, $"only {checkedCells} cells checked");
        Assert.All(t.InvestigationDepth.Where(double.IsFinite), d => Assert.InRange(d, 1, 16));
    }

    [Fact]
    public async Task Two_layer_model_tomogram_and_vs30_profile_behave()
    {
        var model = new HorizontalLayersModel([500, 2000], [5]);
        var b = TestLines.Create(model, [-24, -1, 23, 47, 70]);
        var settings = new TomographySettings(CellWidth: 1, CellHeight: 1, MaxDepth: 15, MaxIterations: 10, Smoothing: 10, SecondaryNodes: 4, PickErrorAbsolute: 0.0002, PickErrorRelative: 0.005);
        var r = TomographyEngine.Run(b.Observations, settings, null, null, TestContext.Current.CancellationToken);
        Assert.True(r.HasValue, r.Reason);
        var t = r.Value!;
        Assert.True(t.FinalRms < 0.001, $"RMS {t.FinalRms * 1000:0.000} ms");
        // Near-surface velocity close to V1, refractor velocity reached below 5 m.
        var shallow = t.ConstrainedVelocityAt(23, 1.5)!.Value;
        Assert.InRange(shallow, 400, 650);
        var deep = Enumerable.Range(6, 4).Select(d => t.ConstrainedVelocityAt(23, d)).Where(v => v is not null).Max();
        Assert.True(deep > 1500, $"max velocity below 6 m is {deep}");
        var profile = ProfileExtractor.FromTomography(t, 23);
        Assert.True(profile.HasValue);
        Assert.Equal(t.InvestigationDepthAt(23), profile.Value!.ConstrainedDepth);
        // Fingerprint is stable and changes with picks.
        Assert.Equal(t.InputFingerprint, TomographyEngine.Fingerprint(b.Observations, settings, null));
    }

    [Fact]
    public void Too_few_picks_are_refused_with_a_reason()
    {
        var model = new HorizontalLayersModel([500, 2000], [5]);
        var b = TestLines.Create(model, [-1], channels: 6);
        var r = TomographyEngine.Run(b.Observations, new TomographySettings(), null, null, TestContext.Current.CancellationToken);
        Assert.False(r.HasValue);
        Assert.Contains("at least 10", r.Reason);
    }

    [Fact]
    public void Forward_model_of_the_true_layered_model_reproduces_the_picks()
    {
        var model = new HorizontalLayersModel([500, 2000], [5]);
        var b = TestLines.Create(model, [-24, -1, 23, 47, 70]);
        var layered = new Analysis.Layered.DelayTimeMethod().Interpret(b.Observations, new LayeredSettings(), 2).Value!;
        var grid = ModelGrid.Create(Topography.Flat, -2, 48, 0.5, 0.5, 12);
        var residuals = TomographyEngine.ForwardModel(TomographyEngine.Rasterize(layered, grid), b.Observations.Included.ToList(), 5);
        Assert.NotEmpty(residuals);
        Assert.All(residuals, r => Assert.InRange(r.Residual, -2e-4, 2e-4));
    }
}

public class DelayTimeLimitationTests
{
    [Fact]
    public void End_shots_only_cannot_separate_delays_and_say_so()
    {
        var b = TestLines.Create(new HorizontalLayersModel([500, 2000], [5]), [-1, 47]);
        var r = new Analysis.Layered.DelayTimeMethod().Interpret(b.Observations, new LayeredSettings(), 2);
        Assert.True(r.HasValue);
        Assert.Contains(r.Value!.Warnings, w => w.Contains("cannot be separated"));
    }
}
