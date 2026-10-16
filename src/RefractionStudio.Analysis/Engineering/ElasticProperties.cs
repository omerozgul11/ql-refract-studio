using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.Engineering;

/// <summary>A density value with provenance.</summary>
/// <param name="Value">Density (kg/m³).</param>
/// <param name="Estimated">True when estimated from Vp by an empirical relation.</param>
/// <param name="Basis">Measured interval note or estimator name.</param>
public sealed record DensityValue(double Value, bool Estimated, string Basis);

/// <summary>Supplies density at a depth. Never infers a value unless an estimator was selected.</summary>
public interface IDensityEstimator
{
    Computed<DensityValue> DensityAt(DensityModel model, double depth, double? vp);
}

/// <inheritdoc />
public sealed class DensityProvider : IDensityEstimator
{
    /// <summary>Lower Vp limit of Gardner et al. (1974) as applied here (m/s).</summary>
    public const double GardnerMinVp = 1500;

    /// <summary>Upper Vp limit of Gardner et al. (1974) as applied here (m/s).</summary>
    public const double GardnerMaxVp = 6100;

    /// <summary>Validity range of Brocher (2005) Nafe–Drake fit (m/s).</summary>
    public const double BrocherMinVp = 1500, BrocherMaxVp = 8500;

    public Computed<DensityValue> DensityAt(DensityModel model, double depth, double? vp)
    {
        if (model.Source == DensitySource.Measured)
        {
            foreach (var i in model.Intervals.IsDefault ? [] : model.Intervals)
            {
                if (depth >= i.TopDepth && depth < i.BottomDepth)
                {
                    if (!(i.Density > 0)) return Computed<DensityValue>.Unavailable($"The density entered for {i.TopDepth:0.#}–{i.BottomDepth:0.#} m is not positive.");
                    return Computed<DensityValue>.Ok(new DensityValue(i.Density, false, i.Note ?? $"Entered for {i.TopDepth:0.#}–{i.BottomDepth:0.#} m"));
                }
            }
            return Computed<DensityValue>.Unavailable($"Missing density: no value has been entered for {depth:0.#} m depth.");
        }
        if (vp is not { } v || !(v > 0)) return Computed<DensityValue>.Unavailable("Density cannot be estimated without Vp at this depth.");
        return model.Estimator switch
        {
            DensityEstimator.Gardner1974 => v is < GardnerMinVp or > GardnerMaxVp
                ? Computed<DensityValue>.Unavailable($"Gardner's relation is applied only for Vp {GardnerMinVp:0}–{GardnerMaxVp:0} m/s (sedimentary rocks); Vp here is {v:0} m/s.")
                : Computed<DensityValue>.Ok(new DensityValue(Gardner(v), true, "Gardner et al. (1974): ρ = 310·Vp^0.25"), "ESTIMATED"),
            DensityEstimator.Brocher2005 => v is < BrocherMinVp or > BrocherMaxVp
                ? Computed<DensityValue>.Unavailable($"Brocher's (2005) Nafe–Drake fit is valid for Vp {BrocherMinVp:0}–{BrocherMaxVp:0} m/s; Vp here is {v:0} m/s.")
                : Computed<DensityValue>.Ok(new DensityValue(Brocher(v), true, "Brocher (2005) Nafe–Drake polynomial"), "ESTIMATED"),
            _ => Computed<DensityValue>.Unavailable("Estimated density was selected without choosing an estimation method."),
        };
    }

    /// <summary>Gardner, Gardner &amp; Gregory (1974), Geophysics 39, 770–780: ρ [kg/m³] = 310·Vp[m/s]^0.25.</summary>
    public static double Gardner(double vp) => 310.0 * Math.Pow(vp, 0.25);

    /// <summary>
    /// Brocher (2005), BSSA 95(6), eq. 1: ρ [g/cm³] = 1.6612Vp − 0.4721Vp² + 0.0671Vp³ − 0.0043Vp⁴ + 0.000106Vp⁵,
    /// Vp in km/s; returned in kg/m³.
    /// </summary>
    public static double Brocher(double vp)
    {
        var v = vp / 1000;
        return 1000 * (1.6612 * v - 0.4721 * v * v + 0.0671 * Math.Pow(v, 3) - 0.0043 * Math.Pow(v, 4) + 0.000106 * Math.Pow(v, 5));
    }
}

