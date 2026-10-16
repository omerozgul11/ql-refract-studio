using System.Globalization;
using RefractionStudio.Domain;
using RefractionStudio.Formats;
using RefractionStudio.SignalProcessing;

namespace RefractionStudio.Synthetics;

/// <summary>Acquisition and data-quality options of a synthetic survey.</summary>
/// <param name="Channels">Receiver channels.</param>
/// <param name="Spacing">Geophone interval (m).</param>
/// <param name="FirstReceiverX">Position of channel 1 (m).</param>
/// <param name="ShotPositions">Shot positions (m).</param>
/// <param name="SampleInterval">Sample interval (s).</param>
/// <param name="Samples">Samples per trace (multiple of 4).</param>
/// <param name="Delay">Record time of the first sample (s, negative = pre-trigger).</param>
/// <param name="Frequency">Dominant frequency of the source wavelet (Hz).</param>
/// <param name="NoiseLevel">RMS of band-limited random noise relative to the first-arrival amplitude at 10 m.</param>
/// <param name="ShotTimeErrors">Trigger error per shot (s, added to all record times of that shot).</param>
/// <param name="DeadChannels">Channels recorded as flat traces.</param>
/// <param name="ReversedChannels">Channels with reversed polarity wiring.</param>
/// <param name="WaveType">P or SH.</param>
/// <param name="Topography">Ground surface.</param>
/// <param name="Seed">Random seed (deterministic output).</param>
/// <param name="ReferenceChannel">If set, an extra channel at the shot recording the source pulse (trigger/spy geophone).</param>
/// <param name="FormatCode">SEG-2 data format code.</param>
public sealed record SyntheticSurveyOptions(
    int Channels = 24,
    double Spacing = 2.0,
    double FirstReceiverX = 0.0,
    double[]? ShotPositions = null,
    double SampleInterval = 0.000125,
    int Samples = 1024,
    double Delay = -0.005,
    double Frequency = 80,
    double NoiseLevel = 0.0,
    double[]? ShotTimeErrors = null,
    int[]? DeadChannels = null,
    int[]? ReversedChannels = null,
    WaveType WaveType = WaveType.P,
    Topography? Topography = null,
    int Seed = 1234,
    bool ReferenceChannel = false,
    byte FormatCode = 4);

/// <summary>One generated shot file.</summary>
/// <param name="FileName">File name.</param>
/// <param name="Bytes">SEG-2 content.</param>
/// <param name="ShotX">Shot position.</param>
/// <param name="Kind">Source kind.</param>
/// <param name="TrueTimes">True first-arrival travel time per receiver channel (s).</param>
/// <param name="ShotTimeError">Trigger error recorded into this file (s).</param>
public sealed record SyntheticShot(string FileName, byte[] Bytes, double ShotX, SourceKind Kind, IReadOnlyDictionary<int, double> TrueTimes, double ShotTimeError);

/// <summary>A generated survey with its truth.</summary>
public sealed record SyntheticSurvey(ISyntheticModel Model, SyntheticSurveyOptions Options, ImmutableArray<SyntheticShot> Shots)
{
    public double ReceiverX(int channel) => Options.FirstReceiverX + (channel - 1) * Options.Spacing;

    /// <summary>Writes all files to a directory and returns their paths.</summary>
    public IReadOnlyList<string> WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        foreach (var s in Shots)
        {
            var p = Path.Combine(directory, s.FileName);
            File.WriteAllBytes(p, s.Bytes);
            paths.Add(p);
        }
        return paths;
    }
}

