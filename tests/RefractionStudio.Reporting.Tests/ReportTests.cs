using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RefractionStudio.Domain;
using RefractionStudio.Domain.Units;

namespace RefractionStudio.Reporting.Tests;

public sealed partial class ReportTests(SampleFixture sample) : IClassFixture<SampleFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private byte[] Generate(ProjectState state, Analysis.AnalysisResult analysis, UnitSystem units, PaperSize paper = PaperSize.A4)
        => ReportGenerator.Generate(new ReportInput(state, analysis, sample.Workspace.Session!.History.ToList(), sample.Workspace.Gather),
            new ReportOptions(paper, units, UnitConversion.EffectiveModulusUnit(units, ModulusDisplayUnit.MPa), "1.0.0-test", "tester", Now));

    private static int PageCount(byte[] pdf) => PageRegex().Matches(Encoding.Latin1.GetString(pdf)).Count;

    [GeneratedRegex(@"/Type\s*/Page[^s]")]
    private static partial Regex PageRegex();

    [Fact]
    public void Sample_report_is_a_complete_pdf()
    {
        var pdf = Generate(sample.Workspace.State, sample.Analysis, UnitSystem.SI);
        var text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-", text, StringComparison.Ordinal);
        Assert.Contains("%%EOF", text[^32..], StringComparison.Ordinal);
        Assert.InRange(PageCount(pdf), 20, 400);
    }

    [Fact]
    public void Imperial_and_letter_reports_are_generated()
    {
        var pdf = Generate(sample.Workspace.State, sample.Analysis, UnitSystem.USCustomary, PaperSize.Letter);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(pdf, 0, 8), StringComparison.Ordinal);
        Assert.True(PageCount(pdf) >= 20);
        // Letter pages are 612 × 792 points.
        Assert.Contains("612 792", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void Deselected_sections_shorten_the_report()
    {
        var state = sample.Workspace.State;
        var full = PageCount(Generate(state, sample.Analysis, UnitSystem.SI));
        var minimal = state with { Report = state.Report with { Sections = [ReportSection.ProjectInformation] } };
        var shortReport = PageCount(Generate(minimal, sample.Analysis, UnitSystem.SI));
        Assert.True(shortReport < full / 2, $"{shortReport} pages vs {full}");
    }

    [Fact]
    public void Project_without_data_still_produces_a_report()
    {
        var empty = ProjectState.CreateNew("Empty", "1.0.0-test", Now);
        var analysis = new Analysis.RefractionAnalyzer().Analyze(empty, null);
        var pdf = ReportGenerator.Generate(new ReportInput(empty, analysis, [], (_, _) => throw new InvalidOperationException("no data")),
            new ReportOptions(PaperSize.A4, UnitSystem.SI, ModulusDisplayUnit.MPa, "1.0.0-test", "tester", Now));
        Assert.True(PageCount(pdf) >= 1);
    }

    [Fact]
    public void Excel_workbook_holds_every_table_with_numbers_at_full_precision()
    {
        var tables = ExportTables.All(sample.Workspace.State, sample.Analysis, sample.Workspace.Session!.History.ToList(), UnitFormatter.SI);
        Assert.NotEmpty(tables);
        var bytes = XlsxWriter.Write(tables, "tester");
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var workbook = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open());
        var sheets = workbook.Descendants(ns + "sheet").ToList();
        Assert.Equal(tables.Count, sheets.Count);
        Assert.All(sheets, s => Assert.InRange(((string)s.Attribute("name")!).Length, 1, 31));
        Assert.Equal(sheets.Count, sheets.Select(s => (string)s.Attribute("name")!).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // First numeric cell of each table round-trips exactly.
        for (var i = 0; i < tables.Count; i++)
        {
            var sheet = XDocument.Load(zip.GetEntry($"xl/worksheets/sheet{i + 1}.xml")!.Open());
            var expected = tables[i].Rows.SelectMany(r => r).FirstOrDefault(c => c.Number is not null).Number;
            if (expected is null) continue;
            var values = sheet.Descendants(ns + "v").Select(v => double.Parse(v.Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            Assert.Contains(expected.Value, values);
        }
    }

    [Fact]
    public void Csv_escapes_separators_quotes_and_line_breaks()
    {
        var table = new DataTable("T", ["Name", "Value (m)"], [["a,b", Cell.Num(1.5)], ["say \"hi\"", Cell.Num(null)], ["two\nlines", Cell.Num(0.1 + 0.2)]]);
        var lines = CsvWriter.Write(table).Replace("\r\n", "\n").Split('\n');
        Assert.Equal("Name,Value (m)", lines[0]);
        Assert.Equal("\"a,b\",1.5", lines[1]);
        Assert.Equal("\"say \"\"hi\"\"\",—", lines[2]);
        Assert.Equal("\"two", lines[3]);
        Assert.Equal("lines\",0.30000000000000004", lines[4]);
    }

    [Fact]
    public void Audit_record_is_valid_json_with_raw_file_hashes()
    {
        var json = AuditExport.Create(sample.Workspace.State, sample.Analysis, sample.Workspace.Session!.History.ToList(), "1.0.0-test", "tester", Now);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var files = doc.RootElement.GetProperty("rawFiles");
        Assert.Equal(sample.Workspace.State.Survey.SourceFiles.Length, files.GetArrayLength());
        Assert.All(files.EnumerateArray(), f => Assert.Equal(64, f.GetProperty("sha256").GetString()!.Length));
    }
}
