namespace RefractionStudio.Domain;

/// <summary>How the shot time (trigger) of each gather is corrected.</summary>
public enum ShotTimeMethod
{
    /// <summary>No correction: the trigger is assumed exact.</summary>
    None,

    /// <summary>From a pick on the reference (shot/spy) geophone channel.</summary>
    ReferenceGeophone,

    /// <summary>A per-gather correction entered by the user.</summary>
    Manual,
}

/// <summary>Line-wide shot-time correction settings.</summary>
/// <param name="Method">Correction method.</param>
/// <param name="ReferenceVelocity">
/// Velocity used to compute the expected arrival at the reference geophone from its distance to the
/// source. Null means the reference geophone is treated as being at the source (expected time 0).
/// </param>
public sealed record ShotTimeSettings(ShotTimeMethod Method = ShotTimeMethod.None, double? ReferenceVelocity = null);

/// <summary>Per-gather shot-time information. Raw times are never modified.</summary>
/// <param name="ManualCorrection">Correction (s) added to every pick of the gather (Manual method).</param>
/// <param name="ReferencePick">Pick on the reference geophone (ReferenceGeophone method).</param>
/// <param name="Note">Why the correction was made.</param>
public sealed record ShotTimeEntry(double? ManualCorrection = null, ArrivalPick? ReferencePick = null, string? Note = null);

/// <summary>First-break picking algorithms.</summary>
public enum PickerAlgorithm
{
    /// <summary>Akaike information criterion on the trace (Maeda 1985 formulation).</summary>
    Aic,

    /// <summary>Ratio of short-term to long-term average energy (Allen 1978).</summary>
    StaLta,

    /// <summary>Modified energy ratio (Wong et al. 2009).</summary>
    EnergyRatio,

    /// <summary>For SH gathers built from opposite impacts: onset of polarity divergence.</summary>
    ShPolarityDivergence,
}

/// <summary>Automatic picker settings.</summary>
/// <param name="Algorithm">Picker.</param>
/// <param name="ShortWindow">STA / energy window (s).</param>
/// <param name="LongWindow">LTA window (s).</param>
/// <param name="MinApparentVelocity">Search window ends at offset / this velocity (+ margin). Null = to the end of the trace.</param>
/// <param name="MaxApparentVelocity">Search window starts at offset / this velocity. Null = from time zero.</param>
/// <param name="WindowMargin">Margin added to the velocity-based window (s).</param>
/// <param name="UseNeighbourGuidance">Refine picks using adjacent-trace consistency.</param>
public sealed record PickerSettings(
    PickerAlgorithm Algorithm = PickerAlgorithm.Aic,
    double ShortWindow = 0.002,
    double LongWindow = 0.02,
    double? MinApparentVelocity = 150.0,
    double? MaxApparentVelocity = 8000.0,
    double WindowMargin = 0.01,
    bool UseNeighbourGuidance = true);

/// <summary>Crossover offsets of one gather: where arrivals move to the next deeper refractor.</summary>
/// <param name="Left">Absolute offsets on the decreasing-x side, increasing; layer = 1 + count(crossover ≤ |offset|).</param>
/// <param name="Right">Absolute offsets on the increasing-x side.</param>
/// <param name="UserEdited">False while the values are an automatic suggestion.</param>
public sealed record GatherSegments(ImmutableArray<double> Left, ImmutableArray<double> Right, bool UserEdited)
{
    public static GatherSegments None { get; } = new([], [], false);
}

/// <summary>Assignment of first arrivals to layers (1 = direct wave in layer 1, n = head wave of refractor n−1).</summary>
/// <param name="LayerCount">Number of layers in the interpretation (≥ 2 for a refraction model).</param>
/// <param name="Gathers">Crossovers per gather.</param>
/// <param name="Overrides">Explicit layer of individual picks, taking precedence over crossovers.</param>
public sealed record SegmentAssignment(int LayerCount, ImmutableDictionary<GatherId, GatherSegments> Gathers, ImmutableDictionary<PickKey, int> Overrides)
{
    public static SegmentAssignment Default { get; } = new(2, ImmutableDictionary<GatherId, GatherSegments>.Empty, ImmutableDictionary<PickKey, int>.Empty);

    /// <summary>Layer of an arrival at signed offset <paramref name="signedOffset"/> (receiver x − shot x).</summary>
    public int LayerOf(PickKey key, double signedOffset)
    {
        if (Overrides.TryGetValue(key, out var forced)) return forced;
        if (!Gathers.TryGetValue(key.Gather, out var seg)) return 1;
        var crossovers = signedOffset < 0 ? seg.Left : seg.Right;
        var layer = 1;
        var abs = Math.Abs(signedOffset);
        if (!crossovers.IsDefaultOrEmpty)
        {
            foreach (var c in crossovers)
                if (abs >= c) layer++;
        }
        return Math.Min(layer, LayerCount);
    }
}

