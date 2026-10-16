using System.Numerics;
using RefractionStudio.SignalProcessing;

namespace RefractionStudio.SignalProcessing.Tests;

public class FftTests
{
    [Fact]
    public void Impulse_has_flat_spectrum()
    {
        var data = new Complex[16];
        data[0] = 1;
        Fft.Transform(data, inverse: false);
        foreach (var c in data)
        {
            Assert.Equal(1.0, c.Real, 12);
            Assert.Equal(0.0, c.Imaginary, 12);
        }
    }

    [Fact]
    public void Forward_then_inverse_round_trips()
    {
        var rng = new Random(7);
        var data = Enumerable.Range(0, 64).Select(_ => new Complex(rng.NextDouble(), rng.NextDouble())).ToArray();
        var copy = data.ToArray();
        Fft.Transform(copy, false);
        Fft.Transform(copy, true);
        for (var i = 0; i < data.Length; i++)
        {
            Assert.Equal(data[i].Real, copy[i].Real, 12);
            Assert.Equal(data[i].Imaginary, copy[i].Imaginary, 12);
        }
    }

    [Fact]
    public void Sine_lands_in_its_bin()
    {
        const int n = 256;
        var x = Enumerable.Range(0, n).Select(i => Math.Sin(2 * Math.PI * 10 * i / n)).ToArray();
        var spec = Fft.ForwardReal(x, n);
        Assert.Equal(n / 2.0, spec[10].Magnitude, 9);
        Assert.True(spec[11].Magnitude < 1e-9);
    }

    [Fact]
    public void Non_power_of_two_is_rejected()
        => Assert.Throws<ArgumentException>(() => Fft.Transform(new Complex[12], false));
}

public class ButterworthTests
{
    private const double Fs = 4000.0;

    // Exact magnitude of a bilinear-transformed Butterworth filter.
    private static double Expected(FilterType type, int order, double fc, double f)
    {
        var r = Math.Tan(Math.PI * f / Fs) / Math.Tan(Math.PI * fc / Fs);
        if (type == FilterType.HighPass) r = 1 / r;
        return 1 / Math.Sqrt(1 + Math.Pow(r, 2 * order));
    }

    [Theory]
    [InlineData(FilterType.LowPass, 1)]
    [InlineData(FilterType.LowPass, 2)]
    [InlineData(FilterType.LowPass, 4)]
    [InlineData(FilterType.LowPass, 5)]
    [InlineData(FilterType.LowPass, 8)]
    [InlineData(FilterType.HighPass, 1)]
    [InlineData(FilterType.HighPass, 3)]
    [InlineData(FilterType.HighPass, 4)]
    [InlineData(FilterType.HighPass, 6)]
    public void Magnitude_matches_the_Butterworth_response(FilterType type, int order)
    {
        const double fc = 100;
        var filter = IirDesign.Butterworth(type, order, fc, Fs);
        Assert.Equal(1 / Math.Sqrt(2), filter.Magnitude(fc, Fs), 9);
        foreach (var f in new[] { 5.0, 30, 70, 150, 400, 1200, 1900 })
            Assert.Equal(Expected(type, order, fc, f), filter.Magnitude(f, Fs), 9);
    }

    [Fact]
    public void Lowpass_has_unit_dc_gain_and_highpass_blocks_dc()
    {
        Assert.Equal(1.0, IirDesign.Butterworth(FilterType.LowPass, 4, 50, Fs).Magnitude(0, Fs), 12);
        Assert.Equal(0.0, IirDesign.Butterworth(FilterType.HighPass, 4, 50, Fs).Magnitude(0, Fs), 12);
    }

