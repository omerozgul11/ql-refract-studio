using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.Picking;

/// <summary>A trace-level pick candidate.</summary>
/// <param name="Index">Fractional sample index of the onset.</param>
/// <param name="Sharpness">0–1 measure of how distinct the onset is for this picker.</param>
/// <param name="Description">What the sharpness value means, in words.</param>
public sealed record PickCandidate(double Index, double Sharpness, string Description);

/// <summary>A single-trace first-break picker.</summary>
public interface IFirstBreakPicker
{
    PickerAlgorithm Algorithm { get; }

    /// <summary>User-facing name.</summary>
    string Name { get; }

    /// <summary>Picks the onset within samples [start, end] of the trace; null if no onset is detectable.</summary>
    PickCandidate? Pick(ReadOnlySpan<double> trace, int start, int end, double sampleInterval, PickerSettings settings);
}

/// <summary>
/// Akaike information criterion picker (Maeda, N., 1985, J. Seismol. Soc. Japan 38, 365–379):
/// AIC(k) = k·ln var(x[a..a+k−1]) + (N − k − 1)·ln var(x[a+k..b]) for k = 2…N−3 (N samples in [a, b]); the onset is the minimum within the onset
/// window (see <see cref="OnsetWindow"/>).
/// Sharpness = (mean AIC − min AIC)/(max AIC − min AIC).
/// </summary>
public sealed class AicPicker : IFirstBreakPicker
{
    public PickerAlgorithm Algorithm => PickerAlgorithm.Aic;
    public string Name => "AIC";

    public PickCandidate? Pick(ReadOnlySpan<double> trace, int start, int end, double sampleInterval, PickerSettings settings)
    {
        start = Math.Max(0, start);
        end = Math.Min(trace.Length - 1, end);
        if (OnsetWindow.IsFlat(trace, start, end)) return null;
        (start, end) = OnsetWindow.Around(trace, start, end, sampleInterval, settings);
        return PickInWindow(trace, start, end);
    }

    /// <summary>AIC minimum within exactly [start, end] (no pre-windowing).</summary>
    public static PickCandidate? PickInWindow(ReadOnlySpan<double> trace, int start, int end)
    {
        var n = end - start + 1;
        if (n < 8) return null;
        var s1 = new double[n + 1];
        var s2 = new double[n + 1];
        for (var i = 0; i < n; i++)
        {
            var v = trace[start + i];
            s1[i + 1] = s1[i] + v;
            s2[i + 1] = s2[i] + v * v;
        }
        double Var(int a, int b) // samples a..b-1 relative
        {
            var m = b - a;
            var mean = (s1[b] - s1[a]) / m;
            return Math.Max((s2[b] - s2[a]) / m - mean * mean, 1e-300);
        }
        var aic = new double[n];
        double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
        var argmin = -1;
        var count = 0;
        for (var k = 2; k < n - 2; k++)
        {
            var value = k * Math.Log(Var(0, k)) + (n - k - 1) * Math.Log(Var(k, n));
            aic[k] = value;
            sum += value; count++;
            if (value < min) { min = value; argmin = k; }
            if (value > max) max = value;
        }
        if (argmin < 0 || max - min <= 0) return null;
        var sharp = Math.Clamp((sum / count - min) / (max - min), 0, 1);
        return new PickCandidate(start + argmin, sharp, $"AIC minimum depth {sharp:0.00} of its range");
    }
}

/// <summary>
/// STA/LTA picker (after Allen, R., 1978, BSSA 68, 1521–1532) on local signal variance. The trigger is the first
/// sample where STA/LTA reaches the trigger threshold (10 % of the window maximum, clamped to 4–50; half the
/// maximum for weak records) and stays there for one STA window; the onset is found by walking
/// back while the ratio is still rising above 1.5. Sharpness = 1 − 1/max ratio.
/// </summary>
public sealed class StaLtaPicker : IFirstBreakPicker
{
    public PickerAlgorithm Algorithm => PickerAlgorithm.StaLta;
    public string Name => "STA/LTA";

