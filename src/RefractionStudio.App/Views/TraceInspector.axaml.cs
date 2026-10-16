using Avalonia;
using Avalonia.Controls;

namespace RefractionStudio.App.Views;

/// <summary>Properties of the selected trace and, on picking pages, its pick.</summary>
public partial class TraceInspector : UserControl
{
    public static readonly StyledProperty<bool> ShowPickToolsProperty = AvaloniaProperty.Register<TraceInspector, bool>(nameof(ShowPickTools));

    public TraceInspector() => InitializeComponent();

    public bool ShowPickTools
    {
        get => GetValue(ShowPickToolsProperty);
        set => SetValue(ShowPickToolsProperty, value);
    }
}
