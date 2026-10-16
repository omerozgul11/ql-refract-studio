using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using RefractionStudio.Visualization;

namespace RefractionStudio.App.Views;

/// <summary>Figure export options: format, size, resolution and background.</summary>
public sealed class ExportFigureDialog : Window
{
    private (FigureFormat Format, FigureOptions Options, bool DarkTheme)? _result;

    private ExportFigureDialog(int width, int height)
    {
        Title = "Export figure";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var format = new ComboBox { ItemsSource = new[] { "PNG (raster)", "SVG (vector)", "PDF (vector)" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var w = new NumericUpDown { Value = Math.Max(800, width), Minimum = 200, Maximum = 8000, Increment = 100, FormatString = "0" };
        var h = new NumericUpDown { Value = Math.Max(500, height), Minimum = 200, Maximum = 8000, Increment = 100, FormatString = "0" };
        var scale = new ComboBox { ItemsSource = new[] { "1× (screen)", "2× (high resolution)", "3× (print, 300 dpi)" }, SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var transparent = new CheckBox { Content = "Transparent background (PNG)" };
        var dark = new CheckBox { Content = "Dark theme (default: print / white)" };
        format.SelectionChanged += (_, _) =>
        {
            scale.IsEnabled = format.SelectedIndex == 0;
            transparent.IsEnabled = format.SelectedIndex == 0;
        };

        var ok = new Button { Content = "Export", IsDefault = true, MinWidth = 90 };
        ok.Classes.Add("primary");
        ok.Click += (_, _) =>
        {
            var f = format.SelectedIndex switch { 1 => FigureFormat.Svg, 2 => FigureFormat.Pdf, _ => FigureFormat.Png };
            var s = scale.SelectedIndex switch { 0 => 1f, 2 => 3f, _ => 2f };
            _result = (f, new FigureOptions((int)(w.Value ?? 1200), (int)(h.Value ?? 700), s, transparent.IsChecked == true), dark.IsChecked == true);
            Close();
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        cancel.Click += (_, _) => Close();

        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "Format", Classes = { "label" } });
        panel.Children.Add(format);
        panel.Children.Add(new TextBlock { Text = "Size (points / pixels)", Classes = { "label" } });
        var size = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        size.Children.Add(w);
        size.Children.Add(new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center });
        size.Children.Add(h);
        panel.Children.Add(size);
        panel.Children.Add(new TextBlock { Text = "Resolution", Classes = { "label" } });
        panel.Children.Add(scale);
        panel.Children.Add(transparent);
        panel.Children.Add(dark);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static async Task<(FigureFormat Format, FigureOptions Options, bool DarkTheme)?> ShowAsync(Window owner, int width, int height)
    {
        var d = new ExportFigureDialog(width, height);
        await d.ShowDialog(owner);
        return d._result;
    }
}
