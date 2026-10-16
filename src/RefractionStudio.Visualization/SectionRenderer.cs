using SkiaSharp;

namespace RefractionStudio.Visualization;

/// <summary>A gridded property section (cell values, row 0 at the top; z is elevation).</summary>
/// <param name="Constrained">Per cell: true if constrained by data (rays). Null = all constrained.</param>
/// <param name="Ground">Ground elevation per column.</param>
public sealed record SectionGrid(double X0, double Dx, int Nx, double ZTop, double Dz, int Nz, double[] Values, bool[]? Constrained, double[] Ground)
{
    public double Value(int column, int row) => Values[row * Nx + column];

    public bool IsConstrained(int column, int row) => Constrained?[row * Nx + column] ?? true;
}

/// <summary>A labelled polyline in (x, elevation).</summary>
public sealed record SectionLine(string Label, IReadOnlyList<(double X, double Z)> Points, SKColor? Color = null, bool Dashed = false);

/// <summary>Model of a section plot.</summary>
public sealed record SectionPlotModel(
    string? Title,
    string ValueLabel,
    SectionGrid? Grid,
    Colormap Colormap,
    double ValueMin,
    double ValueMax,
    ImmutableArray<double> Contours,
    ImmutableArray<(double X, double Z)> Topography,
    ImmutableArray<SectionLine> Interfaces,
    ImmutableArray<IReadOnlyList<(double X, double Z)>> Rays,
    ImmutableArray<(double X, double Z)> Receivers,
    ImmutableArray<(double X, double Z)> Shots,
    ImmutableArray<(double X, double Z, string Text)> Labels,
    double VerticalExaggeration = 1,
    bool ShowUnconstrained = false,
    double? HoverX = null);

/// <summary>Draws velocity/property sections with contours, interfaces, rays and a colour bar.</summary>
public static class SectionRenderer
{
    private const float ColorBarWidth = 76;

    public static AxisOptions Axes(SectionPlotModel m) => new(m.Title, "Position (m)",
        m.VerticalExaggeration != 1 ? $"Elevation (m), V.E. {m.VerticalExaggeration:0.#}×" : "Elevation (m)");

    public static Viewport Fit(SectionPlotModel m, SKRect plot)
    {
        double xMin, xMax, zMin, zMax;
        if (m.Grid is { } g)
        {
            xMin = g.X0; xMax = g.X0 + g.Nx * g.Dx; zMax = g.ZTop; zMin = g.ZTop - g.Nz * g.Dz;
        }
        else
        {
            var pts = m.Topography.Concat(m.Interfaces.SelectMany(i => i.Points)).Concat(m.Receivers).Concat(m.Shots).ToList();
            if (pts.Count == 0) return Viewport.Fit(0, 100, -30, 0, plot, false);
            xMin = pts.Min(p => p.X); xMax = pts.Max(p => p.X); zMin = pts.Min(p => p.Z); zMax = pts.Max(p => p.Z);
            zMin -= 0.15 * (zMax - zMin + 1);
        }
        zMax += Math.Max(1, (zMax - zMin) * 0.05);
        // Honour the vertical exaggeration: metres per pixel vertically = horizontal / VE.
        var mppX = (xMax - xMin) / Math.Max(1, plot.Width);
        var neededHeight = (zMax - zMin) / mppX * m.VerticalExaggeration;
        if (neededHeight < plot.Height)
        {
            var extra = (plot.Height - neededHeight) * mppX / m.VerticalExaggeration;
            zMin -= extra;
        }
        else
        {
            // The depth range needs more pixels than available: widen x so that z fits at the requested exaggeration.
            var mppZ = (zMax - zMin) / Math.Max(1, plot.Height);
            var extraX = (plot.Width * mppZ * m.VerticalExaggeration - (xMax - xMin)) / 2;
            xMin -= extraX; xMax += extraX;
        }
        return new Viewport(xMin, xMax, zMin, zMax, plot, YDown: false);
    }

