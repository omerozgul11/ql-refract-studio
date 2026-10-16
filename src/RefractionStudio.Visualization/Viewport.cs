using SkiaSharp;

namespace RefractionStudio.Visualization;

/// <summary>
/// Maps data coordinates to pixels inside a plot rectangle. Y may be inverted (time or depth increasing
/// downwards). Immutable: zoom and pan return new viewports.
/// </summary>
public sealed record Viewport(double XMin, double XMax, double YMin, double YMax, SKRect Plot, bool YDown)
{
    public double Width => XMax - XMin;
    public double Height => YMax - YMin;

    public float X(double x) => (float)(Plot.Left + (x - XMin) / Width * Plot.Width);

    public float Y(double y) => YDown
        ? (float)(Plot.Top + (y - YMin) / Height * Plot.Height)
        : (float)(Plot.Bottom - (y - YMin) / Height * Plot.Height);

    public SKPoint Point(double x, double y) => new(X(x), Y(y));

    public double DataX(float px) => XMin + (px - Plot.Left) / Plot.Width * Width;

    public double DataY(float py) => YDown
        ? YMin + (py - Plot.Top) / Plot.Height * Height
        : YMin + (Plot.Bottom - py) / Plot.Height * Height;

    /// <summary>Data units per pixel in x.</summary>
    public double XPerPixel => Width / Math.Max(1, Plot.Width);

    /// <summary>Data units per pixel in y.</summary>
    public double YPerPixel => Height / Math.Max(1, Plot.Height);

    public Viewport WithPlot(SKRect plot) => this with { Plot = plot };

    /// <summary>Zooms about a pixel position; factors &gt; 1 zoom in.</summary>
    public Viewport Zoom(double factorX, double factorY, SKPoint about)
    {
        var cx = DataX(about.X);
        var cy = DataY(about.Y);
        return this with
        {
            XMin = cx - (cx - XMin) / factorX,
            XMax = cx + (XMax - cx) / factorX,
            YMin = cy - (cy - YMin) / factorY,
            YMax = cy + (YMax - cy) / factorY,
        };
    }

    /// <summary>Pans by a pixel displacement.</summary>
    public Viewport Pan(float dxPixels, float dyPixels)
    {
        var dx = dxPixels * XPerPixel;
        var dy = dyPixels * YPerPixel * (YDown ? 1 : -1);
        return this with { XMin = XMin - dx, XMax = XMax - dx, YMin = YMin - dy, YMax = YMax - dy };
    }

    public bool Contains(double x, double y) => x >= Math.Min(XMin, XMax) && x <= Math.Max(XMin, XMax) && y >= Math.Min(YMin, YMax) && y <= Math.Max(YMin, YMax);

    /// <summary>A viewport fitting the data range with a relative margin.</summary>
    public static Viewport Fit(double xMin, double xMax, double yMin, double yMax, SKRect plot, bool yDown, double margin = 0.03)
    {
        if (!(xMax > xMin)) { xMin -= 1; xMax += 1; }
        if (!(yMax > yMin)) { yMin -= 1; yMax += 1; }
        var mx = (xMax - xMin) * margin;
        var my = (yMax - yMin) * margin;
        return new Viewport(xMin - mx, xMax + mx, yMin - my, yMax + my, plot, yDown);
    }
}

/// <summary>"Nice" axis ticks (1, 2, 2.5, 5 × 10ⁿ steps).</summary>
public static class Ticks
{
    public static double NiceStep(double range, int targetCount)
    {
        if (!(range > 0) || targetCount < 1) return 1;
        var raw = range / targetCount;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var norm = raw / mag;
        var nice = norm < 1.5 ? 1 : norm < 2.25 ? 2 : norm < 3.5 ? 2.5 : norm < 7.5 ? 5 : 10;
        return nice * mag;
    }

    public static IReadOnlyList<double> Values(double min, double max, int targetCount)
    {
        if (max < min) (min, max) = (max, min);
        var step = NiceStep(max - min, targetCount);
        var first = Math.Ceiling(min / step - 1e-9) * step;
        var list = new List<double>();
        for (var v = first; v <= max + step * 1e-9 && list.Count < 1000; v += step) list.Add(Math.Abs(v) < step * 1e-9 ? 0 : v);
        return list;
    }

    /// <summary>Formats a tick value with the decimals the step needs.</summary>
    public static string Format(double value, double step)
    {
        var decimals = step >= 1 ? 0 : Math.Min(6, (int)Math.Ceiling(-Math.Log10(step) - 1e-9));
        if (step < 1 && step * Math.Pow(10, decimals) % 1 > 1e-6) decimals++;
        return value.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
    }
}
