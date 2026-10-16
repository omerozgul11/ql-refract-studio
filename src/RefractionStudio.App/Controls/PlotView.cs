using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using RefractionStudio.Visualization;
using SkiaSharp;

namespace RefractionStudio.App.Controls;

/// <summary>Adapter between a renderer model and the interactive plot control.</summary>
public interface IPlotAdapter
{
    /// <summary>Rectangle of the data area inside the control bounds.</summary>
    SKRect PlotArea(SKRect bounds, PlotTheme theme);

    /// <summary>Viewport that shows all data.</summary>
    Viewport Fit(SKRect plot);

    /// <summary>Draws the plot with a given viewport.</summary>
    void Draw(SKCanvas canvas, SKRect bounds, PlotTheme theme, Viewport viewport);

    /// <summary>Which axes the mouse wheel zooms by default.</summary>
    ZoomAxes DefaultZoom => ZoomAxes.Both;
}

[Flags]
public enum ZoomAxes
{
    X = 1,
    Y = 2,
    Both = 3,
}

/// <summary>Pointer event in data coordinates.</summary>
public sealed class PlotPointerEventArgs(double x, double y, SKPoint pixel, Viewport viewport, PointerUpdateKind kind, KeyModifiers modifiers, int clickCount) : EventArgs
{
    public double X { get; } = x;
    public double Y { get; } = y;
    public SKPoint Pixel { get; } = pixel;
    public Viewport Viewport { get; } = viewport;
    public PointerUpdateKind Kind { get; } = kind;
    public KeyModifiers Modifiers { get; } = modifiers;
    public int ClickCount { get; } = clickCount;

    /// <summary>Set by a handler that consumed the press (e.g. to drag a pick) so the plot does not pan.</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// Interactive plot surface: draws a renderer with SkiaSharp, zooms with the wheel (Ctrl: horizontal,
/// Shift: both), pans with a right or middle drag (or a left drag not consumed by a handler), resets on
/// double-click, and offers figure export from its context menu.
/// </summary>
public class PlotView : Control
{
    public static readonly StyledProperty<IPlotAdapter?> PlotProperty = AvaloniaProperty.Register<PlotView, IPlotAdapter?>(nameof(Plot));

    /// <summary>Optional overlay drawn on top (e.g. the pick magnifier) in pixel coordinates.</summary>
    public Action<SKCanvas, Viewport, PlotTheme>? Overlay { get; set; }

    private Viewport? _window;
    private Viewport? _lastViewport;
    private Point? _panStart;
    private Viewport? _panViewport;
    private bool _leftDragConsumed;
    private bool _moved;

    static PlotView()
    {
        AffectsRender<PlotView>(PlotProperty);
        FocusableProperty.OverrideDefaultValue<PlotView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<PlotView>(true);
    }

    public PlotView()
    {
        ContextMenu = BuildContextMenu();
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public IPlotAdapter? Plot
    {
        get => GetValue(PlotProperty);
        set => SetValue(PlotProperty, value);
    }

    /// <summary>The viewport used in the last render (null before the first render).</summary>
    public Viewport? CurrentViewport => _lastViewport;

    /// <summary>True while the user has zoomed or panned (fit is not applied automatically).</summary>
    public bool IsZoomed => _window is not null;

    public event EventHandler<PlotPointerEventArgs>? PlotPointerPressed;
    public event EventHandler<PlotPointerEventArgs>? PlotPointerMoved;
    public event EventHandler<PlotPointerEventArgs>? PlotPointerReleased;
    public event EventHandler? PlotPointerExited;

    public PlotTheme PlotTheme => ActualThemeVariant == ThemeVariant.Dark ? PlotTheme.Dark : PlotTheme.Light;

    public void ResetView()
    {
        _window = null;
        InvalidateVisual();
    }

    /// <summary>Sets the visible data window explicitly (e.g. zoom to a trace).</summary>
    public void SetWindow(double xMin, double xMax, double yMin, double yMax)
    {
        var plot = PlotArea();
        if (Plot is null || plot is null) return;
        var fit = Plot.Fit(plot.Value);
        _window = fit with { XMin = xMin, XMax = xMax, YMin = yMin, YMax = yMax };
        InvalidateVisual();
    }

    private SKRect? PlotArea()
    {
        if (Plot is null || Bounds.Width < 10 || Bounds.Height < 10) return null;
        return Plot.PlotArea(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height), PlotTheme);
    }

    private Viewport? ComputeViewport()
    {
        var plot = PlotArea();
        if (Plot is null || plot is null) return null;
        return (_window ?? Plot.Fit(plot.Value)).WithPlot(plot.Value);
    }

    public override void Render(DrawingContext context)
    {
        var plot = Plot;
        var vp = ComputeViewport();
        _lastViewport = vp;
        if (plot is null || vp is null) return;
        var theme = PlotTheme;
        var bounds = new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height);
        var overlay = Overlay;
        context.Custom(new DrawOperation(new Rect(Bounds.Size), canvas =>
        {
            plot.Draw(canvas, bounds, theme, vp);
            overlay?.Invoke(canvas, vp, theme);
        }));
    }

