using Avalonia.Controls;
using Avalonia.Input;
using RefractionStudio.App.Controls;
using RefractionStudio.App.ViewModels;
using RefractionStudio.Visualization;

namespace RefractionStudio.App.Views;

/// <summary>
/// Velocity-model page. On the travel-time plot, dragging a crossover marker moves it (one undo step per
/// drag); clicking a point selects it for layer assignment or exclusion.
/// </summary>
public partial class VelocityModelPageView : UserControl
{
    private CrossoverHandle? _dragging;
    private IDisposable? _gesture;

    public VelocityModelPageView()
    {
        InitializeComponent();
        TravelTimes.PlotPointerPressed += OnPressed;
        TravelTimes.PlotPointerMoved += OnMoved;
        TravelTimes.PlotPointerReleased += (_, _) => EndDrag();
        TravelTimes.PlotPointerExited += (_, _) => TravelTimes.Cursor = Cursor.Default;
    }

    private VelocityModelPageViewModel? Vm => DataContext as VelocityModelPageViewModel;

    private void OnPressed(object? sender, PlotPointerEventArgs e)
    {
        if (Vm is not { } vm || TravelTimes.Plot is not TravelTimePlot plot || e.Kind != PointerUpdateKind.LeftButtonPressed) return;
        if (plot.HandleAt(e.Viewport, e.Pixel.X, vm.SelectedShot?.Id) is { } handle)
        {
            _dragging = handle;
            _gesture = vm.BeginCrossoverDrag();
            e.Handled = true;
            return;
        }
        var point = TravelTimeRenderer.PointAt(plot.Model, e.Viewport, e.Pixel);
        vm.SelectPoint(point);
        e.Handled = point is not null;
    }

    private void OnMoved(object? sender, PlotPointerEventArgs e)
    {
        if (Vm is not { } vm || TravelTimes.Plot is not TravelTimePlot plot) return;
        if (_dragging is { } handle)
        {
            vm.MoveCrossover(handle, e.X);
            e.Handled = true;
            return;
        }
        TravelTimes.Cursor = plot.HandleAt(e.Viewport, e.Pixel.X, vm.SelectedShot?.Id) is not null ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
    }

    private void EndDrag()
    {
        _dragging = null;
        _gesture?.Dispose();
        _gesture = null;
    }
}
