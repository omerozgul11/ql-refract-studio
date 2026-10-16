using System.Globalization;
using System.Text;
using RefractionStudio.Analysis.Engineering;
using RefractionStudio.Analysis.Layered;
using RefractionStudio.Analysis.Tomography;
using RefractionStudio.Analysis.TravelTimes;
using RefractionStudio.Domain;
using RefractionStudio.Domain.Units;
using RefractionStudio.Synthetics;

namespace RefractionStudio.Cli;

/// <summary>
/// Scientific validation: every high-risk calculation is compared with an independently known answer
/// (hand calculation, closed-form solution or synthetic model with known parameters).
/// </summary>
public static class Validation
{
    private sealed record Case(string Group, string Name, string Input, double Expected, double Actual, double Tolerance, string Unit, string Reference)
    {
        public double Difference => Actual - Expected;
        public bool Pass => double.IsFinite(Actual) && Math.Abs(Difference) <= Tolerance;
    }

    public static async Task<int> RunAsync(Args a)
    {
        var folder = Path.GetFullPath(a.Option("out"));
        Directory.CreateDirectory(folder);
        var cases = new List<Case>();
        Formulas(cases);
        RayTracing(cases);
        LayeredMethods(cases);
        await TomographyAndVs30(cases);

        var md = new StringBuilder();
        md.AppendLine("# Refraction Studio — Scientific validation report");
        md.AppendLine();
        md.AppendLine($"Generated {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC by `refraction-studio-cli validate`, version {Commands.Version}.");
        md.AppendLine();
        md.AppendLine("Each row compares the software's result with an independently known value: a hand calculation, a closed-form solution, " +
                      "or a synthetic earth model whose parameters are known exactly. Tolerances reflect the stated accuracy of each method.");
        md.AppendLine();
        md.AppendLine($"**Result: {cases.Count(c => c.Pass)} of {cases.Count} cases pass.**");
        foreach (var group in cases.GroupBy(c => c.Group))
        {
            md.AppendLine();
            md.AppendLine($"## {group.Key}");
            md.AppendLine();
            md.AppendLine("| Case | Input | Expected | Actual | Difference | Tolerance | Result | Reference |");
            md.AppendLine("|---|---|---:|---:|---:|---:|---|---|");
            foreach (var c in group)
                md.AppendLine($"| {c.Name} | {c.Input} | {F(c.Expected)} {c.Unit} | {F(c.Actual)} {c.Unit} | {F(c.Difference)} | ±{F(c.Tolerance)} | {(c.Pass ? "PASS" : "**FAIL**")} | {c.Reference} |");
        }
        md.AppendLine();
        md.AppendLine("Automated unit tests (`dotnet test`) cover the same calculations and many more edge cases; this report documents the quantitative agreement.");
        var path = Path.Combine(folder, "validation-report.md");
        await File.WriteAllTextAsync(path, md.ToString());
        Console.WriteLine($"{cases.Count(c => c.Pass)}/{cases.Count} validation cases pass. Report: {path}");
        foreach (var f in cases.Where(c => !c.Pass)) Console.WriteLine($"FAIL {f.Group}: {f.Name} expected {f.Expected} actual {f.Actual}");
        return cases.All(c => c.Pass) ? 0 : 3;
    }

    private static string F(double v) => double.IsFinite(v)
        ? (Math.Abs(v) >= 1e5 || (Math.Abs(v) < 1e-3 && v != 0) ? v.ToString("0.####e+0", CultureInfo.InvariantCulture) : v.ToString("0.#####", CultureInfo.InvariantCulture))
        : "n/a";

