using RefractionStudio.Analysis.Engineering;
using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.Tests;

public class Vs30Tests
{
    private static VelocityProfile Profile(double constrained)
        => new(10, [new(0, 5, 200), new(5, 20, 400), new(20, double.PositiveInfinity, 800)], constrained, "test");

    [Fact]
    public void Vs30_is_the_harmonic_travel_time_average()
    {
        // 30 / (5/200 + 15/400 + 10/800) = 30 / 0.075 = 400 m/s.
        var r = new Vs30Calculator().Calculate(Profile(35), 0, 30, allowExtrapolation: false);
        Assert.True(r.HasValue);
        Assert.Equal(400, r.Value!.Velocity, 9);
        Assert.Equal(0.075, r.Value.TravelTime, 12);
        Assert.False(r.Value.Extrapolated);
        Assert.Equal("Measured", r.Value.Qualifier);
    }

    [Fact]
    public void Insufficient_depth_is_refused_without_explicit_extrapolation()
    {
        var r = new Vs30Calculator().Calculate(Profile(20), 0, 30, allowExtrapolation: false);
        Assert.False(r.HasValue);
        Assert.Contains("Insufficient measured depth for direct Vs30 calculation", r.Reason);
    }

    [Fact]
    public void Extrapolated_vs30_is_labelled()
    {
        // Constrained to 20 m: 5/200 + 15/400 = 0.0625; below, the deepest constrained velocity (400 m/s) is held:
        // + 10/400 = 0.025 → 30/0.0875 = 342.857 m/s.
        var r = new Vs30Calculator().Calculate(Profile(20), 0, 30, allowExtrapolation: true);
        Assert.True(r.HasValue);
        Assert.Equal(30 / 0.0875, r.Value!.Velocity, 9);
        Assert.True(r.Value.Extrapolated);
        Assert.Contains("EXTRAPOLATED", r.Notes);
        Assert.StartsWith("EXTRAPOLATED below 20 m", r.Value.Qualifier);
    }

    [Fact]
    public void Custom_interval()
    {
        // 5–15 m entirely in the 400 m/s layer.
        var r = new Vs30Calculator().Calculate(Profile(35), 5, 15, false);
        Assert.Equal(400, r.Value!.Velocity, 12);
        var bad = new Vs30Calculator().Calculate(Profile(35), 10, 5, false);
        Assert.False(bad.HasValue);
    }
}

public class ElasticPropertyTests
{
    private static Computed<DensityValue> Rho(double v) => Computed<DensityValue>.Ok(new DensityValue(v, false, "test"));

    [Fact]
    public void Moduli_match_hand_calculation()
    {
        // Vp 1800, Vs 400, ρ 1900:
        // ν = (3.24e6 − 3.2e5) / (2 (3.24e6 − 1.6e5)) = 2.92e6 / 6.16e6 = 0.474025974
        // G = 1900·1.6e5 = 3.04e8 Pa; E = 2G(1+ν) = 8.962078e8 Pa; K = 1900 (3.24e6 − 2.1333e5) = 5.750667e9 Pa.
        var p = new EngineeringPropertyCalculator().Calculate(1800, 400, Rho(1900));
        Assert.Equal(2.92e6 / 6.16e6, p.PoissonRatio.Value, 12);
        Assert.Equal(3.04e8, p.ShearModulus.Value, 3);
        Assert.Equal(2 * 3.04e8 * (1 + 2.92e6 / 6.16e6), p.YoungsModulus.Value, 3);
        Assert.Equal(1900 * (3.24e6 - 4.0 / 3.0 * 1.6e5), p.BulkModulus.Value, 3);
        Assert.Equal(4.5, p.VpVsRatio.Value, 12);
    }

    [Theory]
    [InlineData(1800, 400, 1900)]
    [InlineData(3000, 1500, 2300)]
    [InlineData(600, 250, 1700)]
    public void Equivalent_formulas_agree(double vp, double vs, double rho)
    {
        var p = new EngineeringPropertyCalculator().Calculate(vp, vs, Rho(rho));
        var g = p.ShearModulus.Value;
        var k = p.BulkModulus.Value;
        var e = p.YoungsModulus.Value;
        var nu = p.PoissonRatio.Value;
        Assert.Equal(rho * vs * vs * (3 * vp * vp - 4 * vs * vs) / (vp * vp - vs * vs), e, e * 1e-12);
        Assert.Equal(9 * k * g / (3 * k + g), e, e * 1e-12);
        Assert.Equal((3 * k - 2 * g) / (2 * (3 * k + g)), nu, 1e-12);
        Assert.Equal(rho * vp * vp, k + 4.0 / 3.0 * g, rho * vp * vp * 1e-12); // P-wave modulus M = K + 4G/3
    }

    [Fact]
    public void Vs_not_below_vp_is_flagged_not_computed()
    {
        var p = new EngineeringPropertyCalculator().Calculate(500, 600, Rho(1800));
        Assert.False(p.PoissonRatio.HasValue);
        Assert.False(p.YoungsModulus.HasValue);
        Assert.Contains(p.Flags, f => f.Contains("Vs ≥ Vp"));
    }

    [Fact]
    public void Negative_bulk_modulus_and_negative_poisson_ratio_are_flagged()
    {
        // Vp/Vs = 1.1 < 2/√3: K < 0, ν < 0.
        var p = new EngineeringPropertyCalculator().Calculate(1100, 1000, Rho(2000));
        Assert.False(p.BulkModulus.HasValue);
        Assert.True(p.PoissonRatio.Value < 0);
        Assert.Contains(p.Flags, f => f.Contains("Negative bulk modulus"));
        Assert.Contains(p.Flags, f => f.Contains("Negative Poisson"));
    }

    [Fact]
    public void Missing_density_leaves_moduli_unavailable_but_poisson_available()
    {
        var p = new EngineeringPropertyCalculator().Calculate(1800, 400, Computed<DensityValue>.Unavailable("Missing density"));
        Assert.True(p.PoissonRatio.HasValue);
        Assert.False(p.ShearModulus.HasValue);
        Assert.Equal("Missing density", p.ShearModulus.Reason);
    }
}

public class DensityTests
{
    [Fact]
    public void Gardner_and_brocher_values()
    {
        Assert.Equal(310 * Math.Pow(2000, 0.25), DensityProvider.Gardner(2000), 9);
        Assert.Equal(2073.095, DensityProvider.Gardner(2000), 0.001);
        // Brocher at 2 km/s: 3.3224 − 1.8884 + 0.5368 − 0.0688 + 0.003392 = 1.905392 g/cm³.
        Assert.Equal(1905.392, DensityProvider.Brocher(2000), 0.001);
    }

    [Fact]
    public void Density_is_never_inferred_unless_estimation_is_selected()
    {
        var provider = new DensityProvider();
        var none = provider.DensityAt(DensityModel.None, 5, 2000);
        Assert.False(none.HasValue);
        Assert.Contains("Missing density", none.Reason);

        var measured = new DensityModel(DensitySource.Measured, [new DensityInterval(0, 10, 1850, "lab")], null, null);
        Assert.Equal(1850, provider.DensityAt(measured, 5, null).Value!.Value);
        Assert.False(provider.DensityAt(measured, 12, null).HasValue);

        var estimated = new DensityModel(DensitySource.Estimated, [], DensityEstimator.Gardner1974, null);
        var e = provider.DensityAt(estimated, 5, 2500);
        Assert.True(e.Value!.Estimated);
        Assert.Contains("ESTIMATED", e.Notes);
        Assert.False(provider.DensityAt(estimated, 5, 800).HasValue); // outside the calibrated range
    }
}
