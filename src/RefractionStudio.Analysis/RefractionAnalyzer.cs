using RefractionStudio.Analysis.Engineering;
using RefractionStudio.Analysis.Layered;
using RefractionStudio.Analysis.Quality;
using RefractionStudio.Analysis.Tomography;
using RefractionStudio.Analysis.TravelTimes;
using RefractionStudio.Domain;

namespace RefractionStudio.Analysis;

/// <summary>Analysis of one survey line.</summary>
public sealed record LineAnalysis(
    SurveyLine Line,
    LineObservations Observations,
    ImmutableArray<SegmentFit> Segments,
    Computed<LayeredModel> Layered,
    TomographyResult? Tomography,
    bool TomographyStale,
    ImmutableArray<ReciprocalPair> Reciprocals,
    ImmutableArray<QcFlag> Flags)
{
    /// <summary>The model selected for Vs30/engineering, or the reason it is not available.</summary>
    public Computed<VelocityProfile> ProfileAt(VelocityModelSource source, double x)
    {
        if (source == VelocityModelSource.Tomography)
        {
            if (Tomography is null) return Computed<VelocityProfile>.Unavailable($"No tomography result exists for {Line.Name}; run the inversion first.");
            var p = ProfileExtractor.FromTomography(Tomography, x);
            return TomographyStale && p.HasValue
                ? Computed<VelocityProfile>.Ok(p.Value!, "The tomogram is out of date with respect to the current picks or settings.")
                : p;
        }
        return Layered.HasValue ? ProfileExtractor.FromLayered(Layered.Value!, x) : Computed<VelocityProfile>.Unavailable(Layered.Reason!);
    }
}

/// <summary>Equivalent shear velocity at one position.</summary>
public sealed record Vs30Entry(double X, Computed<EquivalentVelocity> Vs30, ImmutableArray<Computed<EquivalentVelocity>> Custom);

/// <summary>Engineering properties at one depth of a profile.</summary>
public sealed record EngineeringRow(double Depth, Computed<double> Vp, Computed<double> Vs, ElasticProperties? Properties);

/// <summary>Engineering profile at one position.</summary>
public sealed record EngineeringProfile(double X, ImmutableArray<EngineeringRow> Rows, double ConstrainedDepth, ImmutableArray<string> Notes);

/// <summary>Project-level results that combine lines.</summary>
public sealed record EngineeringResult(Computed<ImmutableArray<Vs30Entry>> Vs30, Computed<ImmutableArray<EngineeringProfile>> Profiles);

/// <summary>A QC dashboard card.</summary>
/// <param name="Title">Card title.</param>
/// <param name="Value">Main value as text.</param>
/// <param name="Level">Status.</param>
/// <param name="Detail">Explanation.</param>
public sealed record QcCard(string Title, string Value, QcLevel Level, string Detail);

/// <summary>Overall analysis quality.</summary>
public enum QualityVerdict
{
    Good,
    ReviewRecommended,
    Incomplete,
}

/// <summary>QC summary of the project.</summary>
public sealed record QcSummary(QualityVerdict Verdict, ImmutableArray<string> Reasons, ImmutableArray<QcCard> Cards, ImmutableArray<QcFlag> Flags);

/// <summary>Everything the engine computes from a project.</summary>
public sealed record AnalysisResult(ImmutableArray<LineAnalysis> Lines, EngineeringResult Engineering, QcSummary Qc, DateTimeOffset ComputedAt)
{
    public LineAnalysis? Line(LineId id) => Lines.FirstOrDefault(l => l.Line.Id == id);
}

/// <summary>Supplies stored tomography results.</summary>
public interface ITomographyResultSource
{
    TomographyResult? Get(TomographyResultRef reference);
}

/// <summary>Processed traces of a gather and its picks (display time base), for trace-level checks.</summary>
public sealed record TraceQcInput(Picking.GatherData Gather, IReadOnlyDictionary<int, double> PickTimes);

/// <summary>
/// Supplies processed waveforms to the analysis (implemented by the workspace, which owns the raw data).
/// Returns null when the waveforms are not available.
/// </summary>
public interface ITraceDataSource
{
    TraceQcInput? Get(ProjectState project, LineId line, GatherId gather);
}