    private static void Formulas(List<Case> cases)
    {
        const string g = "Engineering formulas (hand calculations)";
        var calc = new EngineeringPropertyCalculator();
        var rho = Computed<DensityValue>.Ok(new DensityValue(1900, false, "test"));
        var p = calc.Calculate(1800, 400, rho);
        cases.Add(new(g, "Poisson's ratio", "Vp 1800, Vs 400 m/s", 2.92e6 / 6.16e6, p.PoissonRatio.Value, 1e-12, "", "ν = (Vp²−2Vs²)/(2(Vp²−Vs²))"));
        cases.Add(new(g, "Shear modulus", "ρ 1900 kg/m³, Vs 400 m/s", 3.04e8, p.ShearModulus.Value, 1e-3, "Pa", "G = ρVs²"));
        cases.Add(new(g, "Young's modulus", "as above", 2 * 3.04e8 * (1 + 2.92e6 / 6.16e6), p.YoungsModulus.Value, 1e-3, "Pa", "E = 2G(1+ν)"));
        cases.Add(new(g, "Bulk modulus", "as above", 1900 * (3.24e6 - 4.0 / 3 * 1.6e5), p.BulkModulus.Value, 1e-3, "Pa", "K = ρ(Vp² − 4Vs²/3)"));
        cases.Add(new(g, "E cross-check", "as above", 9 * p.BulkModulus.Value * p.ShearModulus.Value / (3 * p.BulkModulus.Value + p.ShearModulus.Value), p.YoungsModulus.Value, 1e-3, "Pa", "E = 9KG/(3K+G)"));
        var profile = new VelocityProfile(0, [new(0, 5, 200), new(5, 20, 400), new(20, double.PositiveInfinity, 800)], 35, "test");
        cases.Add(new(g, "Vs30", "200 m/s 0–5 m, 400 m/s 5–20 m, 800 m/s below", 400, new Vs30Calculator().Calculate(profile, 0, 30, false).Value!.Velocity, 1e-9, "m/s", "30/Σ(h/V) = 30/0.075"));
        cases.Add(new(g, "Gardner density", "Vp 2000 m/s", 310 * Math.Pow(2000, 0.25), DensityProvider.Gardner(2000), 1e-9, "kg/m³", "Gardner et al. (1974)"));
        cases.Add(new(g, "Brocher density", "Vp 2000 m/s", 1905.392, DensityProvider.Brocher(2000), 1e-6, "kg/m³", "Brocher (2005) eq. 1"));
        cases.Add(new(g, "Feet → metres", "1 ft", 0.3048, UnitConversion.FeetToMetres(1), 1e-15, "m", "NIST SP 811"));
        cases.Add(new(g, "pcf → kg/m³", "1 pcf", 16.018463373960138, UnitConversion.PcfToKgM3(1), 1e-9, "kg/m³", "NIST SP 811"));
        cases.Add(new(g, "ksf → Pa", "1 ksf", 47880.25898033584, UnitConversion.KsfToPascals(1), 1e-6, "Pa", "NIST SP 811"));
        cases.Add(new(g, "Straight-ray distance", "x 30 m, Δz 40 m", 50, new Observation(new PickKey(GatherId.New(), 1), 1, 0, 0, 0, 0, 30, 40, 0, 0, 0, 0, 1, true, null, new ArrivalPick(0, ArrivalOrigin.Manual)).Distance, 1e-12, "m", "R = √(x² + z²)"));
    }

    private static void RayTracing(List<Case> cases)
    {
        const string g = "Ray tracing (closed-form travel times)";
        // Homogeneous.
        var grid = ModelGrid.Create(Topography.Flat, 0, 100, 1, 1, 30);
        var s = Enumerable.Repeat(1 / 1500.0, grid.CellCount).ToArray();
        var r = new ShortestPathSolver(5).Solve(grid, s, (0, 0), [(80, -20)], false)[0];
        cases.Add(new(g, "Homogeneous medium", "1500 m/s, receiver at (80, −20) m", Math.Sqrt(80 * 80 + 20 * 20) / 1500, r.Time, 0.005 * Math.Sqrt(6800) / 1500, "s",
            "t = R/V; tolerance 0.5 %: the worst direction-dependent error of the 5-node network is about 0.34 % (unit test)"));
        // Two-layer head wave.
        var g2 = ModelGrid.Create(Topography.Flat, 0, 120, 0.5, 0.5, 20);
        var s2 = new double[g2.CellCount];
        for (var c = 0; c < g2.Nx; c++) for (var row = 0; row < g2.Nz; row++) s2[g2.Index(c, row)] = g2.DepthOf(c, row) < 5 ? 1 / 500.0 : 1 / 2000.0;
        var r2 = new ShortestPathSolver(4).Solve(g2, s2, (0, 0), [(100, 0)], false)[0];
        cases.Add(new(g, "Head wave, two layers", "500/2000 m/s, h = 5 m, x = 100 m", 100 / 2000.0 + 2 * 5 * Math.Sqrt(1 / 250000.0 - 1 / 4e6), r2.Time, 1e-4, "s", "t = x/V₂ + 2h√(1/V₁²−1/V₂²)"));
        // Linear gradient.
        var model = new GradientModel(400, 40);
        var g3 = ModelGrid.Create(Topography.Flat, 0, 100, 0.5, 0.5, 40);
        var s3 = new double[g3.CellCount];
        for (var c = 0; c < g3.Nx; c++) for (var row = 0; row < g3.Nz; row++) s3[g3.Index(c, row)] = 1 / model.VelocityAt(0, g3.DepthOf(c, row));
        var r3 = new ShortestPathSolver(4).Solve(g3, s3, (0, 0), [(80, 0)], false)[0];
        var exact = 2 / 40.0 * Math.Asinh(40 * 80 / (2 * 400.0));
        cases.Add(new(g, "Linear velocity gradient", "v = 400 + 40·z, x = 80 m", exact, r3.Time, 0.01 * exact, "s", "t = (2/g)·asinh(gx/2v₀)"));
    }

