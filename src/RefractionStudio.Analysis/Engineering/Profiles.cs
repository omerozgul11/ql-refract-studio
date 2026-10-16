using RefractionStudio.Analysis.Layered;
using RefractionStudio.Analysis.Tomography;
using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.Engineering;

/// <summary>A constant-velocity depth interval of a 1-D profile.</summary>
public sealed record ProfileInterval(double Top, double Bottom, double Velocity);

/// <summary>A 1-D velocity profile below a position on the line.</summary>
/// <param name="X">Position (m).</param>
/// <param name="Intervals">Intervals from the surface down; the last may extend to infinity (half-space).</param>
/// <param name="ConstrainedDepth">Depth to which the data constrain the profile (m).</param>
/// <param name="Source">Model source description.</param>
public sealed record VelocityProfile(double X, ImmutableArray<ProfileInterval> Intervals, double ConstrainedDepth, string Source)
{
    /// <summary>Velocity at a depth, or null below the last interval.</summary>
    public double? VelocityAt(double depth)
    {
        foreach (var i in Intervals)
            if (depth >= i.Top && depth < i.Bottom) return i.Velocity;
        return null;
    }
}

/// <summary>Extracts 1-D profiles from velocity models.</summary>
public static class ProfileExtractor
{
    /// <summary>
    /// Profile from a tomogram column: cell intervals below the local ground. The constrained depth is
    /// the column's investigation depth (deepest ray-covered cell).
    /// </summary>
    public static Computed<VelocityProfile> FromTomography(TomographyResult result, double x)
    {
        var g = result.Model.Grid;
        if (x < g.X0 || x > g.XMax) return Computed<VelocityProfile>.Unavailable($"x = {x:0.##} m is outside the tomography model ({g.X0:0.##}–{g.XMax:0.##} m).");
        var c = g.ColumnOf(x);
        var doi = result.InvestigationDepth[c];
        if (!(doi > 0)) return Computed<VelocityProfile>.Unavailable($"No rays cross the model at x = {x:0.##} m.");
        var intervals = ImmutableArray.CreateBuilder<ProfileInterval>();
        for (var r = 0; r < g.Nz; r++)
        {
            var idx = g.Index(c, r);
            if (!g.Active[idx]) continue;
            var top = Math.Max(0, g.Ground[c] - (g.ZTop - r * g.Dz));
            var bottom = g.Ground[c] - (g.ZTop - (r + 1) * g.Dz);
            if (bottom <= top) continue;
            intervals.Add(new ProfileInterval(top, bottom, result.Model.Velocity[idx]));
        }
        return Computed<VelocityProfile>.Ok(new VelocityProfile(x, intervals.ToImmutable(), doi, "Tomography"));
    }

    /// <summary>
    /// Profile from a layered model. The half-space below the deepest refractor has no measured
    /// thickness, so the constrained depth is the deepest refractor depth at x.
    /// </summary>
    public static Computed<VelocityProfile> FromLayered(LayeredModel model, double x)
    {
        var intervals = ImmutableArray.CreateBuilder<ProfileInterval>();
        var top = 0.0;
        for (var k = 0; k < model.Refractors.Length; k++)
        {
            var d = model.Refractors[k].DepthAt(x);
            if (d is null) return Computed<VelocityProfile>.Unavailable($"Refractor {k + 1} is not determined at x = {x:0.##} m.");
            if (d > top) intervals.Add(new ProfileInterval(top, d.Value, model.Layers[k].Velocity));
            top = Math.Max(top, d.Value);
        }
        intervals.Add(new ProfileInterval(top, double.PositiveInfinity, model.Layers[^1].Velocity));
        return Computed<VelocityProfile>.Ok(new VelocityProfile(x, intervals.ToImmutable(), top, $"Layered ({model.Method})"));
    }
}
