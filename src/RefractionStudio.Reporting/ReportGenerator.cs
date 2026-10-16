using System.Globalization;
using RefractionStudio.Analysis;
using RefractionStudio.Analysis.Engineering;
using RefractionStudio.Analysis.Quality;
using RefractionStudio.Core.Data;
using RefractionStudio.Domain;
using RefractionStudio.Domain.Units;
using RefractionStudio.Visualization;
using SkiaSharp;

namespace RefractionStudio.Reporting;

/// <summary>Options of a generated report.</summary>
/// <param name="Paper">Paper size.</param>
/// <param name="Units">Display units.</param>
/// <param name="ModulusUnit">Modulus unit.</param>
/// <param name="AppVersion">Application version printed for reproducibility.</param>
/// <param name="UserName">User who generated the report.</param>
/// <param name="GeneratedAt">Generation time.</param>
/// <param name="MaxAppendixPicks">Maximum pick rows printed per line (all rows are in the XLSX export).</param>
public sealed record ReportOptions(PaperSize Paper, UnitSystem Units, ModulusDisplayUnit ModulusUnit, string AppVersion, string UserName, DateTimeOffset GeneratedAt, int MaxAppendixPicks = 2000);

/// <summary>Everything the report needs.</summary>
public sealed record ReportInput(ProjectState State, AnalysisResult Analysis, IReadOnlyList<HistoryEntry> History, Func<LineId, GatherId, ProcessedGather> Gather);

/// <summary>Generates the PDF consulting report.</summary>
public static class ReportGenerator
{
    /// <summary>Statement of the software's role, printed in every report.</summary>
    public const string Limitations =
        "Results were produced with Refraction Studio and depend on the quality of the field data, the acquisition geometry, the picks and the interpretation choices documented in this report. " +
        "The software does not replace professional judgement. The seismic refraction method assumes that velocity increases with depth; low-velocity layers and layers thinner than the resolution of the survey (hidden layers) cannot be detected. " +
        "Layered interpretations assume laterally continuous layers; tomograms are smooth, non-unique models whose reliability decreases where ray coverage is low. " +
        "The user is responsible for assessing compliance with applicable standards (for example ASTM D5777, Standard Guide for Using the Seismic Refraction Method for Subsurface Investigation) and regulations; no compliance is implied.";