/// <summary>Layered interpretation methods.</summary>
public enum LayeredMethod
{
    /// <summary>Slope–intercept analysis of travel-time segments (planar layers; dipping for a reversed pair).</summary>
    InterceptTime,

    /// <summary>Hagedoorn plus–minus method on a reversed shot pair.</summary>
    PlusMinus,

    /// <summary>Palmer generalized reciprocal method on a reversed shot pair.</summary>
    Grm,

    /// <summary>Least-squares delay-time (time-term) inversion over all shots.</summary>
    DelayTime,
}

/// <summary>Settings of the layered interpretation.</summary>
/// <param name="Method">Method.</param>
/// <param name="ForwardShot">Forward (low-x) shot of the reversed pair (intercept, plus–minus, GRM).</param>
/// <param name="ReverseShot">Reverse (high-x) shot of the reversed pair.</param>
/// <param name="GrmXy">GRM XY distance (m); 0 = plus–minus equivalent.</param>
/// <param name="RefractorVelocitySegments">Delay-time: number of lateral refractor-velocity segments.</param>
/// <param name="VelocitySmoothing">Delay-time: Tikhonov weight tying adjacent refractor slowness segments.</param>
/// <param name="CoincidenceTolerance">Distance (m) within which a shot and a receiver share a delay node; null = ¼ of the geophone spacing.</param>
/// <param name="FixedVelocities">Optional user-fixed layer velocities (index 0 = layer 1); null entries are estimated.</param>
/// <param name="MinusTimeWindow">Plus–minus/GRM: number of stations in the sliding regression of the minus/velocity-analysis function.</param>
/// <param name="RegressionMode">Segment line fits: ordinary or robust least squares.</param>
public sealed record LayeredSettings(
    LayeredMethod Method = LayeredMethod.DelayTime,
    GatherId? ForwardShot = null,
    GatherId? ReverseShot = null,
    double GrmXy = 0.0,
    int RefractorVelocitySegments = 1,
    double VelocitySmoothing = 1.0,
    double? CoincidenceTolerance = null,
    ImmutableArray<double?> FixedVelocities = default,
    int MinusTimeWindow = 5,
    RegressionMode RegressionMode = RegressionMode.Ordinary);

/// <summary>Straight-line regression flavours.</summary>
public enum RegressionMode
{
    /// <summary>Ordinary least squares.</summary>
    Ordinary,

    /// <summary>Huber M-estimator via iteratively reweighted least squares (robust to outliers).</summary>
    Robust,
}

/// <summary>Starting model of the tomographic inversion.</summary>
public enum StartingModelKind
{
    /// <summary>Velocity increasing linearly with depth below the surface.</summary>
    Gradient,

    /// <summary>The current layered interpretation, rasterised.</summary>
    Layered,

    /// <summary>A homogeneous model.</summary>
    Constant,
}

/// <summary>Settings of first-arrival travel-time tomography.</summary>
/// <param name="CellWidth">Cell width (m); null = half the geophone spacing.</param>
/// <param name="CellHeight">Cell height (m); null = equal to the cell width.</param>
/// <param name="MaxDepth">Model depth below the lowest ground point of the grid (m); null = the larger of one third of the maximum offset and three cell heights.</param>
/// <param name="StartingModel">Starting model kind.</param>
/// <param name="StartTopVelocity">Gradient/constant start: velocity at the surface (m/s); null = estimated from direct arrivals.</param>
/// <param name="StartBottomVelocity">Gradient start: velocity at the model bottom (m/s); null = from the highest apparent velocity.</param>
/// <param name="Smoothing">Regularisation weight λ of the smoothness constraint.</param>
/// <param name="SmoothingDecay">Factor applied to λ after each iteration (1 = fixed λ).</param>
/// <param name="HorizontalSmoothingRatio">Weight of horizontal relative to vertical roughness (≥ 1 favours layered models).</param>
/// <param name="MaxIterations">Maximum Gauss–Newton iterations.</param>
/// <param name="MinVelocity">Lower velocity bound (m/s).</param>
/// <param name="MaxVelocity">Upper velocity bound (m/s).</param>
/// <param name="SecondaryNodes">Secondary nodes per cell edge of the shortest-path ray tracer.</param>
/// <param name="PickErrorAbsolute">Absolute pick uncertainty (s) used to weight data.</param>
/// <param name="PickErrorRelative">Relative pick uncertainty (fraction of the travel time).</param>
/// <param name="CoverageThreshold">Cells with less total ray length (m) than this are shown as unconstrained.</param>
/// <param name="TargetChi2">Stop when the normalised misfit χ² reaches this value.</param>
/// <param name="RobustDataWeighting">Down-weight outlying picks (Huber weights on normalised residuals) so that a few mis-picks cannot dominate the model; outliers remain flagged in QC.</param>
public sealed record TomographySettings(
    double? CellWidth = null,
    double? CellHeight = null,
    double? MaxDepth = null,
    StartingModelKind StartingModel = StartingModelKind.Gradient,
    double? StartTopVelocity = null,
    double? StartBottomVelocity = null,
    double Smoothing = 30.0,
    double SmoothingDecay = 0.8,
    double HorizontalSmoothingRatio = 2.0,
    int MaxIterations = 10,
    double MinVelocity = 100.0,
    double MaxVelocity = 7000.0,
    int SecondaryNodes = 5,
    double PickErrorAbsolute = 0.0005,
    double PickErrorRelative = 0.02,
    double CoverageThreshold = 0.0,
    double TargetChi2 = 1.0,
    bool RobustDataWeighting = true);

