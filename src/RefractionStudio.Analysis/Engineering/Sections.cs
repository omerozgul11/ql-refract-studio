using RefractionStudio.Analysis.Tomography;
using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.Engineering;

/// <summary>Elastic properties evaluated on the cells of the P-wave model grid.</summary>
/// <param name="Grid">P-wave model grid.</param>
/// <param name="Vp">Vp per cell (NaN where unavailable).</param>
/// <param name="Vs">Vs sampled from the SH model at the cell centre (NaN where unconstrained).</param>
/// <param name="VpVs">Vp/Vs.</param>
/// <param name="Poisson">Poisson's ratio (NaN where invalid).</param>
/// <param name="ShearModulus">G (Pa).</param>
/// <param name="YoungsModulus">E (Pa).</param>
/// <param name="BulkModulus">K (Pa).</param>
/// <param name="Density">Density used (kg/m³).</param>
/// <param name="DensityEstimated">True when density came from an empirical relation.</param>
/// <param name="InvalidCells">Number of cells where the velocity pair was physically inconsistent.</param>
public sealed record PropertySections(ModelGrid Grid, double[] Vp, double[] Vs, double[] VpVs, double[] Poisson, double[] ShearModulus, double[] YoungsModulus,
    double[] BulkModulus, double[] Density, bool DensityEstimated, int InvalidCells);

/// <summary>Computes property sections where both P and SH tomograms are ray-constrained.</summary>
public static class PropertySectionCalculator
{
    public static Computed<PropertySections> Compute(TomographyResult p, TomographyResult s, DensityModel density, IEngineeringPropertyCalculator? calculator = null, IDensityEstimator? densityProvider = null)
    {
        calculator ??= new EngineeringPropertyCalculator();
        densityProvider ??= new DensityProvider();
        var g = p.Model.Grid;
        var n = g.CellCount;
        double[] Nan() => Enumerable.Repeat(double.NaN, n).ToArray();
        var vpA = Nan(); var vsA = Nan(); var ratio = Nan(); var nu = Nan(); var gm = Nan(); var em = Nan(); var km = Nan(); var rho = Nan();
        var estimated = false;
        var invalid = 0;
        var any = false;
        for (var c = 0; c < g.Nx; c++)
        {
            var x = g.CenterX(c);
            for (var r = 0; r < g.Nz; r++)
            {
                var idx = g.Index(c, r);
                if (!g.Active[idx] || !p.IsConstrained(idx)) continue;
                var depth = g.DepthOf(c, r);
                var vs = s.ConstrainedVelocityAt(x, depth);
                if (vs is null) continue;
                var vp = p.Model.Velocity[idx];
                vpA[idx] = vp;
                vsA[idx] = vs.Value;
                var d = densityProvider.DensityAt(density, depth, vp);
                var props = calculator.Calculate(vp, vs.Value, d);
                any = true;
                if (props.VpVsRatio.HasValue) ratio[idx] = props.VpVsRatio.Value;
                if (props.PoissonRatio.HasValue) nu[idx] = props.PoissonRatio.Value; else invalid++;
                if (props.ShearModulus.HasValue) gm[idx] = props.ShearModulus.Value;
                if (props.YoungsModulus.HasValue) em[idx] = props.YoungsModulus.Value;
                if (props.BulkModulus.HasValue) km[idx] = props.BulkModulus.Value;
                if (d.HasValue) { rho[idx] = d.Value!.Value; estimated |= d.Value.Estimated; }
            }
        }
        if (!any) return Computed<PropertySections>.Unavailable("The P and SH tomograms do not overlap in ray-constrained cells.");
        return Computed<PropertySections>.Ok(new PropertySections(g, vpA, vsA, ratio, nu, gm, em, km, rho, estimated, invalid), estimated ? ["ESTIMATED density"] : []);
    }
}