    public static byte[] Generate(ReportInput input, ReportOptions options)
    {
        var state = input.State;
        var a = input.Analysis;
        var r = state.Report;
        var u = new UnitFormatter(options.Units, options.ModulusUnit);
        var style = ReportStyle.Default;
        var doc = new PdfLayout(options.Paper, style);
        var theme = PlotTheme.Print;
        var sections = r.Sections.IsDefaultOrEmpty ? [.. Enum.GetValues<ReportSection>()] : r.Sections.ToHashSet();
        var title = string.IsNullOrWhiteSpace(r.Title) ? "Seismic Refraction Survey Report" : r.Title;
        var company = string.IsNullOrWhiteSpace(r.Branding.CompanyName) ? "" : r.Branding.CompanyName;

        doc.Decorate = (c, page, count) =>
        {
            using var small = doc.Font(7.5f);
            using var muted = new SKPaint { Color = style.Muted, IsAntialias = true };
            using var rule = new SKPaint { Color = style.Rule };
            var top = doc.Margin;
            c.DrawText(company, new SKPoint(doc.ContentLeft, top + 8), SKTextAlign.Left, small, muted);
            c.DrawText(title + (r.ReportNumber is { } n ? " · " + n : ""), new SKPoint(doc.ContentRight, top + 8), SKTextAlign.Right, small, muted);
            c.DrawLine(doc.ContentLeft, top + 14, doc.ContentRight, top + 14, rule);
            var bottom = doc.PageHeight - doc.Margin;
            c.DrawLine(doc.ContentLeft, bottom - 16, doc.ContentRight, bottom - 16, rule);
            c.DrawText(r.Branding.Footer ?? $"Generated with Refraction Studio {options.AppVersion}", new SKPoint(doc.ContentLeft, bottom - 5), SKTextAlign.Left, small, muted);
            c.DrawText($"Page {page} of {count}", new SKPoint(doc.ContentRight, bottom - 5), SKTextAlign.Right, small, muted);
        };

        if (sections.Contains(ReportSection.Cover)) Cover(doc, state, options, style);

        // Summary & reproducibility (always included).
        doc.Heading("Summary", 1);
        doc.Callout($"Analysis quality: {VerdictText(a.Qc.Verdict)}", string.Join(" ", a.Qc.Reasons), a.Qc.Verdict != QualityVerdict.Good);
        SummaryBullets(doc, state, a, u);
        if (options.Units != UnitSystem.SI)
            doc.Paragraph("Units: tables, stated values and velocity profiles are in US customary units (ft, ft/s, pcf, psi/ksf). Gathers, travel-time curves " +
                          "and sections are drawn in SI units (m, m/s), as labelled on their axes and colour scales.", 8.5f, italic: true);
        doc.Heading("Reproducibility", 2);
        doc.KeyValues(
        [
            ("Software", $"Refraction Studio {options.AppVersion}"),
            ("Report generated", options.GeneratedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) + $" by {options.UserName}"),
            ("Analysis computed", a.ComputedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)),
            ("Scenario", state.ActiveScenario.Name),
            ("Units", options.Units == UnitSystem.SI ? "SI (calculations in SI)" : "US customary (calculations in SI, converted for display)"),
            ("Project file history", $"{input.History.Count} recorded changes (Appendix)"),
        ]);

        if (sections.Contains(ReportSection.ProjectInformation))
        {
            doc.Heading("Project information", 1);
            var m = state.Metadata;
            doc.KeyValues(
            [
                ("Project", m.Name), ("Project number", m.ProjectNumber), ("Client", m.Client), ("Client contact", m.ClientContact),
                ("Site", m.SiteName), ("Site address", m.SiteAddress), ("Survey date", m.SurveyDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("Field crew", m.FieldCrew), ("Conditions", m.Conditions), ("Prepared by", r.PreparedBy), ("Checked by", r.CheckedBy),
                ("Engineer / geophysicist", r.Branding.EngineerName), ("Registration", r.Branding.Registration),
            ]);
            if (!string.IsNullOrWhiteSpace(m.Description)) doc.Paragraph(m.Description);
        }

        if (sections.Contains(ReportSection.SurveyConfiguration))
        {
            doc.Heading("Survey configuration", 1);
            var m = state.Metadata;
            doc.KeyValues([("Instrument", m.Instrument), ("Geophones", m.Geophones), ("Source", m.Source)]);
            var rows = state.Survey.Lines.Select(l => (IReadOnlyList<Cell>)
            [
                l.Name, l.WaveType == WaveType.P ? "P (compressional)" : "SH (shear)", Cell.Num(l.Gathers.Count(g => g.Included), "0"),
                Cell.Num(l.Spreads.Sum(s => s.ReceiverChannels.Count()), "0"),
                Cell.Num(l.Spreads.Select(s => s.Spacing).DefaultIfEmpty(double.NaN).Min() is var sp && double.IsFinite(sp) ? u.ToDisplay(sp, QuantityKind.Length) : null, "0.##"),
                Cell.Num(LineLength(l) is { } len ? u.ToDisplay(len, QuantityKind.Length) : null, "0.#"),
                l.Topography.IsFlat ? "Flat (none supplied)" : $"{l.Topography.Points.Length} surveyed points",
            ]).ToList();
            doc.Table(new DataTable("Survey lines", ["Line", "Wave type", "Shots", "Receivers", $"Spacing ({u.Symbol(QuantityKind.Length)})", $"Length ({u.Symbol(QuantityKind.Length)})", "Topography"], rows));
            doc.Table(ReportTables.SourceFiles(state), 60, 7);
        }

        foreach (var line in a.Lines)
        {
            var interp = state.Interpretation(line.Line.Id);
            if (sections.Contains(ReportSection.Geometry))
            {
                doc.Heading($"Geometry — {line.Line.Name}", 1);
                var spread = PlotModels.Spread(line.Line, null);
                doc.Figure((c, b) => SpreadRenderer.Render(c, b, spread, theme), 3.2f, "Ground profile (vertical exaggeration 5×), geophones (triangles) and shot points (stars).");
                var shotRows = line.Line.Gathers.Select(g => (IReadOnlyList<Cell>)
                [
                    g.Label, Cell.Num(double.IsFinite(g.ShotX) ? u.ToDisplay(g.ShotX, QuantityKind.Length) : null, "0.00"),
                    Cell.Num(double.IsFinite(g.ShotX) ? u.ToDisplay(line.Line.ShotGroundElevation(g), QuantityKind.Elevation) : null, "0.00"),
                    Cell.Num(u.ToDisplay(g.ShotDepth, QuantityKind.Depth), "0.00"), g.Combine == CombineMode.Stack ? $"{g.Members.Length} records combined" : "Single record",
                    g.Included ? "Yes" : "No", g.Comment ?? "",
                ]).ToList();
                doc.Table(new DataTable("Shot points", ["Shot", $"x ({u.Symbol(QuantityKind.Length)})", $"Ground elevation ({u.Symbol(QuantityKind.Elevation)})", $"Source depth ({u.Symbol(QuantityKind.Depth)})", "Records", "Included", "Comment"], shotRows));
            }

            if (sections.Contains(ReportSection.ProcessingMethodology))
            {
                doc.Heading($"Processing and picking — {line.Line.Name}", 1);
                doc.Paragraph("Raw records were not modified. All processing is applied non-destructively to copies of the raw traces in the order listed:");
                var steps = interp.Pipeline.EnabledSteps.ToList();
                doc.Bullets(steps.Count == 0 ? ["No processing (raw traces)."] : steps.Select((s, i) => $"{i + 1}. {s.DisplayName}" + (s.Parameters.Count > 0 ? " — " + string.Join(", ", s.Parameters.Select(p => $"{p.Key} = {p.Value}")) : "")));
                foreach (var advice in PipelineRunner.Advice(interp.Pipeline)) doc.Callout("Processing note", advice, warning: true);
                var st = interp.ShotTime;
                doc.Paragraph(st.Method switch
                {
                    ShotTimeMethod.None => "Shot times: the recorded trigger time was used without correction.",
                    ShotTimeMethod.Manual => "Shot times: corrected with user-entered values per shot (table below).",
                    _ => $"Shot times: corrected from a reference (shot) geophone picked on the unprocessed trace{(st.ReferenceVelocity is { } v ? $"; expected arrival at {v:0} m/s from its distance to the source" : ", assumed at the source")}.",
                });
                if (st.Method != ShotTimeMethod.None) doc.Table(ReportTables.ShotTimes(line));
                var origins = line.Observations.All.GroupBy(o => o.Pick.Origin).ToDictionary(g => g.Key, g => g.Count());
                doc.Paragraph($"First arrivals: {line.Observations.All.Length} picks — {origins.GetValueOrDefault(ArrivalOrigin.Manual)} manual, " +
                              $"{origins.GetValueOrDefault(ArrivalOrigin.AcceptedSuggestion)} automatic and accepted, {origins.GetValueOrDefault(ArrivalOrigin.Automatic)} automatic not individually reviewed, " +
                              $"{origins.GetValueOrDefault(ArrivalOrigin.Correlation)} transferred by cross-correlation; {line.Observations.All.Count(o => !o.Included)} excluded. " +
                              $"Automatic picker: {interp.Picker.Algorithm}. Confidence combines signal-to-noise ratio, onset sharpness, consistency with neighbouring traces and agreement between independent pickers.");
            }

            if (sections.Contains(ReportSection.ShotGathers))
            {
                doc.Heading($"Shot gathers — {line.Line.Name}", 1);
                var gathers = line.Line.Gathers.Where(g => g.Included).OrderBy(g => g.ShotX).ToList();
                var chosen = r.IncludeAllGathers ? gathers : Representative(gathers);
                foreach (var g in chosen)
                {
                    try
                    {
                        var pg = input.Gather(line.Line.Id, g.Id);
                        var model = PlotModels.Gather(pg, interp, line, null, $"{line.Line.Name}: shot {g.Label}");
                        doc.Figure((c, b) => GatherRenderer.Render(c, b, model, theme), 1.45f,
                            $"Shot gather {g.Label} ({(interp.Display.ShowProcessed ? "processed" : "unprocessed")}). Red marks: first-break picks (filled = manual or accepted, hollow = automatic); coloured lines: segment fits; dashed: tomography prediction.");
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or Formats.SeismicFormatException or Persistence.ProjectFileException)
                    {
                        doc.Callout("Gather not available", $"Shot {g.Label} could not be drawn: {ex.Message}", warning: true);
                    }
                }
                if (!r.IncludeAllGathers && gathers.Count > chosen.Count)
                    doc.Paragraph($"{chosen.Count} representative gathers of {gathers.Count} are shown.", 8.5f, italic: true, color: style.Muted);
            }

            if (sections.Contains(ReportSection.TravelTimes))
            {
                doc.Heading($"Travel-time curves — {line.Line.Name}", 1);
                var tt = PlotModels.TravelTimes(line);
                doc.Figure((c, b) => TravelTimeRenderer.Render(c, b, tt, theme), 1.5f,
                    "Corrected first-arrival times. Marker shape = assigned layer; crosses = excluded picks; triangles/squares = QC review/problem; solid lines = segment fits; dashed = tomography prediction; vertical red bars = reciprocal-time differences.");
                doc.Table(ReportTables.Segments(line, u), 80, 7);
            }

            if (sections.Contains(ReportSection.LayeredModel))
            {
                doc.Heading($"Layered interpretation — {line.Line.Name}", 1);
                if (line.Layered.HasValue)
                {
                    var m = line.Layered.Value!;
                    doc.Paragraph($"Method: {ReportTables.MethodName(m.Method)} with {m.LayerCount} layers. Depths are below the local ground surface; elevations = ground elevation − depth.");
                    doc.Table(ReportTables.Layers(line, u)!);
                    var section = PlotModels.Section(line, SectionKind.Layered, state.ActiveScenario.Settings, ve: 2);
                    if (section is not null)
                        doc.Figure((c, b) => SectionRenderer.Render(c, b, section, theme), 2.2f, "Layered velocity model (vertical exaggeration 2×). Interfaces beyond the positions where they were determined are held at their nearest depth for display only.");
                    doc.Table(ReportTables.Interfaces(line, u)!, 120, 7);
                    foreach (var w in m.Warnings.Take(25)) doc.Callout("Interpretation note", w, warning: true);
                }
                else
                {
                    doc.Callout("Layered interpretation not available", line.Layered.Reason ?? "", warning: true);
                }
            }

            if (sections.Contains(ReportSection.Tomography) && line.Tomography is { } t)
            {
                doc.Heading($"Travel-time tomography — {line.Line.Name}", 1);
                var ts = t.Settings;
                doc.KeyValues(
                [
                    ("Method", "Regularised Gauss–Newton inversion of first-arrival times, shortest-path ray tracing, log-slowness parameters, smoothness constraint, robust data weighting " + (ts.RobustDataWeighting ? "on" : "off")),
                    ("Grid", $"{t.Model.Grid.Nx} × {t.Model.Grid.Nz} cells of {t.Model.Grid.Dx:0.##} × {t.Model.Grid.Dz:0.##} m"),
                    ("Starting model", ts.StartingModel.ToString()),
                    ("Regularisation", $"λ = {ts.Smoothing:0.##} (decay {ts.SmoothingDecay:0.##}), horizontal/vertical smoothing ratio {ts.HorizontalSmoothingRatio:0.##}"),
                    ("Pick uncertainty", $"{ts.PickErrorAbsolute * 1000:0.##} ms + {ts.PickErrorRelative * 100:0.#} % of travel time"),
                    ("Result", $"{t.Iterations.Length - 1} iterations, RMS {t.FinalRms * 1000:0.00} ms, χ² {t.FinalChi2:0.00}. {t.StopReason}"),
                ]);
                if (line.TomographyStale) doc.Callout("Out of date", "Picks, geometry or settings changed after this tomogram was computed; it does not reflect the current interpretation.", warning: true);
                foreach (var n in t.Notes) doc.Paragraph("Note: " + n, 8.5f, italic: true, color: style.Muted);
                var section = PlotModels.Section(line, SectionKind.Tomography, state.ActiveScenario.Settings, ve: 2);
                if (section is not null)
                    doc.Figure((c, b) => SectionRenderer.Render(c, b, section, theme), 2.0f, "Tomographic velocity model (vertical exaggeration 2×). Hatched cells lie below the depth of investigation (estimated from the ray coverage of each column) and are not interpreted. Dashed lines: layered-interpretation refractors for comparison.");
                var coverage = PlotModels.Section(line, SectionKind.RayCoverage, state.ActiveScenario.Settings, ve: 2);
                if (coverage is not null)
                    doc.Figure((c, b) => SectionRenderer.Render(c, b, coverage, theme), 2.4f, "Ray coverage (total ray length per cell). Low coverage means low reliability.");
                var conv = PlotModels.Convergence(t);
                doc.Figure((c, b) => XyRenderer.Render(c, b, conv, theme), 2.6f, "Convergence of the inversion (RMS travel-time residual per iteration).", 0.7f);
            }
        }

        if (sections.Contains(ReportSection.VelocityProfiles) || sections.Contains(ReportSection.EngineeringParameters) || sections.Contains(ReportSection.Vs30))
        {
            var eng = state.ActiveScenario.Settings.Engineering;
            var p = eng.PLine is { } pid ? a.Line(pid) : null;
            var s = eng.ShLine is { } sid ? a.Line(sid) : null;
            if (sections.Contains(ReportSection.VelocityProfiles))
            {
                doc.Heading("Velocity profiles", 1);
                var positions = (eng.ProfilePositions.IsDefaultOrEmpty ? state.ActiveScenario.Settings.Vs30.Positions : eng.ProfilePositions).ToList();
                if (positions.Count == 0 || (p is null && s is null))
                    doc.Paragraph("No profile positions or lines were selected in the engineering settings.");
                foreach (var x in positions)
                {
                    var list = new List<(string, Computed<VelocityProfile>)>();
                    if (p is not null) list.Add(("Vp", p.ProfileAt(eng.Source, x)));
                    if (s is not null) list.Add(("Vs", s.ProfileAt(eng.Source, x)));
                    var model = PlotModels.Profiles(x, list, u, 35);
                    doc.Figure((c, b) => XyRenderer.Render(c, b, model, theme), 1.3f, $"1-D velocity profiles at x = {u.Format(x, QuantityKind.Length, 1)} from the {eng.Source.ToString().ToLowerInvariant()} model; dashed lines mark the depth constrained by data.", 0.6f);
                    foreach (var (name, prof) in list.Where(l => !l.Item2.HasValue)) doc.Paragraph($"{name}: {prof.Reason}", 8.5f, italic: true, color: style.Muted);
                }
            }
            if (sections.Contains(ReportSection.Vs30))
            {
                var vsSettings = state.ActiveScenario.Settings.Vs30;
                var vsLabel = vsSettings.Label;
                var zText = vsSettings.TargetDepth.ToString("0.#", CultureInfo.InvariantCulture);
                doc.Heading($"{vsLabel} and time-averaged shear-wave velocity", 1);
                doc.Paragraph($"{vsLabel} = {zText} m / Σ(hᵢ/Vsᵢ) over the upper {zText} m (time-averaged shear-wave velocity). It is computed only where the shear-wave model is constrained by data to {zText} m, unless extrapolation was explicitly enabled.");
                var table = ReportTables.Vs30(a, u, vsSettings.TargetDepth);
                if (table is null) doc.Callout($"{vsLabel} not available", a.Engineering.Vs30.Reason ?? "", warning: true);
                else
                {
                    doc.Table(table);
                    if (a.Engineering.Vs30.Value!.Any(e => e.Vs30.HasValue && e.Vs30.Value!.Extrapolated))
                        doc.Callout("EXTRAPOLATED values", $"Some {vsLabel} values include an extrapolation below the depth constrained by the data (constant velocity equal to the deepest constrained value). They are labelled EXTRAPOLATED and should be used with caution.", warning: true);
                    if (a.Engineering.Vs30.Value!.Any(e => !e.Vs30.HasValue))
                        doc.Callout("Insufficient depth", $"Insufficient measured depth for direct {vsLabel} calculation at some positions (see table). No value is reported there.", warning: true);
                }
            }
            if (sections.Contains(ReportSection.EngineeringParameters))
            {
                doc.Heading("Engineering parameters", 1);
                var density = state.ActiveScenario.Settings.Density;
                doc.Paragraph(density.Source == DensitySource.Measured
                    ? $"Density: values entered by the user for {density.Intervals.Length} depth interval(s)." + (density.Notes is { } dn ? " " + dn : "")
                    : $"Density: ESTIMATED from Vp with {density.Estimator} (results marked ESTIMATED).");
                var table = ReportTables.Engineering(a, u);
                if (table is null) doc.Callout("Engineering parameters not available", a.Engineering.Profiles.Reason ?? "", warning: true);
                else doc.Table(table, 200, 6.8f);
                if (p?.Tomography is { } pt && s?.Tomography is { } stomo)
                {
                    var sectionsResult = PropertySectionCalculator.Compute(pt, stomo, density);
                    if (sectionsResult.HasValue)
                    {
                        var ps = PlotModels.PropertySection(sectionsResult.Value!, "Poisson", p.Line, u, "Poisson's ratio (from P and SH tomograms)");
                        doc.Figure((c, b) => SectionRenderer.Render(c, b, ps, theme), 2.2f, "Poisson's ratio where both tomograms are ray-constrained.");
                    }
                }
            }
        }

        if (sections.Contains(ReportSection.QualityControl))
        {
            doc.Heading("Quality control", 1);
            doc.Callout($"Analysis quality: {VerdictText(a.Qc.Verdict)}", string.Join(" ", a.Qc.Reasons), a.Qc.Verdict != QualityVerdict.Good);
            doc.Table(new DataTable("Quality summary", ["Item", "Value", "Status", "Detail"],
                [.. a.Qc.Cards.Select(c => (IReadOnlyList<Cell>)[c.Title, c.Value, ReportTables.LevelText(c.Level), c.Detail])]), null, 8);
            doc.Table(ReportTables.Qc(a), 150, 7, [1.2f, 2f, 1.5f, 6f]);
        }

        if (sections.Contains(ReportSection.ProcessingNotes) && !string.IsNullOrWhiteSpace(r.ProcessingNotes))
        {
            doc.Heading("Processing notes", 1);
            doc.Paragraph(r.ProcessingNotes!);
        }

        if (sections.Contains(ReportSection.Warnings))
        {
            doc.Heading("Warnings and limitations", 1);
            var warnings = CollectWarnings(state, a).ToList();
            if (warnings.Count == 0) doc.Paragraph("No analysis warnings were raised.");
            else doc.Bullets(warnings);
            doc.Callout("Limitations", Limitations, warning: false);
        }

        if (sections.Contains(ReportSection.Conclusions))
        {
            doc.Heading("Conclusions", 1);
            doc.Paragraph(string.IsNullOrWhiteSpace(r.Conclusions) ? "No conclusions were entered." : r.Conclusions!);
        }

        if (sections.Contains(ReportSection.PickTableAppendix))
        {
            doc.Heading("Appendix A — Pick tables", 1);
            doc.Paragraph("Origin: Auto = automatic pick not reviewed, Accepted = automatic pick accepted after review, Manual = picked or moved by the " +
                          "interpreter, Correlation = transferred by cross-correlation. Corrected time = raw time + shot correction + trace shift. " +
                          "Every column of the pick record (algorithm, timing shift, exclusion reason) is in the XLSX/CSV export.", 8f);
            var remaining = options.MaxAppendixPicks;
            foreach (var line in a.Lines)
            {
                doc.Heading($"Picks — {line.Line.Name}", 2);
                foreach (var g in line.Line.Gathers.OrderBy(g => g.ShotX))
                {
                    var table = ReportTables.ShotPicks(line, g.Id, u);
                    if (table.Rows.Count == 0) continue;
                    if (remaining <= 0)
                    {
                        doc.Paragraph($"Further picks are omitted from the report (limit {options.MaxAppendixPicks}); see the XLSX/CSV export.", 8f, italic: true);
                        break;
                    }
                    doc.Table(table, remaining, 6.8f, [0.5f, 0.75f, 0.85f, 0.9f, 1.05f, 0.65f, 0.95f, 1.35f, 0.55f, 0.6f, 2.85f]);
                    remaining -= table.Rows.Count;
                }
            }
        }
        if (sections.Contains(ReportSection.HistoryAppendix))
        {
            doc.Heading("Appendix B — Processing history", 1);
            doc.Table(ReportTables.History(input.History), null, 6.5f, [2.2f, 1.1f, 4f, 1.1f, 0.9f, 4.5f]);
        }

        return doc.Render(title, r.PreparedBy ?? options.UserName, state.Metadata.Name);
    }

    private static void Cover(PdfLayout doc, ProjectState state, ReportOptions options, ReportStyle style)
    {
        var r = state.Report;
        doc.Undecorated.Add(doc.PageCount);
        doc.Overlay(c =>
        {
            using var band = new SKPaint { Color = style.Accent };
            c.DrawRect(new SKRect(0, 0, doc.PageWidth, 8), band);
            var y = doc.Margin + 20;
            if (r.Branding.LogoPngBase64 is { } logo)
            {
                try
                {
                    using var bitmap = SKBitmap.Decode(Convert.FromBase64String(logo));
                    if (bitmap is not null)
                    {
                        var h = 56f;
                        var w = h * bitmap.Width / Math.Max(1, bitmap.Height);
                        using var image = SKImage.FromBitmap(bitmap);
                        c.DrawImage(image, new SKRect(doc.ContentLeft, y, doc.ContentLeft + Math.Min(w, 220), y + h));
                    }
                }
                catch (FormatException)
                {
                    // An invalid logo is ignored; the company name is still printed.
                }
            }
            using var company = doc.Font(13, bold: true);
            using var text = new SKPaint { Color = style.Text, IsAntialias = true };
            using var muted = new SKPaint { Color = style.Muted, IsAntialias = true };
            c.DrawText(r.Branding.CompanyName, new SKPoint(doc.ContentRight, y + 16), SKTextAlign.Right, company, text);
            using var small = doc.Font(8.5f);
            var cy = y + 30;
            foreach (var line in new[] { r.Branding.Address, r.Branding.Phone, r.Branding.Email, r.Branding.Website }.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                c.DrawText(line!, new SKPoint(doc.ContentRight, cy), SKTextAlign.Right, small, muted);
                cy += 11;
            }
            using var titleFont = doc.Font(26, bold: true);
            using var accent = new SKPaint { Color = style.Accent, IsAntialias = true };
            var ty = doc.PageHeight * 0.36f;
            foreach (var l in PdfLayout.Wrap(r.Title, titleFont, doc.ContentWidth))
            {
                c.DrawText(l, new SKPoint(doc.ContentLeft, ty), SKTextAlign.Left, titleFont, accent);
                ty += 32;
            }
            using var sub = doc.Font(14);
            if (!string.IsNullOrWhiteSpace(r.Subtitle)) { c.DrawText(r.Subtitle, new SKPoint(doc.ContentLeft, ty + 4), SKTextAlign.Left, sub, text); ty += 26; }
            using var body = doc.Font(11);
            var m = state.Metadata;
            ty += 30;
            foreach (var (k, v) in new (string, string?)[]
            {
                ("Project", m.Name), ("Site", m.SiteName), ("Client", m.Client), ("Report number", r.ReportNumber),
                ("Survey date", m.SurveyDate?.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)),
                ("Report date", options.GeneratedAt.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)),
                ("Prepared by", r.PreparedBy ?? r.Branding.EngineerName), ("Checked by", r.CheckedBy),
            })
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                c.DrawText(k, new SKPoint(doc.ContentLeft, ty), SKTextAlign.Left, body, muted);
                c.DrawText(v, new SKPoint(doc.ContentLeft + 130, ty), SKTextAlign.Left, body, text);
                ty += 18;
            }
            using var foot = doc.Font(8);
            c.DrawText($"Prepared with Refraction Studio {options.AppVersion}", new SKPoint(doc.ContentLeft, doc.PageHeight - doc.Margin), SKTextAlign.Left, foot, muted);
        });
        doc.NewPage();
    }

    private static void SummaryBullets(PdfLayout doc, ProjectState state, AnalysisResult a, UnitFormatter u)
    {
        var items = new List<string>();
        foreach (var l in a.Lines)
        {
            var picks = l.Observations.Included.Count();
            var text = $"{l.Line.Name}: {l.Line.Gathers.Count(g => g.Included)} shots, {picks} first arrivals used.";
            if (l.Layered.HasValue)
            {
                var m = l.Layered.Value!;
                text += $" {ReportTables.MethodName(m.Method)}: " + string.Join(", ", m.Layers.Select(x => $"V{x.Layer} = {u.Format(x.Velocity, QuantityKind.Velocity)}")) + ".";
                foreach (var r in m.Refractors.Where(r => !r.Points.IsDefaultOrEmpty))
                    text += $" Refractor {r.Refractor} at {u.Format(r.Points.Min(p => p.Depth), QuantityKind.Depth, 1)}–{u.Format(r.Points.Max(p => p.Depth), QuantityKind.Depth, 1)} depth.";
            }
            if (l.Tomography is { } t) text += $" Tomography RMS {t.FinalRms * 1000:0.00} ms{(l.TomographyStale ? " (out of date)" : "")}.";
            items.Add(text);
        }
        if (a.Engineering.Vs30.HasValue)
        {
            foreach (var e in a.Engineering.Vs30.Value!)
                items.Add(e.Vs30.HasValue
                    ? $"{state.ActiveScenario.Settings.Vs30.Label} at x = {u.Format(e.X, QuantityKind.Length, 1)}: {u.Format(e.Vs30.Value!.Velocity, QuantityKind.Velocity)} ({e.Vs30.Value.Qualifier})."
                    : $"{state.ActiveScenario.Settings.Vs30.Label} at x = {u.Format(e.X, QuantityKind.Length, 1)}: not reported — {e.Vs30.Reason}");
        }
        doc.Bullets(items);
    }

    public static IEnumerable<string> CollectWarnings(ProjectState state, AnalysisResult a)
    {
        foreach (var l in a.Lines)
        {
            if (!l.Layered.HasValue) yield return $"{l.Line.Name}: layered interpretation not available — {l.Layered.Reason}";
            else foreach (var w in l.Layered.Value!.Warnings) yield return $"{l.Line.Name}: {w}";
            if (l.TomographyStale) yield return $"{l.Line.Name}: the tomogram is out of date.";
            foreach (var n in l.Tomography?.Notes ?? []) yield return $"{l.Line.Name}: {n}";
            var interp = state.Interpretation(l.Line.Id);
            foreach (var adv in PipelineRunner.Advice(interp.Pipeline)) yield return $"{l.Line.Name}: {adv}";
            var unreviewed = l.Observations.All.Count(o => o.Pick.Origin == ArrivalOrigin.Automatic && o.Included);
            if (unreviewed > 0) yield return $"{l.Line.Name}: {unreviewed} automatic pick(s) were not individually reviewed.";
            if (!interp.Segments.Gathers.Values.Any(g => g.UserEdited)) yield return $"{l.Line.Name}: the layer assignment of arrivals is the automatic suggestion and was not edited.";
        }
        if (state.ActiveScenario.Settings.Density.Source == DensitySource.Estimated) yield return "Density was ESTIMATED from Vp by an empirical relation; moduli are approximate.";
        if (state.ActiveScenario.Settings.Vs30.AllowExtrapolation) yield return "Vs30 extrapolation below the constrained depth was enabled by the user.";
        foreach (var f in a.Qc.Flags.Where(f => f.Level == QcLevel.Problem && f.Key is null).Take(20)) yield return f.Message;
    }

    public static string VerdictText(QualityVerdict v) => v switch
    {
        QualityVerdict.Good => "Good",
        QualityVerdict.ReviewRecommended => "Review recommended",
        _ => "Incomplete",
    };

    private static List<ShotGather> Representative(List<ShotGather> gathers)
    {
        if (gathers.Count <= 3) return gathers;
        return [gathers[0], gathers[gathers.Count / 2], gathers[^1]];
    }

    private static double? LineLength(SurveyLine l)
    {
        var xs = l.Spreads.SelectMany(s => s.ReceiverChannels.Select(c => s.PositionOf(c.Channel))).Where(x => x is not null).Select(x => x!.Value).ToList();
        return xs.Count > 1 ? xs.Max() - xs.Min() : null;
    }
}
