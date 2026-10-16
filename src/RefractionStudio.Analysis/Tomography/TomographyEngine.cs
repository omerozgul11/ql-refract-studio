using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RefractionStudio.Analysis.Layered;
using RefractionStudio.Analysis.Numerics;
using RefractionStudio.Analysis.TravelTimes;
using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.Tomography;

/// <summary>Convergence record of one inversion iteration.</summary>
/// <param name="Iteration">0 = starting model.</param>
/// <param name="Lambda">Regularisation weight used to reach this model.</param>
/// <param name="RmsSeconds">RMS travel-time residual.</param>
/// <param name="Chi2">Normalised misfit Σ(r/σ)²/N.</param>
/// <param name="StepLength">Fraction of the Gauss–Newton step accepted.</param>
public sealed record TomographyIteration(int Iteration, double Lambda, double RmsSeconds, double Chi2, double StepLength);

/// <summary>Progress of a running inversion.</summary>
public sealed record TomographyProgress(int Iteration, int MaxIterations, string Stage, double? RmsSeconds, double? Chi2);

/// <summary>A ray kept for display.</summary>
public sealed record RayPath(PickKey Key, double[] X, double[] Z);

/// <summary>Result of a tomographic inversion.</summary>
/// <param name="Model">Final velocity model.</param>
/// <param name="StartingModel">Starting velocity model.</param>
/// <param name="Coverage">Total ray length (m) per cell in the final model.</param>
/// <param name="Hits">Number of rays per cell.</param>
/// <param name="InvestigationDepth">Per column: depth below ground of the deepest cell with coverage above the threshold (NaN if none).</param>
/// <param name="Iterations">Convergence history.</param>
/// <param name="Residuals">Final residual of each observation used.</param>
/// <param name="Rays">Final rays.</param>
/// <param name="Settings">Settings used.</param>
/// <param name="InputFingerprint">Fingerprint of the inputs (picks, geometry, settings, start model).</param>
/// <param name="Notes">Notes on decisions taken (cell size changes, excluded observations).</param>
/// <param name="StopReason">Why the iteration stopped.</param>
/// <param name="Elapsed">Run time.</param>
public sealed record TomographyResult(
    VelocityModel Model,
    VelocityModel StartingModel,
    ImmutableArray<double> Coverage,
    ImmutableArray<int> Hits,
    ImmutableArray<double> InvestigationDepth,
    ImmutableArray<TomographyIteration> Iterations,
    ImmutableArray<ModelResidual> Residuals,
    ImmutableArray<RayPath> Rays,
    TomographySettings Settings,
    string InputFingerprint,
    ImmutableArray<string> Notes,
    string StopReason,
    TimeSpan Elapsed)
{
    public double FinalRms => Iterations.IsDefaultOrEmpty ? double.NaN : Iterations[^1].RmsSeconds;

    public double FinalChi2 => Iterations.IsDefaultOrEmpty ? double.NaN : Iterations[^1].Chi2;

    /// <summary>
    /// True if the cell lies within the depth of investigation of its column (at or above the deepest cell
    /// crossed by rays with coverage above the threshold). Interior cells that individual network rays happen
    /// to miss are constrained through their neighbours and the regularisation.
    /// </summary>
    public bool IsConstrained(int cell)
    {
        var g = Model.Grid;
        if (!g.Active[cell]) return false;
        var (column, row) = g.ColumnRow(cell);
        var doi = InvestigationDepth[column];
        return double.IsFinite(doi) && g.DepthOf(column, row) <= doi;
    }

    /// <summary>True if rays actually cross the cell (coverage above the threshold).</summary>
    public bool IsCrossedByRays(int cell) => Hits[cell] > 0 && Coverage[cell] > Settings.CoverageThreshold;

    /// <summary>Investigation depth at x (nearest column), NaN outside.</summary>
    public double InvestigationDepthAt(double x)
    {
        var g = Model.Grid;
        if (x < g.X0 || x > g.XMax) return double.NaN;
        return InvestigationDepth[g.ColumnOf(x)];
    }

    /// <summary>Velocity at depth below ground at x if the cell is ray-constrained.</summary>
    public double? ConstrainedVelocityAt(double x, double depth)
    {
        var g = Model.Grid;
        if (x < g.X0 || x > g.XMax) return null;
        var c = g.ColumnOf(x);
        var z = g.Ground[c] - depth;
        if (z < g.ZBottom || z > g.ZTop) return null;
        var idx = g.Index(c, g.RowOf(z));
        return g.Active[idx] && IsConstrained(idx) ? Model.Velocity[idx] : null;
    }
}

