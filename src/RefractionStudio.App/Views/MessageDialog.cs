using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using RefractionStudio.App.Services;

namespace RefractionStudio.App.Views;

/// <summary>A modal message with an icon, a plain-language message, optional technical details and buttons.</summary>
public sealed class MessageDialog : Window
{
    private int _result = -1;

    private MessageDialog(string title, string message, MessageKind kind, string? details, string[] buttons)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var (iconKey, brushKey, label) = kind switch
        {
            MessageKind.Error => ("Icon.Error", "ProblemBrush", "Error"),
            MessageKind.Warning => ("Icon.Warning", "ReviewBrush", "Warning"),
            MessageKind.Question => ("Icon.Help", "AccentBrush", "Question"),
            _ => ("Icon.Info", "AccentBrush", "Information"),
        };
        var icon = new PathIcon { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Top };
        icon.Bind(PathIcon.DataProperty, this.GetResourceObservable(iconKey));
        icon.Bind(ForegroundProperty, this.GetResourceObservable(brushKey));

        var text = new StackPanel { Spacing = 6 };
        text.Children.Add(new TextBlock { Text = label, Classes = { "caption" } });
        text.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 });
        if (!string.IsNullOrWhiteSpace(details))
        {
            text.Children.Add(new Expander
            {
                Header = "Show details",
                Content = new SelectableTextBlock { Text = details, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 11.5 },
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i], MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Classes.Add(i == 0 ? "primary" : "secondary");
            if (i == 0) b.IsDefault = true;
            if (i == buttons.Length - 1 && buttons.Length > 1) b.IsCancel = true;
            b.Click += (_, _) =>
            {
                _result = index;
                Close();
            };
            buttonPanel.Children.Add(b);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(20) };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        text.Margin = new Thickness(14, 0, 0, 0);
        grid.Children.Add(text);
        var root = new StackPanel();
        root.Children.Add(grid);
        buttonPanel.Margin = new Thickness(20, 0, 20, 20);
        root.Children.Add(buttonPanel);
        Content = root;
    }

    public static async Task<int> ShowAsync(Window? owner, string title, string message, MessageKind kind, string? details, string[] buttons)
    {
        var dialog = new MessageDialog(title, message, kind, details, buttons);
        if (owner is null)
        {
            dialog.Show();
            return 0;
        }
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
