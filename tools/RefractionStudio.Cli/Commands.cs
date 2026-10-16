using System.Reflection;
using RefractionStudio.Analysis;
using RefractionStudio.Core;
using RefractionStudio.Domain;
using RefractionStudio.Domain.Units;
using RefractionStudio.Reporting;
using RefractionStudio.Synthetics;
using RefractionStudio.Visualization;

namespace RefractionStudio.Cli;

/// <summary>Minimal argument parser: positional values and --name value options.</summary>
public sealed class Args
{
    private readonly List<string> _positional = [];
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    public Args(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var key = args[i][2..];
                _options[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            }
            else _positional.Add(args[i]);
        }
    }

    public string Positional(int index, string name)
        => index < _positional.Count ? _positional[index] : throw new ArgumentException($"Missing argument: {name}.");

    public string Option(string name) => _options.TryGetValue(name, out var v) ? v : throw new ArgumentException($"Missing option --{name}.");

    public string? OptionOrNull(string name) => _options.TryGetValue(name, out var v) ? v : null;
}

public static class Commands
{
    public static string Version => typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    private static WorkspaceOptions Options(string? recovery = null)
        => new(Version, Environment.UserName, recovery ?? Path.Combine(Path.GetTempPath(), "refraction-studio-cli", "recovery"));

    public static async Task<int> SampleAsync(Args a)
    {
        var output = Path.GetFullPath(a.Option("out"));
        var work = a.OptionOrNull("work") ?? Path.Combine(Path.GetTempPath(), "refraction-studio-sample-" + Guid.NewGuid().ToString("N")[..8]);
        var ws = await SampleProjectBuilder.BuildAsync(work, Options(Path.Combine(work, "recovery")),
            new Progress<SampleProgress>(p => Console.WriteLine($"{p.Fraction * 100,3:0}%  {p.Stage}")));
        ws.Save(output);
        ws.Close();
        if (a.OptionOrNull("work") is null) Directory.Delete(work, recursive: true);
        Console.WriteLine($"Sample project written to {output}");
        return 0;
    }

    private static ProjectWorkspace Open(string path)
    {
        var ws = new ProjectWorkspace(Options());
        ws.Open(path);
        return ws;
    }

    public static Task<int> AnalyzeAsync(Args a)
    {
        var ws = Open(a.Positional(0, "project"));
        var result = ws.Analyze();
        var u = UnitFormatter.SI;
        foreach (var l in result.Lines)
        {
            Console.WriteLine($"{l.Line.Name} ({l.Line.WaveType}): {l.Observations.Included.Count()} picks used of {l.Observations.All.Length}");
            if (l.Layered.HasValue)
                Console.WriteLine("  Layered: " + string.Join(", ", l.Layered.Value!.Layers.Select(x => $"V{x.Layer} {u.Format(x.Velocity, QuantityKind.Velocity)}")));
            else Console.WriteLine("  Layered: " + l.Layered.Reason);
            if (l.Tomography is { } t) Console.WriteLine($"  Tomography: RMS {t.FinalRms * 1000:0.00} ms, χ² {t.FinalChi2:0.00}{(l.TomographyStale ? " (out of date)" : "")}");
        }
        if (result.Engineering.Vs30.HasValue)
            foreach (var e in result.Engineering.Vs30.Value!)
                Console.WriteLine($"Vs30 at {e.X:0.##} m: " + (e.Vs30.HasValue ? $"{e.Vs30.Value!.Velocity:0} m/s ({e.Vs30.Value.Qualifier})" : e.Vs30.Reason));
        Console.WriteLine($"Quality: {ReportGenerator.VerdictText(result.Qc.Verdict)} — {string.Join(" ", result.Qc.Reasons)}");
        return Task.FromResult(0);
    }

    private static (UnitSystem, ModulusDisplayUnit) Units(Args a)
        => (a.OptionOrNull("units")?.ToLowerInvariant() == "us" ? UnitSystem.USCustomary : UnitSystem.SI,
            a.OptionOrNull("units")?.ToLowerInvariant() == "us" ? ModulusDisplayUnit.Ksf : ModulusDisplayUnit.MPa);

