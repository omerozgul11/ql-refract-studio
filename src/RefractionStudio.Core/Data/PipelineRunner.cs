using System.Globalization;
using RefractionStudio.Domain;
using RefractionStudio.SignalProcessing;

namespace RefractionStudio.Core.Data;

/// <summary>Result of running a pipeline on one trace.</summary>
/// <param name="Samples">Processed samples (a new array).</param>
/// <param name="SampleInterval">Sample interval after processing (changes only with resampling).</param>
/// <param name="Problems">Steps that could not be applied and why (they were skipped).</param>
public sealed record PipelineOutput(double[] Samples, double SampleInterval, ImmutableArray<string> Problems);

/// <summary>Executes processing pipelines. Never modifies its input.</summary>
public static class PipelineRunner
{
    /// <summary>Checks every enabled step against a sample interval; returns user-facing problems.</summary>
    public static ImmutableArray<string> Validate(ProcessingPipeline pipeline, double sampleInterval)
    {
        var problems = ImmutableArray.CreateBuilder<string>();
        var dt = sampleInterval;
        foreach (var step in pipeline.EnabledSteps)
        {
            var nyquist = 0.5 / dt;
            string? p = step switch
            {
                HighPassStep h when !(h.CornerHz > 0 && h.CornerHz < nyquist) => $"{step.DisplayName}: the corner must be between 0 and {nyquist:0.#} Hz (Nyquist).",
                LowPassStep l when !(l.CornerHz > 0 && l.CornerHz < nyquist) => $"{step.DisplayName}: the corner must be between 0 and {nyquist:0.#} Hz (Nyquist).",
                BandPassStep b when !(b.LowHz > 0 && b.LowHz < b.HighHz && b.HighHz < nyquist) => $"{step.DisplayName}: corners must satisfy 0 < low < high < {nyquist:0.#} Hz.",
                HighPassStep or LowPassStep or BandPassStep when Order(step) is < 1 or > 10 => $"{step.DisplayName}: the filter order must be 1–10.",
                NotchStep n when !(n.FrequencyHz > 0 && n.FrequencyHz < nyquist && n.Q > 0 && n.Harmonics >= 1) => $"{step.DisplayName}: frequency must be below Nyquist and Q positive.",
                TaperStep t when t.Fraction is < 0 or > 0.5 => $"{step.DisplayName}: the taper fraction must be 0–50 %.",
                AgcStep a when !(a.WindowSeconds > dt) => $"{step.DisplayName}: the window must be longer than one sample.",
                BalanceStep b2 when !(b2.WindowEnd > b2.WindowStart) => $"{step.DisplayName}: the window end must follow its start.",
                ResampleStep r when !(r.NewSampleInterval > 0) => $"{step.DisplayName}: the sample interval must be positive.",
                _ => null,
            };
            if (p is not null) problems.Add(p);
            else if (step is ResampleStep rs) dt = rs.NewSampleInterval;
        }
        return problems.ToImmutable();
    }

    /// <summary>
    /// Advice that is not an error: zero-phase filters are acausal and spread energy ahead of the true
    /// onset, which biases first-break picks early (by up to about a quarter period of the low corner);
    /// AGC distorts relative amplitudes.
    /// </summary>
    public static ImmutableArray<string> Advice(ProcessingPipeline pipeline)
    {
        var advice = ImmutableArray.CreateBuilder<string>();
        if (pipeline.EnabledSteps.Any(s => s is HighPassStep { ZeroPhase: true } or LowPassStep { ZeroPhase: true } or BandPassStep { ZeroPhase: true }))
            advice.Add("Zero-phase filtering spreads energy ahead of the true onset and can bias first-break picks early. Use causal filters while picking, or verify picks on unfiltered traces.");
        foreach (var step in pipeline.EnabledSteps)
        {
            var corner = step switch
            {
                LowPassStep { ZeroPhase: false } l => (double?)l.CornerHz,
                BandPassStep { ZeroPhase: false } b => b.HighHz,
                _ => null,
            };
            var order = step switch { LowPassStep l => l.Order, BandPassStep b => b.Order, _ => 1 };
            if (corner is { } fc && fc > 0)
            {
                var delay = LowPassGroupDelay(order, fc);
                advice.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{step.DisplayName} is causal: its low-pass delays arrivals by about {delay * 1000:0.0} ms (group delay well below {fc:0} Hz), so first breaks picked on it will be late by that amount."));
            }
        }
        if (pipeline.EnabledSteps.Any(s => s is NotchStep))
            advice.Add("The notch filter is applied forward and backward (zero-phase). Its effect on onset times is small for narrow notches (high Q), " +
                       "but its ringing can precede strong arrivals; check picks on unfiltered traces when the notch is close to the signal band.");
        if (pipeline.EnabledSteps.Any(s => s is ResampleStep))
            advice.Add("Resampling uses a zero-phase (symmetric) interpolation and anti-alias filter, which can place a small precursor ahead of sharp onsets; " +
                       "pick on the original sampling when possible.");
        if (pipeline.EnabledSteps.Any(s => s is AgcStep))
            advice.Add("AGC changes relative amplitudes; use it as a display aid only. Signal-to-noise values are measured on the processed traces.");
        return advice.ToImmutable();
    }