/// <summary>Reference to a stored tomography result (the grid lives in the container's results folder).</summary>
/// <param name="ResultId">Result identifier (file name).</param>
/// <param name="InputFingerprint">Fingerprint of picks, geometry and settings used; mismatch = stale.</param>
/// <param name="CreatedAt">When it was computed.</param>
/// <param name="AppVersion">Application version that computed it.</param>
/// <param name="Iterations">Iterations performed.</param>
/// <param name="RmsSeconds">Final RMS travel-time residual (s).</param>
/// <param name="Chi2">Final normalised misfit.</param>
public sealed record TomographyResultRef(Guid ResultId, string InputFingerprint, DateTimeOffset CreatedAt, string AppVersion, int Iterations, double RmsSeconds, double Chi2);

/// <summary>Everything interpretive about one survey line within a scenario.</summary>
public sealed record LineInterpretation(
    ProcessingPipeline Pipeline,
    ImmutableDictionary<PickKey, TraceAdjustment> Adjustments,
    ImmutableDictionary<PickKey, ArrivalPick> Picks,
    ShotTimeSettings ShotTime,
    ImmutableDictionary<GatherId, ShotTimeEntry> ShotTimes,
    PickerSettings Picker,
    SegmentAssignment Segments,
    LayeredSettings Layered,
    TomographySettings Tomography,
    TomographyResultRef? TomographyResult,
    GatherDisplaySettings Display)
{
    public static LineInterpretation Create() => new(
        ProcessingPipeline.Recommended,
        ImmutableDictionary<PickKey, TraceAdjustment>.Empty,
        ImmutableDictionary<PickKey, ArrivalPick>.Empty,
        new ShotTimeSettings(),
        ImmutableDictionary<GatherId, ShotTimeEntry>.Empty,
        new PickerSettings(),
        SegmentAssignment.Default,
        new LayeredSettings(),
        new TomographySettings(),
        null,
        new GatherDisplaySettings());

    public TraceAdjustment AdjustmentOf(PickKey key)
        => Adjustments.TryGetValue(key, out var a) ? a : TraceAdjustment.None;

    public ArrivalPick? PickOf(PickKey key) => Picks.TryGetValue(key, out var p) ? p : null;
}

/// <summary>Where density values come from.</summary>
public enum DensitySource
{
    /// <summary>User-supplied measured/assumed values by depth interval.</summary>
    Measured,

    /// <summary>Estimated from Vp by an explicitly selected empirical relation.</summary>
    Estimated,
}

/// <summary>Empirical density estimators (selected explicitly; results are labelled ESTIMATED).</summary>
public enum DensityEstimator
{
    /// <summary>Gardner, Gardner &amp; Gregory (1974): ρ = 310·Vp(m/s)^0.25 kg/m³ (≈ 1743·Vp(km/s)^0.25).</summary>
    Gardner1974,

    /// <summary>Brocher (2005) polynomial fit to the Nafe–Drake curve, valid 1.5 ≤ Vp ≤ 8.5 km/s.</summary>
    Brocher2005,
}

/// <summary>A density value over a depth interval below ground.</summary>
public sealed record DensityInterval(double TopDepth, double BottomDepth, double Density, string? Note = null);

