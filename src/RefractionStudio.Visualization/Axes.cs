using SkiaSharp;

namespace RefractionStudio.Visualization;

/// <summary>Axis titles and layout options of a 2-D plot.</summary>
/// <param name="Title">Plot title (optional).</param>
/// <param name="XLabel">X axis label with unit, e.g. "Position (m)".</param>
/// <param name="YLabel">Y axis label with unit.</param>
/// <param name="XTickScale">Multiplier applied to x data values for tick labels (e.g. 1000 for ms).</param>
/// <param name="YTickScale">Multiplier applied to y data values for tick labels.</param>
/// <param name="XAxisOnTop">Draw the x axis along the top (seismic convention for gathers and sections).</param>
public sealed record AxisOptions(string? Title, string XLabel, string YLabel, double XTickScale = 1, double YTickScale = 1, bool XAxisOnTop = false);

/// <summary>Draws frames, grids, ticks and labels.</summary>
public static class Axes
{
    /// <summary>Margins (left, top, right, bottom) reserved for axes and titles.</summary>
    public static SKRect PlotArea(SKRect bounds, PlotTheme theme, AxisOptions axes, float rightExtra = 0)
    {
        var fs = theme.FontSize;
        var left = fs * 5.2f;
        var top = (axes.Title is null ? fs * 0.8f : theme.TitleSize * 2.0f) + (axes.XAxisOnTop ? fs * 3.0f : 0);
        var right = fs * 1.2f + rightExtra;
        var bottom = axes.XAxisOnTop ? fs * 0.8f : fs * 3.2f;
        var r = new SKRect(bounds.Left + left, bounds.Top + top, bounds.Right - right, bounds.Bottom - bottom);
        if (r.Width < 20 || r.Height < 20) r = new SKRect(bounds.Left, bounds.Top, Math.Max(bounds.Left + 20, bounds.Right), Math.Max(bounds.Top + 20, bounds.Bottom));
        return r;
    }