    private static TravelTimes Lines(ISyntheticModel model, double[] shots, int channels = 24, double spacing = 2, int layers = 2)
    {
        var spread = Spread.Regular("S", channels, 0, spacing);
        var gathers = shots.Select((x, i) => new ShotGather(GatherId.New(), $"S{i + 1}", x, null, 0, spread.Id, CombineMode.Single, [new GatherMember(RecordId.New(), 1, SourceKind.Vertical)])).ToImmutableArray();
        var line = new SurveyLine(LineId.New(), "Validation", WaveType.P, Topography.Flat, [spread], gathers);
        var picks = ImmutableDictionary.CreateBuilder<PickKey, ArrivalPick>();
        foreach (var gth in gathers)
        {
            var rec = Enumerable.Range(1, channels).Select(c => (spread.PositionOf(c)!.Value, 0.0)).ToList();
            var t = model.FirstArrivals((gth.ShotX, 0), rec);
            for (var c = 1; c <= channels; c++) picks[new PickKey(gth.Id, c)] = new ArrivalPick(t[c - 1], ArrivalOrigin.Manual);
        }
        var interp = LineInterpretation.Create() with { Picks = picks.ToImmutable() };
        var obs = ObservationBuilder.Build(line, interp);
        interp = interp with { Segments = new SegmentAssigner().Suggest(obs, layers) };
        return new TravelTimes(line, interp, ObservationBuilder.Build(line, interp), gathers);
    }

    private sealed record TravelTimes(SurveyLine Line, LineInterpretation Interp, LineObservations Obs, ImmutableArray<ShotGather> Gathers);

    private static void LayeredMethods(List<Case> cases)
    {
        const string g = "Layered methods (synthetic models with known answers)";
        var two = Lines(new HorizontalLayersModel([500, 2000], [5]), [-24, -1, 23, 47, 70]);
        var fwd = two.Gathers.Single(x => x.ShotX == -1).Id;
        var rev = two.Gathers.Single(x => x.ShotX == 47).Id;
        void Add(string method, ILayeredMethod m, LayeredSettings settings)
        {
            var r = m.Interpret(two.Obs, settings, 2);
            if (!r.HasValue)
            {
                cases.Add(new(g, $"{method}: V₂", "500/2000 m/s, h = 5 m", 2000, double.NaN, 1, "m/s", r.Reason ?? ""));
                return;
            }
            cases.Add(new(g, $"{method}: V₁", "500/2000 m/s, h = 5 m", 500, r.Value!.Layers[0].Velocity, 0.5, "m/s", "Pooled direct-wave slope"));
            cases.Add(new(g, $"{method}: V₂", "500/2000 m/s, h = 5 m", 2000, r.Value.Layers[1].Velocity, 2, "m/s", ""));
            var depths = r.Value.Refractors[0].Points.Select(p => p.Depth).ToList();
            cases.Add(new(g, $"{method}: max depth error", $"{depths.Count} positions", 5, depths.MaxBy(d => Math.Abs(d - 5)), 0.01, "m", ""));
        }
        Add("Intercept-time", new InterceptTimeMethod(), new LayeredSettings(LayeredMethod.InterceptTime, fwd, rev));
        Add("Plus–minus", new PlusMinusMethod(), new LayeredSettings(LayeredMethod.PlusMinus, fwd, rev));
        Add("GRM (XY = 2 m)", new GrmMethod(), new LayeredSettings(LayeredMethod.Grm, fwd, rev, GrmXy: 2));
        Add("Delay-time", new DelayTimeMethod(), new LayeredSettings(LayeredMethod.DelayTime));

        var dip = 5 * Math.PI / 180;
        var dipping = new DippingLayerModel(600, 2500, 4, dip);
        var d = Lines(dipping, [-1, 47]);
        var res = new InterceptTimeMethod().Interpret(d.Obs, new LayeredSettings(LayeredMethod.InterceptTime, d.Gathers[0].Id, d.Gathers[1].Id), 2);
        cases.Add(new(g, "Dipping refractor: V₂", "600/2500 m/s, 5° dip", 2500, res.HasValue ? res.Value!.Layers[1].Velocity : double.NaN, 0.01, "m/s", "Exact two-layer dip formulas"));
        if (res.HasValue)
            foreach (var p in res.Value!.Refractors[0].Points)
                cases.Add(new(g, $"Dipping refractor: depth at x = {p.X:0.#} m", "vertical depth", dipping.DepthAt(p.X), p.Depth, 1e-3, "m", ""));

        var three = Lines(new HorizontalLayersModel([400, 1200, 3000], [3, 8]), [-48, -1, 47, 95], 48, 2, 3);
        var r3 = new DelayTimeMethod().Interpret(three.Obs, new LayeredSettings(), 3);
        if (r3.HasValue)
        {
            cases.Add(new(g, "Three layers (delay-time): V₃", "400/1200/3000 m/s, h 3 + 8 m", 3000, r3.Value!.Layers[2].Velocity, 3, "m/s", "Layer stripping"));
            cases.Add(new(g, "Three layers (delay-time): refractor 2 depth", "mean of all positions", 11, r3.Value.Refractors[1].Points.Average(p => p.Depth), 0.1, "m", ""));
        }
    }

