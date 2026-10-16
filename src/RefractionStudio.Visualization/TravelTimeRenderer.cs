using SkiaSharp;

namespace RefractionStudio.Visualization;

/// <summary>An observed first arrival in the travel-time plot.</summary>
/// <param name="Id">Caller's identifier (used by hit testing).</param>
/// <param name="ShotIndex">Index of the shot (colour).</param>
/// <param name="X">Receiver position (m).</param>
/// <param name="Time">Corrected travel time (s).</param>
/// <param name="Layer">Assigned layer (marker shape).</param>
/// <param name="Included">Excluded picks are drawn as crosses but stay visible.</param>
/// <param name="State">QC state.</param>
/// <param name="Selected">Highlighted.</param>
public sealed record TravelTimePoint(string Id, int ShotIndex, double X, double Time, int Layer, bool Included, MarkerState State, bool Selected = false);

/// <summary>A line on the travel-time plot (segment fit, model prediction, crossover).</summary>
public sealed record TravelTimeCurve(int ShotIndex, IReadOnlyList<(double X, double T)> Points, bool Dashed, string? Label = null, SKColor? Color = null);

/// <summary>Model of a travel-time plot.</summary>
public sealed record TravelTimePlotModel(
    string? Title,
    ImmutableArray<TravelTimePoint> Points,
    ImmutableArray<TravelTimeCurve> Curves,
    ImmutableArray<(int ShotIndex, double X, string Label)> Shots,
    ImmutableArray<(double X, double T1, double T2)> ReciprocalPairs,
    bool ShowLegend = true);

/// <summary>Draws travel-time (T–X) plots: position across, time up.</summary>
public static class TravelTimeRenderer
{
    public static AxisOptions Axes(TravelTimePlotModel m) => new(m.Title, "Position (m)", "Corrected travel time (ms)", 1, 1000);

    public static Viewport Fit(TravelTimePlotModel m, SKRect plot)
    {
        var xs = m.Points.Select(p => p.X).Concat(m.Shots.Select(s => s.X)).Where(double.IsFinite).ToList();
        var ts = m.Points.Select(p => p.Time).Where(double.IsFinite).ToList();
        if (xs.Count == 0 || ts.Count == 0) return Viewport.Fit(0, 1, 0, 0.05, plot, yDown: false);
        return Viewport.Fit(xs.Min(), xs.Max(), Math.Min(0, ts.Min()), ts.Max(), plot, yDown: false, 0.04);
    }

