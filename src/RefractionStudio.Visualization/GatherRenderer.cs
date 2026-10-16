using RefractionStudio.Domain;
using SkiaSharp;

namespace RefractionStudio.Visualization;

/// <summary>A trace to draw.</summary>
/// <param name="Channel">Channel number.</param>
/// <param name="X">Receiver position (m).</param>
/// <param name="Samples">Samples (display amplitudes).</param>
/// <param name="SampleInterval">Sample interval (s).</param>
/// <param name="StartTime">Display time of sample 0 (s).</param>
/// <param name="Excluded">Excluded traces are drawn muted and dashed.</param>
/// <param name="Selected">Highlighted trace.</param>
/// <param name="Overlay">Optional second waveform (e.g. raw) drawn in a contrasting colour.</param>
/// <param name="State">QC state of the trace (marker below the axis).</param>
public sealed record GatherPlotTrace(int Channel, double X, double[] Samples, double SampleInterval, double StartTime, bool Excluded = false, bool Selected = false,
    double[]? Overlay = null, MarkerState State = MarkerState.Normal);

/// <summary>A pick mark on a trace (display time).</summary>
public sealed record GatherPlotPick(int Channel, double Time, PickStyle Style, MarkerState State);

/// <summary>A polyline in (x, display time), e.g. a segment fit or model prediction.</summary>
public sealed record GatherPlotLine(IReadOnlyList<(double X, double T)> Points, SKColor Color, string? Label = null, bool Dashed = false);

/// <summary>Everything needed to draw a shot gather.</summary>
public sealed record GatherPlotModel(
    string? Title,
    double ShotX,
    ImmutableArray<GatherPlotTrace> Traces,
    ImmutableArray<GatherPlotPick> Picks,
    ImmutableArray<GatherPlotPick> Suggestions,
    ImmutableArray<GatherPlotLine> Lines,
    GatherDisplaySettings Display,
    double? CursorTime = null)
{
    /// <summary>Reduction applied to a time at position x (s).</summary>
    public double Reduction(double x) => Display.ReductionVelocity is { } v && v > 0 ? Math.Abs(x - ShotX) / v : 0;

    /// <summary>Median receiver spacing (m).</summary>
    public double Spacing
    {
        get
        {
            var xs = Traces.Select(t => t.X).Where(double.IsFinite).Order().ToList();
            var d = xs.Zip(xs.Skip(1), (a, b) => b - a).Where(v => v > 1e-9).Order().ToList();
            return d.Count > 0 ? d[d.Count / 2] : 1;
        }
    }
}

/// <summary>Draws shot gathers (receiver position across, time down).</summary>
public static class GatherRenderer
{
    public static AxisOptions Axes(GatherPlotModel model) => new(model.Title, "Receiver position (m)",
        model.Display.ReductionVelocity is { } v ? $"Reduced time t − |x|/{v:0} (ms)" : "Time (ms)", 1, 1000, XAxisOnTop: true);

    /// <summary>Viewport showing all traces and the display time window.</summary>
    public static Viewport Fit(GatherPlotModel model, SKRect plot)
    {
        var traces = model.Traces.Where(t => double.IsFinite(t.X)).ToList();
        if (traces.Count == 0) return Viewport.Fit(0, 1, 0, 0.1, plot, yDown: true);
        var spacing = model.Spacing;
        var xMin = traces.Min(t => t.X) - spacing;
        var xMax = traces.Max(t => t.X) + spacing;
        var tMin = traces.Min(t => t.StartTime - model.Reduction(t.X));
        var tMax = model.Display.TimeWindowEnd ?? traces.Max(t => t.StartTime + (t.Samples.Length - 1) * t.SampleInterval);
        return new Viewport(xMin, xMax, tMin, tMax, plot, YDown: true);
    }

