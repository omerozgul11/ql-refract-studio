using System.Globalization;
using RefractionStudio.Analysis;
using RefractionStudio.Analysis.Engineering;
using RefractionStudio.Analysis.Quality;
using RefractionStudio.Domain;
using RefractionStudio.Domain.Units;

namespace RefractionStudio.Reporting;

/// <summary>Builds the result tables shown in the UI, exported to CSV/XLSX and printed in reports.</summary>
public static class ReportTables
{
    private static string L(UnitFormatter u) => u.Symbol(QuantityKind.Length);
    private static string V(UnitFormatter u) => u.Symbol(QuantityKind.Velocity);

    /// <summary>Pick table of a line: geometry, raw/correction/corrected times, layer, confidence, origin.</summary>
    public static DataTable Picks(LineAnalysis line, UnitFormatter u)
    {
        var labels = line.Line.Gathers.ToDictionary(g => g.Id, g => g.Label);
        var cols = new[]
        {
            "Shot", $"Shot x ({L(u)})", "Channel", $"Receiver x ({L(u)})", $"Offset ({L(u)})", "Raw pick (ms)", "Shot correction (ms)", "Timing shift (ms)",
            "Corrected time (ms)", "Layer", "Origin", "Algorithm", "Confidence", "Confidence score", "SNR", "Included", "Reason excluded", "Comment",
        };
        var rows = line.Observations.All.OrderBy(o => o.ShotX).ThenBy(o => o.ReceiverX).Select(o => (IReadOnlyList<Cell>)
        [
            labels.GetValueOrDefault(o.Gather, ""), Cell.Num(u.ToDisplay(o.ShotX, QuantityKind.Length), "0.00"), Cell.Num(o.Channel, "0"),
            Cell.Num(u.ToDisplay(o.ReceiverX, QuantityKind.Length), "0.00"), Cell.Num(u.ToDisplay(o.Offset, QuantityKind.Length), "0.00"),
            Cell.Num(o.RawTime * 1000, "0.000"), Cell.Num(double.IsNaN(o.ShotCorrection) ? null : o.ShotCorrection * 1000, "0.000"), Cell.Num(o.TimeShift * 1000, "0.000"),
            Cell.Num(o.CorrectedTime * 1000, "0.000"), Cell.Num(o.Layer, "0"), OriginText(o.Pick.Origin), o.Pick.Algorithm ?? "",
            o.Pick.Confidence?.Level.ToString() ?? "", Cell.Num(o.Pick.Confidence?.Score, "0.00"), Cell.Num(o.Pick.Snr, "0.0"),
            o.Included ? "Yes" : "No", o.ExclusionReason ?? "", o.Pick.Comment ?? "",
        ]).ToList();
        return new DataTable($"Picks — {line.Line.Name}", cols, rows, "Raw pick: record time as recorded. Corrected time = raw + shot correction + timing shift.");
    }

    /// <summary>
    /// Compact pick table of one shot for the report appendix (the full table with every column is in the
    /// XLSX/CSV exports). The shot position and correction are constant within a shot and given in the title.
    /// </summary>
    public static DataTable ShotPicks(LineAnalysis line, GatherId gather, UnitFormatter u)
    {
        var obs = line.Observations.All.Where(o => o.Gather == gather).OrderBy(o => o.ReceiverX).ToList();
        var label = line.Line.Gathers.FirstOrDefault(g => g.Id == gather)?.Label ?? "";
        var first = obs.FirstOrDefault();
        var correction = first is null || double.IsNaN(first.ShotCorrection) ? "not applied" : $"{first.ShotCorrection * 1000:0.000} ms";
        var cols = new[] { "Ch", $"x ({L(u)})", $"Offset ({L(u)})", "Raw (ms)", "Corrected (ms)", "Layer", "Origin", "Confidence", "SNR", "Used", "Note" };
        var rows = obs.Select(o =>
        {
            var note = string.Join(" ", new[]
            {
                Math.Abs(o.TimeShift) > 1e-9 ? $"Trace shift {o.TimeShift * 1000:0.###} ms." : null,
                o.ExclusionReason, o.Pick.Comment,
            }.Where(t => !string.IsNullOrWhiteSpace(t)));
            return (IReadOnlyList<Cell>)
            [
                Cell.Num(o.Channel, "0"), Cell.Num(u.ToDisplay(o.ReceiverX, QuantityKind.Length), "0.0"), Cell.Num(u.ToDisplay(o.Offset, QuantityKind.Length), "0.0"),
                Cell.Num(o.RawTime * 1000, "0.000"), Cell.Num(o.CorrectedTime * 1000, "0.000"), Cell.Num(o.Layer, "0"), ShortOriginText(o.Pick.Origin),
                o.Pick.Confidence is { } c ? $"{c.Level} {c.Score:0.00}" : "", Cell.Num(o.Pick.Snr, "0.0"), o.Included ? "Yes" : "No", note,
            ];
        }).ToList();
        return new DataTable($"Shot {label} — shot correction {correction}", cols, rows);
    }