    public static void Draw(SKCanvas canvas, Viewport vp, PlotTheme theme, AxisOptions axes, bool grid = true)
    {
        var plot = vp.Plot;
        using var font = theme.Font();
        using var titleFont = theme.Font(theme.TitleSize, bold: true);
        using var gridPaint = new SKPaint { Color = theme.Grid, StrokeWidth = 1, IsAntialias = false, Style = SKPaintStyle.Stroke };
        using var axisPaint = new SKPaint { Color = theme.Axis, StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var textPaint = new SKPaint { Color = theme.Foreground, IsAntialias = true };
        using var mutedPaint = new SKPaint { Color = theme.MutedForeground, IsAntialias = true };

        var xTicks = Ticks.Values(Math.Min(vp.XMin, vp.XMax) * axes.XTickScale, Math.Max(vp.XMin, vp.XMax) * axes.XTickScale, Math.Max(2, (int)(plot.Width / 90)));
        var yTicks = Ticks.Values(Math.Min(vp.YMin, vp.YMax) * axes.YTickScale, Math.Max(vp.YMin, vp.YMax) * axes.YTickScale, Math.Max(2, (int)(plot.Height / 45)));
        var xStep = xTicks.Count > 1 ? xTicks[1] - xTicks[0] : 1;
        var yStep = yTicks.Count > 1 ? yTicks[1] - yTicks[0] : 1;

        if (grid)
        {
            foreach (var t in xTicks)
            {
                var px = (float)Math.Round(vp.X(t / axes.XTickScale)) + 0.5f;
                canvas.DrawLine(px, plot.Top, px, plot.Bottom, gridPaint);
            }
            foreach (var t in yTicks)
            {
                var py = (float)Math.Round(vp.Y(t / axes.YTickScale)) + 0.5f;
                canvas.DrawLine(plot.Left, py, plot.Right, py, gridPaint);
            }
        }
        canvas.DrawRect(plot, axisPaint);

        var tick = 4f;
        foreach (var t in xTicks)
        {
            var px = vp.X(t / axes.XTickScale);
            var label = Ticks.Format(t, xStep);
            if (axes.XAxisOnTop)
            {
                canvas.DrawLine(px, plot.Top, px, plot.Top - tick, axisPaint);
                canvas.DrawText(label, new SKPoint(px, plot.Top - tick - 3), SKTextAlign.Center, font, mutedPaint);
            }
            else
            {
                canvas.DrawLine(px, plot.Bottom, px, plot.Bottom + tick, axisPaint);
                canvas.DrawText(label, new SKPoint(px, plot.Bottom + tick + font.Size), SKTextAlign.Center, font, mutedPaint);
            }
        }
        foreach (var t in yTicks)
        {
            var py = vp.Y(t / axes.YTickScale);
            canvas.DrawLine(plot.Left - tick, py, plot.Left, py, axisPaint);
            canvas.DrawText(Ticks.Format(t, yStep), new SKPoint(plot.Left - tick - 3, py + font.Size * 0.35f), SKTextAlign.Right, font, mutedPaint);
        }

        // Axis labels.
        var xLabelY = axes.XAxisOnTop ? plot.Top - tick - font.Size * 1.9f : plot.Bottom + font.Size * 2.7f;
        canvas.DrawText(axes.XLabel, new SKPoint(plot.MidX, xLabelY), SKTextAlign.Center, font, textPaint);
        canvas.Save();
        canvas.Translate(plot.Left - font.Size * 4.3f, plot.MidY);
        canvas.RotateDegrees(-90);
        canvas.DrawText(axes.YLabel, new SKPoint(0, 0), SKTextAlign.Center, font, textPaint);
        canvas.Restore();

        if (axes.Title is { } title)
        {
            var titleY = axes.XAxisOnTop ? plot.Top - tick - font.Size * 3.6f : plot.Top - theme.TitleSize * 0.7f;
            canvas.DrawText(title, new SKPoint(plot.Left, titleY), SKTextAlign.Left, titleFont, textPaint);
        }
    }

    /// <summary>Draws a marker whose shape encodes state (circle normal, triangle review, square problem, cross excluded).</summary>
    public static void Marker(SKCanvas canvas, SKPoint p, float size, MarkerState state, SKColor color, bool filled, PlotTheme theme)
    {
        using var fill = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { Color = state == MarkerState.Normal ? color : theme.StateColor(state), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f };
        var r = size / 2;
        switch (state)
        {
            case MarkerState.Excluded:
                canvas.DrawLine(p.X - r, p.Y - r, p.X + r, p.Y + r, stroke);
                canvas.DrawLine(p.X - r, p.Y + r, p.X + r, p.Y - r, stroke);
                return;
            case MarkerState.Review:
            {
                using var path = new SKPath();
                path.MoveTo(p.X, p.Y - r * 1.15f);
                path.LineTo(p.X + r * 1.1f, p.Y + r * 0.85f);
                path.LineTo(p.X - r * 1.1f, p.Y + r * 0.85f);
                path.Close();
                if (filled) canvas.DrawPath(path, fill);
                canvas.DrawPath(path, stroke);
                return;
            }
            case MarkerState.Problem:
            {
                var rect = new SKRect(p.X - r, p.Y - r, p.X + r, p.Y + r);
                if (filled) canvas.DrawRect(rect, fill);
                canvas.DrawRect(rect, stroke);
                return;
            }
            default:
                if (filled) canvas.DrawCircle(p, r, fill);
                canvas.DrawCircle(p, r, stroke);
                if (state == MarkerState.Selected)
                {
                    using var halo = new SKPaint { Color = theme.Accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
                    canvas.DrawCircle(p, r + 3, halo);
                }
                return;
        }
    }

    /// <summary>Draws a filled layer-shape marker (circle, square, triangle, diamond…) for layer index 1..n.</summary>
    public static void LayerMarker(SKCanvas canvas, SKPoint p, float size, int layer, SKColor color, bool filled)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = filled ? SKPaintStyle.Fill : SKPaintStyle.Stroke, StrokeWidth = 1.3f };
        var r = size / 2;
        using var path = new SKPath();
        switch ((layer - 1) % 4)
        {
            case 0:
                canvas.DrawCircle(p, r, paint);
                return;
            case 1:
                canvas.DrawRect(new SKRect(p.X - r * 0.9f, p.Y - r * 0.9f, p.X + r * 0.9f, p.Y + r * 0.9f), paint);
                return;
            case 2:
                path.MoveTo(p.X, p.Y - r * 1.1f);
                path.LineTo(p.X + r * 1.05f, p.Y + r * 0.8f);
                path.LineTo(p.X - r * 1.05f, p.Y + r * 0.8f);
                break;
            default:
                path.MoveTo(p.X, p.Y - r * 1.2f);
                path.LineTo(p.X + r, p.Y);
                path.LineTo(p.X, p.Y + r * 1.2f);
                path.LineTo(p.X - r, p.Y);
                break;
        }
        path.Close();
        canvas.DrawPath(path, paint);
    }

