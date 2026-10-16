using SkiaSharp;

namespace RefractionStudio.Reporting;

/// <summary>Paper sizes in points.</summary>
public enum PaperSize
{
    A4,
    Letter,
}

/// <summary>Visual style of report text and boxes.</summary>
public sealed record ReportStyle(SKTypeface Typeface, SKColor Text, SKColor Muted, SKColor Accent, SKColor Rule, SKColor TableHeader, SKColor WarningFill, SKColor WarningBorder, SKColor InfoFill)
{
    public static ReportStyle Default { get; } = new(
        Visualization.PlotTheme.Typeface, new SKColor(0x1F, 0x23, 0x28), new SKColor(0x5F, 0x67, 0x70), new SKColor(0x0B, 0x5C, 0x9E), new SKColor(0xD5, 0xDA, 0xE0),
        new SKColor(0xEE, 0xF2, 0xF6), new SKColor(0xFF, 0xF6, 0xE0), new SKColor(0xD9, 0x96, 0x00), new SKColor(0xEC, 0xF4, 0xFB));
}

/// <summary>
/// A simple flowing page layout on top of the Skia PDF back end: text with word wrapping, headings,
/// paginated tables, figures and callouts. Content is laid out first and drawn afterwards so that
/// headers and footers can show "Page n of N".
/// </summary>
public sealed class PdfLayout
{
    private readonly List<List<Action<SKCanvas>>> _pages = [];
    private readonly List<(string Title, int Level, int Page)> _outline = [];
    private float _y;

    public PdfLayout(PaperSize paper, ReportStyle style)
    {
        (PageWidth, PageHeight) = paper == PaperSize.A4 ? (595.28f, 841.89f) : (612f, 792f);
        Style = style;
        NewPage();
    }

    public float PageWidth { get; }
    public float PageHeight { get; }
    public ReportStyle Style { get; }
    public float Margin { get; } = 54;
    public float HeaderHeight { get; } = 26;
    public float FooterHeight { get; } = 26;

    public float ContentLeft => Margin;
    public float ContentRight => PageWidth - Margin;
    public float ContentWidth => ContentRight - ContentLeft;
    public float ContentTop => Margin + HeaderHeight;
    public float ContentBottom => PageHeight - Margin - FooterHeight;
    public int PageCount => _pages.Count;
    public IReadOnlyList<(string Title, int Level, int Page)> Outline => _outline;

    /// <summary>Header/footer painter: (canvas, page index 1-based, page count).</summary>
    public Action<SKCanvas, int, int>? Decorate { get; set; }

    /// <summary>Pages that are not decorated (e.g. the cover).</summary>
    public HashSet<int> Undecorated { get; } = [];

    private List<Action<SKCanvas>> Current => _pages[^1];

    public void NewPage()
    {
        _pages.Add([]);
        _y = ContentTop;
    }

    /// <summary>Ensures <paramref name="height"/> points are available, starting a new page if needed.</summary>
    public void Need(float height)
    {
        if (_y + height > ContentBottom && _y > ContentTop + 1) NewPage();
    }

    public void Space(float points) => _y = Math.Min(ContentBottom, _y + points);

    public SKFont Font(float size, bool bold = false, bool italic = false)
    {
        var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var tf = SKTypeface.FromFamilyName(Style.Typeface.FamilyName, style) ?? Style.Typeface;
        return new SKFont(tf, size) { Subpixel = true };
    }