    public static string ShortOriginText(ArrivalOrigin o) => o switch
    {
        ArrivalOrigin.Manual => "Manual",
        ArrivalOrigin.Automatic => "Auto",
        ArrivalOrigin.AcceptedSuggestion => "Accepted",
        _ => "Correlation",
    };

    public static string OriginText(ArrivalOrigin o) => o switch
    {
        ArrivalOrigin.Manual => "Manual",
        ArrivalOrigin.Automatic => "Automatic (not reviewed)",
        ArrivalOrigin.AcceptedSuggestion => "Automatic, accepted",
        _ => "Cross-correlation",
    };

    /// <summary>Layer velocities of the layered interpretation.</summary>
    public static DataTable? Layers(LineAnalysis line, UnitFormatter u)
    {
        if (!line.Layered.HasValue) return null;
        var m = line.Layered.Value!;
        var rows = m.Layers.Select(l => (IReadOnlyList<Cell>)
            [Cell.Num(l.Layer, "0"), Cell.Num(u.ToDisplay(l.Velocity, QuantityKind.Velocity), "0"), Cell.Num(l.StdError is { } e ? u.ToDisplay(e, QuantityKind.Velocity) : null, "0"), l.Basis]).ToList();
        return new DataTable($"Layer velocities — {line.Line.Name} ({MethodName(m.Method)})", ["Layer", $"Velocity ({V(u)})", $"Std. error ({V(u)})", "Basis"], rows);
    }

    /// <summary>Refractor depths along the line.</summary>
    public static DataTable? Interfaces(LineAnalysis line, UnitFormatter u)
    {
        if (!line.Layered.HasValue) return null;
        var m = line.Layered.Value!;
        var rows = new List<IReadOnlyList<Cell>>();
        foreach (var r in m.Refractors)
            foreach (var p in r.Points)
                rows.Add([Cell.Num(r.Refractor, "0"), Cell.Num(u.ToDisplay(p.X, QuantityKind.Length), "0.00"), Cell.Num(u.ToDisplay(p.Depth, QuantityKind.Depth), "0.00"),
                    Cell.Num(u.ToDisplay(p.Elevation, QuantityKind.Elevation), "0.00"), Cell.Num(p.DepthStdError is { } e ? u.ToDisplay(e, QuantityKind.Depth) : null, "0.00"),
                    Cell.Num(p.DelayTime * 1000, "0.000"), Cell.Num(p.Observations, "0"), p.Questionable ? "Review" : ""]);
        return new DataTable($"Refractor depths — {line.Line.Name}", ["Refractor", $"x ({L(u)})", $"Depth ({L(u)})", $"Elevation ({L(u)})", $"Depth std. error ({L(u)})", "Delay time (ms)", "Arrivals", "Flag"], rows);
    }

