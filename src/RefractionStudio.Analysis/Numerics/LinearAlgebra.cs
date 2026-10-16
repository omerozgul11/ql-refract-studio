namespace RefractionStudio.Analysis.Numerics;

/// <summary>Dense symmetric positive-definite solver (Cholesky) for small least-squares systems.</summary>
public static class DenseSolver
{
    /// <summary>
    /// Solves the weighted least-squares problem min ‖W(Ax − b)‖² through the normal equations
    /// (AᵀW²A) x = AᵀW²b. Returns the solution and the diagonal of (AᵀW²A)⁻¹ (unscaled parameter
    /// variances), or null if the system is singular.
    /// </summary>
    public static (double[] X, double[] CovarianceDiagonal)? LeastSquares(IReadOnlyList<(int[] Columns, double[] Values, double Rhs, double Weight)> rows, int columns)
    {
        var n = columns;
        var ata = new double[n, n];
        var atb = new double[n];
        foreach (var (cols, vals, rhs, weight) in rows)
        {
            var w2 = weight * weight;
            for (var a = 0; a < cols.Length; a++)
            {
                var ca = cols[a];
                atb[ca] += w2 * vals[a] * rhs;
                for (var b = 0; b < cols.Length; b++) ata[ca, cols[b]] += w2 * vals[a] * vals[b];
            }
        }
        if (!Cholesky(ata, n)) return null;
        var x = SolveCholesky(ata, n, atb);
        var cov = new double[n];
        var e = new double[n];
        for (var i = 0; i < n; i++)
        {
            Array.Clear(e);
            e[i] = 1;
            cov[i] = SolveCholesky(ata, n, e)[i];
        }
        return (x, cov);
    }

    /// <summary>In-place Cholesky factorisation (lower triangle). Returns false if not positive definite.</summary>
    public static bool Cholesky(double[,] a, int n)
    {
        for (var j = 0; j < n; j++)
        {
            var sum = a[j, j];
            for (var k = 0; k < j; k++) sum -= a[j, k] * a[j, k];
            if (sum <= 1e-300 || double.IsNaN(sum)) return false;
            var d = Math.Sqrt(sum);
            a[j, j] = d;
            for (var i = j + 1; i < n; i++)
            {
                var s = a[i, j];
                for (var k = 0; k < j; k++) s -= a[i, k] * a[j, k];
                a[i, j] = s / d;
            }
        }
        return true;
    }

    /// <summary>Solves L Lᵀ x = b with the factor stored in the lower triangle.</summary>
    public static double[] SolveCholesky(double[,] l, int n, double[] b)
    {
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            var s = b[i];
            for (var k = 0; k < i; k++) s -= l[i, k] * y[k];
            y[i] = s / l[i, i];
        }
        var x = new double[n];
        for (var i = n - 1; i >= 0; i--)
        {
            var s = y[i];
            for (var k = i + 1; k < n; k++) s -= l[k, i] * x[k];
            x[i] = s / l[i, i];
        }
        return x;
    }
}

/// <summary>Compressed sparse row matrix built row by row.</summary>
public sealed class SparseMatrix
{
    private readonly List<int> _rowStart = [0];
    private readonly List<int> _cols = [];
    private readonly List<double> _vals = [];

    public SparseMatrix(int columns) => Columns = columns;

    public int Columns { get; }

    public int Rows => _rowStart.Count - 1;

    public int NonZeros => _vals.Count;

    /// <summary>Appends a row from (column, value) pairs; duplicate columns are summed by the caller.</summary>
    public void AddRow(IEnumerable<(int Column, double Value)> entries)
    {
        foreach (var (c, v) in entries)
        {
            if (v == 0) continue;
            _cols.Add(c);
            _vals.Add(v);
        }
        _rowStart.Add(_cols.Count);
    }

    public IEnumerable<(int Column, double Value)> Row(int r)
    {
        for (var k = _rowStart[r]; k < _rowStart[r + 1]; k++) yield return (_cols[k], _vals[k]);
    }

    /// <summary>y = A x.</summary>
    public void Multiply(ReadOnlySpan<double> x, Span<double> y)
    {
        for (var r = 0; r < Rows; r++)
        {
            double s = 0;
            for (var k = _rowStart[r]; k < _rowStart[r + 1]; k++) s += _vals[k] * x[_cols[k]];
            y[r] = s;
        }
    }

    /// <summary>y += Aᵀ x.</summary>
    public void MultiplyTransposeAdd(ReadOnlySpan<double> x, Span<double> y)
    {
        for (var r = 0; r < Rows; r++)
        {
            var xr = x[r];
            if (xr == 0) continue;
            for (var k = _rowStart[r]; k < _rowStart[r + 1]; k++) y[_cols[k]] += _vals[k] * xr;
        }
    }

