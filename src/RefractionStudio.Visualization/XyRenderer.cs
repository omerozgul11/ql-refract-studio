using SkiaSharp;

namespace RefractionStudio.Visualization;

/// <summary>How a series is drawn.</summary>
public enum SeriesStyle
{
    Line,
    Step,
    Points,
    LinePoints,
    Area,
}

/// <summary>A data series of an XY chart.</summary>
/// <param name="Name">Legend label.</param>
/// <param name="Points">(x, y) values; NaN breaks the line.</param>
/// <param name="Style">Drawing style.</param>
/// <param name="Color">Colour; null = series colour by index.</param>
/// <param name="Dashed">Dashed line.</param>
/// <param name="States">Optional per-point states (marker shapes).</param>
public sealed record XySeries(string Name, IReadOnlyList<(double X, double Y)> Points, SeriesStyle Style = SeriesStyle.Line, SKColor? Color = null, bool Dashed = false,
    IReadOnlyList<MarkerState>? States = null);

/// <summary>A vertical or horizontal reference line with a label.</summary>
public sealed record XyMarker(bool Vertical, double Value, string Label, SKColor? Color = null);

/// <summary>Model of a generic XY chart (profiles, spectra, convergence, correlation curves).</summary>
/// <param name="YDown">Y increases downwards (depth profiles).</param>
/// <param name="LogY">Logarithmic y axis (positive values only).</param>
public sealed record XyPlotModel(string? Title, string XLabel, string YLabel, ImmutableArray<XySeries> Series, ImmutableArray<XyMarker> Markers,
    bool YDown = false, bool LogY = false, double? XMin = null, double? XMax = null, double? YMin = null, double? YMax = null, double? HoverY = null, bool XAxisOnTop = false);

