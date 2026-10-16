using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace RefractionStudio.App.Controls;

/// <summary>
/// Renders the subset of Markdown used by the user guide: headings, paragraphs, bullet and numbered
/// lists, block quotes, code blocks, tables (as aligned text) and **bold** / `code` inline spans.
/// </summary>
public sealed partial class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Build());
    }

    public MarkdownView() => Spacing = 6;

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static readonly FontFamily Mono = new("Consolas, Menlo, DejaVu Sans Mono, monospace");

    private void Build()
    {
        Children.Clear();
        var lines = (Markdown ?? "").Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count == 0) return;
            Children.Add(Text(string.Join(" ", paragraph), 13.5, FontWeight.Normal));
            paragraph.Clear();
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal); i++) code.Add(lines[i]);
                Children.Add(Block(string.Join("\n", code)));
                continue;
            }
            if (trimmed.StartsWith('|'))
            {
                Flush();
                var rows = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++)
                    if (!SeparatorRow().IsMatch(lines[i])) rows.Add(lines[i].Trim());
                i--;
                Children.Add(Table(rows));
                continue;
            }
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }
            var heading = HeadingRegex().Match(trimmed);
            if (heading.Success)
            {
                Flush();
                var level = heading.Groups[1].Value.Length;
                var tb = Text(heading.Groups[2].Value, level switch { 1 => 22, 2 => 17, 3 => 14.5, _ => 13.5 }, FontWeight.SemiBold);
                tb.Margin = new Thickness(0, level <= 2 ? 12 : 6, 0, 0);
                Children.Add(tb);
                continue;
            }
            var bullet = BulletRegex().Match(line);
            if (bullet.Success)
            {
                Flush();
                var indent = bullet.Groups[1].Value.Length;
                var marker = bullet.Groups[2].Value;
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(8 + indent * 8, 0, 0, 0) };
                var m = new TextBlock { Text = marker is "-" or "*" ? "•" : marker, Margin = new Thickness(0, 0, 8, 0), FontSize = 13.5 };
                var body = Text(bullet.Groups[3].Value, 13.5, FontWeight.Normal);
                Grid.SetColumn(body, 1);
                grid.Children.Add(m);
                grid.Children.Add(body);
                Children.Add(grid);
                continue;
            }
            if (trimmed is "---" or "***" or "___")
            {
                Flush();
                var rule = new Border { Height = 1, Margin = new Thickness(0, 6) };
                rule.Bind(Border.BackgroundProperty, this.GetResourceObservable("BorderBrush"));
                Children.Add(rule);
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                Flush();
                var quoted = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('>'); i++) quoted.Add(lines[i].Trim().TrimStart('>').Trim());
                i--;
                var quote = new Border
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 4),
                    Child = Text(string.Join(" ", quoted.Where(q => q.Length > 0)), 13.5, FontWeight.Normal),
                };
                quote.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AccentBrush"));
                Children.Add(quote);
                continue;
            }
            paragraph.Add(trimmed);
        }
        Flush();
    }

    private static SelectableTextBlock Text(string markdown, double size, FontWeight weight)
    {
        var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = weight };
        tb.Inlines = [];
        foreach (Match m in InlineRegex().Matches(markdown))
        {
            if (m.Groups["bold"].Success) tb.Inlines.Add(new Run(m.Groups["bold"].Value) { FontWeight = FontWeight.SemiBold });
            else if (m.Groups["code"].Success) tb.Inlines.Add(new Run(m.Groups["code"].Value) { FontFamily = Mono });
            else if (m.Groups["link"].Success) tb.Inlines.Add(new Run(m.Groups["link"].Value));
            else tb.Inlines.Add(new Run(m.Value));
        }
        return tb;
    }

    private Border Block(string text)
    {
        var b = new Border
        {
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(4),
            Child = new SelectableTextBlock { Text = text, FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.NoWrap },
        };
        b.Bind(Border.BackgroundProperty, this.GetResourceObservable("SubtleBackground"));
        return b;
    }

    private Control Table(List<string> rows)
    {
        var cells = rows.Select(r => r.Trim('|').Split('|').Select(c => c.Trim().Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal)).ToArray()).ToList();
        var columns = cells.Max(c => c.Length);
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(c == columns - 1 ? GridLength.Star : GridLength.Auto));
        for (var r = 0; r < cells.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < cells[r].Length; c++)
            {
                var tb = new SelectableTextBlock
                {
                    Text = cells[r][c],
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = r == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                    Margin = new Thickness(0, 2, 16, 2),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                Grid.SetRow(tb, r);
                Grid.SetColumn(tb, c);
                grid.Children.Add(tb);
            }
        }
        return grid;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(\s*)([-*]|\d+\.)\s+(.*)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}")]
    private static partial Regex SeparatorRow();

    [GeneratedRegex(@"\*\*(?<bold>[^*]+)\*\*|`(?<code>[^`]+)`|\[(?<link>[^\]]+)\]\([^)]*\)|[^*`\[]+|[*`\[]")]
    private static partial Regex InlineRegex();
}
