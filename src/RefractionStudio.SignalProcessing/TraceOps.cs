namespace RefractionStudio.SignalProcessing;

/// <summary>Elementary non-filtering trace operations. All return new arrays; inputs are never modified.</summary>
public static class TraceOps
{
    public static double[] RemoveMean(ReadOnlySpan<double> x)
    {
        var y = x.ToArray();
        if (y.Length == 0) return y;
        var mean = Statistics.Mean(y);
        for (var i = 0; i < y.Length; i++) y[i] -= mean;
        return y;
    }

    /// <summary>Removes the least-squares straight line a + b·i.</summary>
    public static double[] Detrend(ReadOnlySpan<double> x)
    {
        var n = x.Length;
        var y = x.ToArray();
        if (n < 2) return RemoveMean(x);
        // Centred abscissa makes the normal equations diagonal.
        var mid = (n - 1) / 2.0;
        double sxx = 0, sxy = 0, sy = 0;
        for (var i = 0; i < n; i++)
        {
            var t = i - mid;
            sxx += t * t;
            sxy += t * x[i];
            sy += x[i];
        }
        var a = sy / n;
        var b = sxy / sxx;
        for (var i = 0; i < n; i++) y[i] -= a + b * (i - mid);
        return y;
    }

    /// <summary>Tapers <paramref name="fraction"/> of the length at each end (0 ≤ fraction ≤ 0.5).</summary>
    public static double[] Taper(ReadOnlySpan<double> x, double fraction, bool hann = true)
    {
        if (fraction is < 0 or > 0.5) throw new ArgumentOutOfRangeException(nameof(fraction), "Taper fraction must be between 0 and 0.5.");
        var y = x.ToArray();
        var n = y.Length;
        var m = (int)Math.Floor(fraction * n);
        if (m < 1) return y;
        for (var i = 0; i < m; i++)
        {
            var r = (double)i / m;
            var w = hann ? 0.5 * (1 - Math.Cos(Math.PI * r)) : r;
            y[i] *= w;
            y[n - 1 - i] *= w;
        }
        return y;
    }

    /// <summary>Scales to unit peak (or unit RMS). An all-zero trace is returned unchanged.</summary>
    public static double[] Normalize(ReadOnlySpan<double> x, bool rms = false)
    {
        var y = x.ToArray();
        var scale = rms ? Statistics.Rms(y) : Statistics.MaxAbs(y);
        if (scale > 0)
            for (var i = 0; i < y.Length; i++) y[i] /= scale;
        return y;
    }

    /// <summary>Scales to unit RMS within samples [start, end).</summary>
    public static double[] Balance(ReadOnlySpan<double> x, int start, int end)
    {
        var y = x.ToArray();
        start = Math.Clamp(start, 0, y.Length);
        end = Math.Clamp(end, start, y.Length);
        if (end - start < 1) return y;
        var rms = Statistics.Rms(y.AsSpan(start, end - start));
        if (rms > 0)
            for (var i = 0; i < y.Length; i++) y[i] /= rms;
        return y;
    }

    /// <summary>
    /// Automatic gain control: divides each sample by the RMS amplitude of a centred sliding window of
    /// <paramref name="windowSamples"/> samples. A small fraction of the trace RMS stabilises silent zones.
    /// </summary>
    public static double[] Agc(ReadOnlySpan<double> x, int windowSamples)
    {
        var n = x.Length;
        var y = new double[n];
        if (n == 0) return y;
        windowSamples = Math.Clamp(windowSamples, 1, n);
        var half = windowSamples / 2;
        var prefix = new double[n + 1];
        for (var i = 0; i < n; i++) prefix[i + 1] = prefix[i] + x[i] * x[i];
        var floor = 1e-6 * Math.Sqrt(prefix[n] / n);
        for (var i = 0; i < n; i++)
        {
            var a = Math.Max(0, i - half);
            var b = Math.Min(n, i + half + 1);
            var rms = Math.Sqrt((prefix[b] - prefix[a]) / (b - a));
            y[i] = x[i] / Math.Max(rms, floor == 0 ? double.Epsilon : floor);
        }
        return y;
    }

    public static double[] Scale(ReadOnlySpan<double> x, double factor)
    {
        var y = x.ToArray();
        for (var i = 0; i < y.Length; i++) y[i] *= factor;
        return y;
    }

    /// <summary>
    /// Shifts the trace by <paramref name="seconds"/> (positive = later) on the same time axis using
    /// band-limited (windowed-sinc) interpolation for fractional shifts. Samples shifted in from outside
    /// the trace are zero.
    /// </summary>
    public static double[] Shift(ReadOnlySpan<double> x, double seconds, double sampleInterval)
    {
        var n = x.Length;
        var shiftSamples = seconds / sampleInterval;
        var y = new double[n];
        var whole = Math.Round(shiftSamples);
        if (Math.Abs(shiftSamples - whole) < 1e-9)
        {
            var k = (int)whole;
            for (var i = 0; i < n; i++)
            {
                var j = i - k;
                if (j >= 0 && j < n) y[i] = x[j];
            }
            return y;
        }
        for (var i = 0; i < n; i++) y[i] = Interpolation.Sinc(x, i - shiftSamples);
        return y;
    }