    private static async Task TomographyAndVs30(List<Case> cases)
    {
        const string g = "Tomography and Vs30 (synthetic surveys)";
        var model = new GradientModel(400, 40);
        var lines = Lines(model, [-24, -1, 11, 23, 35, 47, 70]);
        var settings = new TomographySettings(CellWidth: 1, CellHeight: 1, MaxDepth: 16, MaxIterations: 10, Smoothing: 10, SecondaryNodes: 4, PickErrorAbsolute: 0.0001, PickErrorRelative: 0.002);
        var t = (await new TomographyEngine().RunAsync(lines.Obs, settings, null)).Value!;
        var errors = new List<double>();
        var grid = t.Model.Grid;
        for (var c = 0; c < grid.Nx; c++)
        {
            var x = grid.CenterX(c);
            if (x < 6 || x > 40) continue;
            for (var r = 0; r < grid.Nz; r++)
            {
                var idx = grid.Index(c, r);
                var depth = grid.DepthOf(c, r);
                if (!grid.Active[idx] || depth < 1 || depth > 0.8 * t.InvestigationDepth[c] || t.Hits[idx] < 5) continue;
                errors.Add(Math.Abs(t.Model.Velocity[idx] / model.VelocityAt(x, depth) - 1));
            }
        }
        cases.Add(new(g, "Gradient model: median velocity error", $"{errors.Count} well-covered cells", 0, errors.Order().ElementAt(errors.Count / 2), 0.05, "fraction", "v = 400 + 40·z"));
        cases.Add(new(g, "Gradient model: RMS travel-time residual", "exact picks", 0, t.FinalRms, 0.0005, "s", ""));

        // Sample site: Vs30 from automatic picks and SH tomography against the true model.
        var work = Path.Combine(Path.GetTempPath(), "rs-validate-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var ws = await SampleProjectBuilder.BuildAsync(work, new Core.WorkspaceOptions(Commands.Version, "validation", Path.Combine(work, "rec")));
            var analysis = ws.Analyze();
            foreach (var e in analysis.Engineering.Vs30.HasValue ? analysis.Engineering.Vs30.Value! : [])
            {
                double tt = 0;
                for (var d = 0.005; d < 30; d += 0.01) tt += 0.01 / SampleProjectBuilder.Vs(e.X, d);
                cases.Add(new(g, $"Sample site Vs30 at x = {e.X:0.#} m", "automatic SH picks + tomography", 30 / tt, e.Vs30.HasValue ? e.Vs30.Value!.Velocity : double.NaN, 0.05 * 30 / tt, "m/s",
                    "True Vs30 of the synthetic model (±5 %)"));
            }
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }
}