/// <summary>Draws generic XY charts.</summary>
public static class XyRenderer
{
    public static Viewport Fit(XyPlotModel m, SKRect plot)
    {
        var pts = m.Series.SelectMany(s => s.Points).Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).Select(p => (p.X, Y: Transform(m, p.Y))).Where(p => double.IsFinite(p.Y)).ToList();
        var xs = pts.Select(p => p.X).Concat(m.Markers.Where(k => k.Vertical).Select(k => k.Value)).ToList();
        var ys = pts.Select(p => p.Y).Concat(m.Markers.Where(k => !k.Vertical).Select(k => Transform(m, k.Value))).ToList();
        var xMin = m.XMin ?? (xs.Count > 0 ? xs.Min() : 0);
        var xMax = m.XMax ?? (xs.Count > 0 ? xs.Max() : 1);
        var yMin = m.YMin is { } a ? Transform(m, a) : ys.Count > 0 ? ys.Min() : 0;
        var yMax = m.YMax is { } b ? Transform(m, b) : ys.Count > 0 ? ys.Max() : 1;
        var vp = Viewport.Fit(xMin, xMax, yMin, yMax, plot, m.YDown, 0.04);
        if (m.XMin is not null) vp = vp with { XMin = xMin };
        if (m.XMax is not null) vp = vp with { XMax = xMax };
        if (m.YMin is not null) vp = vp with { YMin = yMin };
        if (m.YMax is not null) vp = vp with { YMax = yMax };
        return vp;
    }

    private static double Transform(XyPlotModel m, double y) => m.LogY ? (y > 0 ? Math.Log10(y) : double.NaN) : y;

    public static void Render(SKCanvas canvas, SKRect bounds, XyPlotModel m, PlotTheme theme, Viewport? viewport = null)
    {
        using var bg = new SKPaint { Color = theme.Background };
        canvas.DrawRect(bounds, bg);
        var axes = new AxisOptions(m.Title, m.XLabel, m.LogY ? m.YLabel + " (log₁₀)" : m.YLabel, XAxisOnTop: m.XAxisOnTop);
        var plot = Axes.PlotArea(bounds, theme, axes);
        var vp = (viewport ?? Fit(m, plot)).WithPlot(plot);
        using (var pb = new SKPaint { Color = theme.PlotBackground }) canvas.DrawRect(plot, pb);
        Axes.Draw(canvas, vp, theme, axes);
        canvas.Save();
        canvas.ClipRect(plot);
        for (var i = 0; i < m.Series.Length; i++)
        {
            var s = m.Series[i];
            var color = s.Color ?? theme.SeriesColor(i);
            using var lp = new SKPaint { Color = color, StrokeWidth = 1.8f, IsAntialias = true, Style = SKPaintStyle.Stroke, PathEffect = s.Dashed ? SKPathEffect.CreateDash([6, 4], 0) : null };
            if (s.Style is SeriesStyle.Line or SeriesStyle.LinePoints or SeriesStyle.Step or SeriesStyle.Area)
            {
                using var path = new SKPath();
                var started = false;
                (double X, double Y)? prev = null;
                foreach (var (x, yRaw) in s.Points)
                {
                    var y = Transform(m, yRaw);
                    if (!double.IsFinite(x) || !double.IsFinite(y)) { started = false; prev = null; continue; }
                    var p = vp.Point(x, y);
                    if (!started) { path.MoveTo(p); started = true; }
                    else if (s.Style == SeriesStyle.Step && prev is { } pv)
                    {
                        // Step in the y direction for depth profiles: hold x until the next y.
                        if (m.YDown) path.LineTo(vp.Point(pv.X, y));
                        else path.LineTo(vp.Point(x, pv.Y));
                        path.LineTo(p);
                    }
                    else path.LineTo(p);
                    prev = (x, y);
                }
                if (s.Style == SeriesStyle.Area)
                {
                    using var fill = new SKPaint { Color = color.WithAlpha(50), Style = SKPaintStyle.Fill, IsAntialias = true };
                    using var area = new SKPath(path);
                    var pts = s.Points.Where(p => double.IsFinite(p.X) && double.IsFinite(Transform(m, p.Y))).ToList();
                    if (pts.Count > 1)
                    {
                        area.LineTo(vp.Point(pts[^1].X, vp.YMin));
                        area.LineTo(vp.Point(pts[0].X, vp.YMin));
                        area.Close();
                        canvas.DrawPath(area, fill);
                    }
                }
                canvas.DrawPath(path, lp);
            }
            if (s.Style is SeriesStyle.Points or SeriesStyle.LinePoints)
            {
                for (var k = 0; k < s.Points.Count; k++)
                {
                    var (x, yRaw) = s.Points[k];
                    var y = Transform(m, yRaw);
                    if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
                    var state = s.States is { } st && k < st.Count ? st[k] : MarkerState.Normal;
                    Axes.Marker(canvas, vp.Point(x, y), 7, state, state == MarkerState.Normal ? color : theme.StateColor(state), state != MarkerState.Excluded, theme);
                }
            }
        }
        foreach (var mk in m.Markers)
        {
            using var mp = new SKPaint { Color = mk.Color ?? theme.MutedForeground, StrokeWidth = 1.2f, PathEffect = SKPathEffect.CreateDash([4, 3], 0), IsAntialias = true };
            if (mk.Vertical)
            {
                var x = vp.X(mk.Value);
                canvas.DrawLine(x, plot.Top, x, plot.Bottom, mp);
                Axes.Label(canvas, mk.Label, new SKPoint(x + 3, plot.Top + theme.FontSize + 4), theme);
            }
            else
            {
                var y = vp.Y(Transform(m, mk.Value));
                canvas.DrawLine(plot.Left, y, plot.Right, y, mp);
                Axes.Label(canvas, mk.Label, new SKPoint(plot.Right - 4, y - 4), theme, SKTextAlign.Right);
            }
        }
        if (m.HoverY is { } hy)
        {
            using var hp = new SKPaint { Color = theme.Accent, StrokeWidth = 1 };
            var y = vp.Y(Transform(m, hy));
            canvas.DrawLine(plot.Left, y, plot.Right, y, hp);
        }
        canvas.Restore();
        if (m.Series.Length > 1)
            Axes.Legend(canvas, plot, theme, [.. m.Series.Select((s, i) => (s.Name, s.Color ?? theme.SeriesColor(i), (Action<SKCanvas, SKPoint>?)null))]);
    }
}