/// <summary>
/// The scientific engine entry point. Usable without any UI:
/// <code>var result = new RefractionAnalyzer().Analyze(project, results);</code>
/// Tomography is not re-run here (it is computed on request by <see cref="ITomographyEngine"/>);
/// stored results are attached and checked for staleness.
/// </summary>
public sealed class RefractionAnalyzer
{
    private readonly IReadOnlyDictionary<LayeredMethod, ILayeredMethod> _methods;
    private readonly IVs30Calculator _vs30;
    private readonly IEngineeringPropertyCalculator _elastic;
    private readonly IDensityEstimator _density;

    public RefractionAnalyzer(IEnumerable<ILayeredMethod>? methods = null, IVs30Calculator? vs30 = null,
        IEngineeringPropertyCalculator? elastic = null, IDensityEstimator? density = null)
    {
        _methods = (methods ?? [new InterceptTimeMethod(), new PlusMinusMethod(), new GrmMethod(), new DelayTimeMethod()]).ToDictionary(m => m.Method);
        _vs30 = vs30 ?? new Vs30Calculator();
        _elastic = elastic ?? new EngineeringPropertyCalculator();
        _density = density ?? new DensityProvider();
    }

    public ILayeredMethod Method(LayeredMethod method) => _methods[method];

    /// <summary>Waveform source for trace-level QC (dead, noisy, reversed traces); checks are skipped without it.</summary>
    public ITraceDataSource? Traces { get; set; }

    public AnalysisResult Analyze(ProjectState project, ITomographyResultSource? results = null, CancellationToken ct = default)
    {
        var lines = ImmutableArray.CreateBuilder<LineAnalysis>();
        foreach (var line in project.Survey.Lines)
        {
            ct.ThrowIfCancellationRequested();
            lines.Add(AnalyzeLine(project, line, results));
        }
        var engineering = Engineering(project, lines.ToImmutable());
        var qc = Summarize(project, lines.ToImmutable(), engineering);
        return new AnalysisResult(lines.ToImmutable(), engineering, qc, DateTimeOffset.UtcNow);
    }

    public LineAnalysis AnalyzeLine(ProjectState project, SurveyLine line, ITomographyResultSource? results)
    {
        var interp = project.Interpretation(line.Id);
        var obs = ObservationBuilder.Build(line, interp);
        var fits = SegmentAssigner.FitAll(obs, interp.Layered.RegressionMode);
        var layerCount = Math.Max(2, interp.Segments.LayerCount);
        var layered = obs.Included.Any()
            ? _methods[interp.Layered.Method].Interpret(obs, interp.Layered, layerCount)
            : Computed<LayeredModel>.Unavailable("No included picks.");
        TomographyResult? tomo = null;
        var stale = false;
        if (interp.TomographyResult is { } reference && results?.Get(reference) is { } stored)
        {
            tomo = stored;
            var layeredForStart = interp.Tomography.StartingModel == StartingModelKind.Layered && layered.HasValue ? layered.Value : null;
            stale = TomographyEngine.Fingerprint(obs, stored.Settings, layeredForStart) != reference.InputFingerprint
                    || stored.Settings != interp.Tomography;
        }
        var tolerance = interp.Layered.CoincidenceTolerance ?? obs.TypicalSpacing / 4;
        var reciprocals = QualityControl.ReciprocalPairs(obs, tolerance);
        var flags = QualityControl.CheckLine(obs, project.ActiveScenario.Settings.Qc, fits, reciprocals, layered, tomo, stale);
        if (Traces is { } source)
        {
            var traceFlags = ImmutableArray.CreateBuilder<QcFlag>();
            foreach (var g in line.Gathers.Where(g => g.Included && double.IsFinite(g.ShotX)))
            {
                if (source.Get(project, line.Id, g.Id) is { } input) traceFlags.AddRange(QualityControl.CheckTraces(line.Id, input.Gather, input.PickTimes));
            }
            if (traceFlags.Count > 0) flags = flags.AddRange(traceFlags);
        }
        return new LineAnalysis(line, obs, fits, layered, tomo, stale, reciprocals, flags);
    }

