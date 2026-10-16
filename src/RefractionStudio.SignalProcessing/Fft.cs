using System.Numerics;

namespace RefractionStudio.SignalProcessing;

/// <summary>Iterative radix-2 Cooley–Tukey FFT.</summary>
public static class Fft
{
    /// <summary>Smallest power of two ≥ <paramref name="n"/>.</summary>
    public static int NextPowerOfTwo(int n)
    {
        if (n < 1) return 1;
        var p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>In-place forward (e^{-iωt}) or inverse transform. Inverse is scaled by 1/N.</summary>
    public static void Transform(Span<Complex> data, bool inverse)
    {
        var n = data.Length;
        if (n == 0) return;
        if ((n & (n - 1)) != 0) throw new ArgumentException("FFT length must be a power of two.", nameof(data));

        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wLen = new Complex(Math.Cos(angle), Math.Sin(angle));
            var half = len / 2;
            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (var k = 0; k < half; k++)
                {
                    var u = data[i + k];
                    var v = data[i + k + half] * w;
                    data[i + k] = u + v;
                    data[i + k + half] = u - v;
                    w *= wLen;
                }
            }
        }

        if (inverse)
        {
            var scale = 1.0 / n;
            for (var i = 0; i < n; i++) data[i] *= scale;
        }
    }

    /// <summary>Forward FFT of a real signal zero-padded to <paramref name="n"/> (power of two).</summary>
    public static Complex[] ForwardReal(ReadOnlySpan<double> signal, int n)
    {
        var buffer = new Complex[n];
        var count = Math.Min(n, signal.Length);
        for (var i = 0; i < count; i++) buffer[i] = new Complex(signal[i], 0);
        Transform(buffer, inverse: false);
        return buffer;
    }
}