    public static void Render(SKCanvas canvas, SKRect bounds, TravelTimePlotModel m, PlotTheme theme, Viewport? viewport = null)
    {
        using var bg = new SKPaint { Color = theme.Background };
        canvas.DrawRect(bounds, bg);
        var axes = Axes(m);
        var plot = Visualization.Axes.PlotArea(bounds, theme, axes);
        var vp = (viewport ?? Fit(m, plot)).WithPlot(plot);
        using (var pb = new SKPaint { Color = theme.PlotBackground }) canvas.DrawRect(plot, pb);
        Visualization.Axes.Draw(canvas, vp, theme, axes);
        canvas.Save();
        canvas.ClipRect(plot);

        foreach (var c in m.Curves)
        {
            if (c.Points.Count < 2) continue;
            using var p = new SKPaint
            {
                Color = (c.Color ?? theme.SeriesColor(c.ShotIndex)).WithAlpha(c.Dashed ? (byte)200 : (byte)255),
                StrokeWidth = c.Dashed ? 1.2f : 1.6f,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                PathEffect = c.Dashed ? SKPathEffect.CreateDash([5, 4], 0) : null,
            };
            using var path = new SKPath();
            path.MoveTo(vp.Point(c.Points[0].X, c.Points[0].T));
            foreach (var pt in c.Points.Skip(1)) path.LineTo(vp.Point(pt.X, pt.T));
            canvas.DrawPath(path, p);
        }

        using (var recip = new SKPaint { Color = theme.Problem.WithAlpha(160), StrokeWidth = 2, IsAntialias = true })
        {
            foreach (var (x, t1, t2) in m.ReciprocalPairs) canvas.DrawLine(vp.X(x), vp.Y(t1), vp.X(x), vp.Y(t2), recip);
        }

        foreach (var p in m.Points.OrderBy(p => p.Selected))
        {
            var pt = vp.Point(p.X, p.Time);
            var color = theme.SeriesColor(p.ShotIndex);
            if (!p.Included)
            {
                Visualization.Axes.Marker(canvas, pt, 7, MarkerState.Excluded, theme.ExcludedTrace, false, theme);
                continue;
            }
            Visualization.Axes.LayerMarker(canvas, pt, 7, p.Layer, color, filled: p.State == MarkerState.Normal);
            if (p.State is MarkerState.Review or MarkerState.Problem)
                Visualization.Axes.Marker(canvas, new SKPoint(pt.X + 7, pt.Y - 7), 7, p.State, theme.StateColor(p.State), true, theme);
            if (p.Selected)
            {
                using var halo = new SKPaint { Color = theme.Accent, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
                canvas.DrawCircle(pt, 7, halo);
            }
        }

        // Shot markers along the time axis origin.
        foreach (var (idx, x, label) in m.Shots)
        {
            var px = vp.X(x);
            var py = vp.Y(0);
            using var star = new SKPaint { Color = theme.SeriesColor(idx), IsAntialias = true };
            using var path = new SKPath();
            for (var k = 0; k < 10; k++)
            {
                var r = k % 2 == 0 ? 7f : 3f;
                var a = -Math.PI / 2 + k * Math.PI / 5;
                var q = new SKPoint(px + r * (float)Math.Cos(a), py + r * (float)Math.Sin(a));
                if (k == 0) path.MoveTo(q); else path.LineTo(q);
            }
            path.Close();
            canvas.DrawPath(path, star);
        }
        canvas.Restore();

        if (m.ShowLegend)
        {
            var items = new List<(string, SKColor, Action<SKCanvas, SKPoint>?)>();
            var layers = m.Points.Where(p => p.Included).Select(p => p.Layer).Distinct().Order().ToList();
            foreach (var l in layers)
                items.Add(($"Layer {l} arrivals", theme.Foreground, (c, at) => Visualization.Axes.LayerMarker(c, at, 7, l, theme.Foreground, true)));
            if (m.Points.Any(p => !p.Included))
                items.Add(("Excluded pick", theme.ExcludedTrace, (c, at) => Visualization.Axes.Marker(c, at, 7, MarkerState.Excluded, theme.ExcludedTrace, false, theme)));
            if (m.Points.Any(p => p.State == MarkerState.Review))
                items.Add(("Review", theme.Review, (c, at) => Visualization.Axes.Marker(c, at, 7, MarkerState.Review, theme.Review, true, theme)));
            if (m.Points.Any(p => p.State == MarkerState.Problem))
                items.Add(("Likely problem", theme.Problem, (c, at) => Visualization.Axes.Marker(c, at, 7, MarkerState.Problem, theme.Problem, true, theme)));
            if (m.Curves.Any(c => c.Dashed)) items.Add(("Model prediction", theme.MutedForeground, null));
            Visualization.Axes.Legend(canvas, plot, theme, items);
        }
    }

    /// <summary>Nearest point to a pixel within <paramref name="radius"/> pixels.</summary>
    public static TravelTimePoint? PointAt(TravelTimePlotModel m, Viewport vp, SKPoint px, float radius = 8)
    {
        TravelTimePoint? best = null;
        var bestD = radius * radius;
        foreach (var p in m.Points)
        {
            var q = vp.Point(p.X, p.Time);
            var d = (q.X - px.X) * (q.X - px.X) + (q.Y - px.Y) * (q.Y - px.Y);
            if (d <= bestD) { bestD = d; best = p; }
        }
        return best;
    }
}