    private EngineeringResult Engineering(ProjectState project, ImmutableArray<LineAnalysis> lines)
    {
        var settings = project.ActiveScenario.Settings;
        var eng = settings.Engineering;
        var sh = eng.ShLine is { } shId ? lines.FirstOrDefault(l => l.Line.Id == shId) : null;
        var p = eng.PLine is { } pId ? lines.FirstOrDefault(l => l.Line.Id == pId) : null;
        var positions = settings.Vs30.Positions.IsDefaultOrEmpty ? eng.ProfilePositions : settings.Vs30.Positions;

        Computed<ImmutableArray<Vs30Entry>> vs30;
        if (sh is null) vs30 = Computed<ImmutableArray<Vs30Entry>>.Unavailable("Select an SH-wave line for Vs30 in the engineering settings.");
        else if (sh.Line.WaveType != WaveType.SH) vs30 = Computed<ImmutableArray<Vs30Entry>>.Unavailable($"{sh.Line.Name} is a P-wave line; Vs30 requires shear-wave velocities.");
        else if (positions.IsDefaultOrEmpty) vs30 = Computed<ImmutableArray<Vs30Entry>>.Unavailable("Choose the positions along the line where Vs30 is evaluated.");
        else
        {
            var entries = ImmutableArray.CreateBuilder<Vs30Entry>();
            foreach (var x in positions)
            {
                var profile = sh.ProfileAt(eng.Source, x);
                if (!profile.HasValue)
                {
                    entries.Add(new Vs30Entry(x, Computed<EquivalentVelocity>.Unavailable(profile.Reason!), []));
                    continue;
                }
                var value = _vs30.Calculate(profile.Value!, 0, settings.Vs30.TargetDepth, settings.Vs30.AllowExtrapolation);
                var custom = (settings.Vs30.CustomIntervals.IsDefault ? [] : settings.Vs30.CustomIntervals)
                    .Select(i => _vs30.Calculate(profile.Value!, i.Top, i.Bottom, settings.Vs30.AllowExtrapolation)).ToImmutableArray();
                entries.Add(new Vs30Entry(x, value, custom));
            }
            vs30 = Computed<ImmutableArray<Vs30Entry>>.Ok(entries.ToImmutable());
        }

        Computed<ImmutableArray<EngineeringProfile>> profiles;
        if (p is null || sh is null) profiles = Computed<ImmutableArray<EngineeringProfile>>.Unavailable("Engineering properties need a P-wave line and an SH-wave line covering the same ground; select both in the engineering settings.");
        else if (eng.ProfilePositions.IsDefaultOrEmpty) profiles = Computed<ImmutableArray<EngineeringProfile>>.Unavailable("Choose the positions where engineering profiles are reported.");
        else
        {
            var list = ImmutableArray.CreateBuilder<EngineeringProfile>();
            foreach (var x in eng.ProfilePositions)
            {
                var pp = p.ProfileAt(eng.Source, x);
                var sp = sh.ProfileAt(eng.Source, x);
                var notes = ImmutableArray.CreateBuilder<string>();
                if (!pp.HasValue) notes.Add("P model: " + pp.Reason);
                if (!sp.HasValue) notes.Add("SH model: " + sp.Reason);
                if (!pp.HasValue || !sp.HasValue)
                {
                    list.Add(new EngineeringProfile(x, [], 0, notes.ToImmutable()));
                    continue;
                }
                var depthLimit = Math.Min(pp.Value!.ConstrainedDepth, sp.Value!.ConstrainedDepth);
                notes.AddRange(pp.Notes.Select(n => "P model: " + n));
                notes.AddRange(sp.Notes.Select(n => "SH model: " + n));
                var rows = ImmutableArray.CreateBuilder<EngineeringRow>();
                var step = eng.DepthStep > 0 ? eng.DepthStep : 1.0;
                for (var d = step / 2; d < depthLimit; d += step)
                {
                    var vp = pp.Value.VelocityAt(d);
                    var vs = sp.Value.VelocityAt(d);
                    var cvp = vp is { } a ? Computed<double>.Ok(a) : Computed<double>.Unavailable("No Vp at this depth.");
                    var cvs = vs is { } b ? Computed<double>.Ok(b) : Computed<double>.Unavailable("No Vs at this depth.");
                    ElasticProperties? props = null;
                    if (vp is { } v1 && vs is { } v2)
                        props = _elastic.Calculate(v1, v2, _density.DensityAt(settings.Density, d, v1));
                    rows.Add(new EngineeringRow(d, cvp, cvs, props));
                }
                if (rows.Count == 0) notes.Add($"The P and SH models do not overlap in constrained depth at x = {x:0.##} m.");
                list.Add(new EngineeringProfile(x, rows.ToImmutable(), depthLimit, notes.ToImmutable()));
            }
            profiles = Computed<ImmutableArray<EngineeringProfile>>.Ok(list.ToImmutable());
        }
        return new EngineeringResult(vs30, profiles);
    }

