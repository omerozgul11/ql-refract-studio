using System.Numerics;

namespace RefractionStudio.SignalProcessing;

/// <summary>A normalised biquad section (a0 = 1).</summary>
public readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
{
    /// <summary>Complex response at normalised angular frequency ω (rad/sample).</summary>
    public Complex Response(double omega)
    {
        var z1 = Complex.FromPolarCoordinates(1, -omega);
        var z2 = z1 * z1;
        return (B0 + B1 * z1 + B2 * z2) / (1 + A1 * z1 + A2 * z2);
    }

    /// <summary>Gain at DC (ω = 0).</summary>
    public double DcGain => (B0 + B1 + B2) / (1 + A1 + A2);
}

/// <summary>Filter type of a Butterworth design.</summary>
public enum FilterType
{
    LowPass,
    HighPass,
}

/// <summary>Cascade of second-order sections.</summary>
public sealed class SosFilter
{
    public SosFilter(IReadOnlyList<Biquad> sections) => Sections = sections;

    public IReadOnlyList<Biquad> Sections { get; }

    /// <summary>Magnitude of the response at frequency <paramref name="hz"/>.</summary>
    public double Magnitude(double hz, double sampleRate)
    {
        var omega = 2 * Math.PI * hz / sampleRate;
        var h = Complex.One;
        foreach (var s in Sections) h *= s.Response(omega);
        return h.Magnitude;
    }

    /// <summary>Causal filtering (transposed direct form II), zero initial state.</summary>
    public double[] Filter(ReadOnlySpan<double> x)
    {
        var y = x.ToArray();
        foreach (var s in Sections) ApplySection(s, y, 0, 0);
        return y;
    }

    /// <summary>
    /// Zero-phase forward–backward filtering (magnitude squared, no phase shift). The signal is
    /// extended at both ends by odd reflection and the filter state is initialised to the steady
    /// state of the edge value, which suppresses start-up transients (the approach of Gustafsson
    /// 1996 as used by common scientific libraries).
    /// </summary>
    public double[] FiltFilt(ReadOnlySpan<double> x)
    {
        var n = x.Length;
        if (n == 0) return [];
        var pad = Math.Min(n - 1, 3 * (2 * Sections.Count + 1));
        var ext = new double[n + 2 * pad];
        for (var i = 0; i < pad; i++) ext[i] = 2 * x[0] - x[pad - i];
        x.CopyTo(ext.AsSpan(pad));
        for (var i = 0; i < pad; i++) ext[pad + n + i] = 2 * x[n - 1] - x[n - 2 - i];

        RunWithSteadyState(ext);
        Array.Reverse(ext);
        RunWithSteadyState(ext);
        Array.Reverse(ext);
        return ext.AsSpan(pad, n).ToArray();
    }

    private void RunWithSteadyState(double[] y)
    {
        // Each section starts in the steady state for a constant input equal to its own first input.
        foreach (var s in Sections)
        {
            var x0 = y[0];
            var dc = s.DcGain;
            var z1 = (dc - s.B0) * x0;
            var z2 = (s.B2 - s.A2 * dc) * x0;
            ApplySection(s, y, z1, z2);
        }
    }

    private static void ApplySection(Biquad s, double[] y, double z1, double z2)
    {
        for (var i = 0; i < y.Length; i++)
        {
            var xi = y[i];
            var yi = s.B0 * xi + z1;
            z1 = s.B1 * xi - s.A1 * yi + z2;
            z2 = s.B2 * xi - s.A2 * yi;
            y[i] = yi;
        }
    }
}