    public static void Render(SKCanvas canvas, SKRect bounds, SectionPlotModel m, PlotTheme theme, Viewport? viewport = null)
    {
        using var bg = new SKPaint { Color = theme.Background };
        canvas.DrawRect(bounds, bg);
        var axes = Axes(m);
        var plot = Visualization.Axes.PlotArea(bounds, theme, axes, m.Grid is null ? 0 : ColorBarWidth);
        var vp = (viewport ?? Fit(m, plot)).WithPlot(plot);
        using (var pb = new SKPaint { Color = theme.PlotBackground }) canvas.DrawRect(plot, pb);
        canvas.Save();
        canvas.ClipRect(plot);

        if (m.Grid is { } g)
        {
            using var cell = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };
            using var hatch = new SKPaint { Color = theme.Grid, StrokeWidth = 1, IsAntialias = true };
            var range = m.ValueMax - m.ValueMin;
            for (var r = 0; r < g.Nz; r++)
            {
                for (var c = 0; c < g.Nx; c++)
                {
                    var v = g.Value(c, r);
                    if (!double.IsFinite(v)) continue;
                    var x0 = vp.X(g.X0 + c * g.Dx);
                    var x1 = vp.X(g.X0 + (c + 1) * g.Dx);
                    var y0 = vp.Y(g.ZTop - r * g.Dz);
                    var y1 = vp.Y(g.ZTop - (r + 1) * g.Dz);
                    if (x1 < plot.Left || x0 > plot.Right || y0 > plot.Bottom || y1 < plot.Top) continue;
                    var rect = new SKRect(x0, y0, x1 + 0.6f, y1 + 0.6f);
                    var constrained = g.IsConstrained(c, r);
                    if (!constrained && !m.ShowUnconstrained)
                    {
                        // Unconstrained cells are left blank with a light hatch: no false impression of knowledge.
                        canvas.DrawLine(rect.Left, rect.Bottom, rect.Right, rect.Top, hatch);
                        continue;
                    }
                    var color = m.Colormap.At(range > 0 ? (v - m.ValueMin) / range : 0.5);
                    cell.Color = constrained ? color : color.WithAlpha(90);
                    canvas.DrawRect(rect, cell);
                }
            }
            DrawContours(canvas, vp, g, m.Contours, theme);
        }