    private static QcSummary Summarize(ProjectState project, ImmutableArray<LineAnalysis> lines, EngineeringResult engineering)
    {
        var flags = lines.SelectMany(l => l.Flags).ToImmutableArray();
        var cards = ImmutableArray.CreateBuilder<QcCard>();
        var reasons = ImmutableArray.CreateBuilder<string>();
        var survey = project.Survey;

        cards.Add(new QcCard("Imported records", survey.Records.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            survey.Records.IsEmpty ? QcLevel.Problem : QcLevel.Ok, $"{survey.SourceFiles.Length} file(s), {survey.Records.Sum(r => r.Traces.Length)} traces."));
        var gathers = survey.Lines.Sum(l => l.Gathers.Count(g => g.Included));
        cards.Add(new QcCard("Shot gathers", gathers.ToString(System.Globalization.CultureInfo.InvariantCulture), gathers == 0 ? QcLevel.Problem : QcLevel.Ok,
            $"{survey.Lines.Sum(l => l.Gathers.Count(g => !g.Included))} excluded."));

        var missing = flags.Count(f => f.Code == "missing-picks");
        cards.Add(new QcCard("Missing measurements", missing.ToString(System.Globalization.CultureInfo.InvariantCulture), missing == 0 ? QcLevel.Ok : QcLevel.Review,
            missing == 0 ? "Every receiver trace is picked or excluded." : $"{missing} shot(s) have unpicked traces."));

        foreach (var wave in new[] { WaveType.P, WaveType.SH })
        {
            var obs = lines.Where(l => l.Line.WaveType == wave).SelectMany(l => l.Observations.All).ToList();
            if (obs.Count == 0 && !lines.Any(l => l.Line.WaveType == wave)) continue;
            var accepted = obs.Count(o => o.Included && o.Pick.IsManual);
            var auto = obs.Count(o => o.Included && !o.Pick.IsManual);
            cards.Add(new QcCard($"{wave} picks accepted", $"{accepted} / {obs.Count(o => o.Included)}", obs.Count == 0 ? QcLevel.Problem : auto > 0 ? QcLevel.Review : QcLevel.Ok,
                obs.Count == 0 ? "No picks." : $"{auto} automatic pick(s) not yet reviewed."));
        }
        var allObs = lines.SelectMany(l => l.Observations.All).ToList();
        var low = allObs.Count(o => o.Included && o.Pick.Confidence?.Level == ConfidenceLevel.Low && o.Pick.Origin == ArrivalOrigin.Automatic);
        cards.Add(new QcCard("Low-confidence picks", low.ToString(System.Globalization.CultureInfo.InvariantCulture), low == 0 ? QcLevel.Ok : QcLevel.Review, "Automatic picks with low confidence still included."));
        var corrected = lines.Sum(l => l.Observations.ShotTimes.Count(s => s.Method != ShotTimeMethod.None && s.Correction.HasValue && s.Correction.Value != 0));
        var uncorrected = lines.Sum(l => l.Observations.ShotTimes.Count(s => !s.Correction.HasValue));
        cards.Add(new QcCard("Trigger corrections", corrected.ToString(System.Globalization.CultureInfo.InvariantCulture), uncorrected > 0 ? QcLevel.Problem : QcLevel.Ok,
            uncorrected > 0 ? $"{uncorrected} shot(s) could not be corrected." : "Shot-time corrections applied."));
        var excluded = allObs.Count(o => !o.Included);
        cards.Add(new QcCard("Excluded measurements", excluded.ToString(System.Globalization.CultureInfo.InvariantCulture), QcLevel.Ok, "Excluded picks remain visible and auditable."));

        var dois = lines.Where(l => l.Tomography is not null).SelectMany(l => l.Tomography!.InvestigationDepth).Where(double.IsFinite).ToList();
        var deepest = lines.Where(l => l.Layered.HasValue).SelectMany(l => l.Layered.Value!.Refractors.SelectMany(r => r.Points)).Select(p => p.Depth).DefaultIfEmpty(double.NaN).Max();
        cards.Add(new QcCard("Depth coverage",
            dois.Count > 0 ? $"{dois.Max():0.#} m" : double.IsFinite(deepest) ? $"{deepest:0.#} m" : "—",
            dois.Count > 0 || double.IsFinite(deepest) ? QcLevel.Ok : QcLevel.Review,
            dois.Count > 0 ? "Deepest ray-covered cell of the tomograms." : "Deepest interpreted refractor."));

        var vs30 = engineering.Vs30;
        var vs30Text = !vs30.HasValue ? "Not available" : vs30.Value!.Count(e => e.Vs30.HasValue) + " / " + vs30.Value!.Length;
        var vs30Extrapolated = vs30.HasValue && vs30.Value!.Any(e => e.Vs30.HasValue && e.Vs30.Value!.Extrapolated);
        cards.Add(new QcCard("Vs30 availability", vs30Text, !vs30.HasValue ? QcLevel.Review : vs30Extrapolated ? QcLevel.Review : QcLevel.Ok,
            !vs30.HasValue ? vs30.Reason! : vs30Extrapolated ? "Some values are EXTRAPOLATED." : "Computed from measured depth."));

        var rmsValues = lines.Select(l => l.Tomography?.FinalRms ?? (l.Layered.HasValue ? l.Layered.Value!.Refractors.Select(r => r.RmsResidual).FirstOrDefault(v => v is not null) : null))
            .Where(v => v is { } x && double.IsFinite(x)).Select(v => v!.Value).ToList();
        cards.Add(new QcCard("Model RMS error", rmsValues.Count > 0 ? $"{rmsValues.Max() * 1000:0.00} ms" : "—",
            rmsValues.Count == 0 ? QcLevel.Review : rmsValues.Max() > project.ActiveScenario.Settings.Qc.MaxResidualSeconds ? QcLevel.Review : QcLevel.Ok,
            "Largest RMS travel-time misfit of the current models."));

        var problems = flags.Count(f => f.Level == QcLevel.Problem);
        var reviews = flags.Count(f => f.Level == QcLevel.Review);
        QualityVerdict verdict;
        if (survey.Lines.IsEmpty || allObs.Count == 0)
        {
            verdict = QualityVerdict.Incomplete;
            reasons.Add(survey.Lines.IsEmpty ? "No survey line has been set up." : "No first arrivals have been picked.");
        }
        else if (lines.All(l => !l.Layered.HasValue && l.Tomography is null))
        {
            verdict = QualityVerdict.Incomplete;
            reasons.Add("No velocity model has been computed yet.");
        }
        else if (problems > 0 || reviews > Math.Max(3, allObs.Count / 10))
        {
            verdict = QualityVerdict.ReviewRecommended;
            if (problems > 0) reasons.Add($"{problems} likely problem(s) were found.");
            if (reviews > 0) reasons.Add($"{reviews} item(s) should be reviewed.");
        }
        else
        {
            verdict = QualityVerdict.Good;
            reasons.Add(reviews == 0 ? "No quality issues were detected." : $"Only {reviews} minor item(s) to review.");
        }
        reasons.Add("This summary is an aid to review; it does not imply compliance with any standard.");
        return new QcSummary(verdict, reasons.ToImmutable(), cards.ToImmutable(), flags);
    }
}