    /// <summary>
    /// Resamples to a new sample interval. When the new interval is longer (decimation), a zero-phase
    /// 8th-order Butterworth low-pass at 80 % of the new Nyquist frequency is applied first to prevent
    /// aliasing. Interpolation uses a Kaiser-windowed sinc kernel.
    /// </summary>
    public static double[] Resample(ReadOnlySpan<double> x, double oldInterval, double newInterval)
    {
        if (!(oldInterval > 0 && newInterval > 0)) throw new ArgumentOutOfRangeException(nameof(newInterval), "Sample intervals must be positive.");
        var source = x.ToArray();
        if (newInterval > oldInterval * (1 + 1e-12))
        {
            var newNyquist = 0.5 / newInterval;
            var lp = IirDesign.Butterworth(FilterType.LowPass, 8, 0.8 * newNyquist, 1 / oldInterval);
            source = lp.FiltFilt(source);
        }
        var duration = (x.Length - 1) * oldInterval;
        var count = (int)Math.Floor(duration / newInterval + 1e-9) + 1;
        var y = new double[count];
        for (var i = 0; i < count; i++) y[i] = Interpolation.Sinc(source, i * newInterval / oldInterval);
        return y;
    }

    /// <summary>Envelope (magnitude of the analytic signal), computed with the FFT Hilbert transform.</summary>
    public static double[] Envelope(ReadOnlySpan<double> x)
    {
        var n = x.Length;
        if (n == 0) return [];
        var m = Fft.NextPowerOfTwo(n);
        var spec = Fft.ForwardReal(x, m);
        // Analytic signal: keep DC and Nyquist, double positive frequencies, zero negative ones.
        for (var i = 1; i < m / 2; i++) spec[i] *= 2;
        for (var i = m / 2 + 1; i < m; i++) spec[i] = 0;
        Fft.Transform(spec, inverse: true);
        var env = new double[n];
        for (var i = 0; i < n; i++) env[i] = spec[i].Magnitude;
        return env;
    }
}

/// <summary>Band-limited interpolation helpers.</summary>
public static class Interpolation
{
    /// <summary>Half-width of the interpolation kernel (samples).</summary>
    public const int HalfWidth = 16;

    /// <summary>Kaiser window shape parameter (≈ 80 dB side-lobe attenuation).</summary>
    public const double KaiserBeta = 8.0;

    private static readonly double I0Beta = BesselI0(KaiserBeta);

    /// <summary>
    /// Value at fractional sample position <paramref name="t"/> by Kaiser-windowed sinc interpolation
    /// (half-width 16 samples, β = 8). Positions outside the trace contribute zeros.
    /// </summary>
    public static double Sinc(ReadOnlySpan<double> x, double t)
    {
        var n = x.Length;
        if (t < -HalfWidth || t > n - 1 + HalfWidth) return 0;
        var i0 = (int)Math.Floor(t);
        var frac = t - i0;
        if (frac < 1e-12 && i0 >= 0 && i0 < n) return x[i0];
        double sum = 0;
        for (var k = i0 - HalfWidth + 1; k <= i0 + HalfWidth; k++)
        {
            if (k < 0 || k >= n) continue;
            sum += x[k] * Kernel(t - k);
        }
        return sum;
    }

    /// <summary>Kaiser-windowed sinc kernel value at distance <paramref name="d"/> samples.</summary>
    public static double Kernel(double d)
    {
        if (Math.Abs(d) < 1e-12) return 1;
        if (Math.Abs(d) >= HalfWidth) return 0;
        var pd = Math.PI * d;
        var r = d / HalfWidth;
        var window = BesselI0(KaiserBeta * Math.Sqrt(1 - r * r)) / I0Beta;
        return Math.Sin(pd) / pd * window;
    }

    /// <summary>Modified Bessel function of the first kind, order zero (power series).</summary>
    public static double BesselI0(double x)
    {
        double sum = 1, term = 1;
        var q = x * x / 4;
        for (var k = 1; k < 200; k++)
        {
            term *= q / (k * k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
    }

    /// <summary>Linear interpolation at fractional sample position; clamps to the ends.</summary>
    public static double Linear(ReadOnlySpan<double> x, double t)
    {
        if (x.Length == 0) return 0;
        if (t <= 0) return x[0];
        if (t >= x.Length - 1) return x[^1];
        var i = (int)t;
        var f = t - i;
        return x[i] + (x[i + 1] - x[i]) * f;
    }
}
