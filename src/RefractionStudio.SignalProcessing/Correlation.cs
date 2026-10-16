namespace RefractionStudio.SignalProcessing;

/// <summary>Result of a cross-correlation lag search.</summary>
/// <param name="Lags">Lag of each correlation value in samples (b relative to a; positive = b later).</param>
/// <param name="Coefficients">Normalised correlation coefficient at each lag (−1..1).</param>
/// <param name="BestLag">Lag of the maximum, refined to sub-sample precision by parabolic interpolation.</param>
/// <param name="BestCoefficient">Maximum coefficient.</param>
public sealed record CorrelationResult(int[] Lags, double[] Coefficients, double BestLag, double BestCoefficient);

/// <summary>Normalised cross-correlation.</summary>
public static class Correlation
{
    /// <summary>
    /// Pearson-normalised cross-correlation of windows of <paramref name="a"/> and <paramref name="b"/>
    /// over lags −<paramref name="maxLag"/>..+<paramref name="maxLag"/>. For lag L the window
    /// a[start..start+length) is compared with b[start+L..start+L+length). Lags whose window
    /// leaves <paramref name="b"/> get coefficient 0.
    /// </summary>
    public static CorrelationResult Normalized(ReadOnlySpan<double> a, ReadOnlySpan<double> b, int start, int length, int maxLag, bool absolute = false)
    {
        if (length < 2) throw new ArgumentOutOfRangeException(nameof(length), "Correlation window must contain at least two samples.");
        start = Math.Clamp(start, 0, Math.Max(0, a.Length - length));
        length = Math.Min(length, a.Length - start);
        var lags = new int[2 * maxLag + 1];
        var coeffs = new double[2 * maxLag + 1];
        var wa = a.Slice(start, length);
        var ma = Statistics.Mean(wa);
        double saa = 0;
        foreach (var v in wa) saa += (v - ma) * (v - ma);

        var bestIdx = -1;
        var best = double.NegativeInfinity;
        for (var k = 0; k < lags.Length; k++)
        {
            var lag = k - maxLag;
            lags[k] = lag;
            var bs = start + lag;
            if (bs < 0 || bs + length > b.Length || saa <= 0) { coeffs[k] = 0; continue; }
            var wb = b.Slice(bs, length);
            var mb = Statistics.Mean(wb);
            double sab = 0, sbb = 0;
            for (var i = 0; i < length; i++)
            {
                var da = wa[i] - ma;
                var db = wb[i] - mb;
                sab += da * db;
                sbb += db * db;
            }
            var c = sbb > 0 ? sab / Math.Sqrt(saa * sbb) : 0;
            coeffs[k] = c;
            var score = absolute ? Math.Abs(c) : c;
            if (score > best)
            {
                best = score;
                bestIdx = k;
            }
        }

        if (bestIdx < 0) return new CorrelationResult(lags, coeffs, 0, 0);
        var refined = (double)lags[bestIdx];
        if (bestIdx > 0 && bestIdx < coeffs.Length - 1)
        {
            var sign = absolute && coeffs[bestIdx] < 0 ? -1 : 1;
            var y0 = sign * coeffs[bestIdx - 1];
            var y1 = sign * coeffs[bestIdx];
            var y2 = sign * coeffs[bestIdx + 1];
            var denom = y0 - 2 * y1 + y2;
            if (Math.Abs(denom) > 1e-12)
            {
                var delta = 0.5 * (y0 - y2) / denom;
                if (Math.Abs(delta) <= 1) refined += delta;
            }
        }
        return new CorrelationResult(lags, coeffs, refined, coeffs[bestIdx]);
    }

    /// <summary>Zero-lag Pearson correlation coefficient of two equal-length windows.</summary>
    public static double Pearson(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n < 2) return 0;
        var ma = Statistics.Mean(a[..n]);
        var mb = Statistics.Mean(b[..n]);
        double sab = 0, saa = 0, sbb = 0;
        for (var i = 0; i < n; i++)
        {
            var da = a[i] - ma;
            var db = b[i] - mb;
            sab += da * db;
            saa += da * da;
            sbb += db * db;
        }
        return saa > 0 && sbb > 0 ? sab / Math.Sqrt(saa * sbb) : 0;
    }
}
