using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace RefractionStudio.Reporting;

/// <summary>A table cell: text or a number with a display format.</summary>
public readonly record struct Cell(string? Text, double? Number, string Format = "0.###")
{
    public static implicit operator Cell(string? text) => new(text, null);

    public static Cell Num(double? value, string format = "0.###") => value is { } v && double.IsFinite(v) ? new Cell(null, v, format) : new Cell("—", null);

    public string Display => Number is { } n ? n.ToString(Format, CultureInfo.InvariantCulture) : Text ?? "";
}

/// <summary>A titled table with column headers (including units).</summary>
public sealed record DataTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<Cell>> Rows, string? Note = null);

/// <summary>Writes tables as CSV (RFC 4180, invariant culture, full precision for numbers).</summary>
public static class CsvWriter
{
    public static string Write(DataTable table)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", table.Columns.Select(Escape)));
        foreach (var row in table.Rows)
            sb.AppendLine(string.Join(",", row.Select(c => c.Number is { } n ? n.ToString("R", CultureInfo.InvariantCulture) : Escape(c.Text ?? ""))));
        return sb.ToString();
    }

    private static string Escape(string s)
        => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

/// <summary>
/// Minimal Office Open XML (SpreadsheetML) writer: one worksheet per table, bold header row, frozen
/// header, column widths and numbers stored at full precision (display format applied per cell).
/// </summary>
public static class XlsxWriter
{
    public static byte[] Write(IReadOnlyList<DataTable> tables, string? author = null)
    {
        if (tables.Count == 0) throw new ArgumentException("At least one table is required.", nameof(tables));
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var formats = tables.SelectMany(t => t.Rows.SelectMany(r => r.Where(c => c.Number is not null).Select(c => c.Format))).Distinct().ToList();
            var names = UniqueSheetNames(tables.Select(t => t.Title).ToList());
            Add(zip, "[Content_Types].xml", ContentTypes(tables.Count));
            Add(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>""");
            Add(zip, "docProps/core.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:creator>{X(author ?? "Refraction Studio")}</dc:creator><dcterms:created xsi:type="dcterms:W3CDTF">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created></cp:coreProperties>""");
            var wb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""");
            var rels = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
            for (var i = 0; i < tables.Count; i++)
            {
                wb.Append(CultureInfo.InvariantCulture, $"""<sheet name="{X(names[i])}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
                rels.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>""");
                Add(zip, $"xl/worksheets/sheet{i + 1}.xml", Sheet(tables[i], formats));
            }
            wb.Append("</sheets></workbook>");
            rels.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{tables.Count + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Add(zip, "xl/workbook.xml", wb.ToString());
            Add(zip, "xl/_rels/workbook.xml.rels", rels.ToString());
            Add(zip, "xl/styles.xml", Styles(formats));
        }
        return ms.ToArray();
    }

    private static string Sheet(DataTable t, List<string> formats)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        sb.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        sb.Append("<cols>");
        for (var c = 0; c < t.Columns.Count; c++)
        {
            var width = Math.Clamp(Math.Max(t.Columns[c].Length, t.Rows.Select(r => c < r.Count ? r[c].Display.Length : 0).DefaultIfEmpty(0).Max()) + 2, 8, 60);
            sb.Append(CultureInfo.InvariantCulture, $"""<col min="{c + 1}" max="{c + 1}" width="{width}" customWidth="1"/>""");
        }
        sb.Append("</cols><sheetData>");
        sb.Append("""<row r="1">""");
        for (var c = 0; c < t.Columns.Count; c++)
            sb.Append(CultureInfo.InvariantCulture, $"""<c r="{Ref(c, 1)}" t="inlineStr" s="1"><is><t>{X(t.Columns[c])}</t></is></c>""");
        sb.Append("</row>");
        for (var r = 0; r < t.Rows.Count; r++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"""<row r="{r + 2}">""");
            var row = t.Rows[r];
            for (var c = 0; c < row.Count; c++)
            {
                var cell = row[c];
                if (cell.Number is { } n)
                    sb.Append(CultureInfo.InvariantCulture, $"""<c r="{Ref(c, r + 2)}" s="{2 + formats.IndexOf(cell.Format)}"><v>{n.ToString("R", CultureInfo.InvariantCulture)}</v></c>""");
                else if (!string.IsNullOrEmpty(cell.Text))
                    sb.Append(CultureInfo.InvariantCulture, $"""<c r="{Ref(c, r + 2)}" t="inlineStr"><is><t xml:space="preserve">{X(cell.Text)}</t></is></c>""");
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string Styles(List<string> formats)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        sb.Append(CultureInfo.InvariantCulture, $"<numFmts count=\"{formats.Count}\">");
        for (var i = 0; i < formats.Count; i++) sb.Append(CultureInfo.InvariantCulture, $"""<numFmt numFmtId="{164 + i}" formatCode="{X(formats[i])}"/>""");
        sb.Append("</numFmts>");
        sb.Append("""<fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>""");
        sb.Append("""<fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFE8EEF4"/></patternFill></fill></fills>""");
        sb.Append("""<borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs>""");
        sb.Append(CultureInfo.InvariantCulture, $"<cellXfs count=\"{2 + formats.Count}\"><xf/><xf fontId=\"1\" fillId=\"2\" applyFont=\"1\" applyFill=\"1\"/>");
        for (var i = 0; i < formats.Count; i++) sb.Append(CultureInfo.InvariantCulture, $"""<xf numFmtId="{164 + i}" applyNumberFormat="1"/>""");
        sb.Append("</cellXfs></styleSheet>");
        return sb.ToString();
    }

    private static string ContentTypes(int sheets)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>""");
        for (var i = 1; i <= sheets; i++)
            sb.Append(CultureInfo.InvariantCulture, $"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        sb.Append("</Types>");
        return sb.ToString();
    }

    private static List<string> UniqueSheetNames(List<string> titles)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var t in titles)
        {
            var clean = new string([.. t.Where(ch => ch is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\'))]).Trim();
            if (clean.Length == 0) clean = "Sheet";
            if (clean.Length > 31) clean = clean[..31];
            var name = clean;
            var n = 2;
            while (!used.Add(name)) name = (clean.Length > 27 ? clean[..27] : clean) + " (" + n++ + ")";
            result.Add(name);
        }
        return result;
    }

    private static string Ref(int column, int row)
    {
        var col = "";
        var c = column + 1;
        while (c > 0)
        {
            var m = (c - 1) % 26;
            col = (char)('A' + m) + col;
            c = (c - 1) / 26;
        }
        return col + row.ToString(CultureInfo.InvariantCulture);
    }

    private static string X(string s) => SecurityElement.Escape(s) ?? "";

    private static void Add(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
