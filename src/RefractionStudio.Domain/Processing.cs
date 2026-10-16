using System.Globalization;
using System.Text.Json.Serialization;

namespace RefractionStudio.Domain;

/// <summary>Shape of a taper window.</summary>
public enum TaperShape
{
    /// <summary>Raised cosine (Hann) half-windows at both ends.</summary>
    Hann,

    /// <summary>Linear ramps at both ends.</summary>
    Linear,
}

/// <summary>Amplitude normalisation mode.</summary>
public enum NormalizeMode
{
    /// <summary>Divide by the maximum absolute amplitude.</summary>
    Peak,

    /// <summary>Divide by the root-mean-square amplitude.</summary>
    Rms,
}

/// <summary>
/// One non-destructive operation of a processing pipeline. Steps describe what to do; the
/// implementation lives in Core/SignalProcessing. Records so that pipelines can be fingerprinted.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RemoveMeanStep), "removeMean")]
[JsonDerivedType(typeof(DetrendStep), "detrend")]
[JsonDerivedType(typeof(TaperStep), "taper")]
[JsonDerivedType(typeof(HighPassStep), "highPass")]
[JsonDerivedType(typeof(LowPassStep), "lowPass")]
[JsonDerivedType(typeof(BandPassStep), "bandPass")]
[JsonDerivedType(typeof(NotchStep), "notch")]
[JsonDerivedType(typeof(NormalizeStep), "normalize")]
[JsonDerivedType(typeof(BalanceStep), "balance")]
[JsonDerivedType(typeof(AgcStep), "agc")]
[JsonDerivedType(typeof(PolarityStep), "polarity")]
[JsonDerivedType(typeof(TimeShiftStep), "timeShift")]
[JsonDerivedType(typeof(ResampleStep), "resample")]
public abstract record ProcessingStep(StepId Id, bool Enabled)
{
    /// <summary>Short user-facing name, e.g. "Band-pass 8–80 Hz".</summary>
    public abstract string DisplayName { get; }

    /// <summary>Parameters as text for history and reports.</summary>
    public abstract IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>True for steps that move samples in time (picks remain in record time).</summary>
    public virtual bool ShiftsTime => false;

    /// <summary>True for steps that change amplitudes non-linearly in time (display aids).</summary>
    public virtual bool IsAmplitudeDisplayAid => false;

