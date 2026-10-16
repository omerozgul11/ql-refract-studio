using RefractionStudio.Analysis;
using RefractionStudio.Core;

namespace RefractionStudio.Reporting.Tests;

/// <summary>The sample project opened once, with its analysis.</summary>
public sealed class SampleFixture : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-report-tests-" + Guid.NewGuid().ToString("N"));

    public SampleFixture()
    {
        Directory.CreateDirectory(_dir);
        var copy = Path.Combine(_dir, "sample.rfsx");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Samples", "Refraction-Studio-Sample.rfsx"), copy);
        Workspace = new ProjectWorkspace(new WorkspaceOptions("1.0.0-test", "tester", Path.Combine(_dir, "recovery")));
        Workspace.Open(copy);
        Analysis = Workspace.Analyze();
    }

    public ProjectWorkspace Workspace { get; }

    public AnalysisResult Analysis { get; }

    public void Dispose()
    {
        Workspace.Close();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