    /// <summary>Scales every row r by s[r].</summary>
    public void ScaleRows(ReadOnlySpan<double> s)
    {
        for (var r = 0; r < Rows; r++)
            for (var k = _rowStart[r]; k < _rowStart[r + 1]; k++) _vals[k] *= s[r];
    }

    /// <summary>Scales every column c by s[c].</summary>
    public void ScaleColumns(ReadOnlySpan<double> s)
    {
        for (var k = 0; k < _vals.Count; k++) _vals[k] *= s[_cols[k]];
    }
}

/// <summary>Result of an LSQR solve.</summary>
public sealed record LsqrResult(double[] X, int Iterations, double ResidualNorm, bool Converged);

/// <summary>
/// LSQR (Paige &amp; Saunders 1982, ACM TOMS 8(1)) for min ‖[A₁; A₂] x − [b₁; b₂]‖ with two stacked
/// sparse blocks (data and regularisation), without forming the normal equations.
/// </summary>
public static class Lsqr
{
    public static LsqrResult Solve(SparseMatrix a1, double[] b1, SparseMatrix a2, double[] b2, int maxIterations = 500, double tolerance = 1e-8, CancellationToken ct = default)
    {
        var n = a1.Columns;
        var m1 = a1.Rows;
        var m2 = a2.Rows;
        var x = new double[n];
        var u = new double[m1 + m2];
        b1.CopyTo(u, 0);
        b2.CopyTo(u, m1);
        var beta = Norm(u);
        if (beta == 0) return new LsqrResult(x, 0, 0, true);
        Scale(u, 1 / beta);
        var v = new double[n];
        a1.MultiplyTransposeAdd(u.AsSpan(0, m1), v);
        a2.MultiplyTransposeAdd(u.AsSpan(m1, m2), v);
        var alpha = Norm(v);
        if (alpha == 0) return new LsqrResult(x, 0, beta, true);
        Scale(v, 1 / alpha);
        var w = (double[])v.Clone();
        var phiBar = beta;
        var rhoBar = alpha;
        var bnorm = beta;
        var tmpU = new double[m1 + m2];
        var tmpV = new double[n];
        var iterations = 0;
        var converged = false;
        for (var it = 0; it < maxIterations; it++)
        {
            ct.ThrowIfCancellationRequested();
            iterations = it + 1;
            // u = A v − α u
            a1.Multiply(v, tmpU.AsSpan(0, m1));
            a2.Multiply(v, tmpU.AsSpan(m1, m2));
            for (var i = 0; i < u.Length; i++) u[i] = tmpU[i] - alpha * u[i];
            beta = Norm(u);
            if (beta > 0) Scale(u, 1 / beta);
            // v = Aᵀ u − β v
            Array.Clear(tmpV);
            a1.MultiplyTransposeAdd(u.AsSpan(0, m1), tmpV);
            a2.MultiplyTransposeAdd(u.AsSpan(m1, m2), tmpV);
            for (var i = 0; i < n; i++) v[i] = tmpV[i] - beta * v[i];
            alpha = Norm(v);
            if (alpha > 0) Scale(v, 1 / alpha);

            var rho = Math.Sqrt(rhoBar * rhoBar + beta * beta);
            var c = rhoBar / rho;
            var s = beta / rho;
            var theta = s * alpha;
            rhoBar = -c * alpha;
            var phi = c * phiBar;
            phiBar = s * phiBar;
            var t1 = phi / rho;
            var t2 = -theta / rho;
            for (var i = 0; i < n; i++)
            {
                x[i] += t1 * w[i];
                w[i] = v[i] + t2 * w[i];
            }
            // ‖Aᵀr‖ estimate = φ̄·α·|c|; stop when small relative to ‖b‖.
            var arNorm = phiBar * alpha * Math.Abs(c);
            if (phiBar <= tolerance * bnorm || arNorm <= tolerance * bnorm * Math.Max(alpha, 1e-300))
            {
                converged = true;
                break;
            }
            if (alpha == 0 || beta == 0)
            {
                converged = true;
                break;
            }
        }
        return new LsqrResult(x, iterations, phiBar, converged);
    }

    private static double Norm(ReadOnlySpan<double> v)
    {
        double s = 0;
        foreach (var e in v) s += e * e;
        return Math.Sqrt(s);
    }

    private static void Scale(Span<double> v, double f)
    {
        for (var i = 0; i < v.Length; i++) v[i] *= f;
    }
}
