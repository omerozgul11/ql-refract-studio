using System.Text.Json;
using System.Text.Json.Serialization;
using RefractionStudio.Analysis;
using RefractionStudio.Domain;
using RefractionStudio.Domain.Units;
using RefractionStudio.Visualization;

namespace RefractionStudio.Reporting;

/// <summary>
/// Machine-readable audit record: which raw files were used (with hashes), what processing was applied,
/// which picks were manual or automatic, what was excluded, which methods and settings produced the
/// results, and which software version computed them.
/// </summary>
public static class AuditExport
{
    private static readonly JsonSerializerOptions Json = new(DomainJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static string Create(ProjectState state, AnalysisResult analysis, IReadOnlyList<HistoryEntry> history, string appVersion, string userName, DateTimeOffset now)
    {
        var scenario = state.ActiveScenario;
        var audit = new
        {
            generatedAt = now,
            generatedBy = userName,
            software = new { name = "Refraction Studio", version = appVersion },
            project = new { state.Metadata.Name, state.Metadata.ProjectNumber, state.Metadata.Client, state.Metadata.SiteName, state.SchemaVersion },
            scenario = new { scenario.Name, scenario.Description, scenario.CreatedAt },
            rawFiles = state.Survey.SourceFiles.Select(f => new { f.FileName, f.Sha256, f.Length, f.FormatId, f.ImportedAt }),
            lines = analysis.Lines.Select(l =>
            {
                var interp = state.Interpretation(l.Line.Id);
                return new
                {
                    l.Line.Name,
                    waveType = l.Line.WaveType.ToString(),
                    shots = l.Line.Gathers.Select(g => new { g.Label, g.ShotX, g.ShotDepth, g.Included, combine = g.Combine.ToString(), records = g.Members.Length, g.Comment }),
                    processing = interp.Pipeline.EnabledSteps.Select(s => new { step = s.DisplayName, parameters = s.Parameters }),
                    excludedTraces = interp.Adjustments.Where(a => a.Value.Excluded).Select(a => new { trace = a.Key.ToString(), a.Value.Note }),
                    traceAdjustments = interp.Adjustments.Where(a => !a.Value.IsNeutral).Select(a => new { trace = a.Key.ToString(), a.Value.Gain, a.Value.InvertPolarity, a.Value.TimeShift, a.Value.Note }),
                    shotTime = new { method = interp.ShotTime.Method.ToString(), interp.ShotTime.ReferenceVelocity },
                    picker = interp.Picker,
                    picks = new
                    {
                        total = l.Observations.All.Length,
                        manual = l.Observations.All.Count(o => o.Pick.Origin == ArrivalOrigin.Manual),
                        acceptedAutomatic = l.Observations.All.Count(o => o.Pick.Origin == ArrivalOrigin.AcceptedSuggestion),
                        automaticNotReviewed = l.Observations.All.Count(o => o.Pick.Origin == ArrivalOrigin.Automatic),
                        correlation = l.Observations.All.Count(o => o.Pick.Origin == ArrivalOrigin.Correlation),
                        excluded = l.Observations.All.Where(o => !o.Included).Select(o => new { pick = o.Key.ToString(), reason = o.ExclusionReason }),
                    },
                    layerAssignment = new { interp.Segments.LayerCount, userEdited = interp.Segments.Gathers.Count(g => g.Value.UserEdited), overrides = interp.Segments.Overrides.Count },
                    layered = new
                    {
                        settings = interp.Layered,
                        available = l.Layered.HasValue,
                        reason = l.Layered.Reason,
                        velocities = l.Layered.HasValue ? l.Layered.Value!.Layers.Select(x => new { x.Layer, x.Velocity, x.StdError, x.Basis }) : null,
                    },
                    tomography = l.Tomography is { } t ? new
                    {
                        settings = t.Settings,
                        t.InputFingerprint,
                        stale = l.TomographyStale,
                        iterations = t.Iterations,
                        t.StopReason,
                        t.Notes,
                        computedWith = interp.TomographyResult?.AppVersion,
                        computedAt = interp.TomographyResult?.CreatedAt,
                    } : null,
                };
            }),
            engineering = new
            {
                scenario.Settings.Engineering,
                density = scenario.Settings.Density,
                vs30 = scenario.Settings.Vs30,
            },
            qc = new { verdict = analysis.Qc.Verdict.ToString(), analysis.Qc.Reasons, problems = analysis.Qc.Flags.Count(f => f.Level == Analysis.Quality.QcLevel.Problem), reviews = analysis.Qc.Flags.Count(f => f.Level == Analysis.Quality.QcLevel.Review) },
            history = history.Select(h => new { h.Timestamp, kind = h.Kind.ToString(), h.Operation, h.Description, h.Parameters, h.User, h.AppVersion }),
        };
        return JsonSerializer.Serialize(audit, Json);
    }
}

/// <summary>All result tables of a project (for XLSX/CSV export).</summary>
public static class ExportTables
{
    public static IReadOnlyList<DataTable> All(ProjectState state, AnalysisResult a, IReadOnlyList<HistoryEntry> history, UnitFormatter u)
    {
        var list = new List<DataTable>();
        foreach (var l in a.Lines)
        {
            list.Add(ReportTables.Picks(l, u));
            list.Add(ReportTables.Segments(l, u));
            list.Add(ReportTables.ShotTimes(l));
            if (ReportTables.Layers(l, u) is { } layers) list.Add(layers);
            if (ReportTables.Interfaces(l, u) is { } interfaces) list.Add(interfaces);
        }
        if (ReportTables.Vs30(a, u, state.ActiveScenario.Settings.Vs30.TargetDepth) is { } vs30) list.Add(vs30);
        if (ReportTables.Engineering(a, u) is { } eng) list.Add(eng);
        list.Add(ReportTables.Qc(a));
        list.Add(ReportTables.SourceFiles(state));
        list.Add(ReportTables.History(history));
        return list;
    }

    /// <summary>Writes every table as CSV into a folder; returns the file paths.</summary>
    public static IReadOnlyList<string> WriteCsvFolder(string folder, IReadOnlyList<DataTable> tables)
    {
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables)
        {
            var name = string.Concat(t.Title.Select(c => char.IsLetterOrDigit(c) ? c : '_')).Trim('_');
            while (name.Contains("__", StringComparison.Ordinal)) name = name.Replace("__", "_", StringComparison.Ordinal);
            var unique = name;
            var n = 2;
            while (!used.Add(unique)) unique = $"{name}_{n++}";
            var path = Path.Combine(folder, unique + ".csv");
            File.WriteAllText(path, CsvWriter.Write(t), new System.Text.UTF8Encoding(true));
            paths.Add(path);
        }
        return paths;
    }
}

/// <summary>Figure export helpers (any renderer to PNG/SVG/PDF).</summary>
public static class FigureFiles
{
    public static void Write(string path, FigureFormat format, FigureOptions options, PlotTheme theme, Action<SkiaSharp.SKCanvas, SkiaSharp.SKRect, PlotTheme> draw)
        => File.WriteAllBytes(path, FigureExport.Render(format, options, theme, draw));
}
