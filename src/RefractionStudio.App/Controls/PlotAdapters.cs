using RefractionStudio.Domain;
using RefractionStudio.Visualization;
using SkiaSharp;

namespace RefractionStudio.App.Controls;

/// <summary>Shot gather (time downwards; wheel zooms time).</summary>
public sealed class GatherPlot(GatherPlotModel model) : IPlotAdapter
{
    public GatherPlotModel Model { get; } = model;

    public ZoomAxes DefaultZoom => ZoomAxes.Y;

    public SKRect PlotArea(SKRect bounds, PlotTheme theme) => Axes.PlotArea(bounds, theme, GatherRenderer.Axes(Model));

    public Viewport Fit(SKRect plot) => GatherRenderer.Fit(Model, plot);

    public void Draw(SKCanvas canvas, SKRect bounds, PlotTheme theme, Viewport viewport) => GatherRenderer.Render(canvas, bounds, Model, theme, viewport);
}

/// <summary>A draggable crossover marker on the travel-time plot.</summary>
public sealed record CrossoverHandle(GatherId Gather, int Side, int Index, double X, int ShotIndex);

/// <summary>Travel-time plot with optional crossover handles.</summary>
public sealed class TravelTimePlot(TravelTimePlotModel model, IReadOnlyList<CrossoverHandle> handles, CrossoverHandle? active = null) : IPlotAdapter
{
    public TravelTimePlotModel Model { get; } = model;
    public IReadOnlyList<CrossoverHandle> Handles { get; } = handles;

    public SKRect PlotArea(SKRect bounds, PlotTheme theme) => Axes.PlotArea(bounds, theme, TravelTimeRenderer.Axes(Model));

    public Viewport Fit(SKRect plot) => TravelTimeRenderer.Fit(Model, plot);

    public void Draw(SKCanvas canvas, SKRect bounds, PlotTheme theme, Viewport viewport)
    {
        TravelTimeRenderer.Render(canvas, bounds, Model, theme, viewport);
        if (Handles.Count == 0) return;
        canvas.Save();
        canvas.ClipRect(viewport.Plot);
        foreach (var h in Handles)
        {
            var x = viewport.X(h.X);
            var isActive = active is not null && active.Gather == h.Gather && active.Side == h.Side && active.Index == h.Index;
            using var p = new SKPaint { Color = theme.SeriesColor(h.ShotIndex).WithAlpha(isActive ? (byte)255 : (byte)150), StrokeWidth = isActive ? 3 : 1.5f, IsAntialias = true, PathEffect = SKPathEffect.CreateDash([5, 3], 0) };
            canvas.DrawLine(x, viewport.Plot.Top + 4, x, viewport.Plot.Bottom - 4, p);
            using var fill = new SKPaint { Color = theme.SeriesColor(h.ShotIndex), IsAntialias = true };
            canvas.DrawRect(new SKRect(x - 4, viewport.Plot.Bottom - 14, x + 4, viewport.Plot.Bottom - 4), fill);
        }
        canvas.Restore();
    }

    /// <summary>Handle near a pixel column (within 6 px), preferring the selected shot.</summary>
    public CrossoverHandle? HandleAt(Viewport vp, float px, GatherId? preferred)
        => Handles.Where(h => Math.Abs(vp.X(h.X) - px) <= 6).OrderByDescending(h => h.Gather == preferred).ThenBy(h => Math.Abs(vp.X(h.X) - px)).FirstOrDefault();
}

/// <summary>Velocity / property section (elevation upwards).</summary>
public sealed class SectionPlot(SectionPlotModel model) : IPlotAdapter
{
    public SectionPlotModel Model { get; } = model;

    public SKRect PlotArea(SKRect bounds, PlotTheme theme) => Axes.PlotArea(bounds, theme, SectionRenderer.Axes(Model), Model.Grid is null ? 0 : 76);

    public Viewport Fit(SKRect plot) => SectionRenderer.Fit(Model, plot);

    public void Draw(SKCanvas canvas, SKRect bounds, PlotTheme theme, Viewport viewport) => SectionRenderer.Render(canvas, bounds, Model, theme, viewport);
}

/// <summary>Generic XY chart.</summary>
public sealed class XyPlot(XyPlotModel model) : IPlotAdapter
{
    public XyPlotModel Model { get; } = model;

    public ZoomAxes DefaultZoom => Model.YDown ? ZoomAxes.Y : ZoomAxes.Both;

    public SKRect PlotArea(SKRect bounds, PlotTheme theme)
        => Axes.PlotArea(bounds, theme, new AxisOptions(Model.Title, Model.XLabel, Model.YLabel, XAxisOnTop: Model.XAxisOnTop));

    public Viewport Fit(SKRect plot) => XyRenderer.Fit(Model, plot);

    public void Draw(SKCanvas canvas, SKRect bounds, PlotTheme theme, Viewport viewport) => XyRenderer.Render(canvas, bounds, Model, theme, viewport);
}

/// <summary>Line-geometry diagram (not zoomable beyond fit; always redrawn to fit).</summary>
public sealed class SpreadPlot(SpreadPlotModel model) : IPlotAdapter
{
    public SpreadPlotModel Model { get; } = model;

    public SKRect PlotArea(SKRect bounds, PlotTheme theme) => Axes.PlotArea(bounds, theme, new AxisOptions(Model.Title, "Position (m)", "Elevation (m)"));

    public Viewport Fit(SKRect plot) => Viewport.Fit(0, 1, 0, 1, plot, false);

    public void Draw(SKCanvas canvas, SKRect bounds, PlotTheme theme, Viewport viewport) => SpreadRenderer.Render(canvas, bounds, Model, theme);
}
