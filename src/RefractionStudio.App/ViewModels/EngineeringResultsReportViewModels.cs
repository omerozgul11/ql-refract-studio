using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefractionStudio.Analysis;
using RefractionStudio.Analysis.Engineering;
using RefractionStudio.App.Controls;
using RefractionStudio.App.Services;
using RefractionStudio.Core.Services;
using RefractionStudio.Core.Session;
using RefractionStudio.Domain;
using RefractionStudio.Domain.Units;
using RefractionStudio.Infrastructure;
using RefractionStudio.Persistence;
using RefractionStudio.Reporting;
using RefractionStudio.Visualization;

namespace RefractionStudio.App.ViewModels;

/// <summary>An editable density interval.</summary>
public sealed partial class DensityRow : ViewModelBase
{
    [ObservableProperty] public partial double Top { get; set; }
    [ObservableProperty] public partial double Bottom { get; set; } = 10;
    [ObservableProperty] public partial double Density { get; set; } = 1900;
    [ObservableProperty] public partial string Note { get; set; } = "";
}

/// <summary>A rendered table (header + rows of strings) for display.</summary>
public sealed record ReportTableView(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, string? Note)
{
    public static ReportTableView From(DataTable t) => new(t.Title, t.Columns, [.. t.Rows.Select(r => (IReadOnlyList<string>)[.. r.Select(c => c.Display)])], t.Note);

    /// <summary>Rows as tab-separated text (for simple display and copying).</summary>
    public string Text => string.Join("\n", Rows.Select(r => string.Join("\t", r)));
}

/// <summary>Vs30, density and engineering parameters.</summary>
public sealed partial class EngineeringPageViewModel(StudioContext ctx) : PageViewModel(ctx)
{
    public override string Title => "Engineering";
    public override WorkflowStep Step => WorkflowStep.Engineering;
    public override string HelpTopic => "vs30";
    public override string GuidedText => "Choose the SH line (and the P line for moduli), the model to use and the positions to report. Vs30 is given only where the shear-wave model reaches 30 m; extrapolation must be enabled deliberately and is always labelled. Enter densities, or explicitly choose an empirical estimate (labelled ESTIMATED).";

    public ObservableCollection<SurveyLine> PLines { get; } = [];
    public ObservableCollection<SurveyLine> ShLines { get; } = [];
    public ObservableCollection<DensityRow> DensityRows { get; } = [];
    public ObservableCollection<string> Vs30Rows { get; } = [];
    public IReadOnlyList<VelocityModelSource> Sources { get; } = Enum.GetValues<VelocityModelSource>();
    public IReadOnlyList<DensitySource> DensitySources { get; } = Enum.GetValues<DensitySource>();
    public IReadOnlyList<DensityEstimator> Estimators { get; } = Enum.GetValues<DensityEstimator>();
    public IReadOnlyList<string> Properties { get; } = ["Poisson", "VpVs", "G", "E", "K", "Density"];

    [ObservableProperty] public partial SurveyLine? PLine { get; set; }
    [ObservableProperty] public partial SurveyLine? ShLine { get; set; }
    [ObservableProperty] public partial VelocityModelSource Source { get; set; }
    [ObservableProperty] public partial string Positions { get; set; } = "";
    [ObservableProperty] public partial double DepthStep { get; set; } = 1;
    [ObservableProperty] public partial double TargetDepth { get; set; } = 30;
    [ObservableProperty] public partial bool AllowExtrapolation { get; set; }
    [ObservableProperty] public partial string CustomIntervals { get; set; } = "";
    [ObservableProperty] public partial DensitySource DensitySource { get; set; }
    [ObservableProperty] public partial DensityEstimator Estimator { get; set; }
    [ObservableProperty] public partial string DensityNotes { get; set; } = "";
    [ObservableProperty] public partial ReportTableView? EngineeringTable { get; private set; }
    [ObservableProperty] public partial string Vs30Message { get; private set; } = "";
    [ObservableProperty] public partial string EngineeringMessage { get; private set; } = "";
    [ObservableProperty] public partial IPlotAdapter? ProfilePlot { get; private set; }
    [ObservableProperty] public partial IPlotAdapter? PropertyPlot { get; private set; }
    [ObservableProperty] public partial string Property { get; set; } = "Poisson";
    [ObservableProperty] public partial int ProfileIndex { get; set; }
    [ObservableProperty] public partial bool ShowExtrapolationWarning { get; private set; }