    public static void Render(SKCanvas canvas, SKRect bounds, GatherPlotModel model, PlotTheme theme, Viewport? viewport = null)
    {
        using var bg = new SKPaint { Color = theme.Background };
        canvas.DrawRect(bounds, bg);
        var axes = Axes(model);
        var plot = Visualization.Axes.PlotArea(bounds, theme, axes);
        var vp = (viewport ?? Fit(model, plot)).WithPlot(plot);
        using (var pb = new SKPaint { Color = theme.PlotBackground })
            canvas.DrawRect(plot, pb);
        Visualization.Axes.Draw(canvas, vp, theme, axes, grid: model.Display.Mode != TraceDisplayMode.Image);

        DrawContent(canvas, vp, model, theme);

        // Trace state markers under the axis (non-colour indicator for QC).
        foreach (var t in model.Traces.Where(t => t.State != MarkerState.Normal || t.Excluded))
        {
            var x0 = vp.X(t.X);
            if (x0 < plot.Left || x0 > plot.Right) continue;
            Visualization.Axes.Marker(canvas, new SKPoint(x0, plot.Bottom - 7), 8, t.Excluded ? MarkerState.Excluded : t.State, theme.StateColor(t.Excluded ? MarkerState.Excluded : t.State), true, theme);
        }
    }

    /// <summary>Draws traces, lines and picks inside the viewport's plot rectangle (no axes).</summary>
    public static void DrawContent(SKCanvas canvas, Viewport vp, GatherPlotModel model, PlotTheme theme)
    {
        var plot = vp.Plot;
        canvas.Save();
        canvas.ClipRect(plot);
        var spacingPx = (float)(model.Spacing / vp.XPerPixel);
        var amplitudePx = Math.Max(2f, spacingPx * 0.5f) * (float)model.Display.Gain;
        var clipPx = (float)(spacingPx * model.Display.Clip);
        var globalScale = model.Display.Scaling == DisplayScaling.Global
            ? model.Traces.Where(t => !t.Excluded).Select(t => MaxAbs(t.Samples)).DefaultIfEmpty(1).Max()
            : 0;

        // Shot position.
        using (var shotPaint = new SKPaint { Color = theme.Accent.WithAlpha(90), StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash([4, 4], 0), IsAntialias = true })
        {
            var sx = vp.X(model.ShotX);
            canvas.DrawLine(sx, plot.Top, sx, plot.Bottom, shotPaint);
        }

        foreach (var t in model.Traces)
        {
            if (!double.IsFinite(t.X)) continue;
            var x0 = vp.X(t.X);
            if (x0 < plot.Left - spacingPx * 2 || x0 > plot.Right + spacingPx * 2) continue;
            var norm = model.Display.Scaling == DisplayScaling.Global ? globalScale : MaxAbs(t.Samples);
            if (!(norm > 0)) norm = 1;
            var scale = amplitudePx / norm;
            if (model.Display.Mode == TraceDisplayMode.Image)
                DrawImageTrace(canvas, vp, t, model, (float)Math.Max(1, spacingPx), scale / amplitudePx, theme);
            else
                DrawWiggle(canvas, vp, t, t.Samples, model, x0, (float)scale, clipPx, theme, model.Display.Mode != TraceDisplayMode.Wiggle, model.Display.Mode != TraceDisplayMode.VariableArea);
            if (t.Overlay is { } overlay)
            {
                var oNorm = model.Display.Scaling == DisplayScaling.Global ? globalScale : MaxAbs(overlay);
                DrawWiggle(canvas, vp, t, overlay, model, x0, (float)(amplitudePx / (oNorm > 0 ? oNorm : 1)), clipPx, theme with { Trace = theme.Accent.WithAlpha(170) }, false, true);
            }
            if (t.Selected)
            {
                using var sel = new SKPaint { Color = theme.Selection };
                canvas.DrawRect(new SKRect(x0 - spacingPx / 2, plot.Top, x0 + spacingPx / 2, plot.Bottom), sel);
            }
        }

        // Model / segment lines.
        foreach (var line in model.Lines)
        {
            if (line.Points.Count < 2) continue;
            using var p = new SKPaint { Color = line.Color, StrokeWidth = 1.6f, IsAntialias = true, Style = SKPaintStyle.Stroke, PathEffect = line.Dashed ? SKPathEffect.CreateDash([6, 4], 0) : null };
            using var path = new SKPath();
            path.MoveTo(vp.Point(line.Points[0].X, line.Points[0].T - model.Reduction(line.Points[0].X)));
            foreach (var pt in line.Points.Skip(1)) path.LineTo(vp.Point(pt.X, pt.T - model.Reduction(pt.X)));
            canvas.DrawPath(path, p);
        }

        // Suggestions then picks.
        var traceByChannel = model.Traces.GroupBy(t => t.Channel).ToDictionary(g => g.Key, g => g.First());
        foreach (var s in model.Suggestions) DrawPick(canvas, vp, model, traceByChannel, s, spacingPx, theme);
        foreach (var p in model.Picks) DrawPick(canvas, vp, model, traceByChannel, p, spacingPx, theme);

        if (model.CursorTime is { } ct)
        {
            using var cursor = new SKPaint { Color = theme.MutedForeground, StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash([2, 3], 0) };
            var y = vp.Y(ct);
            canvas.DrawLine(plot.Left, y, plot.Right, y, cursor);
        }
        canvas.Restore();

    }