    [Fact]
    public void Corner_above_nyquist_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => IirDesign.Butterworth(FilterType.LowPass, 4, 2500, Fs));

    [Fact]
    public void Filtfilt_preserves_phase_and_squares_magnitude()
    {
        const int n = 4000;
        const double f = 40;
        var x = Enumerable.Range(0, n).Select(i => Math.Sin(2 * Math.PI * f * i / Fs)).ToArray();
        var filter = IirDesign.BandPass(4, 10, 100, Fs);
        var y = filter.FiltFilt(x);
        var gain = filter.Magnitude(f, Fs);
        // Away from the edge transients (time constant of the 10 Hz high-pass ≈ 40 ms) the output is
        // the input scaled by |H|², with zero phase.
        for (var i = 1600; i < 2400; i++)
            Assert.Equal(gain * gain * x[i], y[i], 1e-4);
    }

    [Fact]
    public void Filtfilt_of_a_constant_has_no_startup_transient()
    {
        var x = Enumerable.Repeat(3.5, 500).ToArray();
        var y = IirDesign.Butterworth(FilterType.LowPass, 6, 80, Fs).FiltFilt(x);
        foreach (var v in y) Assert.Equal(3.5, v, 8);
    }

    [Fact]
    public void Causal_filter_matches_direct_difference_equation()
    {
        var filter = IirDesign.Butterworth(FilterType.LowPass, 2, 200, Fs);
        var s = filter.Sections[0];
        var x = Enumerable.Range(0, 50).Select(i => Math.Sin(i * 0.3) + (i == 10 ? 2 : 0)).ToArray();
        var y = filter.Filter(x);
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var yi = s.B0 * x[i] + s.B1 * x1 + s.B2 * x2 - s.A1 * y1 - s.A2 * y2;
            Assert.Equal(yi, y[i], 12);
            x2 = x1; x1 = x[i]; y2 = y1; y1 = yi;
        }
    }

    [Fact]
    public void Notch_removes_its_frequency()
    {
        var notch = new SosFilter([IirDesign.Notch(60, 10, Fs)]);
        Assert.True(notch.Magnitude(60, Fs) < 1e-12);
        Assert.Equal(1.0, notch.Magnitude(0, Fs), 12);
        Assert.True(notch.Magnitude(200, Fs) > 0.99);
    }
}

public class TraceOpsTests
{
    [Fact]
    public void Detrend_removes_a_line_exactly()
    {
        var x = Enumerable.Range(0, 101).Select(i => 3.0 + 0.25 * i).ToArray();
        foreach (var v in TraceOps.Detrend(x)) Assert.Equal(0, v, 10);
    }

    [Fact]
    public void Demean_gives_zero_mean_and_keeps_input()
    {
        var x = new[] { 1.0, 2, 3, 10 };
        var y = TraceOps.RemoveMean(x);
        Assert.Equal(0, y.Sum(), 12);
        Assert.Equal(1.0, x[0]);
    }

    [Fact]
    public void Taper_zeroes_the_ends_and_keeps_the_middle()
    {
        var y = TraceOps.Taper(Enumerable.Repeat(1.0, 100).ToArray(), 0.1);
        Assert.Equal(0, y[0], 12);
        Assert.Equal(0, y[99], 12);
        Assert.Equal(1, y[50], 12);
    }

    [Fact]
    public void Agc_equalises_amplitude()
    {
        var x = Enumerable.Range(0, 2000).Select(i => (i < 1000 ? 0.01 : 10.0) * Math.Sin(i * 0.5)).ToArray();
        var y = TraceOps.Agc(x, 101);
        var early = Statistics.Rms(y.AsSpan(200, 600));
        var late = Statistics.Rms(y.AsSpan(1200, 600));
        Assert.Equal(early, late, 1);
    }

    [Fact]
    public void Integer_shift_moves_samples()
    {
        var y = TraceOps.Shift(new double[] { 0, 1, 2, 3, 4 }, 0.002, 0.001);
        Assert.Equal(new double[] { 0, 0, 0, 1, 2 }, y);
    }

    [Fact]
    public void Fractional_shift_of_a_band_limited_signal_is_accurate()
    {
        const double dt = 0.001;
        Func<double, double> s = t => Math.Sin(2 * Math.PI * 25 * t);
        var x = Enumerable.Range(0, 400).Select(i => s(i * dt)).ToArray();
        var y = TraceOps.Shift(x, 0.00037, dt);
        for (var i = 50; i < 350; i++) Assert.Equal(s(i * dt - 0.00037), y[i], 1e-4);
    }