    /// <summary>
    /// Low-frequency group delay of an analog Butterworth low-pass of order n and corner fc:
    /// τ = Σ_k −Re(1/p_k)/ω_c = (1/ω_c)·Σ sin((2k−1)π/2n)⁻¹ … evaluated from the normalised poles.
    /// </summary>
    public static double LowPassGroupDelay(int order, double cornerHz)
    {
        // For H(s) = Π 1/(s/ω_c − p_k), τ(0) = Σ −1/(ω_c·Re(p_k)) · (Re(p_k)²/|p_k|²) = Σ −Re(p_k)/(ω_c |p_k|²) with |p_k| = 1.
        double sum = 0;
        for (var k = 0; k < order; k++)
        {
            var theta = Math.PI / 2 + Math.PI * (2 * k + 1) / (2.0 * order);
            sum += -Math.Cos(theta);
        }
        return sum / (2 * Math.PI * cornerHz);
    }

    private static int Order(ProcessingStep s) => s switch
    {
        HighPassStep h => h.Order,
        LowPassStep l => l.Order,
        BandPassStep b => b.Order,
        _ => 1,
    };

    /// <summary>Runs the enabled steps in order; invalid steps are skipped and reported.</summary>
    /// <param name="samples">Input samples.</param>
    /// <param name="sampleInterval">Sample interval (s).</param>
    /// <param name="startTime">Record time of sample 0 (for time windows).</param>
    /// <param name="pipeline">Pipeline.</param>
    public static PipelineOutput Run(ReadOnlySpan<double> samples, double sampleInterval, double startTime, ProcessingPipeline pipeline)
    {
        var x = samples.ToArray();
        var dt = sampleInterval;
        var problems = ImmutableArray.CreateBuilder<string>();
        foreach (var step in pipeline.EnabledSteps)
        {
            var issues = Validate(new ProcessingPipeline([step]), dt);
            if (!issues.IsEmpty)
            {
                problems.AddRange(issues.Select(i => i + " Step skipped."));
                continue;
            }
            var fs = 1 / dt;
            x = step switch
            {
                RemoveMeanStep => TraceOps.RemoveMean(x),
                DetrendStep => TraceOps.Detrend(x),
                TaperStep t => TraceOps.Taper(x, t.Fraction, t.Shape == TaperShape.Hann),
                HighPassStep h => Filter(IirDesign.Butterworth(FilterType.HighPass, h.Order, h.CornerHz, fs), x, h.ZeroPhase),
                LowPassStep l => Filter(IirDesign.Butterworth(FilterType.LowPass, l.Order, l.CornerHz, fs), x, l.ZeroPhase),
                BandPassStep b => Filter(IirDesign.BandPass(b.Order, b.LowHz, b.HighHz, fs), x, b.ZeroPhase),
                NotchStep n => Notch(x, n, fs, problems),
                NormalizeStep nm => TraceOps.Normalize(x, nm.Mode == NormalizeMode.Rms),
                BalanceStep bal => TraceOps.Balance(x, (int)Math.Round((bal.WindowStart - startTime) / dt), (int)Math.Round((bal.WindowEnd - startTime) / dt)),
                AgcStep a => TraceOps.Agc(x, Math.Max(1, (int)Math.Round(a.WindowSeconds / dt))),
                PolarityStep => TraceOps.Scale(x, -1),
                TimeShiftStep ts => TraceOps.Shift(x, ts.Seconds, dt),
                ResampleStep r => TraceOps.Resample(x, dt, r.NewSampleInterval),
                _ => x,
            };
            if (step is ResampleStep rs) dt = rs.NewSampleInterval;
        }
        return new PipelineOutput(x, dt, problems.ToImmutable());
    }

    private static double[] Filter(SosFilter f, double[] x, bool zeroPhase) => zeroPhase ? f.FiltFilt(x) : f.Filter(x);

    private static double[] Notch(double[] x, NotchStep n, double fs, ImmutableArray<string>.Builder problems)
    {
        var y = x;
        for (var h = 1; h <= n.Harmonics; h++)
        {
            var f = n.FrequencyHz * h;
            if (f >= fs / 2)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"Notch harmonic {h} ({f:0.#} Hz) is above Nyquist and was skipped."));
                break;
            }
            y = new SosFilter([IirDesign.Notch(f, n.Q, fs)]).FiltFilt(y);
        }
        return y;
    }
}