    public static DataTable Segments(LineAnalysis line, UnitFormatter u)
    {
        var labels = line.Line.Gathers.ToDictionary(g => g.Id, g => g.Label);
        var rows = line.Segments.OrderBy(s => labels.GetValueOrDefault(s.Gather)).ThenBy(s => s.Side).ThenBy(s => s.Layer).Select(s => (IReadOnlyList<Cell>)
        [
            labels.GetValueOrDefault(s.Gather, ""), s.Side < 0 ? "Decreasing x" : "Increasing x", Cell.Num(s.Layer, "0"), Cell.Num(s.Count, "0"),
            Cell.Num(double.IsNaN(s.ApparentVelocity) ? null : u.ToDisplay(s.ApparentVelocity, QuantityKind.Velocity), "0"),
            Cell.Num(s.InterceptTime * 1000, "0.00"), Cell.Num(s.Fit?.RSquared, "0.0000"), Cell.Num(s.Fit is { } f ? f.RmsResidual * 1000 : null, "0.000"),
            Cell.Num(u.ToDisplay(s.MinOffset, QuantityKind.Length), "0.0"), Cell.Num(u.ToDisplay(s.MaxOffset, QuantityKind.Length), "0.0"),
        ]).ToList();
        return new DataTable($"Travel-time segments — {line.Line.Name}",
            ["Shot", "Direction", "Layer", "Arrivals", $"Apparent velocity ({V(u)})", "Intercept (ms)", "R²", "RMS (ms)", $"Min offset ({L(u)})", $"Max offset ({L(u)})"], rows);
    }

    public static DataTable ShotTimes(LineAnalysis line)
    {
        var labels = line.Line.Gathers.ToDictionary(g => g.Id, g => g.Label);
        var rows = line.Observations.ShotTimes.Select(s => (IReadOnlyList<Cell>)
        [
            labels.GetValueOrDefault(s.Gather, ""), s.Method.ToString(), Cell.Num(s.ReferenceTime * 1000, "0.000"), Cell.Num(s.ExpectedReferenceTime * 1000, "0.000"),
            s.Correction.HasValue ? Cell.Num(s.Correction.Value * 1000, "0.000") : (Cell)"n/a", s.Correction.HasValue ? string.Join(" ", s.Correction.Notes) : s.Correction.Reason,
        ]).ToList();
        return new DataTable($"Shot-time corrections — {line.Line.Name}", ["Shot", "Method", "Reference pick (ms)", "Expected (ms)", "Correction (ms)", "Note"], rows);
    }

    public static DataTable? Vs30(AnalysisResult a, UnitFormatter u, double targetDepth = 30)
    {
        var label = Vs30Settings.LabelFor(targetDepth);
        if (!a.Engineering.Vs30.HasValue) return null;
        var rows = new List<IReadOnlyList<Cell>>();
        foreach (var e in a.Engineering.Vs30.Value!)
        {
            Add(e.Vs30, label);
            foreach (var c in e.Custom) Add(c, c.HasValue ? $"Vs {c.Value!.Top:0.#}–{c.Value.Bottom:0.#} m" : "Custom interval");
            void Add(Computed<EquivalentVelocity> v, string name) => rows.Add(v.HasValue
                ? [Cell.Num(u.ToDisplay(e.X, QuantityKind.Length), "0.00"), name, Cell.Num(u.ToDisplay(v.Value!.Velocity, QuantityKind.Velocity), "0"),
                    Cell.Num(v.Value.TravelTime * 1000, "0.00"), Cell.Num(u.ToDisplay(v.Value.ConstrainedDepth, QuantityKind.Depth), "0.0"), v.Value.Qualifier, v.Value.Source]
                : [Cell.Num(u.ToDisplay(e.X, QuantityKind.Length), "0.00"), name, "Not available", "", "", v.Reason, ""]);
        }
        return new DataTable("Time-averaged shear-wave velocity", [$"x ({L(u)})", "Quantity", $"Velocity ({V(u)})", "Vertical travel time (ms)", $"Constrained depth ({L(u)})", "Status", "Model"], rows,
            $"{label} = {targetDepth:0.#} m / Σ(hᵢ/Vsᵢ). Values are not extrapolated unless the user enabled extrapolation; extrapolated values are labelled EXTRAPOLATED.");
    }

