using Avalonia.Controls;
using RefractionStudio.App.Controls;
using RefractionStudio.App.ViewModels;
using RefractionStudio.Visualization;

namespace RefractionStudio.App.Views;

public partial class PicksPageView : UserControl
{
    public PicksPageView()
    {
        InitializeComponent();
        TravelTimes.PlotPointerPressed += (_, e) =>
        {
            if (DataContext is not PicksPageViewModel vm || TravelTimes.Plot is not TravelTimePlot plot) return;
            if (TravelTimeRenderer.PointAt(plot.Model, e.Viewport, e.Pixel) is { } point)
            {
                vm.SelectPoint(point.Id);
                e.Handled = true;
            }
        };
    }
}
