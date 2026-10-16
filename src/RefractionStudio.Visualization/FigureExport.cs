using SkiaSharp;

namespace RefractionStudio.Visualization;

/// <summary>Raster/vector figure formats.</summary>
public enum FigureFormat
{
    Png,
    Svg,
    Pdf,
}

/// <summary>Options of an exported figure.</summary>
/// <param name="Width">Width in points (1/72 in) for vector output, in logical pixels for PNG.</param>
/// <param name="Height">Height (same units).</param>
/// <param name="Scale">Raster scale factor (2 = double resolution) for PNG.</param>
/// <param name="TransparentBackground">PNG only: leave the background transparent.</param>
public sealed record FigureOptions(int Width = 1200, int Height = 700, float Scale = 2, bool TransparentBackground = false);

/// <summary>Renders any plot callback to PNG, SVG or PDF using the same renderer as the screen.</summary>
public static class FigureExport
{
    public static byte[] Render(FigureFormat format, FigureOptions options, PlotTheme theme, Action<SKCanvas, SKRect, PlotTheme> draw)
    {
        var bounds = new SKRect(0, 0, options.Width, options.Height);
        var drawTheme = options.TransparentBackground && format == FigureFormat.Png
            ? theme with { Background = SKColors.Transparent, PlotBackground = SKColors.Transparent }
            : theme;
        using var ms = new MemoryStream();
        switch (format)
        {
            case FigureFormat.Png:
            {
                var w = (int)Math.Ceiling(options.Width * options.Scale);
                var h = (int)Math.Ceiling(options.Height * options.Scale);
                var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Could not create a drawing surface of that size.");
                var canvas = surface.Canvas;
                canvas.Clear(drawTheme.Background);
                canvas.Scale(options.Scale);
                draw(canvas, bounds, drawTheme);
                canvas.Flush();
                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                data.SaveTo(ms);
                break;
            }
            case FigureFormat.Svg:
            {
                using (var wstream = new SKManagedWStream(ms))
                using (var canvas = SKSvgCanvas.Create(bounds, wstream))
                {
                    draw(canvas, bounds, drawTheme);
                }
                break;
            }
            default:
            {
                using (var wstream = new SKManagedWStream(ms))
                using (var doc = SKDocument.CreatePdf(wstream))
                {
                    var canvas = doc.BeginPage(options.Width, options.Height);
                    draw(canvas, bounds, drawTheme);
                    doc.EndPage();
                    doc.Close();
                }
                break;
            }
        }
        return ms.ToArray();
    }

    public static string Extension(FigureFormat f) => f switch { FigureFormat.Png => ".png", FigureFormat.Svg => ".svg", _ => ".pdf" };
}