    public PickCandidate? Pick(ReadOnlySpan<double> trace, int start, int end, double sampleInterval, PickerSettings settings)
    {
        end = Math.Min(trace.Length - 1, end);
        start = Math.Max(0, start);
        if (end - start < 8 || OnsetWindow.IsFlat(trace, start, end)) return null;
        var (trigger, rmax, ratios, first) = OnsetWindow.Trigger(trace, start, end, sampleInterval, settings);
        if (trigger < 0) return null;
        // Onset: walk back from the trigger while the ratio is still rising above the stationary level (≈ 1).
        var onset = trigger - first;
        while (onset > 0 && ratios[onset - 1] > 1.5 && ratios[onset - 1] < ratios[onset]) onset--;
        var sharp = Math.Clamp(1 - 1 / rmax, 0, 1);
        return new PickCandidate(first + onset, sharp, $"STA/LTA maximum {rmax:0.0}");
    }
}

/// <summary>
/// Modified energy ratio picker (Wong, J., Han, L., Bancroft, J., Stewart, R., 2009, CREWES Report 21):
/// with L = 2 × short window: ER(i) = energy of x[i..i+L−1] / variance of x[i−L..i−1], both about the mean m of the
/// preceding window (insensitive to DC offsets), and MER(i) = (ER(i)·|x_i − m|)³. The maximum of MER within the onset
/// window (see <see cref="OnsetWindow"/>) usually sits on the first peak, so the pick steps back to the start of that peak.
/// Sharpness grows with the energy ratio at the pick (log scale, 1 at ER ≥ 100).
/// </summary>
public sealed class EnergyRatioPicker : IFirstBreakPicker
{
    public PickerAlgorithm Algorithm => PickerAlgorithm.EnergyRatio;
    public string Name => "Modified energy ratio";

    public PickCandidate? Pick(ReadOnlySpan<double> trace, int start, int end, double sampleInterval, PickerSettings settings)
    {
        if (OnsetWindow.IsFlat(trace, start, end)) return null;
        (start, end) = OnsetWindow.Around(trace, Math.Max(0, start), Math.Min(trace.Length - 1, end), sampleInterval, settings);
        var l = Math.Max(3, (int)Math.Round(settings.ShortWindow * 2 / sampleInterval));
        start = Math.Max(l, start);
        end = Math.Min(trace.Length - l, end);
        if (end - start < 4) return null;
        var p1 = new double[trace.Length + 1];
        var p2 = new double[trace.Length + 1];
        for (var i = 0; i < trace.Length; i++)
        {
            p1[i + 1] = p1[i] + trace[i];
            p2[i + 1] = p2[i] + trace[i] * trace[i];
        }
        var eps = 1e-12 * p2[^1] / trace.Length + 1e-300;
        var best = -1;
        double bestMer = 0, bestEr = 0, baseline = 0;
        for (var i = start; i <= end; i++)
        {
            // Energies about the mean of the preceding window (insensitive to DC offsets).
            var mBefore = (p1[i] - p1[i - l]) / l;
            var before = OnsetWindow.LocalVariance(p1, p2, i - l, i);
            var after = ((p2[i + l] - p2[i]) - 2 * mBefore * (p1[i + l] - p1[i])) / l + mBefore * mBefore;
            var er = Math.Max(0, after) / Math.Max(before, eps);
            var m = er * Math.Abs(trace[i] - mBefore);
            var mer = m * m * m;
            if (mer > bestMer) { bestMer = mer; best = i; bestEr = er; baseline = mBefore; }
        }
        if (best < 0) return null;
        // The MER maximum tends to sit on the first peak; step back to the zero crossing that starts it.
        var onset = best;
        var sign = Math.Sign(trace[best] - baseline);
        while (onset > start && Math.Sign(trace[onset - 1] - baseline) == sign && Math.Abs(trace[onset - 1] - baseline) < Math.Abs(trace[onset] - baseline)) onset--;
        var sharp = Math.Clamp(Math.Log10(Math.Max(bestEr, 1)) / 2, 0, 1);
        return new PickCandidate(onset, sharp, $"energy ratio {bestEr:0.0}");
    }
}

