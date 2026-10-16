using SkiaSharp;

namespace RefractionStudio.Visualization;

/// <summary>A continuous colour map defined by evenly spaced control points.</summary>
public sealed class Colormap
{
    private readonly SKColor[] _stops;

    public Colormap(string name, string description, bool perceptuallyUniform, params SKColor[] stops)
    {
        Name = name;
        Description = description;
        PerceptuallyUniform = perceptuallyUniform;
        _stops = stops;
    }

    public string Name { get; }

    public string Description { get; }

    /// <summary>True if lightness changes monotonically (recommended for quantitative sections).</summary>
    public bool PerceptuallyUniform { get; }

    /// <summary>Colour at t ∈ [0, 1].</summary>
    public SKColor At(double t)
    {
        if (double.IsNaN(t)) return SKColors.Transparent;
        t = Math.Clamp(t, 0, 1) * (_stops.Length - 1);
        var i = Math.Min((int)t, _stops.Length - 2);
        var f = (float)(t - i);
        var a = _stops[i];
        var b = _stops[i + 1];
        return new SKColor(
            (byte)(a.Red + (b.Red - a.Red) * f),
            (byte)(a.Green + (b.Green - a.Green) * f),
            (byte)(a.Blue + (b.Blue - a.Blue) * f));
    }

    /// <summary>Viridis (van der Walt &amp; Smith, matplotlib; CC0), 11 control points.</summary>
    public static Colormap Viridis { get; } = new("Viridis", "Perceptually uniform, colour-blind friendly (recommended).", true,
        Hex("440154"), Hex("482475"), Hex("414487"), Hex("355F8D"), Hex("2A788E"), Hex("21918C"), Hex("22A884"), Hex("44BF70"), Hex("7AD151"), Hex("BDDF26"), Hex("FDE725"));

    /// <summary>Cividis (Nuñez, Anderton &amp; Renslow 2018), optimised for colour-vision deficiency.</summary>
    public static Colormap Cividis { get; } = new("Cividis", "Perceptually uniform; optimised for colour-vision deficiency.", true,
        Hex("00204D"), Hex("123570"), Hex("3B496C"), Hex("575D6D"), Hex("707173"), Hex("8A8779"), Hex("A69D75"), Hex("C4B56C"), Hex("E4CF5B"), Hex("FFEA46"));

    /// <summary>Classic rainbow common in refraction reports. Not perceptually uniform; offered for familiarity.</summary>
    public static Colormap Rainbow { get; } = new("Classic rainbow", "Familiar in refraction reports but not perceptually uniform: equal velocity steps do not look equal.", false,
        Hex("2B2C8F"), Hex("2A5DC9"), Hex("2E9BD6"), Hex("3BC5B5"), Hex("74D46A"), Hex("C3DC3F"), Hex("F4C531"), Hex("F38C2A"), Hex("E2512A"), Hex("B5212B"));

    /// <summary>Diverging blue–white–red for signed quantities (amplitudes, residuals).</summary>
    public static Colormap Diverging { get; } = new("Blue–white–red", "Diverging; for signed values such as residuals and amplitudes.", true,
        Hex("2166AC"), Hex("4393C3"), Hex("92C5DE"), Hex("D1E5F0"), Hex("F7F7F7"), Hex("FDDBC7"), Hex("F4A582"), Hex("D6604D"), Hex("B2182B"));

    /// <summary>Greyscale for amplitude images.</summary>
    public static Colormap Grey { get; } = new("Grey", "Greyscale.", true, Hex("000000"), Hex("FFFFFF"));

    public static IReadOnlyList<Colormap> All { get; } = [Viridis, Cividis, Rainbow, Diverging, Grey];

    public static Colormap ByName(string? name) => All.FirstOrDefault(c => c.Name == name) ?? Viridis;

    private static SKColor Hex(string hex) => SKColor.Parse(hex);
}
