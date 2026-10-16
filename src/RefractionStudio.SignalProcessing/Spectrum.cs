namespace RefractionStudio.SignalProcessing;

/// <summary>One-sided amplitude spectrum with summary measures.</summary>
/// <param name="Frequencies">Frequency of each bin (Hz).</param>
/// <param name="Amplitudes">Amplitude of each bin (input units, Hann-window corrected).</param>
/// <param name="DominantFrequency">Frequency of the largest amplitude above 0 Hz.</param>
/// <param name="BandLow">Lower frequency where the smoothed spectrum falls to −<see cref="BandLevelDb"/> dB of the peak.</param>
/// <param name="BandHigh">Upper frequency at that level.</param>
/// <param name="BandLevelDb">Level used for the bandwidth.</param>
/// <param name="SpectralCentroid">Amplitude-weighted mean frequency.</param>
public sealed record AmplitudeSpectrum(
    double[] Frequencies,
    double[] Amplitudes,
    double DominantFrequency,
    double BandLow,
    double BandHigh,
    double BandLevelDb,
    double SpectralCentroid)
{
    public double Bandwidth => BandHigh - BandLow;
}

/// <summary>Spectral estimation.</summary>
public static class SpectrumAnalysis
{
    /// <summary>
    /// Hann-windowed amplitude spectrum. The bandwidth is measured on a 5-bin moving-average smoothed
    /// spectrum at <paramref name="bandLevelDb"/> dB below the peak (default −20 dB).
    /// </summary>
    public static AmplitudeSpectrum Compute(ReadOnlySpan<double> signal, double sampleInterval, double bandLevelDb = 20)
    {
        var n = signal.Length;
        if (n < 4) throw new ArgumentException("At least four samples are needed for a spectrum.", nameof(signal));
        var mean = Statistics.Mean(signal);
        var windowed = new double[n];
        double windowSum = 0;
        for (var i = 0; i < n; i++)
        {
            var w = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
            windowSum += w;
            windowed[i] = (signal[i] - mean) * w;
        }
        var m = Fft.NextPowerOfTwo(n) * 2;
        var spec = Fft.ForwardReal(windowed, m);
        var bins = m / 2 + 1;
        var freqs = new double[bins];
        var amps = new double[bins];
        var df = 1.0 / (m * sampleInterval);
        for (var k = 0; k < bins; k++)
        {
            freqs[k] = k * df;
            var scale = (k == 0 || k == m / 2 ? 1.0 : 2.0) / windowSum;
            amps[k] = spec[k].Magnitude * scale;
        }

        var peakIdx = 1;
        for (var k = 1; k < bins; k++)
            if (amps[k] > amps[peakIdx]) peakIdx = k;

        var smooth = new double[bins];
        for (var k = 0; k < bins; k++)
        {
            double s = 0;
            var c = 0;
            for (var j = Math.Max(0, k - 2); j <= Math.Min(bins - 1, k + 2); j++) { s += amps[j]; c++; }
            smooth[k] = s / c;
        }
        var smoothPeak = 1;
        for (var k = 1; k < bins; k++)
            if (smooth[k] > smooth[smoothPeak]) smoothPeak = k;
        var threshold = smooth[smoothPeak] * Math.Pow(10, -bandLevelDb / 20);
        var lo = smoothPeak;
        while (lo > 1 && smooth[lo - 1] >= threshold) lo--;
        var hi = smoothPeak;
        while (hi < bins - 1 && smooth[hi + 1] >= threshold) hi++;

        double num = 0, den = 0;
        for (var k = 1; k < bins; k++) { num += freqs[k] * amps[k]; den += amps[k]; }

        return new AmplitudeSpectrum(freqs, amps, freqs[peakIdx], freqs[lo], freqs[hi], bandLevelDb, den > 0 ? num / den : 0);
    }

    /// <summary>
    /// Average amplitude spectrum of several traces (each normalised to unit RMS first so that
    /// strong near-shot traces do not dominate).
    /// </summary>
    public static AmplitudeSpectrum Average(IReadOnlyList<double[]> traces, double sampleInterval, double bandLevelDb = 20)
    {
        if (traces.Count == 0) throw new ArgumentException("No traces.", nameof(traces));
        var spectra = traces.Where(t => t.Length >= 4).Select(t => Compute(TraceOps.Normalize(t, rms: true), sampleInterval, bandLevelDb)).ToList();
        if (spectra.Count == 0) throw new ArgumentException("Traces are too short for a spectrum.", nameof(traces));
        var bins = spectra.Min(s => s.Frequencies.Length);
        var amps = new double[bins];
        foreach (var s in spectra)
            for (var k = 0; k < bins; k++) amps[k] += s.Amplitudes[k] / spectra.Count;
        // Rebuild measures on the averaged spectrum using the same rules.
        var freqs = spectra[0].Frequencies[..bins];
        var peakIdx = 1;
        for (var k = 1; k < bins; k++) if (amps[k] > amps[peakIdx]) peakIdx = k;
        var threshold = amps[peakIdx] * Math.Pow(10, -bandLevelDb / 20);
        var lo = peakIdx;
        while (lo > 1 && amps[lo - 1] >= threshold) lo--;
        var hi = peakIdx;
        while (hi < bins - 1 && amps[hi + 1] >= threshold) hi++;
        double num = 0, den = 0;
        for (var k = 1; k < bins; k++) { num += freqs[k] * amps[k]; den += amps[k]; }
        return new AmplitudeSpectrum(freqs, amps, freqs[peakIdx], freqs[lo], freqs[hi], bandLevelDb, den > 0 ? num / den : 0);
    }

    /// <summary>
    /// Suggests band-pass corners from a spectrum: the −20 dB band widened by half an octave on each
    /// side, the high corner limited to 80 % of Nyquist and the low corner to at least 1 Hz.
    /// This is a suggestion only; it is never applied automatically.
    /// </summary>
    public static (double LowHz, double HighHz) SuggestBandPass(AmplitudeSpectrum spectrum, double sampleInterval)
    {
        var nyquist = 0.5 / sampleInterval;
        var low = Math.Max(1.0, spectrum.BandLow / Math.Sqrt(2));
        var high = Math.Min(0.8 * nyquist, spectrum.BandHigh * Math.Sqrt(2));
        if (high <= low * 1.5) high = Math.Min(0.8 * nyquist, low * 4);
        return (low, high);
    }
}