    private bool _loading;

    public override void Activate() => LoadSettings();
    public override void OnProjectChanged() => LoadSettings();
    public override void OnAnalysisUpdated(AnalysisResult result) => Refresh(result);
    public override void OnSettingsChanged()
    {
        base.OnSettingsChanged();
        if (Context.Result is { } r) Refresh(r);
    }

    partial void OnPropertyChanged(string value)
    {
        if (Context.Result is { } r) Refresh(r);
    }

    partial void OnProfileIndexChanged(int value)
    {
        if (Context.Result is { } r) Refresh(r);
    }

    private void LoadSettings()
    {
        var state = Context.State;
        if (state is null) return;
        _loading = true;
        var s = state.ActiveScenario.Settings;
        PLines.Clear();
        ShLines.Clear();
        foreach (var l in state.Survey.Lines.Where(l => l.WaveType == WaveType.P)) PLines.Add(l);
        foreach (var l in state.Survey.Lines.Where(l => l.WaveType == WaveType.SH)) ShLines.Add(l);
        PLine = PLines.FirstOrDefault(l => l.Id == s.Engineering.PLine);
        ShLine = ShLines.FirstOrDefault(l => l.Id == s.Engineering.ShLine);
        Source = s.Engineering.Source;
        var positions = s.Engineering.ProfilePositions.IsDefaultOrEmpty ? s.Vs30.Positions : s.Engineering.ProfilePositions;
        Positions = string.Join(", ", positions.Select(p => p.ToString("0.##", CultureInfo.InvariantCulture)));
        DepthStep = s.Engineering.DepthStep;
        TargetDepth = s.Vs30.TargetDepth;
        AllowExtrapolation = s.Vs30.AllowExtrapolation;
        CustomIntervals = string.Join(", ", (s.Vs30.CustomIntervals.IsDefault ? [] : s.Vs30.CustomIntervals).Select(i => $"{i.Top:0.##}-{i.Bottom:0.##}"));
        DensitySource = s.Density.Source;
        Estimator = s.Density.Estimator ?? DensityEstimator.Gardner1974;
        DensityNotes = s.Density.Notes ?? "";
        DensityRows.Clear();
        foreach (var i in s.Density.Intervals) DensityRows.Add(new DensityRow { Top = i.TopDepth, Bottom = i.BottomDepth, Density = i.Density, Note = i.Note ?? "" });
        ShowExtrapolationWarning = AllowExtrapolation;
        _loading = false;
        if (Context.Result is { } r) Refresh(r);
    }

    partial void OnAllowExtrapolationChanged(bool value)
    {
        ShowExtrapolationWarning = value;
    }

