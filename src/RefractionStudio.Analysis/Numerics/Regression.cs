using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.Numerics;

/// <summary>A fitted straight line y = Intercept + Slope·x with statistics.</summary>
/// <param name="Slope">Slope.</param>
/// <param name="Intercept">Intercept at x = 0.</param>
/// <param name="SlopeStdError">Standard error of the slope (NaN when n ≤ 2).</param>
/// <param name="InterceptStdError">Standard error of the intercept (NaN when n ≤ 2).</param>
/// <param name="RSquared">Coefficient of determination.</param>
/// <param name="RmsResidual">Root-mean-square residual.</param>
/// <param name="Count">Number of points used.</param>
/// <param name="Weights">Final weights (1 for OLS; Huber weights for robust fits).</param>
/// <param name="Mode">Estimator.</param>
public sealed record LineFit(double Slope, double Intercept, double SlopeStdError, double InterceptStdError, double RSquared, double RmsResidual, int Count, ImmutableArray<double> Weights, RegressionMode Mode)
{
    public double Evaluate(double x) => Intercept + Slope * x;
}

/// <summary>Straight-line regression: ordinary least squares and Huber robust regression.</summary>
public static class Regression
{
    /// <summary>Huber tuning constant (95 % efficiency for Gaussian errors; Huber 1981).</summary>
    public const double HuberK = 1.345;

    /// <summary>Fits a line; returns null with fewer than 2 points or a degenerate abscissa.</summary>
    public static LineFit? Fit(IReadOnlyList<double> x, IReadOnlyList<double> y, RegressionMode mode = RegressionMode.Ordinary)
        => mode == RegressionMode.Robust ? Huber(x, y) : Weighted(x, y, null, RegressionMode.Ordinary);

    /// <summary>Weighted least squares with optional weights.</summary>
    public static LineFit? Weighted(IReadOnlyList<double> x, IReadOnlyList<double> y, IReadOnlyList<double>? w, RegressionMode mode)
    {
        var n = x.Count;
        if (n < 2 || y.Count != n) return null;
        double sw = 0, sx = 0, sy = 0;
        for (var i = 0; i < n; i++)
        {
            var wi = w?[i] ?? 1;
            sw += wi; sx += wi * x[i]; sy += wi * y[i];
        }
        if (sw <= 0) return null;
        var mx = sx / sw;
        var my = sy / sw;
        double sxx = 0, sxy = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            var wi = w?[i] ?? 1;
            var dx = x[i] - mx;
            var dy = y[i] - my;
            sxx += wi * dx * dx; sxy += wi * dx * dy; syy += wi * dy * dy;
        }
        if (sxx <= 1e-300) return null;
        var slope = sxy / sxx;
        var intercept = my - slope * mx;
        double sse = 0, ssr = 0;
        for (var i = 0; i < n; i++)
        {
            var r = y[i] - (intercept + slope * x[i]);
            sse += (w?[i] ?? 1) * r * r;
            ssr += r * r;
        }
        var r2 = syy > 0 ? 1 - sse / syy : 1;
        double seSlope = double.NaN, seIntercept = double.NaN;
        if (n > 2)
        {
            var s2 = sse / (n - 2);
            seSlope = Math.Sqrt(s2 / sxx);
            seIntercept = Math.Sqrt(s2 * (1.0 / sw + mx * mx / sxx));
        }
        var weights = w is null ? Enumerable.Repeat(1.0, n).ToImmutableArray() : [.. w];
        return new LineFit(slope, intercept, seSlope, seIntercept, r2, Math.Sqrt(ssr / n), n, weights, mode);
    }

    /// <summary>
    /// Huber M-estimate by iteratively reweighted least squares, scale from the median absolute
    /// deviation of residuals (1.4826·MAD), at most 50 iterations.
    /// </summary>
    public static LineFit? Huber(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var fit = Weighted(x, y, null, RegressionMode.Robust);
        if (fit is null || x.Count < 3) return fit;
        var w = new double[x.Count];
        for (var iter = 0; iter < 50; iter++)
        {
            var res = new double[x.Count];
            for (var i = 0; i < x.Count; i++) res[i] = y[i] - fit.Evaluate(x[i]);
            var abs = res.Select(Math.Abs).OrderBy(v => v).ToArray();
            var med = abs.Length % 2 == 1 ? abs[abs.Length / 2] : 0.5 * (abs[abs.Length / 2 - 1] + abs[abs.Length / 2]);
            var scale = 1.4826 * med;
            if (scale <= 1e-15) break;
            for (var i = 0; i < x.Count; i++)
            {
                var u = Math.Abs(res[i]) / scale;
                w[i] = u <= HuberK ? 1 : HuberK / u;
            }
            var next = Weighted(x, y, w, RegressionMode.Robust);
            if (next is null) break;
            var converged = Math.Abs(next.Slope - fit.Slope) <= 1e-12 * Math.Max(1, Math.Abs(fit.Slope))
                         && Math.Abs(next.Intercept - fit.Intercept) <= 1e-12 * Math.Max(1, Math.Abs(fit.Intercept));
            fit = next;
            if (converged) break;
        }
        return fit;
    }
}