    private sealed class DrawOperation(Rect bounds, Action<SKCanvas> draw) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public bool HitTest(Point p) => Bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            try
            {
                draw(canvas);
            }
            finally
            {
                canvas.Restore();
            }
        }

        public void Dispose()
        {
        }
    }

    private PlotPointerEventArgs Args(PointerEventArgs e, Viewport vp, int clickCount = 0)
    {
        var p = e.GetPosition(this);
        var px = new SKPoint((float)p.X, (float)p.Y);
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        return new PlotPointerEventArgs(vp.DataX(px.X), vp.DataY(px.Y), px, vp, kind, e.KeyModifiers, clickCount);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var vp = ComputeViewport();
        if (vp is null) return;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed && e.ClickCount == 2)
        {
            var a2 = Args(e, vp, 2);
            PlotPointerPressed?.Invoke(this, a2);
            if (!a2.Handled) ResetView();
            e.Handled = true;
            return;
        }
        _moved = false;
        if (props.IsLeftButtonPressed)
        {
            var args = Args(e, vp, e.ClickCount);
            PlotPointerPressed?.Invoke(this, args);
            _leftDragConsumed = args.Handled;
        }
        else
        {
            _leftDragConsumed = false;
        }
        if (!_leftDragConsumed && (props.IsLeftButtonPressed || props.IsMiddleButtonPressed || props.IsRightButtonPressed))
        {
            _panStart = e.GetPosition(this);
            _panViewport = vp;
        }
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var vp = ComputeViewport();
        if (vp is null) return;
        if (_panStart is { } start && _panViewport is { } pv)
        {
            var p = e.GetPosition(this);
            var dx = (float)(p.X - start.X);
            var dy = (float)(p.Y - start.Y);
            if (Math.Abs(dx) + Math.Abs(dy) > 3) _moved = true;
            if (_moved)
            {
                _window = pv.Pan(dx, dy);
                InvalidateVisual();
            }
            return;
        }
        PlotPointerMoved?.Invoke(this, Args(e, vp));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var vp = ComputeViewport();
        var wasPanning = _panStart is not null;
        _panStart = null;
        _panViewport = null;
        e.Pointer.Capture(null);
        if (vp is not null && (_leftDragConsumed || !wasPanning || !_moved)) PlotPointerReleased?.Invoke(this, Args(e, vp));
        // A right click without dragging opens the context menu (handled by Avalonia); a right drag must not.
        if (e.InitialPressMouseButton == MouseButton.Right && _moved) e.Handled = true;
        _leftDragConsumed = false;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        PlotPointerExited?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var vp = ComputeViewport();
        if (vp is null || Plot is null) return;
        var factor = e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
        var axes = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? ZoomAxes.Both
            : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? (Plot.DefaultZoom == ZoomAxes.Y ? ZoomAxes.X : ZoomAxes.Y)
            : Plot.DefaultZoom;
        var p = e.GetPosition(this);
        _window = vp.Zoom(axes.HasFlag(ZoomAxes.X) ? factor : 1, axes.HasFlag(ZoomAxes.Y) ? factor : 1, new SKPoint((float)p.X, (float)p.Y));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Func<Task> action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += async (_, _) => await action();
            return mi;
        }
        menu.Items.Add(Item("Fit all", () => { ResetView(); return Task.CompletedTask; }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy image", CopyAsync));
        menu.Items.Add(Item("Export figure…", ExportAsync));
        return menu;
    }

    /// <summary>Renders the current view (same zoom) to a figure.</summary>
    public byte[]? RenderFigure(FigureFormat format, FigureOptions options, PlotTheme theme)
    {
        var plot = Plot;
        if (plot is null) return null;
        var current = _window;
        return FigureExport.Render(format, options, theme, (c, b, t) =>
        {
            var area = plot.PlotArea(b, t);
            var vp = (current ?? plot.Fit(area)).WithPlot(area);
            plot.Draw(c, b, t, vp);
        });
    }

    private async Task CopyAsync()
    {
        var png = RenderFigure(FigureFormat.Png, new FigureOptions((int)Math.Max(400, Bounds.Width), (int)Math.Max(300, Bounds.Height), 2), PlotTheme.Print);
        if (png is null) return;
        var dialogs = (Avalonia.Application.Current as App) is not null ? App.Services.GetService(typeof(Services.DialogService)) as Services.DialogService : null;
        if (dialogs is not null) await dialogs.SetClipboardImageAsync(png);
    }

    private async Task ExportAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var options = await Views.ExportFigureDialog.ShowAsync(owner, (int)Bounds.Width, (int)Bounds.Height);
        if (options is null) return;
        var dialogs = App.Services.GetService(typeof(Services.DialogService)) as Services.DialogService;
        if (dialogs is null) return;
        var ext = FigureExport.Extension(options.Value.Format);
        var path = await dialogs.SaveFileAsync("Export figure", "figure" + ext, ext.TrimStart('.'),
            new Services.FileFilter(options.Value.Format.ToString().ToUpperInvariant() + " image", "*" + ext));
        if (path is null) return;
        var bytes = RenderFigure(options.Value.Format, options.Value.Options, options.Value.DarkTheme ? PlotTheme.Dark : PlotTheme.Print);
        if (bytes is not null) await File.WriteAllBytesAsync(path, bytes);
    }
}