    [RelayCommand]
    private async Task ApplySettingsAsync()
    {
        if (_loading) return;
        var positions = ParseList(Positions);
        if (positions is null)
        {
            await Context.Dialogs.ShowAsync("Positions", "Enter positions in metres separated by commas, e.g. “30, 70.5, 110”.", MessageKind.Warning);
            return;
        }
        var intervals = new List<DepthInterval>();
        foreach (var part in CustomIntervals.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ab = part.Split('-', StringSplitOptions.TrimEntries);
            if (ab.Length == 2 && double.TryParse(ab[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(ab[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > a && a >= 0)
                intervals.Add(new DepthInterval(a, b));
            else
            {
                await Context.Dialogs.ShowAsync("Custom intervals", "Enter depth intervals as “top-bottom” separated by commas, e.g. “0-10, 5-15”.", MessageKind.Warning);
                return;
            }
        }
        var state = Context.State;
        if (state is null) return;
        if (AllowExtrapolation && !state.ActiveScenario.Settings.Vs30.AllowExtrapolation)
        {
            if (await Context.Dialogs.AskAsync("Enable Vs30 extrapolation?",
                    "Where the shear-wave model does not reach the averaging depth, the deepest constrained velocity will be assumed to continue below it. Such values are estimates, not measurements, and are labelled EXTRAPOLATED in the application and the report.",
                    MessageKind.Warning, "Enable extrapolation", "Cancel") != 0)
            {
                AllowExtrapolation = false;
                return;
            }
        }
        using (Context.BeginGesture("Engineering settings"))
        {
            Context.Edit(Edits.SetEngineeringSettings(new EngineeringSettings(PLine?.Id, ShLine?.Id, Source, [.. positions], Math.Clamp(DepthStep, 0.1, 10))));
            Context.Edit(Edits.SetVs30Settings(new Vs30Settings([.. positions], Math.Clamp(TargetDepth, 1, 200), AllowExtrapolation, [.. intervals])));
        }
    }

    [RelayCommand]
    private async Task ApplyDensityAsync()
    {
        var rows = DensityRows.ToList();
        if (DensitySource == DensitySource.Measured)
        {
            foreach (var r in rows)
            {
                if (!(r.Bottom > r.Top) || r.Top < 0 || !(r.Density > 0))
                {
                    await Context.Dialogs.ShowAsync("Density", "Each interval needs a top depth of 0 m or more, a bottom deeper than its top and a positive density (kg/m³).", MessageKind.Warning);
                    return;
                }
            }
        }
        else if (await Context.Dialogs.AskAsync("Use estimated density?",
                     $"Density will be estimated from Vp with {Estimator}. Empirical relations were calibrated for rocks and are not valid for most soils; values outside their range are not computed. All results will be labelled ESTIMATED.",
                     MessageKind.Warning, "Use estimate", "Cancel") != 0)
        {
            return;
        }
        var model = new DensityModel(DensitySource, [.. rows.Select(r => new DensityInterval(r.Top, r.Bottom, r.Density, string.IsNullOrWhiteSpace(r.Note) ? null : r.Note))],
            DensitySource == DensitySource.Estimated ? Estimator : null, string.IsNullOrWhiteSpace(DensityNotes) ? null : DensityNotes);
        Context.Edit(Edits.SetDensityModel(model));
    }

    [RelayCommand]
    private void AddDensityRow()
    {
        var last = DensityRows.LastOrDefault();
        DensityRows.Add(new DensityRow { Top = last?.Bottom ?? 0, Bottom = (last?.Bottom ?? 0) + 10, Density = last?.Density ?? 1900 });
    }

    [RelayCommand]
    private void RemoveDensityRow(DensityRow? row)
    {
        if (row is not null) DensityRows.Remove(row);
    }

    private static List<double>? ParseList(string text)
    {
        var list = new List<double>();
        foreach (var p in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v)) return null;
            list.Add(v);
        }
        return list;
    }

    private void Refresh(AnalysisResult r)
    {
        var u = Context.Units;
        Vs30Rows.Clear();
        if (r.Engineering.Vs30.HasValue)
        {
            Vs30Message = $"{Vs30Settings.LabelFor(TargetDepth)} = {TargetDepth:0.#} m / Σ(hᵢ/Vsᵢ)";
            foreach (var e in r.Engineering.Vs30.Value!)
            {
                Vs30Rows.Add(e.Vs30.HasValue
                    ? $"x = {u.Format(e.X, QuantityKind.Length, 1)}:  {Vs30Settings.LabelFor(TargetDepth)} = {u.Format(e.Vs30.Value!.Velocity, QuantityKind.Velocity)}   ({e.Vs30.Value.Qualifier}; constrained to {u.Format(e.Vs30.Value.ConstrainedDepth, QuantityKind.Depth, 1)})"
                    : $"x = {u.Format(e.X, QuantityKind.Length, 1)}:  not reported — {e.Vs30.Reason}");
                foreach (var c in e.Custom)
                    Vs30Rows.Add(c.HasValue ? $"      Vs {c.Value!.Top:0.#}–{c.Value.Bottom:0.#} m = {u.Format(c.Value.Velocity, QuantityKind.Velocity)} ({c.Value.Qualifier})" : $"      custom interval: {c.Reason}");
            }
        }
        else Vs30Message = r.Engineering.Vs30.Reason ?? "";
        var table = ReportTables.Engineering(r, u);
        EngineeringTable = table is null ? null : ReportTableView.From(table);
        EngineeringMessage = r.Engineering.Profiles.HasValue
            ? string.Join(" ", r.Engineering.Profiles.Value!.SelectMany(p => p.Notes).Distinct())
            : r.Engineering.Profiles.Reason ?? "";

        // Profile plot at the chosen position.
        var eng = Context.State?.ActiveScenario.Settings.Engineering;
        var positions = eng is null ? [] : (eng.ProfilePositions.IsDefaultOrEmpty ? Context.State!.ActiveScenario.Settings.Vs30.Positions : eng.ProfilePositions);
        if (eng is not null && !positions.IsDefaultOrEmpty)
        {
            var x = positions[Math.Clamp(ProfileIndex, 0, positions.Length - 1)];
            var list = new List<(string, Computed<VelocityProfile>)>();
            if (eng.PLine is { } p && r.Line(p) is { } pa) list.Add(("Vp", pa.ProfileAt(eng.Source, x)));
            if (eng.ShLine is { } sh && r.Line(sh) is { } sa) list.Add(("Vs", sa.ProfileAt(eng.Source, x)));
            ProfilePlot = list.Count == 0 ? null : new XyPlot(PlotModels.Profiles(x, list, u, Math.Max(35, TargetDepth + 5)));
        }
        else ProfilePlot = null;

        // Property section.
        PropertyPlot = null;
        if (eng?.PLine is { } pl && eng.ShLine is { } sl && r.Line(pl)?.Tomography is { } pt && r.Line(sl)?.Tomography is { } st && Context.State is { } state)
        {
            var sections = PropertySectionCalculator.Compute(pt, st, state.ActiveScenario.Settings.Density);
            if (sections.HasValue) PropertyPlot = new SectionPlot(PlotModels.PropertySection(sections.Value!, Property, r.Line(pl)!.Line, u));
        }
    }
}

/// <summary>A row of the scenario comparison.</summary>
public sealed record ComparisonRow(string Line, string Quantity, IReadOnlyList<string> Values);

/// <summary>Results summary, scenario comparison and data exports.</summary>
public sealed partial class ResultsPageViewModel(StudioContext ctx) : PageViewModel(ctx)
{
    public override string Title => "Results";
    public override WorkflowStep Step => WorkflowStep.Results;
    public override string HelpTopic => "scenarios";
    public override string GuidedText => "Review the main results, compare scenarios (alternative interpretations of the same data) and export tables (XLSX, CSV), the audit record and figures.";

    public ObservableCollection<string> Summary { get; } = [];
    public ObservableCollection<ComparisonRow> Comparison { get; } = [];
    public ObservableCollection<string> ScenarioNames { get; } = [];

    [ObservableProperty] public partial string ExportMessage { get; private set; } = "";

    public override void Activate()
    {
        if (Context.Result is { } r) Refresh(r);
    }

    public override void OnAnalysisUpdated(AnalysisResult result) => Refresh(result);

    private void Refresh(AnalysisResult r)
    {
        var u = Context.Units;
        Summary.Clear();
        foreach (var l in r.Lines)
        {
            Summary.Add($"{l.Line.Name}: {l.Observations.Included.Count()} picks used of {l.Observations.All.Length}.");
            if (l.Layered.HasValue)
                Summary.Add("   Layered (" + ReportTables.MethodName(l.Layered.Value!.Method) + "): " + string.Join(", ", l.Layered.Value.Layers.Select(x => $"V{x.Layer} {u.Format(x.Velocity, QuantityKind.Velocity)}")));
            else Summary.Add("   Layered: " + l.Layered.Reason);
            if (l.Tomography is { } t) Summary.Add($"   Tomography: RMS {t.FinalRms * 1000:0.00} ms{(l.TomographyStale ? " (out of date)" : "")}");
        }
        if (r.Engineering.Vs30.HasValue)
            foreach (var e in r.Engineering.Vs30.Value!)
                Summary.Add($"{Context.State?.ActiveScenario.Settings.Vs30.Label ?? "Vs30"} at {u.Format(e.X, QuantityKind.Length, 1)}: " + (e.Vs30.HasValue ? $"{u.Format(e.Vs30.Value!.Velocity, QuantityKind.Velocity)} ({e.Vs30.Value.Qualifier})" : e.Vs30.Reason));
        Summary.Add($"Analysis quality: {ReportGenerator.VerdictText(r.Qc.Verdict)}.");
    }

    [RelayCommand]
    private async Task CompareScenariosAsync()
    {
        var state = Context.State;
        if (state is null) return;
        var summaries = await Context.Busy.RunAsync("Comparing scenarios…", (_, _) => Task.Run(() => (IReadOnlyList<ScenarioSummary>)ScenarioComparer.Compare(state, Context.Workspace.Analyzer, Context.Workspace.Results)), cancellable: false);
        if (summaries is null) return;
        Comparison.Clear();
        ScenarioNames.Clear();
        foreach (var s in summaries) ScenarioNames.Add(s.Name);
        var u = Context.Units;
        var keys = summaries.SelectMany(s => s.Values.Select(v => (v.Line, v.Quantity, v.X, v.Kind))).Distinct().ToList();
        foreach (var (line, quantity, x, kind) in keys)
        {
            var values = summaries.Select(s => s.Values.FirstOrDefault(v => v.Line == line && v.Quantity == quantity && v.X == x) is { } v
                ? (v.Value is { } d ? (kind is { } k ? u.Format(d, k) : d.ToString(Math.Abs(d) >= 100 ? "0" : "0.00", CultureInfo.InvariantCulture)) : "—") +
                  (string.IsNullOrEmpty(v.Note) ? "" : $" ({v.Note})")
                : "—").ToList();
            var label = x is { } px ? $"{quantity} at x = {u.Format(px, QuantityKind.Length, 1)}" : quantity;
            Comparison.Add(new ComparisonRow(line, label, values));
        }
    }

    [RelayCommand]
    private async Task ExportXlsxAsync()
    {
        var ws = Context.Workspace;
        if (ws.Session is null || Context.Result is not { } r) return;
        var path = await Context.Dialogs.SaveFileAsync("Export tables", ws.State.Metadata.Name + " results.xlsx", "xlsx", new FileFilter("Excel workbook", "*.xlsx"));
        if (path is null) return;
        var tables = ExportTables.All(ws.State, r, ws.Session.History, Context.Units);
        await File.WriteAllBytesAsync(path, XlsxWriter.Write(tables, Context.Workspace.Options.UserName));
        ExportMessage = $"{tables.Count} tables written to {path}.";
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var ws = Context.Workspace;
        if (ws.Session is null || Context.Result is not { } r) return;
        var folder = await Context.Dialogs.OpenFolderAsync("Folder for CSV files");
        if (folder is null) return;
        var files = ExportTables.WriteCsvFolder(folder, ExportTables.All(ws.State, r, ws.Session.History, Context.Units));
        ExportMessage = $"{files.Count} CSV files written to {folder}.";
    }

    [RelayCommand]
    private async Task ExportAuditAsync()
    {
        var ws = Context.Workspace;
        if (ws.Session is null || Context.Result is not { } r) return;
        var path = await Context.Dialogs.SaveFileAsync("Export audit record", ws.State.Metadata.Name + " audit.json", "json", new FileFilter("JSON", "*.json"));
        if (path is null) return;
        await File.WriteAllTextAsync(path, AuditExport.Create(ws.State, r, ws.Session.History, AppInfo.Version, ws.Options.UserName, DateTimeOffset.UtcNow));
        ExportMessage = $"Audit record written to {path}.";
    }

    [RelayCommand]
    private async Task ExportFiguresAsync()
    {
        var ws = Context.Workspace;
        if (ws.Session is null || Context.Result is not { } r) return;
        var folder = await Context.Dialogs.OpenFolderAsync("Folder for figures");
        if (folder is null) return;
        var count = 0;
        foreach (var l in r.Lines)
        {
            var safe = string.Concat(l.Line.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            var tt = PlotModels.TravelTimes(l);
            await File.WriteAllBytesAsync(Path.Combine(folder, $"{safe}_traveltimes.png"), FigureExport.Render(FigureFormat.Png, new FigureOptions(), PlotTheme.Print, (c, b, t) => TravelTimeRenderer.Render(c, b, tt, t)));
            await File.WriteAllBytesAsync(Path.Combine(folder, $"{safe}_traveltimes.svg"), FigureExport.Render(FigureFormat.Svg, new FigureOptions(), PlotTheme.Print, (c, b, t) => TravelTimeRenderer.Render(c, b, tt, t)));
            count += 2;
            foreach (var kind in new[] { SectionKind.Layered, SectionKind.Tomography })
            {
                if (PlotModels.Section(l, kind, ws.State.ActiveScenario.Settings, ve: 2) is not { } sec) continue;
                await File.WriteAllBytesAsync(Path.Combine(folder, $"{safe}_{kind.ToString().ToLowerInvariant()}.png"), FigureExport.Render(FigureFormat.Png, new FigureOptions(1400, 620), PlotTheme.Print, (c, b, t) => SectionRenderer.Render(c, b, sec, t)));
                await File.WriteAllBytesAsync(Path.Combine(folder, $"{safe}_{kind.ToString().ToLowerInvariant()}.pdf"), FigureExport.Render(FigureFormat.Pdf, new FigureOptions(1000, 440), PlotTheme.Print, (c, b, t) => SectionRenderer.Render(c, b, sec, t)));
                count += 2;
            }
        }
        ExportMessage = $"{count} figures written to {folder}.";
    }
}

/// <summary>A report section toggle.</summary>
public sealed partial class SectionToggle(ReportSection section, bool included) : ViewModelBase
{
    public ReportSection Section { get; } = section;
    public string Name => Section switch
    {
        ReportSection.ProjectInformation => "Project information",
        ReportSection.SurveyConfiguration => "Survey configuration",
        ReportSection.ProcessingMethodology => "Processing methodology",
        ReportSection.QualityControl => "Quality control",
        ReportSection.ShotGathers => "Shot gathers",
        ReportSection.TravelTimes => "Travel-time curves",
        ReportSection.LayeredModel => "Layered model",
        ReportSection.VelocityProfiles => "Velocity profiles",
        ReportSection.EngineeringParameters => "Engineering parameters",
        ReportSection.ProcessingNotes => "Processing notes",
        ReportSection.Warnings => "Warnings and limitations",
        ReportSection.PickTableAppendix => "Appendix: pick tables",
        ReportSection.HistoryAppendix => "Appendix: processing history",
        _ => Section.ToString(),
    };
    [ObservableProperty] public partial bool Included { get; set; } = included;
}

/// <summary>Report content, branding, templates and PDF generation.</summary>
public sealed partial class ReportPageViewModel(StudioContext ctx) : PageViewModel(ctx)
{
    public override string Title => "Report";
    public override string HelpTopic => "report";
    public override WorkflowStep Step => WorkflowStep.Report;
    public override string GuidedText => "Complete the report details and conclusions, choose the sections, then generate the PDF. Branding comes from Settings → Reporting and can be saved as a template. The report states the software version, methods, settings, warnings and limitations for reproducibility.";

    public ObservableCollection<SectionToggle> Sections { get; } = [];
    public ObservableCollection<ReportTemplate> Templates { get; } = [];
    public IReadOnlyList<UnitSystem> UnitSystems { get; } = Enum.GetValues<UnitSystem>();
    public IReadOnlyList<string> Papers { get; } = ["A4", "Letter"];

    [ObservableProperty] public partial string ReportTitle { get; set; } = "";
    [ObservableProperty] public partial string? Subtitle { get; set; }
    [ObservableProperty] public partial string? ReportNumber { get; set; }
    [ObservableProperty] public partial string? PreparedBy { get; set; }
    [ObservableProperty] public partial string? CheckedBy { get; set; }
    [ObservableProperty] public partial string? ProcessingNotes { get; set; }
    [ObservableProperty] public partial string? Conclusions { get; set; }
    [ObservableProperty] public partial bool IncludeAllGathers { get; set; }
    [ObservableProperty] public partial UnitSystem Units { get; set; }
    [ObservableProperty] public partial string Paper { get; set; } = "A4";
    [ObservableProperty] public partial string CompanyName { get; private set; } = "";
    [ObservableProperty] public partial ReportTemplate? SelectedTemplate { get; set; }
    [ObservableProperty] public partial string TemplateName { get; set; } = "";
    [ObservableProperty] public partial string Message { get; private set; } = "";
    [ObservableProperty] public partial string? LastReport { get; private set; }

    public override void Activate()
    {
        Load();
        _ = LoadTemplatesAsync();
    }

    public override void OnProjectChanged() => Load();

    private void Load()
    {
        var state = Context.State;
        if (state is null) return;
        var r = state.Report;
        ReportTitle = r.Title;
        Subtitle = r.Subtitle;
        ReportNumber = r.ReportNumber;
        PreparedBy = r.PreparedBy ?? Context.Settings.Current.Branding.EngineerName;
        CheckedBy = r.CheckedBy;
        ProcessingNotes = r.ProcessingNotes;
        Conclusions = r.Conclusions;
        IncludeAllGathers = r.IncludeAllGathers;
        Units = r.UnitsOverride ?? Context.Settings.Current.Units;
        Paper = Context.Settings.Current.PaperSize;
        CompanyName = string.IsNullOrWhiteSpace(r.Branding.CompanyName) ? Context.Settings.Current.Branding.CompanyName : r.Branding.CompanyName;
        Sections.Clear();
        foreach (var s in Enum.GetValues<ReportSection>()) Sections.Add(new SectionToggle(s, r.Sections.IsDefaultOrEmpty || r.Sections.Contains(s)));
    }

    private async Task LoadTemplatesAsync()
    {
        Templates.Clear();
        foreach (var t in await Context.Catalog.ReportTemplatesAsync()) Templates.Add(t);
    }

    private ReportSettings Build()
    {
        var state = Context.State!;
        var branding = string.IsNullOrWhiteSpace(state.Report.Branding.CompanyName) ? Context.Settings.Current.Branding : state.Report.Branding;
        return state.Report with
        {
            Branding = branding,
            Title = string.IsNullOrWhiteSpace(ReportTitle) ? "Seismic Refraction Survey Report" : ReportTitle.Trim(),
            Subtitle = Subtitle,
            ReportNumber = ReportNumber,
            PreparedBy = PreparedBy,
            CheckedBy = CheckedBy,
            ProcessingNotes = ProcessingNotes,
            Conclusions = Conclusions,
            Sections = [.. Sections.Where(s => s.Included).Select(s => s.Section)],
            IncludeAllGathers = IncludeAllGathers,
            UnitsOverride = Units,
        };
    }

    [RelayCommand]
    private void ApplySettings()
    {
        if (Context.State is null) return;
        var settings = Build();
        if (settings != Context.State.Report) Context.Edit(Edits.SetReport(settings));
        Message = "Report settings saved in the project.";
    }

    [RelayCommand]
    private void UseCompanyBranding()
    {
        if (Context.State is null) return;
        Context.Edit(Edits.SetReport(Build() with { Branding = Context.Settings.Current.Branding }));
        CompanyName = Context.Settings.Current.Branding.CompanyName;
        Message = string.IsNullOrWhiteSpace(CompanyName) ? "No company branding is set: enter it in Settings → Reporting." : $"Branding of “{CompanyName}” applied.";
    }

    [RelayCommand]
    private async Task SaveTemplateAsync()
    {
        var settings = Build();
        var name = string.IsNullOrWhiteSpace(TemplateName) ? settings.Branding.CompanyName + " report" : TemplateName.Trim();
        await Context.Catalog.SaveReportTemplateAsync(new ReportTemplate(Guid.NewGuid(), name, settings.Branding, settings.Sections));
        TemplateName = "";
        await LoadTemplatesAsync();
        Message = $"Template “{name}” saved.";
    }

    [RelayCommand]
    private void ApplyTemplate()
    {
        if (SelectedTemplate is not { } t || Context.State is null) return;
        foreach (var s in Sections) s.Included = t.Sections.IsDefaultOrEmpty || t.Sections.Contains(s.Section);
        Context.Edit(Edits.SetReport(Build() with { Branding = t.Branding, Sections = t.Sections }));
        CompanyName = t.Branding.CompanyName;
        Message = $"Template “{t.Name}” applied.";
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        var ws = Context.Workspace;
        if (ws.Session is null) return;
        ApplySettings();
        var path = await Context.Dialogs.SaveFileAsync("Save report", (ws.State.Report.ReportNumber ?? ws.State.Metadata.Name) + ".pdf", "pdf", new FileFilter("PDF", "*.pdf"));
        if (path is null) return;
        var state = ws.State;
        var history = ws.Session.History.ToList();
        var options = new ReportOptions(Paper == "Letter" ? PaperSize.Letter : PaperSize.A4, Units, UnitConversion.EffectiveModulusUnit(Units, Context.Settings.Current.ModulusUnit),
            AppInfo.Version, ws.Options.UserName, DateTimeOffset.UtcNow);
        var ok = await Context.Busy.RunAsync("Generating the report…", (progress, ct) => Task.Run(() =>
        {
            progress.Report((double.NaN, "Analysing…"));
            var analysis = ws.Analyzer.Analyze(state, ws.Results, ct);
            progress.Report((double.NaN, "Laying out pages…"));
            var pdf = ReportGenerator.Generate(new ReportInput(state, analysis, history, ws.Gather), options);
            File.WriteAllBytes(path, pdf);
            return true;
        }, ct));
        if (!ok) return;
        ws.Session.Record(HistoryKind.Report, "report.generate", $"Generate report {Path.GetFileName(path)}", ImmutableDictionary<string, string>.Empty.Add("file", Path.GetFileName(path)));
        LastReport = path;
        Message = $"Report written to {path}.";
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (LastReport is not null && File.Exists(LastReport)) Process.Start(new ProcessStartInfo(LastReport) { UseShellExecute = true });
    }
}