        foreach (var ray in m.Rays)
        {
            if (ray.Count < 2) continue;
            using var rp = new SKPaint { Color = theme.Foreground.WithAlpha(45), StrokeWidth = 0.7f, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var path = new SKPath();
            path.MoveTo(vp.Point(ray[0].X, ray[0].Z));
            foreach (var p in ray.Skip(1)) path.LineTo(vp.Point(p.X, p.Z));
            canvas.DrawPath(path, rp);
        }

        var series = 0;
        foreach (var line in m.Interfaces)
        {
            if (line.Points.Count == 0) continue;
            var color = line.Color ?? theme.SeriesColor(series++ + 3);
            using var lp = new SKPaint { Color = color, StrokeWidth = 2.2f, IsAntialias = true, Style = SKPaintStyle.Stroke, PathEffect = line.Dashed ? SKPathEffect.CreateDash([6, 4], 0) : null };
            using var path = new SKPath();
            path.MoveTo(vp.Point(line.Points[0].X, line.Points[0].Z));
            foreach (var p in line.Points.Skip(1)) path.LineTo(vp.Point(p.X, p.Z));
            canvas.DrawPath(path, lp);
            using var dot = new SKPaint { Color = color, IsAntialias = true };
            if (line.Points.Count < 40)
                foreach (var p in line.Points) canvas.DrawCircle(vp.Point(p.X, p.Z), 2.5f, dot);
        }

        if (m.Topography.Length >= 2)
        {
            using var tp = new SKPaint { Color = theme.Topography, StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var path = new SKPath();
            path.MoveTo(vp.Point(m.Topography[0].X, m.Topography[0].Z));
            foreach (var p in m.Topography.Skip(1)) path.LineTo(vp.Point(p.X, p.Z));
            canvas.DrawPath(path, tp);
            // Air above the ground is masked.
            using var air = new SKPath();
            air.MoveTo(vp.Point(m.Topography[0].X, m.Topography[0].Z));
            foreach (var p in m.Topography.Skip(1)) air.LineTo(vp.Point(p.X, p.Z));
            air.LineTo(plot.Right + 10, vp.Y(m.Topography[^1].Z));
            air.LineTo(plot.Right + 10, plot.Top - 10);
            air.LineTo(plot.Left - 10, plot.Top - 10);
            air.LineTo(plot.Left - 10, vp.Y(m.Topography[0].Z));
            air.Close();
            using var airPaint = new SKPaint { Color = theme.PlotBackground.WithAlpha(235) };
            canvas.DrawPath(air, airPaint);
            canvas.DrawPath(path, tp);
        }

        using (var rec = new SKPaint { Color = theme.Foreground, IsAntialias = true })
        {
            foreach (var (x, z) in m.Receivers)
            {
                var p = vp.Point(x, z);
                using var path = new SKPath();
                path.MoveTo(p.X, p.Y);
                path.LineTo(p.X - 3.5f, p.Y - 6);
                path.LineTo(p.X + 3.5f, p.Y - 6);
                path.Close();
                canvas.DrawPath(path, rec);
            }
        }
        using (var shot = new SKPaint { Color = theme.Problem, IsAntialias = true })
        {
            foreach (var (x, z) in m.Shots) canvas.DrawCircle(vp.Point(x, z + 0.0), 4, shot);
        }
        foreach (var (x, z, text) in m.Labels) Visualization.Axes.Label(canvas, text, vp.Point(x, z), theme, SKTextAlign.Center);

        if (m.HoverX is { } hx)
        {
            using var hp = new SKPaint { Color = theme.Accent, StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash([3, 3], 0) };
            canvas.DrawLine(vp.X(hx), plot.Top, vp.X(hx), plot.Bottom, hp);
        }
        canvas.Restore();
        Visualization.Axes.Draw(canvas, vp, theme, axes, grid: false);

        if (m.Grid is not null)
            ColorBar(canvas, new SKRect(plot.Right + 18, plot.Top + 10, plot.Right + 34, plot.Bottom - 10), m.Colormap, m.ValueMin, m.ValueMax, m.ValueLabel, theme, m.Contours);
    }

    /// <summary>Vertical colour bar with ticks and a label.</summary>
    public static void ColorBar(SKCanvas canvas, SKRect rect, Colormap map, double min, double max, string label, PlotTheme theme, ImmutableArray<double> marks = default)
    {
        using var paint = new SKPaint();
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            paint.Color = map.At(1 - (y - rect.Top) / rect.Height);
            canvas.DrawRect(new SKRect(rect.Left, y, rect.Right, y + 1.2f), paint);
        }
        using var border = new SKPaint { Color = theme.Axis, Style = SKPaintStyle.Stroke };
        canvas.DrawRect(rect, border);
        using var font = theme.Font();
        using var text = new SKPaint { Color = theme.MutedForeground, IsAntialias = true };
        var ticks = Ticks.Values(min, max, 6);
        var step = ticks.Count > 1 ? ticks[1] - ticks[0] : 1;
        foreach (var t in ticks)
        {
            var y = rect.Bottom - (float)((t - min) / (max - min)) * rect.Height;
            canvas.DrawLine(rect.Right, y, rect.Right + 3, y, border);
            canvas.DrawText(Ticks.Format(t, step), new SKPoint(rect.Right + 5, y + font.Size * 0.35f), SKTextAlign.Left, font, text);
        }
        if (!marks.IsDefaultOrEmpty)
        {
            using var mk = new SKPaint { Color = theme.Foreground, StrokeWidth = 1.5f };
            foreach (var mv in marks.Where(v => v >= min && v <= max))
            {
                var y = rect.Bottom - (float)((mv - min) / (max - min)) * rect.Height;
                canvas.DrawLine(rect.Left - 3, y, rect.Right, y, mk);
            }
        }
        canvas.Save();
        canvas.Translate(rect.Right + font.Size * 4.2f, rect.MidY);
        canvas.RotateDegrees(90);
        using var lp = new SKPaint { Color = theme.Foreground, IsAntialias = true };
        canvas.DrawText(label, new SKPoint(0, 0), SKTextAlign.Center, font, lp);
        canvas.Restore();
    }

