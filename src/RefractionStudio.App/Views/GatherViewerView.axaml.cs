using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using RefractionStudio.App.Controls;
using RefractionStudio.App.ViewModels;
using RefractionStudio.Visualization;
using SkiaSharp;

namespace RefractionStudio.App.Views;

/// <summary>
/// Gather viewer. With <see cref="AllowPicking"/>: click places a pick (Shift or the snap option snaps to
/// the nearby onset), dragging near a pick moves it with a magnifier, ←/→ nudge one sample (Shift: ten),
/// ↑/↓ select traces, Page Up/Down change shot, Enter accepts, Delete removes.
/// </summary>
public partial class GatherViewerView : UserControl
{
    public static readonly StyledProperty<bool> AllowPickingProperty = AvaloniaProperty.Register<GatherViewerView, bool>(nameof(AllowPicking));
    public static readonly StyledProperty<bool> SnapToOnsetProperty = AvaloniaProperty.Register<GatherViewerView, bool>(nameof(SnapToOnset));

    private IDisposable? _gesture;
    private int? _dragChannel;
    private (double X, double T)? _magnifier;

    public GatherViewerView()
    {
        InitializeComponent();
        Plot.PlotPointerPressed += OnPressed;
        Plot.PlotPointerMoved += OnMoved;
        Plot.PlotPointerReleased += OnReleased;
        Plot.PlotPointerExited += (_, _) => (DataContext as GatherViewerViewModel)?.Hover(null, 0);
        Plot.Overlay = DrawMagnifier;
        Plot.KeyDown += OnPlotKeyDown;
    }

    public bool AllowPicking
    {
        get => GetValue(AllowPickingProperty);
        set => SetValue(AllowPickingProperty, value);
    }

    public bool SnapToOnset
    {
        get => GetValue(SnapToOnsetProperty);
        set => SetValue(SnapToOnsetProperty, value);
    }

    private GatherViewerViewModel? Vm => DataContext as GatherViewerViewModel;

    private GatherPlotModel? Model => (Plot.Plot as GatherPlot)?.Model;

    private void OnPressed(object? sender, PlotPointerEventArgs e)
    {
        if (Vm is not { } vm || Model is not { } model) return;
        var trace = GatherRenderer.TraceAt(model, e.Viewport, e.Pixel.X);
        if (trace is null) return;
        var time = GatherRenderer.TimeAt(model, e.Viewport, trace, e.Pixel.Y);
        if (!AllowPicking || e.ClickCount == 2)
        {
            vm.SelectChannel(trace.Channel);
            e.Handled = e.ClickCount == 2;
            return;
        }
        e.Handled = true;
        var existing = model.Picks.FirstOrDefault(p => p.Channel == trace.Channel);
        var nearExisting = existing is not null && Math.Abs(e.Viewport.Y(existing.Time - model.Reduction(trace.X)) - e.Pixel.Y) <= 8;
        if (nearExisting)
        {
            vm.SelectChannel(trace.Channel);
            _gesture = vm.BeginDrag(trace.Channel);
            _dragChannel = trace.Channel;
            _magnifier = (trace.X, time);
            return;
        }
        vm.PlacePick(trace.Channel, time, SnapToOnset || e.Modifiers.HasFlag(KeyModifiers.Shift));
    }

    private void OnMoved(object? sender, PlotPointerEventArgs e)
    {
        if (Vm is not { } vm || Model is not { } model) return;
        if (_dragChannel is { } ch)
        {
            var trace = model.Traces.FirstOrDefault(t => t.Channel == ch);
            if (trace is null) return;
            var time = GatherRenderer.TimeAt(model, e.Viewport, trace, e.Pixel.Y);
            vm.DragPick(ch, time);
            _magnifier = (trace.X, time);
            Plot.InvalidateVisual();
            return;
        }
        var hover = GatherRenderer.TraceAt(model, e.Viewport, e.Pixel.X);
        vm.Hover(hover, hover is null ? 0 : GatherRenderer.TimeAt(model, e.Viewport, hover, e.Pixel.Y));
    }

    private void OnReleased(object? sender, PlotPointerEventArgs e)
    {
        _gesture?.Dispose();
        _gesture = null;
        _dragChannel = null;
        _magnifier = null;
        Plot.InvalidateVisual();
    }

    private void DrawMagnifier(SKCanvas canvas, Viewport vp, PlotTheme theme)
    {
        if (_magnifier is not { } m || Model is not { } model) return;
        var w = Math.Min(260f, vp.Plot.Width * 0.35f);
        var h = Math.Min(200f, vp.Plot.Height * 0.4f);
        var rect = new SKRect(vp.Plot.Right - w - 10, vp.Plot.Top + 10, vp.Plot.Right - 10, vp.Plot.Top + 10 + h);
        GatherRenderer.RenderInset(canvas, rect, model, theme, m.X, m.T - model.Reduction(m.X), model.Spacing * 2.5, Math.Abs(vp.Height) * 0.05);
    }

    private void OnPlotKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        var big = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left when AllowPicking:
                vm.NudgePick(-big);
                break;
            case Key.Right when AllowPicking:
                vm.NudgePick(big);
                break;
            case Key.Up:
                vm.SelectNextTrace(-1);
                break;
            case Key.Down:
                vm.SelectNextTrace(1);
                break;
            case Key.PageUp:
                vm.PreviousGather();
                break;
            case Key.PageDown:
                vm.NextGather();
                break;
            case Key.Enter when AllowPicking:
                vm.AcceptSelected();
                break;
            case Key.Delete when AllowPicking:
            case Key.Back when AllowPicking:
                vm.DeleteSelectedPick();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