    [Fact]
    public void Resampling_preserves_a_band_limited_sine()
    {
        const double dt = 0.00025;
        Func<double, double> s = t => Math.Sin(2 * Math.PI * 30 * t) + 0.5 * Math.Cos(2 * Math.PI * 55 * t);
        var x = Enumerable.Range(0, 2000).Select(i => s(i * dt)).ToArray();
        var down = TraceOps.Resample(x, dt, 0.001);
        Assert.Equal(500, down.Length);
        for (var i = 50; i < 450; i++) Assert.Equal(s(i * 0.001), down[i], 5e-3);
        var up = TraceOps.Resample(x, dt, dt / 2);
        for (var i = 200; i < 3800; i++) Assert.Equal(s(i * dt / 2), up[i], 1e-4);
    }

    [Fact]
    public void Envelope_of_a_modulated_carrier_recovers_the_modulation()
    {
        const int n = 2048;
        var x = Enumerable.Range(0, n).Select(i => (1 + 0.5 * Math.Sin(2 * Math.PI * i / 1024.0)) * Math.Cos(2 * Math.PI * 100 * i / n)).ToArray();
        var env = TraceOps.Envelope(x);
        for (var i = 200; i < 1800; i++) Assert.Equal(1 + 0.5 * Math.Sin(2 * Math.PI * i / 1024.0), env[i], 2);
    }
}

public class CorrelationAndSpectrumTests
{
    private static double[] Ricker(int n, double centre, double f, double dt)
        => Enumerable.Range(0, n).Select(i =>
        {
            var t = i * dt - centre;
            var a = Math.PI * Math.PI * f * f * t * t;
            return (1 - 2 * a) * Math.Exp(-a);
        }).ToArray();

    [Fact]
    public void Correlation_recovers_a_sub_sample_lag()
    {
        const double dt = 0.0005;
        var a = Ricker(600, 0.1, 40, dt);
        var b = Ricker(600, 0.1 + 3.4 * dt, 40, dt);
        var r = Correlation.Normalized(a, b, 100, 200, 20);
        Assert.Equal(3.4, r.BestLag, 1);
        Assert.True(r.BestCoefficient > 0.99);
    }

    [Fact]
    public void Absolute_correlation_detects_reversed_polarity()
    {
        const double dt = 0.0005;
        var a = Ricker(600, 0.1, 40, dt);
        var b = Ricker(600, 0.1, 40, dt).Select(v => -v).ToArray();
        var r = Correlation.Normalized(a, b, 100, 200, 10, absolute: true);
        Assert.Equal(0, r.BestLag, 1);
        Assert.True(r.BestCoefficient < -0.99);
    }

    [Fact]
    public void Spectrum_finds_the_dominant_frequency()
    {
        const double dt = 0.0005;
        var x = Enumerable.Range(0, 2000).Select(i => Math.Sin(2 * Math.PI * 47 * i * dt)).ToArray();
        var s = SpectrumAnalysis.Compute(x, dt);
        var df = s.Frequencies[1];
        Assert.InRange(s.DominantFrequency, 47 - df, 47 + df);
        Assert.Equal(1.0, s.Amplitudes.Max(), 1);
        Assert.True(s.BandLow < 47 && s.BandHigh > 47);
    }

    [Fact]
    public void Band_suggestion_brackets_the_signal_band()
    {
        const double dt = 0.0005;
        var x = Ricker(2000, 0.5, 40, dt);
        var s = SpectrumAnalysis.Compute(x, dt);
        var (lo, hi) = SpectrumAnalysis.SuggestBandPass(s, dt);
        Assert.True(lo < 40 && hi > 40 && lo >= 1 && hi <= 0.8 * 1000);
    }

    [Fact]
    public void Robust_statistics()
    {
        var x = new[] { 1.0, 2, 3, 4, 100 };
        Assert.Equal(3, Statistics.Median(x));
        Assert.Equal(1, Statistics.Mad(x));
        Assert.Equal(2, Statistics.Percentile(x, 25));
    }
}