    protected static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Subtracts the mean (DC offset removal).</summary>
public sealed record RemoveMeanStep(StepId Id, bool Enabled) : ProcessingStep(Id, Enabled)
{
    public override string DisplayName => "Remove DC offset (demean)";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string>();
}

/// <summary>Removes the least-squares straight line.</summary>
public sealed record DetrendStep(StepId Id, bool Enabled) : ProcessingStep(Id, Enabled)
{
    public override string DisplayName => "Linear detrend";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string>();
}

/// <summary>Tapers both ends of the trace.</summary>
/// <param name="Fraction">Fraction (0–0.5) of the trace length tapered at each end.</param>
public sealed record TaperStep(StepId Id, bool Enabled, double Fraction, TaperShape Shape) : ProcessingStep(Id, Enabled)
{
    public override string DisplayName => $"Taper {F(Fraction * 100)} %";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string>
    {
        ["fraction"] = F(Fraction), ["shape"] = Shape.ToString(),
    };
}

/// <summary>Butterworth high-pass.</summary>
public sealed record HighPassStep(StepId Id, bool Enabled, double CornerHz, int Order, bool ZeroPhase) : ProcessingStep(Id, Enabled)
{
    public override string DisplayName => $"High-pass {F(CornerHz)} Hz";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string>
    {
        ["cornerHz"] = F(CornerHz), ["order"] = Order.ToString(CultureInfo.InvariantCulture), ["zeroPhase"] = ZeroPhase ? "yes" : "no",
    };
}

/// <summary>Butterworth low-pass.</summary>
public sealed record LowPassStep(StepId Id, bool Enabled, double CornerHz, int Order, bool ZeroPhase) : ProcessingStep(Id, Enabled)
{
    public override string DisplayName => $"Low-pass {F(CornerHz)} Hz";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string>
    {
        ["cornerHz"] = F(CornerHz), ["order"] = Order.ToString(CultureInfo.InvariantCulture), ["zeroPhase"] = ZeroPhase ? "yes" : "no",
    };
}

/// <summary>Butterworth band-pass (cascade of high- and low-pass sections).</summary>
public sealed record BandPassStep(StepId Id, bool Enabled, double LowHz, double HighHz, int Order, bool ZeroPhase) : ProcessingStep(Id, Enabled)
{
    public override string DisplayName => $"Band-pass {F(LowHz)}–{F(HighHz)} Hz";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string>
    {
        ["lowHz"] = F(LowHz), ["highHz"] = F(HighHz), ["order"] = Order.ToString(CultureInfo.InvariantCulture), ["zeroPhase"] = ZeroPhase ? "yes" : "no",
    };
}

/// <summary>Second-order IIR notch, optionally including harmonics.</summary>
public sealed record NotchStep(StepId Id, bool Enabled, double FrequencyHz, double Q, int Harmonics) : ProcessingStep(Id, Enabled)
{
    public override string DisplayName => Harmonics > 1 ? $"Notch {F(FrequencyHz)} Hz ×{Harmonics}" : $"Notch {F(FrequencyHz)} Hz";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string>
    {
        ["frequencyHz"] = F(FrequencyHz), ["q"] = F(Q), ["harmonics"] = Harmonics.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>Scales every trace to unit peak or RMS.</summary>
public sealed record NormalizeStep(StepId Id, bool Enabled, NormalizeMode Mode) : ProcessingStep(Id, Enabled)
{
    public override string DisplayName => Mode == NormalizeMode.Peak ? "Normalize (peak)" : "Normalize (RMS)";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string> { ["mode"] = Mode.ToString() };
}

/// <summary>Trace balance: scales each trace to unit RMS inside a record-time window.</summary>
public sealed record BalanceStep(StepId Id, bool Enabled, double WindowStart, double WindowEnd) : ProcessingStep(Id, Enabled)
{
    public override string DisplayName => $"Trace balance {F(WindowStart * 1000)}–{F(WindowEnd * 1000)} ms";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string>
    {
        ["windowStartS"] = F(WindowStart), ["windowEndS"] = F(WindowEnd),
    };
}

/// <summary>Automatic gain control (sliding RMS window). A display aid: it distorts amplitudes.</summary>
public sealed record AgcStep(StepId Id, bool Enabled, double WindowSeconds) : ProcessingStep(Id, Enabled)
{
    public override string DisplayName => $"AGC {F(WindowSeconds * 1000)} ms";
    public override bool IsAmplitudeDisplayAid => true;
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string> { ["windowS"] = F(WindowSeconds) };
}

/// <summary>Reverses the polarity of every trace.</summary>
public sealed record PolarityStep(StepId Id, bool Enabled) : ProcessingStep(Id, Enabled)
{
    public override string DisplayName => "Polarity reversal";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string>();
}

/// <summary>Shifts all samples by a constant time (positive = later). Picks stay in record time.</summary>
public sealed record TimeShiftStep(StepId Id, bool Enabled, double Seconds) : ProcessingStep(Id, Enabled)
{
    public override string DisplayName => $"Time shift {F(Seconds * 1000)} ms";
    public override bool ShiftsTime => true;
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string> { ["seconds"] = F(Seconds) };
}

/// <summary>Band-limited resampling to a new sample interval with anti-alias filtering.</summary>
public sealed record ResampleStep(StepId Id, bool Enabled, double NewSampleInterval) : ProcessingStep(Id, Enabled)
{
    public override string DisplayName => $"Resample to {F(NewSampleInterval * 1e6)} µs";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string> { ["sampleIntervalS"] = F(NewSampleInterval) };
}

/// <summary>An ordered, editable list of processing steps.</summary>
public sealed record ProcessingPipeline(ImmutableArray<ProcessingStep> Steps)
{
    public static ProcessingPipeline Empty { get; } = new([]);

    /// <summary>A conservative default used in guided mode: demean only.</summary>
    public static ProcessingPipeline Recommended { get; } = new([new RemoveMeanStep(StepId.New(), true)]);

    public IEnumerable<ProcessingStep> EnabledSteps => Steps.Where(s => s.Enabled);

    /// <summary>Stable text fingerprint of the enabled steps (cache key).</summary>
    public string Fingerprint()
    {
        var parts = EnabledSteps.Select(s => s.GetType().Name + "(" +
            string.Join(",", s.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + ")");
        return string.Join("|", parts);
    }

    /// <summary>Total time shift applied by enabled steps.</summary>
    public double TotalTimeShift => EnabledSteps.OfType<TimeShiftStep>().Sum(s => s.Seconds);

    public ProcessingPipeline Replace(ProcessingStep step)
        => new(Steps.Select(s => s.Id == step.Id ? step : s).ToImmutableArray());
}

/// <summary>User adjustments of a single trace (non-destructive).</summary>
/// <param name="Gain">Display/processing gain multiplier.</param>
/// <param name="InvertPolarity">Reverse this trace only.</param>
/// <param name="Excluded">Exclude the trace from picking and analysis (kept visible, marked).</param>
/// <param name="TimeShift">Static shift of this trace in seconds (positive = later).</param>
/// <param name="Note">Reason for the adjustment.</param>
public sealed record TraceAdjustment(double Gain = 1.0, bool InvertPolarity = false, bool Excluded = false, double TimeShift = 0.0, string? Note = null)
{
    public static TraceAdjustment None { get; } = new();

    public bool IsNeutral => Gain == 1.0 && !InvertPolarity && !Excluded && TimeShift == 0.0;

    public string Fingerprint() => string.Create(CultureInfo.InvariantCulture, $"{Gain:R}|{InvertPolarity}|{TimeShift:R}");
}

/// <summary>How gathers are drawn.</summary>
public enum TraceDisplayMode
{
    Wiggle,
    VariableArea,
    WiggleVariableArea,
    Image,
}

/// <summary>Amplitude scaling for display.</summary>
public enum DisplayScaling
{
    /// <summary>Each trace scaled to its own maximum.</summary>
    PerTrace,

    /// <summary>All traces share one scale (true relative amplitudes).</summary>
    Global,
}

/// <summary>Presentation settings of the gather viewer. Never affect picks or analysis.</summary>
public sealed record GatherDisplaySettings(
    TraceDisplayMode Mode = TraceDisplayMode.WiggleVariableArea,
    DisplayScaling Scaling = DisplayScaling.PerTrace,
    double Gain = 1.0,
    double Clip = 1.5,
    double? ReductionVelocity = null,
    bool ShowProcessed = true,
    bool OverlayRaw = false,
    double? TimeWindowEnd = null);