/// <summary>
/// Locates the first energy onset with an STA/LTA threshold trigger and returns a narrow window
/// around it. Pickers that respond to the largest change in a window (AIC, energy ratio) are run
/// inside this window so that strong later arrivals (surface waves) cannot capture the pick
/// (cf. the two-step approach of Sleeman &amp; van Eck 1999, PEPI 113).
/// </summary>
public static class OnsetWindow
{
    /// <summary>Lower bound of the STA/LTA trigger threshold.</summary>
    public const double TriggerThreshold = 4.0;

    /// <summary>Upper bound of the trigger threshold for very clean records.</summary>
    public const double MaxTriggerThreshold = 50.0;

    /// <summary>
    /// STA/LTA ratio series for samples first..end (index 0 = first), non-overlapping form: the long-term
    /// window [i−ns−nl+1, i−ns] gives the background mean m and variance σ²; the short-term window
    /// [i−ns+1, i] gives the mean squared deviation from m. The ratio is ≈ 1 on stationary noise, rises at an
    /// onset and stays high while the long window is still before it; DC offsets cancel exactly.
    /// </summary>
    public static double[] Ratio(ReadOnlySpan<double> trace, int first, int end, int ns, int nl)
    {
        var p1 = new double[trace.Length + 1];
        var p2 = new double[trace.Length + 1];
        for (var i = 0; i < trace.Length; i++)
        {
            p1[i + 1] = p1[i] + trace[i];
            p2[i + 1] = p2[i] + trace[i] * trace[i];
        }
        var floor = 1e-12 * p2[^1] / Math.Max(1, trace.Length) + 1e-300;
        var ratios = new double[end - first + 1];
        for (var i = first; i <= end; i++)
        {
            var sEnd = i + 1;
            var sStart = Math.Max(0, i - ns + 1);
            var lEnd = sStart;
            var lStart = Math.Max(0, lEnd - nl);
            if (lEnd - lStart < 4)
            {
                ratios[i - first] = 0;
                continue;
            }
            var m = (p1[lEnd] - p1[lStart]) / (lEnd - lStart);
            var lta = LocalVariance(p1, p2, lStart, lEnd);
            var n = sEnd - sStart;
            var sta = ((p2[sEnd] - p2[sStart]) - 2 * m * (p1[sEnd] - p1[sStart])) / n + m * m;
            ratios[i - first] = Math.Max(0, sta) / Math.Max(lta, floor);
        }
        return ratios;
    }

    /// <summary>Variance of samples [a, b) from prefix sums of x and x².</summary>
    public static double LocalVariance(double[] p1, double[] p2, int a, int b)
    {
        var n = b - a;
        if (n <= 0) return 0;
        var mean = (p1[b] - p1[a]) / n;
        return Math.Max(0, (p2[b] - p2[a]) / n - mean * mean);
    }