/// <summary>Density model. Density is never inferred unless <see cref="DensitySource.Estimated"/> is chosen.</summary>
public sealed record DensityModel(DensitySource Source, ImmutableArray<DensityInterval> Intervals, DensityEstimator? Estimator, string? Notes)
{
    public static DensityModel None { get; } = new(DensitySource.Measured, [], null, null);
}

/// <summary>Which velocity model feeds Vs30 and engineering properties.</summary>
public enum VelocityModelSource
{
    Tomography,
    Layered,
}

/// <summary>A depth interval for equivalent-velocity calculations.</summary>
public sealed record DepthInterval(double Top, double Bottom);

/// <summary>Vs30 / VsZ settings.</summary>
/// <param name="Positions">In-line positions (m) where 1-D profiles are evaluated.</param>
/// <param name="TargetDepth">Averaging depth (30 m for Vs30).</param>
/// <param name="AllowExtrapolation">User-enabled extrapolation below the deepest constrained depth (results are labelled EXTRAPOLATED).</param>
/// <param name="CustomIntervals">Additional equivalent-Vs intervals.</param>
public sealed record Vs30Settings(ImmutableArray<double> Positions, double TargetDepth = 30.0, bool AllowExtrapolation = false, ImmutableArray<DepthInterval> CustomIntervals = default)
{
    /// <summary>Name of the time-averaged velocity for this depth: "Vs30", "Vs20", …</summary>
    public string Label => LabelFor(TargetDepth);

    public static string LabelFor(double depth) => "Vs" + depth.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Pairing of P and SH lines for Vp/Vs-based properties.</summary>
/// <param name="PLine">P-wave line.</param>
/// <param name="ShLine">SH-wave line (also used for Vs30).</param>
/// <param name="Source">Model used.</param>
/// <param name="ProfilePositions">Positions (m) where 1-D property profiles are reported.</param>
/// <param name="DepthStep">Depth sampling of reported profiles (m).</param>
public sealed record EngineeringSettings(LineId? PLine, LineId? ShLine, VelocityModelSource Source, ImmutableArray<double> ProfilePositions, double DepthStep = 1.0);

/// <summary>A user-defined velocity class for section colouring and reporting (no built-in meaning).</summary>
public sealed record VelocityClass(string Name, double MinVelocity, double MaxVelocity, string ColorHex, string? Description);

/// <summary>Thresholds of automatic quality checks (documented in docs/algorithms/qc.md).</summary>
/// <param name="ReciprocalToleranceAbsolute">Allowed reciprocal-time difference (s).</param>
/// <param name="ReciprocalToleranceRelative">Allowed reciprocal-time difference as a fraction of the reciprocal time.</param>
/// <param name="LowSnr">Amplitude SNR below which a pick is flagged.</param>
/// <param name="MaxResidualSeconds">Model residual above which a pick is flagged.</param>
/// <param name="AirWaveVelocityMin">Lower bound of the air-wave velocity band (m/s).</param>
/// <param name="AirWaveVelocityMax">Upper bound of the air-wave velocity band (m/s).</param>
/// <param name="TriggerInterceptTolerance">Direct-wave zero-offset intercept beyond which a trigger problem is suspected (s).</param>
/// <param name="TimeReversalTolerance">Allowed decrease of travel time with increasing offset (s).</param>
public sealed record QcThresholds(
    double ReciprocalToleranceAbsolute = 0.002,
    double ReciprocalToleranceRelative = 0.03,
    double LowSnr = 3.0,
    double MaxResidualSeconds = 0.003,
    double AirWaveVelocityMin = 320.0,
    double AirWaveVelocityMax = 360.0,
    double TriggerInterceptTolerance = 0.002,
    double TimeReversalTolerance = 0.001);

/// <summary>Project-wide analysis settings of a scenario.</summary>
public sealed record ScenarioSettings(
    EngineeringSettings Engineering,
    DensityModel Density,
    Vs30Settings Vs30,
    ImmutableArray<VelocityClass> VelocityClasses,
    ImmutableArray<double> IsolineVelocities,
    QcThresholds Qc)
{
    public static ScenarioSettings Default { get; } = new(
        new EngineeringSettings(null, null, VelocityModelSource.Tomography, []),
        DensityModel.None,
        new Vs30Settings([], 30.0, false, []),
        [],
        [],
        new QcThresholds());
}

/// <summary>An alternative interpretation sharing the project's raw data.</summary>
public sealed record AnalysisScenario(
    ScenarioId Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    ImmutableDictionary<LineId, LineInterpretation> Lines,
    ScenarioSettings Settings)
{
    public LineInterpretation LineOrDefault(LineId id)
        => Lines.TryGetValue(id, out var l) ? l : LineInterpretation.Create();
}