    /// <summary>Splits text into lines that fit <paramref name="width"/>.</summary>
    public static List<string> Wrap(string text, SKFont font, float width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var words = paragraph.Split(' ');
            var current = "";
            foreach (var w in words)
            {
                var candidate = current.Length == 0 ? w : current + " " + w;
                if (font.MeasureText(candidate) <= width || current.Length == 0)
                {
                    current = candidate;
                    // Break single words longer than the width.
                    while (font.MeasureText(current) > width && current.Length > 1)
                    {
                        var cut = current.Length - 1;
                        while (cut > 1 && font.MeasureText(current[..cut]) > width) cut--;
                        lines.Add(current[..cut]);
                        current = current[cut..];
                    }
                }
                else
                {
                    lines.Add(current);
                    current = w;
                }
            }
            lines.Add(current);
        }
        return lines;
    }

    public void Heading(string text, int level)
    {
        var size = level switch { 1 => 17f, 2 => 12.5f, _ => 10.5f };
        var before = level switch { 1 => 6f, 2 => 12f, _ => 8f };
        Need(before + size * 2.8f);
        if (level == 1 && _y > ContentTop + 1) { NewPage(); }
        _y += before;
        var font = Font(size, bold: true);
        var y = _y + size;
        var color = level == 1 ? Style.Accent : Style.Text;
        var page = _pages.Count;
        Current.Add(c =>
        {
            using var p = new SKPaint { Color = color, IsAntialias = true };
            c.DrawText(text, new SKPoint(ContentLeft, y), SKTextAlign.Left, font, p);
            if (level == 1)
            {
                using var rule = new SKPaint { Color = Style.Rule, StrokeWidth = 1 };
                c.DrawLine(ContentLeft, y + 6, ContentRight, y + 6, rule);
            }
        });
        _outline.Add((text, level, page));
        _y = y + (level == 1 ? 14 : 8);
    }

    public void Paragraph(string text, float size = 9.5f, bool bold = false, bool italic = false, SKColor? color = null, float indent = 0)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var font = Font(size, bold, italic);
        var lineHeight = size * 1.38f;
        foreach (var line in Wrap(text, font, ContentWidth - indent))
        {
            Need(lineHeight);
            var y = _y + size;
            var c0 = color ?? Style.Text;
            Current.Add(c =>
            {
                using var p = new SKPaint { Color = c0, IsAntialias = true };
                c.DrawText(line, new SKPoint(ContentLeft + indent, y), SKTextAlign.Left, font, p);
            });
            _y += lineHeight;
        }
        _y += size * 0.5f;
    }

    public void Bullets(IEnumerable<string> items, float size = 9.5f)
    {
        var font = Font(size);
        var lineHeight = size * 1.38f;
        foreach (var item in items)
        {
            var lines = Wrap(item, font, ContentWidth - 14);
            for (var i = 0; i < lines.Count; i++)
            {
                Need(lineHeight);
                var y = _y + size;
                var text = lines[i];
                var first = i == 0;
                Current.Add(c =>
                {
                    using var p = new SKPaint { Color = Style.Text, IsAntialias = true };
                    if (first) c.DrawCircle(ContentLeft + 4, y - size * 0.32f, 1.8f, p);
                    c.DrawText(text, new SKPoint(ContentLeft + 14, y), SKTextAlign.Left, font, p);
                });
                _y += lineHeight;
            }
        }
        _y += size * 0.4f;
    }

    /// <summary>Two-column key/value list.</summary>
    public void KeyValues(IEnumerable<(string Key, string? Value)> rows, float keyWidth = 150)
    {
        var keyFont = Font(9.5f, bold: true);
        var font = Font(9.5f);
        const float lh = 9.5f * 1.38f;
        foreach (var (key, value) in rows)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var lines = Wrap(value!, font, ContentWidth - keyWidth);
            Need(lh * Math.Min(lines.Count, 3));
            var y0 = _y;
            var k = key;
            Current.Add(c =>
            {
                using var p = new SKPaint { Color = Style.Muted, IsAntialias = true };
                c.DrawText(k, new SKPoint(ContentLeft, y0 + 9.5f), SKTextAlign.Left, keyFont, p);
            });
            foreach (var line in lines)
            {
                Need(lh);
                var y = _y + 9.5f;
                var l = line;
                Current.Add(c =>
                {
                    using var p = new SKPaint { Color = Style.Text, IsAntialias = true };
                    c.DrawText(l, new SKPoint(ContentLeft + keyWidth, y), SKTextAlign.Left, font, p);
                });
                _y += lh;
            }
            _y += 2;
        }
        _y += 6;
    }

    /// <summary>A coloured box with a title and text (warnings, notes).</summary>
    public void Callout(string title, string text, bool warning)
    {
        var font = Font(9.5f);
        var titleFont = Font(9.5f, bold: true);
        var lines = Wrap(text, font, ContentWidth - 24);
        var height = 12 + 9.5f * 1.4f * (lines.Count + 1) + 8;
        Need(Math.Min(height, ContentBottom - ContentTop));
        var top = _y;
        var fill = warning ? Style.WarningFill : Style.InfoFill;
        var border = warning ? Style.WarningBorder : Style.Accent;
        Current.Add(c =>
        {
            var rect = new SKRect(ContentLeft, top, ContentRight, top + height);
            using var f = new SKPaint { Color = fill };
            using var b = new SKPaint { Color = border, StrokeWidth = 3 };
            c.DrawRect(rect, f);
            c.DrawLine(rect.Left + 1.5f, rect.Top, rect.Left + 1.5f, rect.Bottom, b);
            using var tp = new SKPaint { Color = Style.Text, IsAntialias = true };
            c.DrawText(title, new SKPoint(rect.Left + 14, rect.Top + 16), SKTextAlign.Left, titleFont, tp);
            var y = rect.Top + 16 + 9.5f * 1.4f;
            foreach (var l in lines)
            {
                c.DrawText(l, new SKPoint(rect.Left + 14, y), SKTextAlign.Left, font, tp);
                y += 9.5f * 1.4f;
            }
        });
        _y += height + 10;
    }

    /// <summary>A figure drawn by a plot renderer into a box of the given aspect ratio, with a caption.</summary>
    public void Figure(Action<SKCanvas, SKRect> draw, float aspect, string caption, float widthFraction = 1)
    {
        var width = ContentWidth * widthFraction;
        var height = Math.Min(width / aspect, ContentBottom - ContentTop - 40);
        Need(height + 30);
        var top = _y;
        var left = ContentLeft + (ContentWidth - width) / 2;
        Current.Add(c =>
        {
            c.Save();
            c.ClipRect(new SKRect(left, top, left + width, top + height));
            c.Translate(left, top);
            draw(c, new SKRect(0, 0, width, height));
            c.Restore();
        });
        _y += height + 4;
        Paragraph(caption, 8.5f, italic: true, color: Style.Muted);
    }

    /// <summary>A table that breaks across pages, repeating its header.</summary>
    public void Table(DataTable table, int? maxRows = null, float fontSize = 7.5f, IReadOnlyList<float>? relativeWidths = null)
    {
        var font = Font(fontSize);
        var headFont = Font(fontSize, bold: true);
        var cols = table.Columns.Count;
        var rows = maxRows is { } m ? table.Rows.Take(m).ToList() : [.. table.Rows];
        var widths = new float[cols];
        if (relativeWidths is { Count: > 0 } rw && rw.Count == cols)
        {
            var sum = rw.Sum();
            for (var c = 0; c < cols; c++) widths[c] = ContentWidth * rw[c] / sum;
        }
        else
        {
            // Natural width: unwrapped header or longest cell. Minimum: longest header word, and for numeric
            // columns the widest number (numbers are never wrapped). Only text columns give up width.
            var natural = new float[cols];
            var minimum = new float[cols];
            for (var c = 0; c < cols; c++)
            {
                var header = headFont.MeasureText(table.Columns[c]);
                var words = table.Columns[c].Split(' ').Select(w => headFont.MeasureText(w)).DefaultIfEmpty(0).Max();
                var cells = rows.Where(r => c < r.Count).Select(r => r[c]).ToList();
                var content = cells.Select(cell => font.MeasureText(cell.Display)).DefaultIfEmpty(0).Max();
                var numeric = cells.Count > 0 && cells.All(cell => cell.Number is not null || cell.Display is "—" or "" or "n/a");
                natural[c] = Math.Max(Math.Min(header, 120), Math.Min(content, 260)) + 8;
                minimum[c] = Math.Max(words, numeric ? content : Math.Min(content, 40)) + 8;
                natural[c] = Math.Max(natural[c], minimum[c]);
            }
            var sumNatural = natural.Sum();
            var sumMin = minimum.Sum();
            if (sumNatural <= ContentWidth)
            {
                for (var c = 0; c < cols; c++) widths[c] = natural[c] * ContentWidth / sumNatural;
            }
            else if (sumMin <= ContentWidth)
            {
                var spare = ContentWidth - sumMin;
                var flexible = sumNatural - sumMin;
                for (var c = 0; c < cols; c++) widths[c] = minimum[c] + (flexible > 0 ? spare * (natural[c] - minimum[c]) / flexible : 0);
            }
            else
            {
                for (var c = 0; c < cols; c++) widths[c] = minimum[c] * ContentWidth / sumMin;
            }
        }
        var lh = fontSize * 1.3f;

        List<string>[] HeaderLines() => [.. table.Columns.Select((h, c) => Wrap(h, headFont, widths[c] - 6))];
        void DrawHeader()
        {
            var lines = HeaderLines();
            var h = lines.Max(l => l.Count) * lh + 6;
            Need(h + lh * 2);
            var top = _y;
            Current.Add(c =>
            {
                using var bg = new SKPaint { Color = Style.TableHeader };
                c.DrawRect(new SKRect(ContentLeft, top, ContentRight, top + h), bg);
                using var p = new SKPaint { Color = Style.Text, IsAntialias = true };
                var x = ContentLeft;
                for (var col = 0; col < cols; col++)
                {
                    var y = top + 3 + fontSize;
                    foreach (var line in lines[col])
                    {
                        c.DrawText(line, new SKPoint(x + 3, y), SKTextAlign.Left, headFont, p);
                        y += lh;
                    }
                    x += widths[col];
                }
            });
            _y += h;
        }

        Need(lh * 4);
        if (!string.IsNullOrEmpty(table.Title)) Paragraph(table.Title, 9.5f, bold: true);
        DrawHeader();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var cellLines = new List<string>[cols];
            for (var c = 0; c < cols; c++) cellLines[c] = c < row.Count ? Wrap(row[c].Display, font, widths[c] - 6) : [""];
            var h = cellLines.Max(l => l.Count) * lh + 3;
            if (_y + h > ContentBottom)
            {
                NewPage();
                DrawHeader();
            }
            var top = _y;
            var shade = r % 2 == 1;
            var numeric = row.Select(cell => cell.Number is not null).ToArray();
            Current.Add(c =>
            {
                if (shade)
                {
                    using var bg = new SKPaint { Color = new SKColor(0xF7, 0xF8, 0xFA) };
                    c.DrawRect(new SKRect(ContentLeft, top, ContentRight, top + h), bg);
                }
                using var p = new SKPaint { Color = Style.Text, IsAntialias = true };
                var x = ContentLeft;
                for (var col = 0; col < cols; col++)
                {
                    var y = top + 2 + fontSize;
                    foreach (var line in cellLines[col])
                    {
                        if (col < numeric.Length && numeric[col]) c.DrawText(line, new SKPoint(x + widths[col] - 3, y), SKTextAlign.Right, font, p);
                        else c.DrawText(line, new SKPoint(x + 3, y), SKTextAlign.Left, font, p);
                        y += lh;
                    }
                    x += widths[col];
                }
            });
            _y += h;
        }
        var endY = _y;
        Current.Add(c =>
        {
            using var rule = new SKPaint { Color = Style.Rule };
            c.DrawLine(ContentLeft, endY, ContentRight, endY, rule);
        });
        _y += 4;
        if (maxRows is { } max && table.Rows.Count > max)
            Paragraph($"{table.Rows.Count - max} further row(s) are in the accompanying spreadsheet.", 8, italic: true, color: Style.Muted);
        if (table.Note is { } note) Paragraph(note, 8, italic: true, color: Style.Muted);
        _y += 6;
    }

    /// <summary>Free drawing on the current position (fixed height).</summary>
    public void Custom(float height, Action<SKCanvas, SKRect> draw)
    {
        Need(height);
        var rect = new SKRect(ContentLeft, _y, ContentRight, _y + height);
        Current.Add(c => draw(c, rect));
        _y += height;
    }

    /// <summary>Draws on the current page without moving the cursor.</summary>
    public void Overlay(Action<SKCanvas> draw) => Current.Add(draw);

    /// <summary>Writes the PDF.</summary>
    public byte[] Render(string title, string author, string subject)
    {
        using var ms = new MemoryStream();
        using (var ws = new SKManagedWStream(ms))
        {
            var meta = new SKDocumentPdfMetadata
            {
                Title = title,
                Author = author,
                Subject = subject,
                Creator = "Refraction Studio",
                Producer = "Refraction Studio (SkiaSharp PDF)",
                Creation = DateTime.Now,
                Modified = DateTime.Now,
                RasterDpi = 300,
            };
            using var doc = SKDocument.CreatePdf(ws, meta);
            for (var i = 0; i < _pages.Count; i++)
            {
                var canvas = doc.BeginPage(PageWidth, PageHeight);
                foreach (var op in _pages[i]) op(canvas);
                if (!Undecorated.Contains(i + 1)) Decorate?.Invoke(canvas, i + 1, _pages.Count);
                doc.EndPage();
            }
            doc.Close();
        }
        return ms.ToArray();
    }
}
