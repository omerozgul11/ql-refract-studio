using SkiaSharp;

namespace RefractionStudio.Visualization;

/// <summary>Severity/state of a plotted item. Shown by colour AND marker shape (never colour alone).</summary>
public enum MarkerState
{
    Normal,
    Review,
    Problem,
    Excluded,
    Selected,
}

/// <summary>How a pick was made (shown by marker fill/line style).</summary>
public enum PickStyle
{
    Manual,
    Automatic,
    Accepted,
    Correlation,
    Suggestion,
}

/// <summary>Colours and fonts of plots. Light, dark and print variants are provided.</summary>
public sealed record PlotTheme(
    SKColor Background,
    SKColor PlotBackground,
    SKColor Foreground,
    SKColor MutedForeground,
    SKColor Grid,
    SKColor Axis,
    SKColor Trace,
    SKColor TraceFill,
    SKColor ExcludedTrace,
    SKColor Accent,
    SKColor Pick,
    SKColor Suggestion,
    SKColor Ok,
    SKColor Review,
    SKColor Problem,
    SKColor Selection,
    SKColor Topography,
    IReadOnlyList<SKColor> Series,
    float FontSize,
    float TitleSize)
{
    /// <summary>Typeface used for all plot text. The application may replace it (e.g. with Inter).</summary>
    public static SKTypeface Typeface { get; set; } = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;

    // Series colours chosen for distinguishability (Okabe–Ito palette, colour-blind safe).
    private static readonly SKColor[] OkabeIto =
    [
        new(0x00, 0x72, 0xB2), new(0xD5, 0x5E, 0x00), new(0x00, 0x9E, 0x73), new(0xCC, 0x79, 0xA7),
        new(0xE6, 0x9F, 0x00), new(0x56, 0xB4, 0xE9), new(0x7F, 0x7F, 0x7F), new(0xF0, 0xE4, 0x42),
    ];

    public static PlotTheme Light { get; } = new(
        new SKColor(0xFF, 0xFF, 0xFF), new SKColor(0xFF, 0xFF, 0xFF), new SKColor(0x1F, 0x23, 0x28), new SKColor(0x5F, 0x67, 0x70),
        new SKColor(0xE6, 0xE9, 0xED), new SKColor(0x8A, 0x92, 0x9B), new SKColor(0x1F, 0x23, 0x28), new SKColor(0x1F, 0x23, 0x28, 0xD0),
        new SKColor(0xB8, 0xBE, 0xC5), new SKColor(0x0B, 0x6E, 0xBF), new SKColor(0xD0, 0x1F, 0x3C), new SKColor(0x0B, 0x6E, 0xBF),
        new SKColor(0x1A, 0x7F, 0x37), new SKColor(0xB0, 0x7D, 0x00), new SKColor(0xC4, 0x2B, 0x1C), new SKColor(0x0B, 0x6E, 0xBF, 0x40),
        new SKColor(0x6B, 0x4F, 0x2A), OkabeIto, 11f, 13f);

    public static PlotTheme Dark { get; } = new(
        new SKColor(0x1B, 0x1E, 0x22), new SKColor(0x16, 0x19, 0x1D), new SKColor(0xE6, 0xE8, 0xEB), new SKColor(0x9A, 0xA2, 0xAB),
        new SKColor(0x2C, 0x31, 0x37), new SKColor(0x6B, 0x73, 0x7C), new SKColor(0xDD, 0xE1, 0xE6), new SKColor(0xDD, 0xE1, 0xE6, 0xC0),
        new SKColor(0x55, 0x5C, 0x64), new SKColor(0x4C, 0xA8, 0xF0), new SKColor(0xFF, 0x5C, 0x70), new SKColor(0x4C, 0xA8, 0xF0),
        new SKColor(0x4C, 0xC3, 0x6E), new SKColor(0xF2, 0xC0, 0x3A), new SKColor(0xFF, 0x6B, 0x5B), new SKColor(0x4C, 0xA8, 0xF0, 0x40),
        new SKColor(0xC9, 0xA4, 0x6B), [.. OkabeIto.Select(c => Lighten(c, 0.15f))], 11f, 13f);

    /// <summary>Print/export theme: white background, black text, slightly larger text.</summary>
    public static PlotTheme Print { get; } = Light with { FontSize = 10f, TitleSize = 12f };

    public SKColor SeriesColor(int index) => Series[((index % Series.Count) + Series.Count) % Series.Count];

    public SKColor StateColor(MarkerState state) => state switch
    {
        MarkerState.Review => Review,
        MarkerState.Problem => Problem,
        MarkerState.Excluded => ExcludedTrace,
        MarkerState.Selected => Accent,
        _ => Ok,
    };

    public SKFont Font(float? size = null, bool bold = false)
        => new(bold ? SKTypeface.FromFamilyName(Typeface.FamilyName, SKFontStyle.Bold) ?? Typeface : Typeface, size ?? FontSize) { Subpixel = true };

    private static SKColor Lighten(SKColor c, float f)
        => new((byte)(c.Red + (255 - c.Red) * f), (byte)(c.Green + (255 - c.Green) * f), (byte)(c.Blue + (255 - c.Blue) * f), c.Alpha);
}
