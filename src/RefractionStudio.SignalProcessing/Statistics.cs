namespace RefractionStudio.SignalProcessing;

/// <summary>Descriptive statistics on spans.</summary>
public static class Statistics
{
    public static double Mean(ReadOnlySpan<double> x)
    {
        if (x.Length == 0) return double.NaN;
        double s = 0;
        foreach (var v in x) s += v;
        return s / x.Length;
    }

    public static double Rms(ReadOnlySpan<double> x)
    {
        if (x.Length == 0) return 0;
        double s = 0;
        foreach (var v in x) s += v * v;
        return Math.Sqrt(s / x.Length);
    }

    public static double MaxAbs(ReadOnlySpan<double> x)
    {
        double m = 0;
        foreach (var v in x)
        {
            var a = Math.Abs(v);
            if (a > m) m = a;
        }
        return m;
    }

    public static double Variance(ReadOnlySpan<double> x)
    {
        if (x.Length < 2) return 0;
        var mean = Mean(x);
        double s = 0;
        foreach (var v in x) s += (v - mean) * (v - mean);
        return s / (x.Length - 1);
    }

    public static double StandardDeviation(ReadOnlySpan<double> x) => Math.Sqrt(Variance(x));

    public static double Median(ReadOnlySpan<double> x)
    {
        if (x.Length == 0) return double.NaN;
        var a = x.ToArray();
        Array.Sort(a);
        var m = a.Length / 2;
        return a.Length % 2 == 1 ? a[m] : 0.5 * (a[m - 1] + a[m]);
    }

    /// <summary>Median absolute deviation (unscaled).</summary>
    public static double Mad(ReadOnlySpan<double> x)
    {
        if (x.Length == 0) return double.NaN;
        var med = Median(x);
        var d = new double[x.Length];
        for (var i = 0; i < x.Length; i++) d[i] = Math.Abs(x[i] - med);
        return Median(d);
    }

    /// <summary>Robust standard deviation estimate 1.4826·MAD (consistent for Gaussian data).</summary>
    public static double RobustSigma(ReadOnlySpan<double> x) => 1.4826 * Mad(x);

    /// <summary>Linear-interpolated percentile, p in [0, 100].</summary>
    public static double Percentile(ReadOnlySpan<double> x, double p)
    {
        if (x.Length == 0) return double.NaN;
        var a = x.ToArray();
        Array.Sort(a);
        var pos = Math.Clamp(p, 0, 100) / 100.0 * (a.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        return a[lo] + (a[hi] - a[lo]) * (pos - lo);
    }
}