    /// <summary>A small legend box in the top-right of the plot.</summary>
    public static void Legend(SKCanvas canvas, SKRect plot, PlotTheme theme, IReadOnlyList<(string Label, SKColor Color, Action<SKCanvas, SKPoint>? Glyph)> items)
    {
        if (items.Count == 0) return;
        using var font = theme.Font();
        var lineH = font.Size * 1.45f;
        var width = items.Max(i => font.MeasureText(i.Label)) + 32;
        var rect = new SKRect(plot.Right - width - 8, plot.Top + 8, plot.Right - 8, plot.Top + 8 + lineH * items.Count + 8);
        using var bg = new SKPaint { Color = theme.PlotBackground.WithAlpha(230), Style = SKPaintStyle.Fill };
        using var border = new SKPaint { Color = theme.Grid, Style = SKPaintStyle.Stroke, IsAntialias = true };
        using var text = new SKPaint { Color = theme.Foreground, IsAntialias = true };
        canvas.DrawRoundRect(rect, 4, 4, bg);
        canvas.DrawRoundRect(rect, 4, 4, border);
        for (var i = 0; i < items.Count; i++)
        {
            var y = rect.Top + 4 + lineH * (i + 0.5f);
            var glyphCenter = new SKPoint(rect.Left + 14, y);
            if (items[i].Glyph is { } g) g(canvas, glyphCenter);
            else
            {
                using var p = new SKPaint { Color = items[i].Color, StrokeWidth = 2, IsAntialias = true };
                canvas.DrawLine(glyphCenter.X - 8, y, glyphCenter.X + 8, y, p);
            }
            canvas.DrawText(items[i].Label, new SKPoint(rect.Left + 28, y + font.Size * 0.35f), SKTextAlign.Left, font, text);
        }
    }

    /// <summary>Text drawn on a translucent background (for labels on top of data).</summary>
    public static void Label(SKCanvas canvas, string text, SKPoint at, PlotTheme theme, SKTextAlign align = SKTextAlign.Left, float? size = null)
    {
        using var font = theme.Font(size);
        var w = font.MeasureText(text);
        var left = align switch { SKTextAlign.Center => at.X - w / 2, SKTextAlign.Right => at.X - w, _ => at.X };
        var rect = new SKRect(left - 3, at.Y - font.Size, left + w + 3, at.Y + font.Size * 0.3f);
        using var bg = new SKPaint { Color = theme.PlotBackground.WithAlpha(210) };
        using var fg = new SKPaint { Color = theme.Foreground, IsAntialias = true };
        canvas.DrawRect(rect, bg);
        canvas.DrawText(text, new SKPoint(left, at.Y), SKTextAlign.Left, font, fg);
    }
}
