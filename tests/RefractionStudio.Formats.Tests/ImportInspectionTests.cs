using System.Globalization;
using RefractionStudio.Formats;

namespace RefractionStudio.Formats.Tests;

public sealed class ImportInspectionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rs-import-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, double dt, int channels, double sourceX, Func<int, int, double>? sample = null, int samples = 400)
    {
        var traces = Enumerable.Range(1, channels).Select(c => new Seg2TraceToWrite(
            new Dictionary<string, string>
            {
                ["SAMPLE_INTERVAL"] = dt.ToString("R", CultureInfo.InvariantCulture),
                ["CHANNEL_NUMBER"] = c.ToString(CultureInfo.InvariantCulture),
                ["SOURCE_LOCATION"] = sourceX.ToString("R", CultureInfo.InvariantCulture),
            },
            Enumerable.Range(0, samples).Select(i => sample?.Invoke(c, i) ?? Math.Sin(i * 0.1 + c) * 100 + (sourceX + c) * 0.001).ToArray())).ToList();
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Seg2Writer.Write(new Dictionary<string, string>(), traces, 4));
        return path;
    }

    [Fact]
    public async Task Clean_batch_has_only_informational_findings()
    {
        var paths = new[] { WriteFile("a.sg2", 0.00025, 12, 0), WriteFile("b.sg2", 0.00025, 12, 24) };
        var files = await ImportInspector.ReadFilesAsync(FormatRegistry.CreateDefault(), paths);
        var issues = ImportInspector.Validate(files);
        Assert.DoesNotContain(issues, i => i.Severity != ImportSeverity.Information);
        var summary = ImportInspector.Summarize(files);
        Assert.Equal(2, summary.RecordCount);
        Assert.Equal(24, summary.TraceCount);
        Assert.Equal([0.00025], summary.SampleIntervals);
    }

    [Fact]
    public async Task Different_sample_rate_is_explained_in_plain_language()
    {
        var paths = new[] { WriteFile("a.sg2", 0.00025, 12, 0), WriteFile("b.sg2", 0.00025, 12, 12), WriteFile("c.sg2", 0.0005, 12, 24) };
        var issues = ImportInspector.Validate(await ImportInspector.ReadFilesAsync(FormatRegistry.CreateDefault(), paths));
        var issue = Assert.Single(issues, i => i.Code == "sample-interval-mismatch");
        Assert.Equal("c.sg2", issue.FileName);
        Assert.Contains("(2 kHz) from the other records (4 kHz)", issue.Message);
        Assert.Equal(ImportSeverity.Warning, issue.Severity);
    }

    [Fact]
    public async Task Duplicates_dead_and_unreadable_files_are_reported_with_fixes_not_applied()
    {
        var a = WriteFile("a.sg2", 0.00025, 12, 0);
        var copy = Path.Combine(_dir, "a_copy.sg2");
        File.Copy(a, copy);
        var dead = WriteFile("dead.sg2", 0.00025, 12, 48, (c, i) => c == 5 ? 0 : Math.Sin(i * 0.2) * c);
        var bad = Path.Combine(_dir, "bad.sg2");
        File.WriteAllBytes(bad, [0x55, 0x3a, 1, 0]);
        var files = await ImportInspector.ReadFilesAsync(FormatRegistry.CreateDefault(), [a, copy, dead, bad]);
        var issues = ImportInspector.Validate(files);

        Assert.Contains(issues, i => i.Code == "duplicate-file" && i.FileName == "a_copy.sg2" && i.Fix!.Kind == ImportFixKind.SkipFile);
        Assert.Contains(issues, i => i.Code == "dead-trace" && i.Channel == 5 && i.Fix!.Kind == ImportFixKind.SkipTrace);
        Assert.Contains(issues, i => i.Code == "unreadable" && i.Severity == ImportSeverity.Error && i.FileName == "bad.sg2");
        Assert.Contains(issues, i => i.Code == "repeated-source"); // a and its copy share x = 0
    }

    [Fact]
    public async Task Missing_channels_are_reported()
    {
        var traces = new[] { 1, 2, 4 }.Select(c => new Seg2TraceToWrite(
            new Dictionary<string, string> { ["SAMPLE_INTERVAL"] = "0.00025", ["CHANNEL_NUMBER"] = c.ToString(CultureInfo.InvariantCulture) },
            Enumerable.Range(0, 100).Select(i => Math.Sin(i + c)).ToArray())).ToList();
        var path = Path.Combine(_dir, "gap.sg2");
        File.WriteAllBytes(path, Seg2Writer.Write(new Dictionary<string, string>(), traces));
        var issues = ImportInspector.Validate(await ImportInspector.ReadFilesAsync(FormatRegistry.CreateDefault(), [path]));
        Assert.Contains(issues, i => i.Code == "missing-channels" && i.Message.Contains("channel(s) 3"));
    }
}
