using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.Engineering;

/// <summary>Time-averaged shear-wave velocity over a depth interval.</summary>
/// <param name="X">Position (m).</param>
/// <param name="Top">Top of the interval (m).</param>
/// <param name="Bottom">Bottom of the interval (m).</param>
/// <param name="Velocity">Harmonic (travel-time) average velocity (m/s).</param>
/// <param name="TravelTime">Vertical travel time through the interval (s).</param>
/// <param name="Extrapolated">True when part of the interval lies below the constrained depth.</param>
/// <param name="ConstrainedDepth">Depth constrained by data (m).</param>
/// <param name="ExtrapolationVelocity">Velocity assumed below the constrained depth (m/s), when extrapolated.</param>
/// <param name="Source">Model source.</param>
public sealed record EquivalentVelocity(double X, double Top, double Bottom, double Velocity, double TravelTime, bool Extrapolated, double ConstrainedDepth, double? ExtrapolationVelocity, string Source)
{
    /// <summary>Label shown everywhere the value appears.</summary>
    public string Qualifier => Extrapolated
        ? $"EXTRAPOLATED below {ConstrainedDepth:0.#} m at constant {ExtrapolationVelocity:0} m/s"
        : "Measured";
}

/// <summary>Vs30 and equivalent-velocity calculations.</summary>
public interface IVs30Calculator
{
    Computed<EquivalentVelocity> Calculate(VelocityProfile profile, double top, double bottom, bool allowExtrapolation);
}

/// <summary>
/// Vs_Z = (Z_b − Z_t) / Σ h_i/V_i over the interval (time-averaged velocity; Vs30 for 0–30 m).
/// If the profile is constrained to less than the bottom depth, the value is not computed unless the
/// user has enabled extrapolation; extrapolation holds the deepest constrained velocity constant and the
/// result is labelled EXTRAPOLATED.
/// </summary>
public sealed class Vs30Calculator : IVs30Calculator
{
    public Computed<EquivalentVelocity> Calculate(VelocityProfile profile, double top, double bottom, bool allowExtrapolation)
    {
        if (!(bottom > top) || top < 0) return Computed<EquivalentVelocity>.Unavailable("The depth interval is invalid.");
        var constrained = profile.ConstrainedDepth;
        var extrapolate = constrained < bottom - 1e-9;
        if (extrapolate && !allowExtrapolation)
        {
            return Computed<EquivalentVelocity>.Unavailable(
                $"Insufficient measured depth for direct Vs{(top == 0 ? bottom.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "Z")} calculation: " +
                $"the model at x = {profile.X:0.##} m is constrained only to {constrained:0.#} m.");
        }
        if (top >= constrained && extrapolate)
            return Computed<EquivalentVelocity>.Unavailable($"The interval starts below the constrained depth ({constrained:0.#} m).");

        double time = 0, covered = 0;
        double? lastVelocity = null;
        var limit = Math.Min(bottom, constrained);
        foreach (var i in profile.Intervals)
        {
            var a = Math.Max(i.Top, top);
            var b = Math.Min(i.Bottom, limit);
            if (b <= a) continue;
            if (!(i.Velocity > 0)) return Computed<EquivalentVelocity>.Unavailable($"Invalid velocity in the profile at {i.Top:0.#}–{i.Bottom:0.#} m.");
            time += (b - a) / i.Velocity;
            covered += b - a;
            lastVelocity = i.Velocity;
        }
        if (lastVelocity is null) return Computed<EquivalentVelocity>.Unavailable("The profile has no velocities in the interval.");
        if (Math.Abs(covered - (limit - top)) > 1e-6) return Computed<EquivalentVelocity>.Unavailable("The profile has gaps in the interval.");

        double? extrapolationVelocity = null;
        if (extrapolate)
        {
            extrapolationVelocity = lastVelocity;
            time += (bottom - limit) / lastVelocity.Value;
        }
        var value = new EquivalentVelocity(profile.X, top, bottom, (bottom - top) / time, time, extrapolate, constrained, extrapolationVelocity, profile.Source);
        return extrapolate ? Computed<EquivalentVelocity>.Ok(value, "EXTRAPOLATED") : Computed<EquivalentVelocity>.Ok(value);
    }
}