    public static DataTable? Engineering(AnalysisResult a, UnitFormatter u)
    {
        if (!a.Engineering.Profiles.HasValue) return null;
        var mod = u.Symbol(QuantityKind.Modulus);
        var rows = new List<IReadOnlyList<Cell>>();
        foreach (var p in a.Engineering.Profiles.Value!)
            foreach (var r in p.Rows)
            {
                var e = r.Properties;
                string Est() => e?.Density.HasValue == true && e.Density.Value!.Estimated ? "ESTIMATED density" : "";
                rows.Add([
                    Cell.Num(u.ToDisplay(p.X, QuantityKind.Length), "0.00"), Cell.Num(u.ToDisplay(r.Depth, QuantityKind.Depth), "0.0"),
                    r.Vp.HasValue ? Cell.Num(u.ToDisplay(r.Vp.Value, QuantityKind.Velocity), "0") : (Cell)"—",
                    r.Vs.HasValue ? Cell.Num(u.ToDisplay(r.Vs.Value, QuantityKind.Velocity), "0") : (Cell)"—",
                    e?.VpVsRatio.HasValue == true ? Cell.Num(e.VpVsRatio.Value, "0.00") : (Cell)"—",
                    e?.PoissonRatio.HasValue == true ? Cell.Num(e.PoissonRatio.Value, "0.000") : (Cell)"—",
                    e?.Density.HasValue == true ? Cell.Num(u.ToDisplay(e.Density.Value!.Value, QuantityKind.Density), "0") : (Cell)"—",
                    e?.ShearModulus.HasValue == true ? Cell.Num(u.ToDisplay(e.ShearModulus.Value, QuantityKind.Modulus), "0.0") : (Cell)"—",
                    e?.YoungsModulus.HasValue == true ? Cell.Num(u.ToDisplay(e.YoungsModulus.Value, QuantityKind.Modulus), "0.0") : (Cell)"—",
                    e?.BulkModulus.HasValue == true ? Cell.Num(u.ToDisplay(e.BulkModulus.Value, QuantityKind.Modulus), "0.0") : (Cell)"—",
                    string.Join(" ", (e?.Flags ?? []).Append(Est()).Where(s => s.Length > 0)),
                ]);
            }
        return new DataTable("Engineering properties",
            [$"x ({L(u)})", $"Depth ({L(u)})", $"Vp ({V(u)})", $"Vs ({V(u)})", "Vp/Vs", "Poisson's ratio", $"Density ({u.Symbol(QuantityKind.Density)})", $"G ({mod})", $"E ({mod})", $"K ({mod})", "Flags"], rows,
            "ν = (Vp² − 2Vs²)/[2(Vp² − Vs²)], G = ρVs², E = 2G(1+ν), K = ρ(Vp² − 4Vs²/3). Isotropic linear elasticity at small strain.");
    }

    public static DataTable Qc(AnalysisResult a)
    {
        var lineNames = a.Lines.ToDictionary(l => l.Line.Id, l => l.Line.Name);
        var rows = a.Qc.Flags.OrderByDescending(f => f.Level).Select(f => (IReadOnlyList<Cell>)
            [LevelText(f.Level), f.Title, f.Line is { } l ? lineNames.GetValueOrDefault(l, "") : "", f.Message]).ToList();
        return new DataTable("Quality control findings", ["Status", "Finding", "Line", "Details"], rows);
    }

    public static string LevelText(QcLevel l) => l switch { QcLevel.Ok => "OK", QcLevel.Review => "Review", _ => "Likely problem" };

    public static DataTable History(IReadOnlyList<HistoryEntry> history)
    {
        var rows = history.Select(h => (IReadOnlyList<Cell>)
        [
            h.Timestamp.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), h.Kind.ToString(), h.Description, h.User, h.AppVersion,
            string.Join("; ", h.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")),
        ]).ToList();
        return new DataTable("Processing history", ["Time", "Category", "Change", "User", "Version", "Parameters"], rows);
    }

    public static DataTable SourceFiles(ProjectState state)
    {
        var rows = state.Survey.SourceFiles.Select(f => (IReadOnlyList<Cell>)
            [f.FileName, f.FormatId, Cell.Num(f.Length, "0"), f.Sha256, f.ImportedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)]).ToList();
        return new DataTable("Raw data files", ["File", "Format", "Bytes", "SHA-256", "Imported"], rows);
    }

    public static string MethodName(LayeredMethod m) => m switch
    {
        LayeredMethod.InterceptTime => "Intercept-time",
        LayeredMethod.PlusMinus => "Plus–minus (Hagedoorn)",
        LayeredMethod.Grm => "Generalized reciprocal method (Palmer)",
        _ => "Delay-time (time-term) least squares",
    };
}