    public static Task<int> ReportAsync(Args a)
    {
        var ws = Open(a.Positional(0, "project"));
        var (units, modulus) = Units(a);
        var paper = a.OptionOrNull("paper")?.ToLowerInvariant() == "letter" || (a.OptionOrNull("paper") is null && units == UnitSystem.USCustomary) ? PaperSize.Letter : PaperSize.A4;
        var analysis = ws.Analyze();
        var pdf = ReportGenerator.Generate(new ReportInput(ws.State, analysis, ws.Session!.History, ws.Gather),
            new ReportOptions(paper, units, modulus, Version, Environment.UserName, DateTimeOffset.UtcNow));
        var output = Path.GetFullPath(a.Option("out"));
        File.WriteAllBytes(output, pdf);
        Console.WriteLine($"Report written to {output} ({pdf.Length / 1024} KiB)");
        return Task.FromResult(0);
    }

    public static Task<int> ExportAsync(Args a)
    {
        var ws = Open(a.Positional(0, "project"));
        var folder = Path.GetFullPath(a.Option("out"));
        Directory.CreateDirectory(folder);
        var (units, modulus) = Units(a);
        var u = new UnitFormatter(units, modulus);
        var analysis = ws.Analyze();
        var tables = ExportTables.All(ws.State, analysis, ws.Session!.History, u);
        File.WriteAllBytes(Path.Combine(folder, "results.xlsx"), XlsxWriter.Write(tables, Environment.UserName));
        ExportTables.WriteCsvFolder(Path.Combine(folder, "csv"), tables);
        File.WriteAllText(Path.Combine(folder, "audit.json"), AuditExport.Create(ws.State, analysis, ws.Session.History, Version, Environment.UserName, DateTimeOffset.UtcNow));
        var figures = Path.Combine(folder, "figures");
        Directory.CreateDirectory(figures);
        foreach (var l in analysis.Lines)
        {
            var safe = string.Concat(l.Line.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            var tt = PlotModels.TravelTimes(l);
            FigureFiles.Write(Path.Combine(figures, $"{safe}_traveltimes.png"), FigureFormat.Png, new FigureOptions(), PlotTheme.Print, (c, b, t) => TravelTimeRenderer.Render(c, b, tt, t));
            FigureFiles.Write(Path.Combine(figures, $"{safe}_traveltimes.svg"), FigureFormat.Svg, new FigureOptions(), PlotTheme.Print, (c, b, t) => TravelTimeRenderer.Render(c, b, tt, t));
            if (PlotModels.Section(l, SectionKind.Tomography, ws.State.ActiveScenario.Settings, ve: 2) is { } sec)
            {
                FigureFiles.Write(Path.Combine(figures, $"{safe}_tomogram.png"), FigureFormat.Png, new FigureOptions(1400, 600), PlotTheme.Print, (c, b, t) => SectionRenderer.Render(c, b, sec, t));
                FigureFiles.Write(Path.Combine(figures, $"{safe}_tomogram.pdf"), FigureFormat.Pdf, new FigureOptions(1000, 430), PlotTheme.Print, (c, b, t) => SectionRenderer.Render(c, b, sec, t));
            }
            var first = l.Line.Gathers.FirstOrDefault(g => g.Included);
            if (first is not null)
            {
                var gm = PlotModels.Gather(ws.Gather(l.Line.Id, first.Id), ws.State.Interpretation(l.Line.Id), l);
                FigureFiles.Write(Path.Combine(figures, $"{safe}_gather_{first.Label.Split(' ')[0]}.png"), FigureFormat.Png, new FigureOptions(1200, 800), PlotTheme.Print, (c, b, t) => GatherRenderer.Render(c, b, gm, t));
            }
        }
        Console.WriteLine($"Exported {tables.Count} tables, audit and figures to {folder}");
        return Task.FromResult(0);
    }

    public static Task<int> ValidateAsync(Args a) => Validation.RunAsync(a);

    public static Task<int> KeysAsync(Args a) => Signing.KeysAsync(a);

    public static Task<int> SignManifestAsync(Args a) => Signing.SignManifestAsync(a);
}