/// <summary>
/// Generates synthetic refraction shot records. Each trace contains a causal wavelet starting exactly
/// at the first-arrival time (so the true pick is known), a later slower arrival, a low-velocity
/// surface wave, and optional band-limited noise. Amplitudes decay as 1/√distance.
/// </summary>
public static class SurveyGenerator
{
    public static SyntheticSurvey Generate(ISyntheticModel model, SyntheticSurveyOptions options)
    {
        var shots = options.ShotPositions ?? DefaultShots(options);
        var topo = options.Topography ?? Topography.Flat;
        var rng = new Random(options.Seed);
        var list = ImmutableArray.CreateBuilder<SyntheticShot>();
        var shotIndex = 0;
        foreach (var sx in shots)
        {
            var error = options.ShotTimeErrors is { } errs && shotIndex < errs.Length ? errs[shotIndex] : 0.0;
            var kinds = options.WaveType == WaveType.SH ? new[] { SourceKind.ShLeft, SourceKind.ShRight } : [SourceKind.Vertical];
            foreach (var kind in kinds)
            {
                list.Add(GenerateShot(model, options, topo, sx, kind, error, shotIndex, rng));
            }
            shotIndex++;
        }
        return new SyntheticSurvey(model, options, list.ToImmutable());
    }

    public static double[] DefaultShots(SyntheticSurveyOptions o)
    {
        var length = (o.Channels - 1) * o.Spacing;
        var first = o.FirstReceiverX;
        return [first - o.Spacing / 2 - length / 2, first - o.Spacing / 2, first + length / 2 - o.Spacing / 2 + o.Spacing / 2, first + length + o.Spacing / 2, first + length + o.Spacing / 2 + length / 2];
    }

    private static SyntheticShot GenerateShot(ISyntheticModel model, SyntheticSurveyOptions o, Topography topo, double sx, SourceKind kind, double shotError, int shotIndex, Random rng)
    {
        var receivers = Enumerable.Range(1, o.Channels).Select(c => (X: o.FirstReceiverX + (c - 1) * o.Spacing, Z: topo.ElevationAt(o.FirstReceiverX + (c - 1) * o.Spacing))).ToList();
        var source = (sx, topo.ElevationAt(sx));
        var times = model.FirstArrivals(source, receivers);
        var surfaceWaveVelocity = 0.4 * model.VelocityAt(sx, 0);
        var shSign = kind == SourceKind.ShRight ? -1.0 : 1.0;
        var traces = new List<Seg2TraceToWrite>();
        var truth = new Dictionary<int, double>();
        var dead = new HashSet<int>(o.DeadChannels ?? []);
        var reversed = new HashSet<int>(o.ReversedChannels ?? []);

        for (var c = 1; c <= o.Channels; c++)
        {
            var (rx, rz) = receivers[c - 1];
            var t0 = times[c - 1];
            truth[c] = t0;
            var distance = Math.Max(1.0, Math.Sqrt((rx - sx) * (rx - sx) + (rz - source.Item2) * (rz - source.Item2)));
            var amp = 1 / Math.Sqrt(distance);
            var s = new double[o.Samples];
            for (var i = 0; i < o.Samples; i++)
            {
                var t = o.Delay + i * o.SampleInterval - shotError; // record time → true time
                var v = amp * Wavelet(t - t0, o.Frequency);
                // A later, slower arrival (e.g. the direct wave beyond the crossover) of similar strength.
                var directLater = distance / model.VelocityAt(sx, 0);
                if (directLater > t0 + 2 / o.Frequency) v += 0.8 * amp * Wavelet(t - directLater, o.Frequency);
                // Surface wave: strong, low frequency, slow.
                v += 3 * amp * Wavelet(t - distance / surfaceWaveVelocity, o.Frequency / 3);
                if (o.WaveType == WaveType.SH)
                {
                    v *= shSign;
                    // P energy recorded on horizontal geophones does not reverse with the impact direction.
                    var tp = t0 * 0.45;
                    v += 0.3 * amp * Wavelet(t - tp, o.Frequency * 1.5);
                }
                s[i] = v;
            }
            if (o.NoiseLevel > 0)
            {
                var noise = BandLimitedNoise(rng, o.Samples, o.SampleInterval, o.Frequency);
                var level = o.NoiseLevel / Math.Sqrt(10);
                for (var i = 0; i < o.Samples; i++) s[i] += level * noise[i];
            }
            if (dead.Contains(c)) Array.Clear(s);
            if (reversed.Contains(c)) for (var i = 0; i < s.Length; i++) s[i] = -s[i];
            traces.Add(Trace(o, c, rx, rz, sx, s));
        }

        if (o.ReferenceChannel)
        {
            var s = new double[o.Samples];
            for (var i = 0; i < o.Samples; i++)
            {
                var t = o.Delay + i * o.SampleInterval - shotError;
                s[i] = Wavelet(t, o.Frequency * 2);
            }
            traces.Add(Trace(o, o.Channels + 1, sx, source.Item2, sx, s));
        }

        var fileKw = new Dictionary<string, string>
        {
            ["ACQUISITION_DATE"] = "10/OCT/2026",
            ["ACQUISITION_TIME"] = $"10:{shotIndex / 60 % 60:00}:{shotIndex % 60:00}",
            ["INSTRUMENT"] = "SYNTHETIC",
            ["SHOT_SEQUENCE_NUMBER"] = (shotIndex + 1).ToString(CultureInfo.InvariantCulture),
            ["NOTE"] = model.Description,
        };
        var suffix = kind switch { SourceKind.ShLeft => "_L", SourceKind.ShRight => "_R", _ => "" };
        var name = $"shot_{shotIndex + 1:000}{suffix}.sg2";
        var bytes = Seg2Writer.Write(fileKw, traces, o.FormatCode);
        return new SyntheticShot(name, bytes, sx, kind, truth, shotError);
    }