/// <summary>First-arrival travel-time tomography.</summary>
public interface ITomographyEngine
{
    Task<Computed<TomographyResult>> RunAsync(LineObservations observations, TomographySettings settings, LayeredModel? layeredStart,
        IProgress<TomographyProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Regularised Gauss–Newton travel-time tomography. Parameters: logarithm of cell slowness.
/// Each iteration traces rays in the current model (shortest-path method), then solves with LSQR
/// min ‖W(d − g(m) − JΔm)‖² + λ²‖R(m + Δm)‖², where W holds 1/σ per pick
/// (σ = absolute + relative·t), J = ∂t/∂ln s, and R the first differences between neighbouring
/// cells (horizontal differences weighted by √ratio). With robust data weighting (default) the data
/// rows are re-weighted every iteration with Huber weights on |r|/σ (threshold 2), i.e. the misfit is
/// quadratic for normal residuals and linear for outliers. A step-halving line search keeps the misfit
/// decreasing; velocities are bounded. When an iteration improves the misfit by less than 1 % the
/// regularisation is relaxed; iteration stops at the target misfit, on stagnation at the smallest λ,
/// or after the maximum number of iterations. See docs/algorithms/tomography.md.
/// </summary>
public sealed class TomographyEngine : ITomographyEngine
{
    /// <summary>Upper limit on the number of cells; larger grids are coarsened (and the note says so).</summary>
    public const int MaxCells = 60000;

    /// <summary>Huber threshold on normalised residuals |r|/σ for robust data weighting.</summary>
    public const double HuberThreshold = 2.0;

    public Task<Computed<TomographyResult>> RunAsync(LineObservations observations, TomographySettings settings, LayeredModel? layeredStart,
        IProgress<TomographyProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Run(observations, settings, layeredStart, progress, cancellationToken), cancellationToken);

    public static Computed<TomographyResult> Run(LineObservations observations, TomographySettings settings, LayeredModel? layeredStart,
        IProgress<TomographyProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var notes = ImmutableArray.CreateBuilder<string>();
        var data = observations.Included.Where(o => o.AbsOffset > 0 || Math.Abs(o.ReceiverElevation - o.SourceElevation) > 0).ToList();
        if (data.Count < 10) return Computed<TomographyResult>.Unavailable($"Tomography needs at least 10 included first-arrival picks; this line has {data.Count}.");
        if (settings.MinVelocity <= 0 || settings.MaxVelocity <= settings.MinVelocity)
            return Computed<TomographyResult>.Unavailable("The velocity bounds are invalid (minimum must be positive and below the maximum).");

        // Grid.
        var spacing = observations.TypicalSpacing;
        var dx = settings.CellWidth ?? spacing / 2;
        var dz = settings.CellHeight ?? dx;
        var xMin = Math.Min(data.Min(o => o.ShotX), data.Min(o => o.ReceiverX));
        var xMax = Math.Max(data.Max(o => o.ShotX), data.Max(o => o.ReceiverX));
        var maxOffset = data.Max(o => o.AbsOffset);
        var maxDepth = settings.MaxDepth ?? Math.Max(maxOffset / 3, 3 * dz);
        var grid = ModelGrid.Create(observations.Line.Topography, xMin, xMax, dx, dz, maxDepth);
        while (grid.CellCount > MaxCells)
        {
            dx *= 1.25; dz *= 1.25;
            grid = ModelGrid.Create(observations.Line.Topography, xMin, xMax, dx, dz, maxDepth);
        }
        if (dz > 1.5 * dx || dx > 1.5 * dz)
            notes.Add($"Cells are {dx:0.###} × {dz:0.###} m. Strongly non-square cells reduce ray-tracing accuracy for steep or shallow ray segments; square cells are recommended.");
        if (Math.Abs(dx - (settings.CellWidth ?? spacing / 2)) > 1e-9)
            notes.Add($"Cells were enlarged to {dx:0.###} × {dz:0.###} m to stay within {MaxCells} cells.");
        if (!observations.Line.Topography.IsFlat)
        {
            var (tMin, tMax) = observations.Line.Topography.Extent!.Value;
            if (xMin < tMin - 1e-6 || xMax > tMax + 1e-6) notes.Add("Topography does not cover the whole model; the end elevations were held constant beyond the surveyed points.");
        }

        // Starting model.
        var start = StartingModel(grid, observations, data, settings, layeredStart, notes);
        if (!start.HasValue) return Computed<TomographyResult>.Unavailable(start.Reason!);
        var startVelocity = start.Value!;

        // Parameters: active cells only.
        var activeCells = Enumerable.Range(0, grid.CellCount).Where(i => grid.Active[i]).ToArray();
        var paramOf = new int[grid.CellCount];
        Array.Fill(paramOf, -1);
        for (var p = 0; p < activeCells.Length; p++) paramOf[activeCells[p]] = p;
        var lnSMin = Math.Log(1 / settings.MaxVelocity);
        var lnSMax = Math.Log(1 / settings.MinVelocity);
        var m = activeCells.Select(c => Math.Clamp(Math.Log(1 / startVelocity[c]), lnSMin, lnSMax)).ToArray();

        var sigma = data.Select(o => settings.PickErrorAbsolute + settings.PickErrorRelative * Math.Abs(o.CorrectedTime)).Select(s => Math.Max(s, 1e-6)).ToArray();
        var solver = new ShortestPathSolver(settings.SecondaryNodes);
        var shots = data.GroupBy(o => o.Gather).Select(g => (Source: (g.First().ShotX, g.First().SourceElevation), Obs: g.ToList())).ToList();
        var indexOf = data.Select((o, i) => (o.Key, i)).ToDictionary(t => t.Key, t => t.i);

        (double[] Times, Ray?[] Rays) Forward(double[] model, bool rays)
        {
            var slowness = new double[grid.CellCount];
            Array.Fill(slowness, double.NaN);
            for (var p = 0; p < activeCells.Length; p++) slowness[activeCells[p]] = Math.Exp(model[p]);
            var times = new double[data.Count];
            var rayList = new Ray?[data.Count];
            Parallel.ForEach(shots, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, shot =>
            {
                var receivers = shot.Obs.Select(o => (o.ReceiverX, o.ReceiverElevation)).ToList();
                var res = solver.Solve(grid, slowness, shot.Source, receivers, rays);
                for (var k = 0; k < shot.Obs.Count; k++)
                {
                    var i = indexOf[shot.Obs[k].Key];
                    times[i] = res[k].Time;
                    rayList[i] = res[k].Ray;
                }
            });
            return (times, rayList);
        }

        (double Rms, double Chi2, double Objective) Misfit(double[] times)
        {
            double sse = 0, chi = 0, obj = 0;
            for (var i = 0; i < data.Count; i++)
            {
                var r = data[i].CorrectedTime - times[i];
                var u = Math.Abs(r) / sigma[i];
                sse += r * r;
                chi += u * u;
                obj += settings.RobustDataWeighting && u > HuberThreshold ? 2 * HuberThreshold * u - HuberThreshold * HuberThreshold : u * u;
            }
            return (Math.Sqrt(sse / data.Count), chi / data.Count, obj / data.Count);
        }

        double RobustWeight(int i, double[] current)
        {
            if (!settings.RobustDataWeighting) return 1;
            var u = Math.Abs(data[i].CorrectedTime - current[i]) / sigma[i];
            return u <= HuberThreshold ? 1 : HuberThreshold / u;
        }

        // Roughness operator.
        var roughness = new SparseMatrix(activeCells.Length);
        var hWeight = Math.Sqrt(Math.Max(settings.HorizontalSmoothingRatio, 1e-6));
        foreach (var c in activeCells)
        {
            var (col, row) = grid.ColumnRow(c);
            if (col + 1 < grid.Nx && paramOf[grid.Index(col + 1, row)] is var right && right >= 0)
                roughness.AddRow([(paramOf[c], hWeight), (right, -hWeight)]);
            if (row + 1 < grid.Nz && paramOf[grid.Index(col, row + 1)] is var below && below >= 0)
                roughness.AddRow([(paramOf[c], 1.0), (below, -1.0)]);
        }

        progress?.Report(new TomographyProgress(0, settings.MaxIterations, "Tracing rays in the starting model", null, null));
        var (times, rays) = Forward(m, true);
        var unreachable = Enumerable.Range(0, data.Count).Count(i => !double.IsFinite(times[i]));
        if (unreachable > 0) return Computed<TomographyResult>.Unavailable($"{unreachable} receivers could not be reached by rays; check the line geometry and topography.");
        var (rms, chi2, objective) = Misfit(times);
        var iterations = ImmutableArray.CreateBuilder<TomographyIteration>();
        iterations.Add(new TomographyIteration(0, double.NaN, rms, chi2, 0));
        progress?.Report(new TomographyProgress(0, settings.MaxIterations, "Starting model", rms, chi2));

        var lambda = settings.Smoothing;
        var lambdaFloor = settings.Smoothing * 0.1;
        var stopReason = "Maximum number of iterations reached.";
        for (var it = 1; it <= settings.MaxIterations; it++)
        {
            ct.ThrowIfCancellationRequested();
            if (objective <= settings.TargetChi2)
            {
                stopReason = TargetReason();
                break;
            }
            // Jacobian J = W·G·diag(s).
            var j = new SparseMatrix(activeCells.Length);
            var rhs = new double[data.Count];
            for (var i = 0; i < data.Count; i++)
            {
                var entries = new Dictionary<int, double>();
                var ray = rays[i]!;
                for (var sIdx = 0; sIdx < ray.Cells.Length; sIdx++)
                {
                    var cell = ray.Cells[sIdx];
                    var p = paramOf[cell];
                    if (p < 0) continue;
                    entries[p] = entries.GetValueOrDefault(p) + ray.SegmentLength(sIdx) * Math.Exp(m[p]) / sigma[i];
                }
                var rw = Math.Sqrt(RobustWeight(i, times));
                j.AddRow(entries.Select(e => (e.Key, e.Value * rw)));
                rhs[i] = rw * (data[i].CorrectedTime - times[i]) / sigma[i];
            }
            var rm = new double[roughness.Rows];
            roughness.Multiply(m, rm);
            var scaledRoughness = Scaled(roughness, lambda);
            var regRhs = rm.Select(v => -lambda * v).ToArray();
            progress?.Report(new TomographyProgress(it, settings.MaxIterations, "Solving the linearised system", rms, chi2));
            var step = Lsqr.Solve(j, rhs, scaledRoughness, regRhs, maxIterations: 400, tolerance: 1e-6, ct: ct).X;

            // Line search.
            var accepted = false;
            var improvement = 0.0;
            foreach (var alpha in new[] { 1.0, 0.5, 0.25 })
            {
                var trial = new double[m.Length];
                for (var p = 0; p < m.Length; p++) trial[p] = Math.Clamp(m[p] + alpha * step[p], lnSMin, lnSMax);
                progress?.Report(new TomographyProgress(it, settings.MaxIterations, $"Tracing rays (step {alpha:0.##})", rms, chi2));
                var (tTimes, tRays) = Forward(trial, true);
                if (Enumerable.Range(0, data.Count).Any(i => !double.IsFinite(tTimes[i]))) continue;
                var (tRms, tChi2, tObjective) = Misfit(tTimes);
                if (tObjective < objective)
                {
                    improvement = (objective - tObjective) / objective;
                    m = trial; times = tTimes; rays = tRays; rms = tRms; chi2 = tChi2; objective = tObjective;
                    iterations.Add(new TomographyIteration(it, lambda, rms, chi2, alpha));
                    progress?.Report(new TomographyProgress(it, settings.MaxIterations, "Iteration complete", rms, chi2));
                    accepted = true;
                    break;
                }
            }
            var atFloor = lambda <= lambdaFloor * (1 + 1e-9);
            if (!accepted || improvement < 0.01)
            {
                // Before stopping, relax the regularisation: a smoother model may simply be too constrained.
                if (!atFloor)
                {
                    lambda = Math.Max(lambdaFloor, lambda * settings.SmoothingDecay * settings.SmoothingDecay);
                    continue;
                }
                stopReason = accepted
                    ? $"Misfit stagnated (improvement {improvement * 100:0.0} % < 1 % at the smallest regularisation)."
                    : "No further reduction of the misfit along the model update.";
                break;
            }
            lambda = Math.Max(lambdaFloor, lambda * settings.SmoothingDecay);
        }
        if (iterations.Count > 0 && objective <= settings.TargetChi2 && !stopReason.StartsWith("Target", StringComparison.Ordinal))
            stopReason = TargetReason();
        var downWeighted = settings.RobustDataWeighting ? Enumerable.Range(0, data.Count).Count(i => RobustWeight(i, times) < 0.5) : 0;
        if (downWeighted > 0)
            notes.Add($"{downWeighted} pick(s) with residuals above {2 * HuberThreshold:0}σ were down-weighted (robust data weighting); review them in QC.");

        string TargetReason() => settings.RobustDataWeighting
            ? $"Target misfit reached (robust misfit {objective:0.00} ≤ {settings.TargetChi2:0.00}; χ² = {chi2:0.00})."
            : $"Target misfit reached (χ² = {chi2:0.00} ≤ {settings.TargetChi2:0.00}).";

        // Coverage from final rays.
        var coverage = new double[grid.CellCount];
        var hits = new int[grid.CellCount];
        var rayPaths = ImmutableArray.CreateBuilder<RayPath>(data.Count);
        for (var i = 0; i < data.Count; i++)
        {
            var ray = rays[i]!;
            var seen = new HashSet<int>();
            for (var s = 0; s < ray.Cells.Length; s++)
            {
                coverage[ray.Cells[s]] += ray.SegmentLength(s);
                if (seen.Add(ray.Cells[s])) hits[ray.Cells[s]]++;
            }
            rayPaths.Add(new RayPath(data[i].Key, ray.X, ray.Z));
        }
        var doi = new double[grid.Nx];
        for (var c = 0; c < grid.Nx; c++)
        {
            doi[c] = double.NaN;
            for (var r = grid.Nz - 1; r >= 0; r--)
            {
                var idx = grid.Index(c, r);
                if (grid.Active[idx] && hits[idx] > 0 && coverage[idx] > settings.CoverageThreshold)
                {
                    doi[c] = grid.Ground[c] - (grid.CenterZ(r) - grid.Dz / 2);
                    break;
                }
            }
        }

        var velocity = new double[grid.CellCount];
        Array.Fill(velocity, double.NaN);
        for (var p = 0; p < activeCells.Length; p++) velocity[activeCells[p]] = 1 / Math.Exp(m[p]);
        var residuals = data.Select((o, i) => new ModelResidual(o.Key, o.CorrectedTime, times[i])).ToImmutableArray();
        var result = new TomographyResult(
            new VelocityModel(grid, [.. velocity]),
            new VelocityModel(grid, [.. startVelocity]),
            [.. coverage], [.. hits], [.. doi],
            iterations.ToImmutable(), residuals, rayPaths.ToImmutable(), settings,
            Fingerprint(observations, settings, layeredStart), notes.ToImmutable(), stopReason, sw.Elapsed);
        return Computed<TomographyResult>.Ok(result);
    }

    private static SparseMatrix Scaled(SparseMatrix r, double lambda)
    {
        var s = new SparseMatrix(r.Columns);
        for (var i = 0; i < r.Rows; i++) s.AddRow(r.Row(i).Select(e => (e.Column, e.Value * lambda)));
        return s;
    }

    private static Computed<double[]> StartingModel(ModelGrid grid, LineObservations observations, List<Observation> data, TomographySettings settings,
        LayeredModel? layered, ImmutableArray<string>.Builder notes)
    {
        var v = new double[grid.CellCount];
        Array.Fill(v, double.NaN);
        double Clamp(double x) => Math.Clamp(x, settings.MinVelocity, settings.MaxVelocity);

        if (settings.StartingModel == StartingModelKind.Layered)
        {
            if (layered is null) return Computed<double[]>.Unavailable("The layered starting model was selected but no layered interpretation is available.");
            var extended = false;
            for (var c = 0; c < grid.Nx; c++)
            {
                var x = grid.CenterX(c);
                for (var r = 0; r < grid.Nz; r++)
                {
                    var idx = grid.Index(c, r);
                    if (!grid.Active[idx]) continue;
                    var depth = grid.DepthOf(c, r);
                    var value = layered.VelocityAt(x, depth);
                    if (value is null)
                    {
                        // Beyond the covered range of an interface: hold the nearest interface depths.
                        value = VelocityWithHeldInterfaces(layered, x, depth);
                        extended = true;
                    }
                    v[idx] = Clamp(value ?? layered.Layers[^1].Velocity);
                }
            }
            if (extended) notes.Add("Layered starting model: interfaces were held at their nearest depth beyond the positions where they were determined.");
            notes.Add("Starting model: layered interpretation (" + layered.Method + ").");
            return Computed<double[]>.Ok(v);
        }

        var top = settings.StartTopVelocity ?? NearOffsetVelocity(data, observations.TypicalSpacing);
        if (!(top > 0)) return Computed<double[]>.Unavailable("A starting velocity could not be derived from the picks; enter the starting velocities.");
        double bottom;
        if (settings.StartingModel == StartingModelKind.Constant)
        {
            bottom = top;
        }
        else
        {
            var fits = SegmentAssigner.FitAll(observations, RegressionMode.Ordinary).Select(f => f.ApparentVelocity).Where(double.IsFinite).Where(x => x > 0).ToList();
            bottom = settings.StartBottomVelocity ?? (fits.Count > 0 ? fits.Max() : data.Where(o => o.CorrectedTime > 0).Max(o => o.AbsOffset / o.CorrectedTime));
            if (bottom < top) bottom = top;
        }
        var maxDepth = grid.ZTop - grid.ZBottom;
        for (var c = 0; c < grid.Nx; c++)
            for (var r = 0; r < grid.Nz; r++)
            {
                var idx = grid.Index(c, r);
                if (!grid.Active[idx]) continue;
                var depth = Math.Max(0, grid.DepthOf(c, r));
                v[idx] = Clamp(top + (bottom - top) * Math.Min(1, depth / maxDepth));
            }
        notes.Add(settings.StartingModel == StartingModelKind.Constant
            ? $"Starting model: constant {top:0} m/s{(settings.StartTopVelocity is null ? " (from near-offset arrivals)" : "")}."
            : $"Starting model: gradient from {top:0} m/s at the surface{(settings.StartTopVelocity is null ? " (from near-offset arrivals)" : "")} to {bottom:0} m/s at the model base.");
        return Computed<double[]>.Ok(v);
    }

    /// <summary>
    /// Surface velocity estimated from the arrivals nearest the shots (offset ≤ 2.5 geophone intervals):
    /// median of offset / corrected time. Independent of the layer assignment.
    /// </summary>
    public static double NearOffsetVelocity(IReadOnlyList<Observation> data, double spacing)
    {
        var near = data.Where(o => o.AbsOffset > 0 && o.AbsOffset <= 2.5 * spacing && o.CorrectedTime > 0).Select(o => o.AbsOffset / o.CorrectedTime).Order().ToList();
        if (near.Count == 0) near = [.. data.Where(o => o.AbsOffset > 0 && o.CorrectedTime > 0).Select(o => o.AbsOffset / o.CorrectedTime).Order().Take(5)];
        return near.Count == 0 ? double.NaN : near[near.Count / 2];
    }

    private static double? VelocityWithHeldInterfaces(LayeredModel model, double x, double depth)
    {
        for (var k = 0; k < model.Refractors.Length; k++)
        {
            var pts = model.Refractors[k].Points;
            if (pts.IsDefaultOrEmpty) return null;
            var d = model.Refractors[k].DepthAt(x) ?? (x < pts[0].X ? pts[0].Depth : pts[^1].Depth);
            if (depth < d) return model.Layers[k].Velocity;
        }
        return model.Layers[^1].Velocity;
    }

    /// <summary>Fingerprint of everything that determines a tomography result.</summary>
    public static string Fingerprint(LineObservations observations, TomographySettings settings, LayeredModel? layeredStart)
    {
        var sb = new StringBuilder();
        foreach (var o in observations.Included.OrderBy(o => o.Key.ToString(), StringComparer.Ordinal))
            sb.Append(CultureInfo.InvariantCulture, $"{o.Key}|{o.CorrectedTime:R}|{o.ShotX:R}|{o.SourceElevation:R}|{o.ReceiverX:R}|{o.ReceiverElevation:R};");
        foreach (var p in observations.Line.Topography.Points) sb.Append(CultureInfo.InvariantCulture, $"T{p.X:R},{p.Elevation:R};");
        sb.Append(settings.ToString());
        if (settings.StartingModel == StartingModelKind.Layered && layeredStart is not null)
        {
            foreach (var l in layeredStart.Layers) sb.Append(CultureInfo.InvariantCulture, $"L{l.Velocity:R};");
            foreach (var r in layeredStart.Refractors)
                foreach (var p in r.Points) sb.Append(CultureInfo.InvariantCulture, $"P{p.X:R},{p.Depth:R};");
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Forward-models the observations through a velocity model (e.g. a rasterised layered model).</summary>
    public static ImmutableArray<ModelResidual> ForwardModel(VelocityModel model, IReadOnlyList<Observation> observations, int secondaryNodes = 3, CancellationToken ct = default)
    {
        var grid = model.Grid;
        var slowness = model.Velocity.Select(v => double.IsFinite(v) && v > 0 ? 1 / v : double.NaN).ToArray();
        var solver = new ShortestPathSolver(secondaryNodes);
        var results = new ModelResidual?[observations.Count];
        var groups = observations.Select((o, i) => (o, i)).GroupBy(t => t.o.Gather).ToList();
        Parallel.ForEach(groups, new ParallelOptions { CancellationToken = ct }, g =>
        {
            var first = g.First().o;
            var res = solver.Solve(grid, slowness, (first.ShotX, first.SourceElevation), g.Select(t => (t.o.ReceiverX, t.o.ReceiverElevation)).ToList(), false);
            var k = 0;
            foreach (var (o, i) in g)
            {
                if (double.IsFinite(res[k].Time)) results[i] = new ModelResidual(o.Key, o.CorrectedTime, res[k].Time);
                k++;
            }
        });
        return [.. results.Where(r => r is not null).Select(r => r!)];
    }

    /// <summary>Rasterises a layered model onto a grid for forward modelling (interfaces held flat beyond their range).</summary>
    public static VelocityModel Rasterize(LayeredModel model, ModelGrid grid)
    {
        var v = new double[grid.CellCount];
        Array.Fill(v, double.NaN);
        for (var c = 0; c < grid.Nx; c++)
            for (var r = 0; r < grid.Nz; r++)
            {
                var idx = grid.Index(c, r);
                if (!grid.Active[idx]) continue;
                v[idx] = VelocityWithHeldInterfaces(model, grid.CenterX(c), grid.DepthOf(c, r)) ?? double.NaN;
            }
        return new VelocityModel(grid, [.. v]);
    }
}