/// <summary>Digital IIR filter design.</summary>
public static class IirDesign
{
    /// <summary>
    /// Butterworth low- or high-pass of order <paramref name="order"/> (1–10) by the bilinear transform
    /// with frequency pre-warping, returned as second-order sections. The −3 dB point of the causal
    /// filter is at <paramref name="cornerHz"/>; zero-phase use squares the magnitude (−6 dB at the corner).
    /// </summary>
    public static SosFilter Butterworth(FilterType type, int order, double cornerHz, double sampleRate)
    {
        if (order is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(order), "Butterworth order must be 1–10.");
        var nyquist = sampleRate / 2;
        if (!(cornerHz > 0 && cornerHz < nyquist))
            throw new ArgumentOutOfRangeException(nameof(cornerHz), $"Corner frequency must be between 0 and the Nyquist frequency ({nyquist:0.#} Hz).");

        // Pre-warped analog corner (fs = 2 convention: s = (z−1)/(z+1)).
        var warped = Math.Tan(Math.PI * cornerHz / sampleRate);
        var sections = new List<Biquad>();

        for (var k = 0; k < order / 2; k++)
        {
            // Conjugate pole pair of the normalised prototype: p = exp(iθ), θ = π/2 + π(2k+1)/(2N).
            var theta = Math.PI / 2 + Math.PI * (2 * k + 1) / (2.0 * order);
            var re = Math.Cos(theta); // negative
            // Prototype section: 1 / (s² − 2 re s + 1).
            var c1 = -2 * re;
            sections.Add(type == FilterType.LowPass
                ? BilinearLowPass2(c1, warped)
                : BilinearHighPass2(c1, warped));
        }

        if (order % 2 == 1)
            sections.Add(type == FilterType.LowPass ? BilinearLowPass1(warped) : BilinearHighPass1(warped));

        return new SosFilter(sections);
    }

    // Low-pass 2nd order: H(s) = W² / (s² + c1 W s + W²), s = (z−1)/(z+1).
    private static Biquad BilinearLowPass2(double c1, double w)
    {
        var w2 = w * w;
        var a0 = 1 + c1 * w + w2;
        return new Biquad(w2 / a0, 2 * w2 / a0, w2 / a0, 2 * (w2 - 1) / a0, (1 - c1 * w + w2) / a0);
    }

    // High-pass 2nd order: H(s) = s² / (s² + c1 W s + W²).
    private static Biquad BilinearHighPass2(double c1, double w)
    {
        var w2 = w * w;
        var a0 = 1 + c1 * w + w2;
        return new Biquad(1 / a0, -2 / a0, 1 / a0, 2 * (w2 - 1) / a0, (1 - c1 * w + w2) / a0);
    }

    // Low-pass 1st order: H(s) = W / (s + W).
    private static Biquad BilinearLowPass1(double w)
    {
        var a0 = 1 + w;
        return new Biquad(w / a0, w / a0, 0, (w - 1) / a0, 0);
    }

    // High-pass 1st order: H(s) = s / (s + W).
    private static Biquad BilinearHighPass1(double w)
    {
        var a0 = 1 + w;
        return new Biquad(1 / a0, -1 / a0, 0, (w - 1) / a0, 0);
    }

    /// <summary>
    /// Band-pass as a cascade of a Butterworth high-pass at <paramref name="lowHz"/> and a low-pass at
    /// <paramref name="highHz"/>, each of order <paramref name="order"/>.
    /// </summary>
    public static SosFilter BandPass(int order, double lowHz, double highHz, double sampleRate)
    {
        if (!(lowHz < highHz)) throw new ArgumentException("The low corner must be below the high corner.");
        var hp = Butterworth(FilterType.HighPass, order, lowHz, sampleRate);
        var lp = Butterworth(FilterType.LowPass, order, highHz, sampleRate);
        return new SosFilter([.. hp.Sections, .. lp.Sections]);
    }

    /// <summary>
    /// Second-order notch at <paramref name="frequencyHz"/> with quality factor <paramref name="q"/>
    /// (bandwidth = f0/Q), from the bilinear transform of H(s) = (s² + ω0²)/(s² + (ω0/Q)s + ω0²)
    /// (R. Bristow-Johnson, "Cookbook formulae for audio EQ biquad filter coefficients").
    /// </summary>
    public static Biquad Notch(double frequencyHz, double q, double sampleRate)
    {
        if (!(frequencyHz > 0 && frequencyHz < sampleRate / 2))
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), "Notch frequency must be between 0 and Nyquist.");
        if (!(q > 0)) throw new ArgumentOutOfRangeException(nameof(q), "Q must be positive.");
        var w0 = 2 * Math.PI * frequencyHz / sampleRate;
        var alpha = Math.Sin(w0) / (2 * q);
        var cos = Math.Cos(w0);
        var a0 = 1 + alpha;
        return new Biquad(1 / a0, -2 * cos / a0, 1 / a0, -2 * cos / a0, (1 - alpha) / a0);
    }
}