    private static Seg2TraceToWrite Trace(SyntheticSurveyOptions o, int channel, double rx, double rz, double sx, double[] samples)
    {
        var kw = new Dictionary<string, string>
        {
            ["CHANNEL_NUMBER"] = channel.ToString(CultureInfo.InvariantCulture),
            ["SAMPLE_INTERVAL"] = o.SampleInterval.ToString("R", CultureInfo.InvariantCulture),
            ["DELAY"] = o.Delay.ToString("R", CultureInfo.InvariantCulture),
            ["RECEIVER_LOCATION"] = string.Create(CultureInfo.InvariantCulture, $"{rx:R} 0 {rz:R}"),
            ["SOURCE_LOCATION"] = sx.ToString("R", CultureInfo.InvariantCulture),
            ["STACK"] = "1",
        };
        if (o.FormatCode is 1 or 2 or 3)
        {
            // Integer encodings: scale to use the range and record the descaling factor.
            var max = Math.Max(1e-30, samples.Max(Math.Abs));
            var full = o.FormatCode == 1 ? 30000.0 : 1e7;
            var scale = full / max;
            kw["DESCALING_FACTOR"] = (1 / scale).ToString("R", CultureInfo.InvariantCulture);
            samples = [.. samples.Select(v => Math.Round(v * scale))];
        }
        return new Seg2TraceToWrite(kw, samples);
    }

    /// <summary>
    /// Causal source wavelet: zero before τ = 0, then sin(2πfτ)·e^(−πfτ/1.5)·(1 − e^(−20fτ)).
    /// The ramp makes the onset continuous; the first break is at τ = 0.
    /// </summary>
    public static double Wavelet(double tau, double f)
    {
        if (tau <= 0) return 0;
        return Math.Sin(2 * Math.PI * f * tau) * Math.Exp(-Math.PI * f * tau / 1.5) * (1 - Math.Exp(-20 * f * tau));
    }

    private static double[] BandLimitedNoise(Random rng, int n, double dt, double f)
    {
        var white = new double[n];
        for (var i = 0; i < n; i++)
        {
            // Box–Muller.
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            white[i] = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
        var nyquist = 0.5 / dt;
        var bp = IirDesign.BandPass(2, Math.Max(1, f / 4), Math.Min(0.9 * nyquist, f * 3), 1 / dt);
        var filtered = bp.Filter(white);
        var rms = Statistics.Rms(filtered);
        return rms > 0 ? [.. filtered.Select(v => v / rms * 0.25)] : filtered;
    }
}