    /// <summary>Iso-value lines by marching squares on cell centres, labelled at their midpoint.</summary>
    private static void DrawContours(SKCanvas canvas, Viewport vp, SectionGrid g, ImmutableArray<double> levels, PlotTheme theme)
    {
        if (levels.IsDefaultOrEmpty) return;
        using var paint = new SKPaint { Color = theme.Foreground.WithAlpha(200), StrokeWidth = 1.1f, IsAntialias = true, Style = SKPaintStyle.Stroke };
        foreach (var level in levels)
        {
            var segments = ContourSegments(g, level);
            if (segments.Count == 0) continue;
            foreach (var (a, b) in segments) canvas.DrawLine(vp.Point(a.X, a.Z), vp.Point(b.X, b.Z), paint);
            var mid = segments[segments.Count / 2].A;
            Visualization.Axes.Label(canvas, level.ToString("0", System.Globalization.CultureInfo.InvariantCulture), vp.Point(mid.X, mid.Z), theme, SKTextAlign.Center, theme.FontSize - 1);
        }
    }

    /// <summary>Marching-squares segments of an iso-value on cell centres (cells with NaN or unconstrained are skipped).</summary>
    public static List<((double X, double Z) A, (double X, double Z) B)> ContourSegments(SectionGrid g, double level)
    {
        var segs = new List<((double, double), (double, double))>();
        double V(int c, int r) => g.IsConstrained(c, r) ? g.Value(c, r) : double.NaN;
        (double, double) P(int c, int r) => (g.X0 + (c + 0.5) * g.Dx, g.ZTop - (r + 0.5) * g.Dz);
        (double, double) Lerp((double X, double Z) p, (double X, double Z) q, double vp, double vq)
        {
            var t = (level - vp) / (vq - vp);
            return (p.X + (q.X - p.X) * t, p.Z + (q.Z - p.Z) * t);
        }
        for (var r = 0; r + 1 < g.Nz; r++)
        {
            for (var c = 0; c + 1 < g.Nx; c++)
            {
                double v0 = V(c, r), v1 = V(c + 1, r), v2 = V(c + 1, r + 1), v3 = V(c, r + 1);
                if (!double.IsFinite(v0) || !double.IsFinite(v1) || !double.IsFinite(v2) || !double.IsFinite(v3)) continue;
                var p0 = P(c, r); var p1 = P(c + 1, r); var p2 = P(c + 1, r + 1); var p3 = P(c, r + 1);
                var crossings = new List<(double, double)>();
                void Edge((double, double) a, (double, double) b, double va, double vb)
                {
                    if ((va < level) != (vb < level)) crossings.Add(Lerp(a, b, va, vb));
                }
                Edge(p0, p1, v0, v1);
                Edge(p1, p2, v1, v2);
                Edge(p2, p3, v2, v3);
                Edge(p3, p0, v3, v0);
                if (crossings.Count == 2) segs.Add((crossings[0], crossings[1]));
                else if (crossings.Count == 4)
                {
                    segs.Add((crossings[0], crossings[1]));
                    segs.Add((crossings[2], crossings[3]));
                }
            }
        }
        return segs;
    }

    /// <summary>Data coordinates under a pixel.</summary>
    public static (double X, double Z) DataAt(Viewport vp, SKPoint px) => (vp.DataX(px.X), vp.DataY(px.Y));
}
