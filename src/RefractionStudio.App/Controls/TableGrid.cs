using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RefractionStudio.App.ViewModels;

namespace RefractionStudio.App.Controls;

/// <summary>Displays a read-only <see cref="ReportTableView"/> (report table) as a simple grid.</summary>
public sealed class TableGrid : ContentControl
{
    public static readonly StyledProperty<ReportTableView?> TableProperty = AvaloniaProperty.Register<TableGrid, ReportTableView?>(nameof(Table));

    static TableGrid()
    {
        TableProperty.Changed.AddClassHandler<TableGrid>((g, _) => g.Build());
    }

    public ReportTableView? Table
    {
        get => GetValue(TableProperty);
        set => SetValue(TableProperty, value);
    }

    private void Build()
    {
        if (Table is not { } t || t.Columns.Count == 0)
        {
            Content = null;
            return;
        }
        var grid = new Grid();
        foreach (var _ in t.Columns) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r <= t.Rows.Count; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < t.Columns.Count; c++)
        {
            var header = new TextBlock { Text = t.Columns[c], FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 18, 4), TextWrapping = TextWrapping.NoWrap };
            Grid.SetColumn(header, c);
            grid.Children.Add(header);
        }
        for (var r = 0; r < t.Rows.Count; r++)
        {
            var row = t.Rows[r];
            for (var c = 0; c < Math.Min(row.Count, t.Columns.Count); c++)
            {
                var cell = new SelectableTextBlock
                {
                    Text = row[c],
                    Margin = new Thickness(0, 1, 18, 1),
                    HorizontalAlignment = c == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                    FontFeatures = FontFeatureCollection.Parse("tnum"),
                };
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        Content = grid;
    }
}