/// <summary>Model of the line-geometry diagram.</summary>
public sealed record SpreadPlotModel(
    string? Title,
    ImmutableArray<(double X, double Z)> Topography,
    ImmutableArray<(int Channel, double X, double Z, MarkerState State)> Receivers,
    ImmutableArray<(string Label, double X, double Z, double Depth, bool Selected, bool Included)> Shots,
    ImmutableArray<(double X, double Z)> ReferenceGeophones,
    double VerticalExaggeration = 5);

/// <summary>Draws the survey line: ground profile, geophones, shots and their labels.</summary>
public static class SpreadRenderer
{
    public static void Render(SKCanvas canvas, SKRect bounds, SpreadPlotModel m, PlotTheme theme)
    {
        var pts = m.Topography.Concat(m.Receivers.Select(r => (r.X, r.Z))).Concat(m.Shots.Where(s => double.IsFinite(s.X)).Select(s => (s.X, s.Z))).ToList();
        if (pts.Count == 0)
        {
            using var bg = new SKPaint { Color = theme.Background };
            canvas.DrawRect(bounds, bg);
            return;
        }
        var xMin = pts.Min(p => p.X);
        var xMax = pts.Max(p => p.X);
        var zMin = pts.Min(p => p.Z) - Math.Max(2, m.Shots.Select(s => s.Depth).DefaultIfEmpty(0).Max() + 1);
        var zMax = pts.Max(p => p.Z) + 2;
        var section = new SectionPlotModel(m.Title, "", null, Colormap.Viridis, 0, 1, [], m.Topography, [], [],
            [.. m.Receivers.Select(r => (r.X, r.Z))], [], [.. m.Shots.Where(s => double.IsFinite(s.X)).Select(s => (s.X, s.Z, s.Label))], m.VerticalExaggeration);
        var axes = SectionRenderer.Axes(section);
        var plot = Axes.PlotArea(bounds, theme, axes);
        var vp = Viewport.Fit(xMin, xMax, zMin, zMax, plot, false, 0.05);
        SectionRenderer.Render(canvas, bounds, section, theme, vp);
        foreach (var r in m.Receivers.Where(r => r.State != MarkerState.Normal))
            Axes.Marker(canvas, new SKPoint(vp.X(r.X), vp.Y(r.Z) - 12), 7, r.State, theme.StateColor(r.State), true, theme);
        foreach (var s in m.Shots.Where(s => double.IsFinite(s.X)))
        {
            var p = vp.Point(s.X, s.Z - s.Depth);
            using var star = new SKPaint { Color = s.Included ? (s.Selected ? theme.Accent : theme.Problem) : theme.ExcludedTrace, IsAntialias = true };
            using var path = new SKPath();
            for (var k = 0; k < 10; k++)
            {
                var r = k % 2 == 0 ? (s.Selected ? 9f : 7f) : 3f;
                var a = -Math.PI / 2 + k * Math.PI / 5;
                var q = new SKPoint(p.X + r * (float)Math.Cos(a), p.Y + r * (float)Math.Sin(a));
                if (k == 0) path.MoveTo(q); else path.LineTo(q);
            }
            path.Close();
            canvas.DrawPath(path, star);
        }
        using var refPaint = new SKPaint { Color = theme.Accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        foreach (var (x, z) in m.ReferenceGeophones) canvas.DrawRect(new SKRect(vp.X(x) - 4, vp.Y(z) - 10, vp.X(x) + 4, vp.Y(z) - 2), refPaint);
    }
}