/// <summary>Elastic properties at one point.</summary>
public sealed record ElasticProperties(
    double Vp,
    double Vs,
    Computed<DensityValue> Density,
    Computed<double> VpVsRatio,
    Computed<double> PoissonRatio,
    Computed<double> ShearModulus,
    Computed<double> YoungsModulus,
    Computed<double> BulkModulus,
    ImmutableArray<string> Flags);

/// <summary>Elastic moduli from velocities and density.</summary>
public interface IEngineeringPropertyCalculator
{
    ElasticProperties Calculate(double vp, double vs, Computed<DensityValue> density);
}

/// <summary>
/// Isotropic linear elasticity:
/// ν = (Vp² − 2Vs²)/[2(Vp² − Vs²)], G = ρVs², E = 2G(1 + ν), K = ρ(Vp² − 4Vs²/3).
/// Physically inconsistent inputs are flagged, not corrected.
/// </summary>
public sealed class EngineeringPropertyCalculator : IEngineeringPropertyCalculator
{
    /// <summary>Relative threshold below which Vp² − Vs² is treated as zero.</summary>
    public const double DegenerateFraction = 1e-6;

    public ElasticProperties Calculate(double vp, double vs, Computed<DensityValue> density)
    {
        var flags = ImmutableArray.CreateBuilder<string>();
        if (!(vp > 0) || !(vs > 0))
        {
            var reason = "Vp and Vs must both be positive.";
            return new ElasticProperties(vp, vs, density, Computed<double>.Unavailable(reason), Computed<double>.Unavailable(reason),
                Computed<double>.Unavailable(reason), Computed<double>.Unavailable(reason), Computed<double>.Unavailable(reason), [reason]);
        }
        var ratio = Computed<double>.Ok(vp / vs);
        Computed<double> nu;
        var vp2 = vp * vp;
        var vs2 = vs * vs;
        if (vs >= vp)
        {
            const string msg = "Vs ≥ Vp is physically inconsistent for an elastic material; check the P and S models at this position.";
            flags.Add(msg);
            nu = Computed<double>.Unavailable(msg);
        }
        else if (vp2 - vs2 < DegenerateFraction * vp2)
        {
            const string msg = "Vp² − Vs² is close to zero; Poisson's ratio is undefined.";
            flags.Add(msg);
            nu = Computed<double>.Unavailable(msg);
        }
        else
        {
            var v = (vp2 - 2 * vs2) / (2 * (vp2 - vs2));
            nu = Computed<double>.Ok(v);
            if (v < 0) flags.Add($"Negative Poisson's ratio ({v:0.00}, Vp/Vs < √2) is unusual for soils and rocks; review the velocities.");
            else if (v > 0.48) flags.Add($"Poisson's ratio {v:0.00} is typical of saturated soils, where Vp mainly reflects the pore water.");
        }

        Computed<double> g, e, k;
        if (!density.HasValue)
        {
            var msg = density.Reason ?? "Missing density.";
            g = Computed<double>.Unavailable(msg);
            e = Computed<double>.Unavailable(msg);
            k = Computed<double>.Unavailable(msg);
        }
        else
        {
            var rho = density.Value!.Value;
            var notes = density.Value.Estimated ? new[] { "ESTIMATED density" } : [];
            g = Computed<double>.Ok(rho * vs2, notes);
            e = nu.HasValue ? Computed<double>.Ok(2 * g.Value * (1 + nu.Value), notes) : Computed<double>.Unavailable(nu.Reason!);
            var kv = rho * (vp2 - 4.0 * vs2 / 3.0);
            if (kv < 0)
            {
                var msg = $"Negative bulk modulus (Vp/Vs = {vp / vs:0.00} < 2/√3); the velocity pair is inconsistent.";
                flags.Add(msg);
                k = Computed<double>.Unavailable(msg);
            }
            else
            {
                k = Computed<double>.Ok(kv, notes);
            }
        }
        return new ElasticProperties(vp, vs, density, ratio, nu, g, e, k, flags.ToImmutable());
    }
}