    /// <summary>First trigger index (absolute sample) or −1, and the maximum ratio.</summary>
    public static (int Trigger, double MaxRatio, double[] Ratios, int First) Trigger(ReadOnlySpan<double> trace, int start, int end, double sampleInterval, PickerSettings settings)
    {
        var ns = Math.Max(2, (int)Math.Round(settings.ShortWindow / sampleInterval));
        var nl = Math.Max(ns + 1, (int)Math.Round(settings.LongWindow / sampleInterval));
        var first = Math.Max(start, 1);
        if (end - first < 8) return (-1, 0, [], first);
        var ratios = Ratio(trace, first, end, ns, nl);
        var rmax = ratios.Max();
        if (rmax <= 1.05) return (-1, rmax, ratios, first);
        // Scale with the strongest onset: noise can briefly reach a ratio of a few, a clear arrival
        // reaches hundreds or thousands. Weak records fall back to half the window maximum.
        var threshold = rmax < 2 * TriggerThreshold ? 0.5 * rmax : Math.Clamp(0.1 * rmax, TriggerThreshold, MaxTriggerThreshold);
        // The ratio must stay above the threshold for one short window: brief noise bursts do not trigger.
        var sustain = Math.Max(2, ns);
        for (var i = 0; i < ratios.Length; i++)
        {
            if (ratios[i] < threshold) continue;
            var j = i;
            while (j < ratios.Length && ratios[j] >= threshold && j - i < sustain) j++;
            if (j - i >= sustain || j == ratios.Length) return (first + i, rmax, ratios, first);
            i = j;
        }
        return (-1, rmax, ratios, first);
    }

    /// <summary>Narrow window [trigger − LTA, trigger + 2·STA] around the first onset, or the input window.</summary>
    public static (int Start, int End) Around(ReadOnlySpan<double> trace, int start, int end, double sampleInterval, PickerSettings settings)
    {
        var (trigger, _, _, _) = Trigger(trace, start, end, sampleInterval, settings);
        if (trigger < 0) return (start, end);
        var ns = Math.Max(2, (int)Math.Round(settings.ShortWindow / sampleInterval));
        var nl = Math.Max(ns + 1, (int)Math.Round(settings.LongWindow / sampleInterval));
        return (Math.Max(start, trigger - nl), Math.Min(end, trigger + 2 * ns));
    }

    /// <summary>True if the window contains no signal at all (flat trace).</summary>
    public static bool IsFlat(ReadOnlySpan<double> trace, int start, int end)
    {
        start = Math.Max(0, start);
        end = Math.Min(trace.Length - 1, end);
        if (end < start) return true;
        var first = trace[start];
        for (var i = start + 1; i <= end; i++)
            if (trace[i] != first) return false;
        return true;
    }
}

/// <summary>Registry of single-trace pickers.</summary>
public static class Pickers
{
    public static IReadOnlyList<IFirstBreakPicker> All { get; } = [new AicPicker(), new StaLtaPicker(), new EnergyRatioPicker()];

    public static IFirstBreakPicker For(PickerAlgorithm algorithm)
        => All.FirstOrDefault(p => p.Algorithm == algorithm) ?? All[0];
}

/// <summary>Signal-to-noise measurement around a pick.</summary>
public static class SignalToNoise
{
    /// <summary>
    /// Amplitude SNR = RMS(signal window after the pick) / RMS(noise window before it). The noise window
    /// is <paramref name="noiseSeconds"/> long ending one sample before the pick; null when fewer than
    /// five noise samples exist (for example near-shot traces with no pre-arrival record).
    /// </summary>
    public static double? Measure(ReadOnlySpan<double> trace, double pickIndex, double sampleInterval, double signalSeconds, double noiseSeconds)
    {
        var p = (int)Math.Round(pickIndex);
        var ns = Math.Max(5, (int)Math.Round(signalSeconds / sampleInterval));
        var nn = Math.Max(5, (int)Math.Round(noiseSeconds / sampleInterval));
        var noiseEnd = p - 1;
        var noiseStart = Math.Max(0, noiseEnd - nn);
        if (noiseEnd - noiseStart < 5 || p >= trace.Length) return null;
        var signalEnd = Math.Min(trace.Length, p + ns);
        var noise = SignalProcessing.Statistics.Rms(trace[noiseStart..noiseEnd]);
        var signal = SignalProcessing.Statistics.Rms(trace[p..signalEnd]);
        if (noise <= 0) return signal > 0 ? 1e6 : null;
        return signal / noise;
    }
}