    /// <summary>A magnified inset of the gather (used while dragging a pick).</summary>
    public static void RenderInset(SKCanvas canvas, SKRect rect, GatherPlotModel model, PlotTheme theme, double centerX, double centerTime, double halfWidth, double halfTime)
    {
        using var bg = new SKPaint { Color = theme.PlotBackground };
        using var border = new SKPaint { Color = theme.Accent, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        canvas.DrawRect(rect, bg);
        var vp = new Viewport(centerX - halfWidth, centerX + halfWidth, centerTime - halfTime, centerTime + halfTime, rect, YDown: true);
        DrawContent(canvas, vp, model with { CursorTime = centerTime }, theme);
        canvas.DrawRect(rect, border);
        using var font = theme.Font(theme.FontSize - 1);
        using var text = new SKPaint { Color = theme.MutedForeground, IsAntialias = true };
        canvas.DrawText($"{centerTime * 1000:0.00} ms", new SKPoint(rect.Left + 4, rect.Bottom - 4), SKTextAlign.Left, font, text);
    }

    private static void DrawWiggle(SKCanvas canvas, Viewport vp, GatherPlotTrace t, double[] samples, GatherPlotModel model, float x0, float scale, float clipPx,
        PlotTheme theme, bool fill, bool line)
    {
        if (samples.Length < 2) return;
        var reduction = model.Reduction(t.X);
        var dt = t.SampleInterval;
        var tTop = vp.YMin + reduction;
        var tBottom = vp.YMax + reduction;
        var i0 = Math.Max(0, (int)Math.Floor((tTop - t.StartTime) / dt) - 1);
        var i1 = Math.Min(samples.Length - 1, (int)Math.Ceiling((tBottom - t.StartTime) / dt) + 1);
        if (i1 <= i0) return;
        var samplesPerPixel = vp.YPerPixel / dt;
        float Amp(double v) => Math.Clamp((float)(v * scale), -clipPx, clipPx);
        float Py(double sampleIndex) => vp.Y(t.StartTime + sampleIndex * dt - reduction);

        using var wiggle = new SKPath();
        using var positive = new SKPath();
        if (samplesPerPixel <= 1.5)
        {
            wiggle.MoveTo(x0 + Amp(samples[i0]), Py(i0));
            for (var i = i0 + 1; i <= i1; i++) wiggle.LineTo(x0 + Amp(samples[i]), Py(i));
            if (fill)
            {
                positive.MoveTo(x0, Py(i0));
                for (var i = i0; i <= i1; i++) positive.LineTo(x0 + Math.Max(0, Amp(samples[i])), Py(i));
                positive.LineTo(x0, Py(i1));
                positive.Close();
            }
        }
        else
        {
            // Min/max decimation per pixel row keeps every peak visible.
            var bin = (int)Math.Floor(samplesPerPixel);
            var first = true;
            if (fill) positive.MoveTo(x0, Py(i0));
            for (var i = i0; i <= i1; i += bin)
            {
                var end = Math.Min(i1, i + bin - 1);
                double lo = double.MaxValue, hi = double.MinValue;
                for (var k = i; k <= end; k++) { lo = Math.Min(lo, samples[k]); hi = Math.Max(hi, samples[k]); }
                var y = Py(i + (end - i) / 2.0);
                if (first) { wiggle.MoveTo(x0 + Amp(lo), y); first = false; }
                else wiggle.LineTo(x0 + Amp(lo), y);
                wiggle.LineTo(x0 + Amp(hi), y);
                if (fill) positive.LineTo(x0 + Math.Max(0, Amp(hi)), y);
            }
            if (fill)
            {
                positive.LineTo(x0, Py(i1));
                positive.Close();
            }
        }
        var color = t.Excluded ? theme.ExcludedTrace : theme.Trace;
        if (fill && !t.Excluded)
        {
            using var fp = new SKPaint { Color = theme.TraceFill, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawPath(positive, fp);
        }
        if (line || t.Excluded)
        {
            using var lp = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.9f, PathEffect = t.Excluded ? SKPathEffect.CreateDash([3, 3], 0) : null };
            canvas.DrawPath(wiggle, lp);
        }
    }

    private static void DrawImageTrace(SKCanvas canvas, Viewport vp, GatherPlotTrace t, GatherPlotModel model, float widthPx, double scale, PlotTheme theme)
    {
        var reduction = model.Reduction(t.X);
        var x0 = vp.X(t.X);
        var top = Math.Max(vp.Plot.Top, vp.Y(t.StartTime - reduction));
        var bottom = Math.Min(vp.Plot.Bottom, vp.Y(t.StartTime + (t.Samples.Length - 1) * t.SampleInterval - reduction));
        using var paint = new SKPaint { Style = SKPaintStyle.Fill };
        var gain = model.Display.Gain;
        for (var py = top; py < bottom; py += 1)
        {
            var time = vp.DataY(py) + reduction;
            var idx = (int)Math.Round((time - t.StartTime) / t.SampleInterval);
            if (idx < 0 || idx >= t.Samples.Length) continue;
            var v = Math.Clamp(t.Samples[idx] * scale * gain, -1, 1);
            paint.Color = (t.Excluded ? Colormap.Grey : Colormap.Diverging).At(0.5 + 0.5 * v);
            canvas.DrawRect(new SKRect(x0 - widthPx / 2, py, x0 + widthPx / 2, py + 1), paint);
        }
    }

    private static void DrawPick(SKCanvas canvas, Viewport vp, GatherPlotModel model, Dictionary<int, GatherPlotTrace> traces, GatherPlotPick p, float spacingPx, PlotTheme theme)
    {
        if (!traces.TryGetValue(p.Channel, out var t)) return;
        var x = vp.X(t.X);
        var y = vp.Y(p.Time - model.Reduction(t.X));
        var half = Math.Clamp(spacingPx * 0.45f, 4, 14);
        var color = p.Style == PickStyle.Suggestion ? theme.Suggestion : p.State == MarkerState.Normal ? theme.Pick : theme.StateColor(p.State);
        using var paint = new SKPaint
        {
            Color = color,
            StrokeWidth = p.Style == PickStyle.Suggestion ? 1.2f : 2.4f,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            PathEffect = p.Style is PickStyle.Correlation or PickStyle.Suggestion ? SKPathEffect.CreateDash([3, 2], 0) : null,
        };
        canvas.DrawLine(x - half, y, x + half, y, paint);
        if (p.Style == PickStyle.Suggestion) return;
        // Origin glyph: filled = manual/accepted, hollow = automatic.
        var filled = p.Style is PickStyle.Manual or PickStyle.Accepted;
        var state = p.State == MarkerState.Normal ? MarkerState.Normal : p.State;
        Visualization.Axes.Marker(canvas, new SKPoint(x + half + 4, y), 6, state, color, filled, theme);
    }

    private static double MaxAbs(double[] s)
    {
        double m = 0;
        foreach (var v in s) m = Math.Max(m, Math.Abs(v));
        return m;
    }

    /// <summary>The trace nearest to a pixel column (within half a spacing), or null.</summary>
    public static GatherPlotTrace? TraceAt(GatherPlotModel model, Viewport vp, float px)
    {
        var halfSpacingPx = (float)(model.Spacing / vp.XPerPixel) / 2;
        return model.Traces.Where(t => double.IsFinite(t.X)).OrderBy(t => Math.Abs(vp.X(t.X) - px)).FirstOrDefault(t => Math.Abs(vp.X(t.X) - px) <= Math.Max(halfSpacingPx, 3));
    }

    /// <summary>Display time (without reduction) at a pixel row for a trace.</summary>
    public static double TimeAt(GatherPlotModel model, Viewport vp, GatherPlotTrace trace, float py) => vp.DataY(py) + model.Reduction(trace.X);
}
