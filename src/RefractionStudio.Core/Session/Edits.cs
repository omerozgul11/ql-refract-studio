using System.Globalization;
using RefractionStudio.Analysis.Picking;
using RefractionStudio.Domain;

namespace RefractionStudio.Core.Session;

/// <summary>Factory of all undoable project edits. Each edit is a pure state transformation with an audit record.</summary>
public static class Edits
{
    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Ms(double seconds) => (seconds * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " ms";

    // ───────────────────────────── Project ─────────────────────────────

    public static ProjectEdit SetMetadata(ProjectMetadata metadata) => ProjectEdit.Create(HistoryKind.Project, "project.metadata",
        "Edit project information", s => s with { Metadata = metadata }, ("name", metadata.Name));

    public static ProjectEdit SetReport(ReportSettings report) => ProjectEdit.Create(HistoryKind.Report, "report.settings",
        "Edit report settings", s => s with { Report = report }, ("title", report.Title));

    // ───────────────────────────── Geometry ─────────────────────────────

    public static ProjectEdit SetTopography(LineId line, Topography topography) => ProjectEdit.Create(HistoryKind.Geometry, "geometry.topography",
        topography.IsFlat ? "Set flat topography" : $"Set topography ({topography.Points.Length} points)",
        s => s.WithLine(s.Survey.Line(line)! with { Topography = topography }), ("line", line.ToString()), ("points", topography.Points.Length.ToString(CultureInfo.InvariantCulture)));

    public static ProjectEdit UpdateLine(SurveyLine updated, string description) => ProjectEdit.Create(HistoryKind.Geometry, "geometry.line",
        description, s => s.WithLine(updated), ("line", updated.Id.ToString()), ("name", updated.Name));

    public static ProjectEdit SetSpread(LineId line, Spread spread) => ProjectEdit.Create(HistoryKind.Geometry, "geometry.spread",
        $"Edit spread “{spread.Name}” (first receiver {F(spread.FirstReceiverX)} m, spacing {F(spread.Spacing)} m)",
        s =>
        {
            var l = s.Survey.Line(line)!;
            return s.WithLine(l with { Spreads = [.. l.Spreads.Select(x => x.Id == spread.Id ? spread : x)] });
        },
        ("line", line.ToString()), ("firstX", F(spread.FirstReceiverX)), ("spacing", F(spread.Spacing)), ("direction", spread.Direction.ToString()));

    public static ProjectEdit SetGatherGeometry(LineId line, GatherId gather, double shotX, double? shotElevation, double shotDepth)
        => ProjectEdit.Create(HistoryKind.Geometry, "geometry.shot",
            $"Set shot position {F(shotX)} m",
            s => MapGather(s, line, gather, g => g with { ShotX = shotX, ShotElevation = shotElevation, ShotDepth = Math.Max(0, shotDepth), Included = g.Included || double.IsFinite(shotX) }),
            ("gather", gather.ToString()), ("x", F(shotX)), ("elevation", shotElevation is { } e ? F(e) : "topography"), ("depth", F(shotDepth)));

    public static ProjectEdit SetGatherIncluded(LineId line, GatherId gather, bool included, string label) => ProjectEdit.Create(HistoryKind.Geometry,
        "geometry.shot.include", included ? $"Include shot {label}" : $"Exclude shot {label}",
        s => MapGather(s, line, gather, g => g with { Included = included }), ("gather", gather.ToString()), ("included", included.ToString()));

    public static ProjectEdit SetGatherMembers(LineId line, GatherId gather, CombineMode mode, ImmutableArray<GatherMember> members) => ProjectEdit.Create(HistoryKind.Geometry,
        "geometry.shot.members", mode == CombineMode.Stack ? $"Combine {members.Length} records (polarities {string.Join("/", members.Select(m => m.Polarity > 0 ? "+" : "−"))})" : "Use a single record",
        s => MapGather(s, line, gather, g => g with { Combine = mode, Members = members }), ("gather", gather.ToString()), ("mode", mode.ToString()), ("members", members.Length.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Merges gathers into one (stacking or SH pairing). Picks of removed gathers are discarded.</summary>
    public static ProjectEdit MergeGathers(LineId line, GatherId target, IReadOnlyList<GatherId> others, ImmutableArray<GatherMember> members, string description)
        => ProjectEdit.Create(HistoryKind.Geometry, "geometry.shot.merge", description, s =>
        {
            var l = s.Survey.Line(line)!;
            var removed = others.Where(o => o != target).ToHashSet();
            var gathers = l.Gathers.Where(g => !removed.Contains(g.Id)).Select(g => g.Id == target ? g with { Combine = CombineMode.Stack, Members = members } : g).ToImmutableArray();
            var state = s.WithLine(l with { Gathers = gathers });
            return state.WithLineInterpretation(line, i => i with
            {
                Picks = i.Picks.RemoveRange(i.Picks.Keys.Where(k => removed.Contains(k.Gather))),
                Adjustments = i.Adjustments.RemoveRange(i.Adjustments.Keys.Where(k => removed.Contains(k.Gather))),
            });
        }, ("target", target.ToString()), ("merged", string.Join(",", others)));

    public static ProjectEdit SetChannelAssignment(LineId line, SpreadId spread, ChannelAssignment assignment) => ProjectEdit.Create(HistoryKind.Geometry,
        "geometry.channel", $"Channel {assignment.Channel}: {assignment.Role}{(assignment.InvertPolarity ? ", reversed polarity" : "")}{(assignment.XOverride is { } x ? $", x = {F(x)} m" : "")}",
        s =>
        {
            var l = s.Survey.Line(line)!;
            var sp = l.Spread(spread)!;
            var channels = sp.Channels.Any(c => c.Channel == assignment.Channel)
                ? sp.Channels.Select(c => c.Channel == assignment.Channel ? assignment : c).ToImmutableArray()
                : sp.Channels.Add(assignment).Sort((a, b) => a.Channel.CompareTo(b.Channel));
            return s.WithLine(l with { Spreads = [.. l.Spreads.Select(x => x.Id == spread ? sp with { Channels = channels } : x)] });
        }, ("channel", assignment.Channel.ToString(CultureInfo.InvariantCulture)), ("role", assignment.Role.ToString()), ("invert", assignment.InvertPolarity.ToString()));

    public static ProjectEdit RenameLine(LineId line, string name) => ProjectEdit.Create(HistoryKind.Geometry, "geometry.line.rename",
        $"Rename line to “{name}”", s => s.WithLine(s.Survey.Line(line)! with { Name = name }), ("name", name));

    // ───────────────────────────── Processing ─────────────────────────────

    public static ProjectEdit AddStep(LineId line, ProcessingStep step, int? index = null) => ProjectEdit.Create(HistoryKind.Processing, "processing.add",
        $"Add {step.DisplayName}", s => s.WithLineInterpretation(line, i =>
        {
            var steps = i.Pipeline.Steps;
            steps = index is { } at ? steps.Insert(Math.Clamp(at, 0, steps.Length), step) : steps.Add(step);
            return i with { Pipeline = new ProcessingPipeline(steps) };
        }), [.. step.Parameters.Select(p => (p.Key, p.Value)).Prepend(("step", step.DisplayName))]);

    public static ProjectEdit UpdateStep(LineId line, ProcessingStep step) => ProjectEdit.Create(HistoryKind.Processing, "processing.edit",
        $"Edit {step.DisplayName}", s => s.WithLineInterpretation(line, i => i with { Pipeline = i.Pipeline.Replace(step) }),
        [.. step.Parameters.Select(p => (p.Key, p.Value)).Prepend(("step", step.DisplayName)).Append(("enabled", step.Enabled.ToString()))]);

    public static ProjectEdit SetStepEnabled(LineId line, ProcessingStep step, bool enabled) => ProjectEdit.Create(HistoryKind.Processing, "processing.toggle",
        $"{(enabled ? "Enable" : "Disable")} {step.DisplayName}", s => s.WithLineInterpretation(line, i => i with { Pipeline = i.Pipeline.Replace(step with { Enabled = enabled }) }),
        ("step", step.DisplayName), ("enabled", enabled.ToString()));

    public static ProjectEdit RemoveStep(LineId line, StepId step, string name) => ProjectEdit.Create(HistoryKind.Processing, "processing.remove",
        $"Remove {name}", s => s.WithLineInterpretation(line, i => i with { Pipeline = new ProcessingPipeline([.. i.Pipeline.Steps.Where(x => x.Id != step)]) }),
        ("step", name));

    public static ProjectEdit MoveStep(LineId line, StepId step, int newIndex, string name) => ProjectEdit.Create(HistoryKind.Processing, "processing.move",
        $"Move {name}", s => s.WithLineInterpretation(line, i =>
        {
            var steps = i.Pipeline.Steps;
            var item = steps.First(x => x.Id == step);
            steps = steps.Remove(item);
            return i with { Pipeline = new ProcessingPipeline(steps.Insert(Math.Clamp(newIndex, 0, steps.Length), item)) };
        }), ("step", name), ("index", newIndex.ToString(CultureInfo.InvariantCulture)));

    public static ProjectEdit SetPipeline(LineId line, ProcessingPipeline pipeline, string description) => ProjectEdit.Create(HistoryKind.Processing, "processing.pipeline",
        description, s => s.WithLineInterpretation(line, i => i with { Pipeline = pipeline }), ("fingerprint", pipeline.Fingerprint()));

    public static ProjectEdit SetTraceAdjustment(LineId line, PickKey key, TraceAdjustment adjustment, string description) => ProjectEdit.Create(HistoryKind.Processing,
        "trace.adjust", description, s => s.WithLineInterpretation(line, i => i with
        {
            Adjustments = adjustment.IsNeutral && adjustment.Note is null ? i.Adjustments.Remove(key) : i.Adjustments.SetItem(key, adjustment),
        }), ("trace", key.ToString()), ("gain", F(adjustment.Gain)), ("invert", adjustment.InvertPolarity.ToString()), ("excluded", adjustment.Excluded.ToString()), ("timeShift", F(adjustment.TimeShift)));

    public static ProjectEdit SetDisplay(LineId line, GatherDisplaySettings display) => ProjectEdit.Create(HistoryKind.Processing, "display.settings",
        "Change display settings", s => s.WithLineInterpretation(line, i => i with { Display = display }), ("mode", display.Mode.ToString()));

    // ───────────────────────────── Picking ─────────────────────────────

    /// <summary>Places or moves a manual pick, keeping the automatic suggestion for reference.</summary>
    public static ProjectEdit SetManualPick(LineId line, PickKey key, double time) => ProjectEdit.Create(HistoryKind.Picking, "pick.set",
        $"Pick channel {key.Channel} at {Ms(time)}", s => s.WithLineInterpretation(line, i =>
        {
            var old = i.PickOf(key);
            var suggestion = old?.Suggestion ?? (old is { Origin: ArrivalOrigin.Automatic } a ? new PickSuggestion(a.Time, a.Algorithm ?? "automatic", a.Confidence!, a.Snr) : null);
            var pick = new ArrivalPick(time, ArrivalOrigin.Manual, null, null, old?.Snr, old?.Included ?? true, old?.Comment, suggestion);
            return i with { Picks = i.Picks.SetItem(key, pick) };
        }), ("trace", key.ToString()), ("time", F(time)));

    public static ProjectEdit DeletePick(LineId line, PickKey key) => ProjectEdit.Create(HistoryKind.Picking, "pick.delete",
        $"Delete pick on channel {key.Channel}", s => s.WithLineInterpretation(line, i => i with { Picks = i.Picks.Remove(key) }), ("trace", key.ToString()));

    public static ProjectEdit DeletePicks(LineId line, IReadOnlyCollection<PickKey> keys, string description) => ProjectEdit.Create(HistoryKind.Picking, "pick.delete-many",
        description, s => s.WithLineInterpretation(line, i => i with { Picks = i.Picks.RemoveRange(keys) }), ("count", keys.Count.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Restores the automatic suggestion of a manually changed pick.</summary>
    public static ProjectEdit RestoreAutomaticPick(LineId line, PickKey key) => ProjectEdit.Create(HistoryKind.Picking, "pick.restore-auto",
        $"Restore automatic pick on channel {key.Channel}", s => s.WithLineInterpretation(line, i =>
        {
            var old = i.PickOf(key);
            if (old?.Suggestion is not { } sug) return i;
            return i with { Picks = i.Picks.SetItem(key, new ArrivalPick(sug.Time, ArrivalOrigin.Automatic, sug.Algorithm, sug.Confidence, sug.Snr, old.Included, old.Comment, sug)) };
        }), ("trace", key.ToString()));

    /// <summary>Accepts automatic picks (they become user decisions, origin AcceptedSuggestion).</summary>
    public static ProjectEdit AcceptPicks(LineId line, IReadOnlyCollection<PickKey> keys) => ProjectEdit.Create(HistoryKind.Picking, "pick.accept",
        keys.Count == 1 ? "Accept suggested pick" : $"Accept {keys.Count} suggested picks", s => s.WithLineInterpretation(line, i =>
        {
            var picks = i.Picks.ToBuilder();
            foreach (var k in keys)
                if (picks.TryGetValue(k, out var p) && p.Origin == ArrivalOrigin.Automatic)
                    picks[k] = p with { Origin = ArrivalOrigin.AcceptedSuggestion };
            return i with { Picks = picks.ToImmutable() };
        }), ("count", keys.Count.ToString(CultureInfo.InvariantCulture)));

    public static ProjectEdit SetPickIncluded(LineId line, PickKey key, bool included) => ProjectEdit.Create(HistoryKind.Picking, "pick.include",
        included ? $"Include pick on channel {key.Channel}" : $"Exclude pick on channel {key.Channel}",
        s => s.WithLineInterpretation(line, i => i.PickOf(key) is { } p ? i with { Picks = i.Picks.SetItem(key, p with { Included = included }) } : i),
        ("trace", key.ToString()), ("included", included.ToString()));

    public static ProjectEdit SetPickComment(LineId line, PickKey key, string? comment) => ProjectEdit.Create(HistoryKind.Picking, "pick.comment",
        $"Comment on channel {key.Channel}", s => s.WithLineInterpretation(line, i => i.PickOf(key) is { } p ? i with { Picks = i.Picks.SetItem(key, p with { Comment = comment }) } : i),
        ("trace", key.ToString()));

    /// <summary>
    /// Stores automatic picks. Manual and accepted picks are never overwritten; only their stored
    /// suggestion is refreshed.
    /// </summary>
    public static ProjectEdit ApplyAutomaticPicks(LineId line, IReadOnlyDictionary<PickKey, AutoPick> picks, PickerSettings settings) => ProjectEdit.Create(HistoryKind.Picking,
        "pick.automatic", $"Automatic picking ({picks.Count} traces, {settings.Algorithm})", s => s.WithLineInterpretation(line, i =>
        {
            var builder = i.Picks.ToBuilder();
            foreach (var (key, a) in picks)
            {
                var suggestion = new PickSuggestion(a.Time, a.Algorithm, a.Confidence, a.Snr);
                if (builder.TryGetValue(key, out var existing) && existing.IsManual)
                    builder[key] = existing with { Suggestion = suggestion };
                else if (existing is { Origin: ArrivalOrigin.Correlation })
                    builder[key] = existing with { Suggestion = suggestion };
                else
                    builder[key] = new ArrivalPick(a.Time, ArrivalOrigin.Automatic, a.Algorithm, a.Confidence, a.Snr, existing?.Included ?? true, existing?.Comment, suggestion);
            }
            return i with { Picks = builder.ToImmutable(), Picker = settings };
        }), ("algorithm", settings.Algorithm.ToString()), ("count", picks.Count.ToString(CultureInfo.InvariantCulture)),
        ("staS", F(settings.ShortWindow)), ("ltaS", F(settings.LongWindow)));

    /// <summary>Stores picks transferred by cross-correlation.</summary>
    public static ProjectEdit ApplyCorrelationPicks(LineId line, IReadOnlyDictionary<PickKey, (double Time, double Coefficient)> picks, string description) => ProjectEdit.Create(HistoryKind.Picking,
        "pick.correlation", description, s => s.WithLineInterpretation(line, i =>
        {
            var builder = i.Picks.ToBuilder();
            foreach (var (key, (time, coefficient)) in picks)
            {
                if (builder.TryGetValue(key, out var existing) && existing.IsManual) continue;
                var confidence = new PickConfidence(Math.Clamp(coefficient, 0, 1), PickConfidence.LevelFor(Math.Clamp(coefficient, 0, 1)),
                    [new ConfidenceFactor("Cross-correlation", Math.Clamp(coefficient, 0, 1), 1, $"Correlation coefficient {coefficient:0.00} with the reference trace.")],
                    coefficient < 0.7 ? ["Weak correlation with the reference trace."] : []);
                builder[key] = new ArrivalPick(time, ArrivalOrigin.Correlation, "Cross-correlation", confidence, existing?.Snr, existing?.Included ?? true, existing?.Comment, existing?.Suggestion);
            }
            return i with { Picks = builder.ToImmutable() };
        }), ("count", picks.Count.ToString(CultureInfo.InvariantCulture)));

    public static ProjectEdit SetPickerSettings(LineId line, PickerSettings settings) => ProjectEdit.Create(HistoryKind.Picking, "picker.settings",
        $"Picker settings: {settings.Algorithm}", s => s.WithLineInterpretation(line, i => i with { Picker = settings }), ("algorithm", settings.Algorithm.ToString()));

    // ───────────────────────────── Shot time ─────────────────────────────

    public static ProjectEdit SetShotTimeSettings(LineId line, ShotTimeSettings settings) => ProjectEdit.Create(HistoryKind.Processing, "shottime.settings",
        $"Shot-time correction: {settings.Method}", s => s.WithLineInterpretation(line, i => i with { ShotTime = settings }),
        ("method", settings.Method.ToString()), ("referenceVelocity", settings.ReferenceVelocity is { } v ? F(v) : "at shot"));

    public static ProjectEdit SetShotTimeEntry(LineId line, GatherId gather, ShotTimeEntry entry, string description) => ProjectEdit.Create(HistoryKind.Processing,
        "shottime.entry", description, s => s.WithLineInterpretation(line, i => i with { ShotTimes = i.ShotTimes.SetItem(gather, entry) }),
        ("gather", gather.ToString()), ("manual", entry.ManualCorrection is { } m ? F(m) : ""), ("reference", entry.ReferencePick is { } p ? F(p.Time) : ""));

    // ───────────────────────────── Interpretation ─────────────────────────────

    public static ProjectEdit SetSegments(LineId line, SegmentAssignment segments, string description) => ProjectEdit.Create(HistoryKind.Interpretation, "layers.segments",
        description, s => s.WithLineInterpretation(line, i => i with { Segments = segments }), ("layers", segments.LayerCount.ToString(CultureInfo.InvariantCulture)));

    public static ProjectEdit SetGatherCrossovers(LineId line, GatherId gather, GatherSegments segments) => ProjectEdit.Create(HistoryKind.Interpretation, "layers.crossover",
        $"Move crossover (left {string.Join("/", segments.Left.Select(F))} m, right {string.Join("/", segments.Right.Select(F))} m)",
        s => s.WithLineInterpretation(line, i => i with { Segments = i.Segments with { Gathers = i.Segments.Gathers.SetItem(gather, segments with { UserEdited = true }) } }),
        ("gather", gather.ToString()), ("left", string.Join(";", segments.Left.Select(F))), ("right", string.Join(";", segments.Right.Select(F))));

    public static ProjectEdit SetPickLayer(LineId line, PickKey key, int? layer) => ProjectEdit.Create(HistoryKind.Interpretation, "layers.override",
        layer is { } l ? $"Assign channel {key.Channel} to layer {l}" : $"Clear layer override of channel {key.Channel}",
        s => s.WithLineInterpretation(line, i => i with
        {
            Segments = i.Segments with { Overrides = layer is { } l2 ? i.Segments.Overrides.SetItem(key, l2) : i.Segments.Overrides.Remove(key) },
        }), ("trace", key.ToString()), ("layer", layer?.ToString(CultureInfo.InvariantCulture) ?? ""));

    public static ProjectEdit SetLayeredSettings(LineId line, LayeredSettings settings) => ProjectEdit.Create(HistoryKind.Interpretation, "layers.method",
        $"Layered method: {settings.Method}", s => s.WithLineInterpretation(line, i => i with { Layered = settings }),
        ("method", settings.Method.ToString()), ("xy", F(settings.GrmXy)), ("segments", settings.RefractorVelocitySegments.ToString(CultureInfo.InvariantCulture)),
        ("forward", settings.ForwardShot?.ToString() ?? ""), ("reverse", settings.ReverseShot?.ToString() ?? ""), ("regression", settings.RegressionMode.ToString()));

    public static ProjectEdit SetTomographySettings(LineId line, TomographySettings settings) => ProjectEdit.Create(HistoryKind.Inversion, "tomography.settings",
        "Edit tomography settings", s => s.WithLineInterpretation(line, i => i with { Tomography = settings }),
        ("cellWidth", settings.CellWidth is { } w ? F(w) : "auto"), ("lambda", F(settings.Smoothing)), ("iterations", settings.MaxIterations.ToString(CultureInfo.InvariantCulture)),
        ("start", settings.StartingModel.ToString()));

    public static ProjectEdit SetTomographyResult(LineId line, TomographyResultRef reference, TomographySettings settings) => ProjectEdit.Create(HistoryKind.Inversion, "tomography.run",
        $"Tomographic inversion: {reference.Iterations} iterations, RMS {Ms(reference.RmsSeconds)}, χ² {reference.Chi2:0.00}",
        s => s.WithLineInterpretation(line, i => i with { TomographyResult = reference, Tomography = settings }),
        ("result", reference.ResultId.ToString("N")), ("rmsS", F(reference.RmsSeconds)), ("chi2", F(reference.Chi2)), ("fingerprint", reference.InputFingerprint));

    // ───────────────────────────── Scenario settings ─────────────────────────────

    public static ProjectEdit SetScenarioSettings(ScenarioSettings settings, string description) => ProjectEdit.Create(HistoryKind.Interpretation, "scenario.settings",
        description, s => s.WithActiveScenario(sc => sc with { Settings = settings }));

    public static ProjectEdit SetDensityModel(DensityModel model) => ProjectEdit.Create(HistoryKind.Interpretation, "density.model",
        model.Source == DensitySource.Estimated ? $"Density: ESTIMATED ({model.Estimator})" : $"Density: {model.Intervals.Length} measured interval(s)",
        s => s.WithActiveScenario(sc => sc with { Settings = sc.Settings with { Density = model } }),
        ("source", model.Source.ToString()), ("estimator", model.Estimator?.ToString() ?? ""));

    public static ProjectEdit SetVs30Settings(Vs30Settings settings) => ProjectEdit.Create(HistoryKind.Interpretation, "vs30.settings",
        settings.AllowExtrapolation ? "Vs30 settings (extrapolation ENABLED)" : "Vs30 settings",
        s => s.WithActiveScenario(sc => sc with { Settings = sc.Settings with { Vs30 = settings } }),
        ("positions", string.Join(";", settings.Positions.Select(F))), ("depth", F(settings.TargetDepth)), ("extrapolate", settings.AllowExtrapolation.ToString()));

    public static ProjectEdit SetEngineeringSettings(EngineeringSettings settings) => ProjectEdit.Create(HistoryKind.Interpretation, "engineering.settings",
        "Engineering settings", s => s.WithActiveScenario(sc => sc with { Settings = sc.Settings with { Engineering = settings } }),
        ("pLine", settings.PLine?.ToString() ?? ""), ("shLine", settings.ShLine?.ToString() ?? ""), ("source", settings.Source.ToString()),
        ("positions", string.Join(";", settings.ProfilePositions.Select(F))));

    // ───────────────────────────── Scenarios ─────────────────────────────

    public static ProjectEdit DuplicateScenario(string name, DateTimeOffset now) => ProjectEdit.Create(HistoryKind.Scenario, "scenario.duplicate",
        $"Create scenario “{name}”", s =>
        {
            var copy = s.ActiveScenario with { Id = ScenarioId.New(), Name = name, CreatedAt = now };
            return s with { Scenarios = s.Scenarios.Add(copy), ActiveScenarioId = copy.Id };
        }, ("name", name));

    public static ProjectEdit SwitchScenario(ScenarioId id, string name) => ProjectEdit.Create(HistoryKind.Scenario, "scenario.switch",
        $"Switch to scenario “{name}”", s => s with { ActiveScenarioId = id }, ("scenario", id.ToString()));

    public static ProjectEdit RenameScenario(ScenarioId id, string name, string? description) => ProjectEdit.Create(HistoryKind.Scenario, "scenario.rename",
        $"Rename scenario to “{name}”", s => s with { Scenarios = [.. s.Scenarios.Select(x => x.Id == id ? x with { Name = name, Description = description } : x)] }, ("name", name));

    public static ProjectEdit DeleteScenario(ScenarioId id, string name) => ProjectEdit.Create(HistoryKind.Scenario, "scenario.delete",
        $"Delete scenario “{name}”", s =>
        {
            if (s.Scenarios.Length <= 1) return s;
            var remaining = s.Scenarios.Where(x => x.Id != id).ToImmutableArray();
            return s with { Scenarios = remaining, ActiveScenarioId = s.ActiveScenarioId == id ? remaining[0].Id : s.ActiveScenarioId };
        }, ("scenario", id.ToString()));

    private static ProjectState MapGather(ProjectState s, LineId line, GatherId gather, Func<ShotGather, ShotGather> map)
    {
        var l = s.Survey.Line(line) ?? throw new InvalidOperationException("Line not found.");
        return s.WithLine(l with { Gathers = [.. l.Gathers.Select(g => g.Id == gather ? map(g) : g)] });
    }
}
