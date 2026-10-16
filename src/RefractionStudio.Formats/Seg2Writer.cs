using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace RefractionStudio.Formats;

/// <summary>A trace to be written to SEG-2.</summary>
/// <param name="Keywords">Trace descriptor strings (SAMPLE_INTERVAL, DELAY, CHANNEL_NUMBER… are added if missing).</param>
/// <param name="Samples">Sample values (already scaled to the stored integer range for integer formats).</param>
public sealed record Seg2TraceToWrite(IReadOnlyDictionary<string, string> Keywords, double[] Samples);

/// <summary>
/// Writes SEG-2 files. Used for synthetic data, the sample project, golden test files and export.
/// Integer encodings round to nearest; the 20-bit encoding chooses, for each sample, the smallest exponent
/// (0–15) that fits its mantissa in 16 bits (four 4-bit exponents per group of four samples).
/// </summary>
public static class Seg2Writer
{
    /// <summary>Writes a file to a byte array.</summary>
    /// <param name="fileKeywords">File descriptor strings (ACQUISITION_DATE, INSTRUMENT…).</param>
    /// <param name="traces">Traces.</param>
    /// <param name="formatCode">1 int16, 2 int32, 3 20-bit, 4 float32, 5 float64.</param>
    /// <param name="littleEndian">Byte order.</param>
    public static byte[] Write(IReadOnlyDictionary<string, string> fileKeywords, IReadOnlyList<Seg2TraceToWrite> traces, byte formatCode = 4, bool littleEndian = true)
    {
        if (traces.Count is 0 or > 65535) throw new ArgumentException("A SEG-2 file holds 1–65535 traces.", nameof(traces));
        if (formatCode is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(formatCode));
        using var ms = new MemoryStream();
        var le = littleEndian;
        var pointerBlock = traces.Count * 4;

        var head = new byte[32];
        W16(head, 0, Seg2Reader.FileDescriptorId, le);
        W16(head, 2, 1, le);
        W16(head, 4, (ushort)pointerBlock, le);
        W16(head, 6, (ushort)traces.Count, le);
        head[8] = 1; head[9] = 0; head[10] = 0;  // string terminator: NUL
        head[11] = 1; head[12] = 0x0a; head[13] = 0; // line terminator: LF
        ms.Write(head);
        ms.Write(new byte[pointerBlock]);
        ms.Write(EncodeStrings(fileKeywords, le));
        Pad4(ms);

        var pointers = new uint[traces.Count];
        for (var i = 0; i < traces.Count; i++)
        {
            pointers[i] = (uint)ms.Position;
            var t = traces[i];
            var kw = new Dictionary<string, string>(t.Keywords, StringComparer.Ordinal);
            kw.TryAdd("CHANNEL_NUMBER", (i + 1).ToString(CultureInfo.InvariantCulture));
            if (!kw.ContainsKey("SAMPLE_INTERVAL")) throw new ArgumentException($"Trace {i + 1} has no SAMPLE_INTERVAL.");
            var strings = EncodeStrings(kw, le);
            var blockSize = 32 + strings.Length;
            blockSize += (4 - blockSize % 4) % 4;
            var payload = EncodeSamples(t.Samples, formatCode, le);
            var desc = new byte[blockSize];
            W16(desc, 0, Seg2Reader.TraceDescriptorId, le);
            W16(desc, 2, (ushort)blockSize, le);
            W32(desc, 4, (uint)payload.Length, le);
            W32(desc, 8, (uint)t.Samples.Length, le);
            desc[12] = formatCode;
            strings.CopyTo(desc, 32);
            ms.Write(desc);
            ms.Write(payload);
            Pad4(ms);
        }

        var bytes = ms.ToArray();
        for (var i = 0; i < pointers.Length; i++) W32(bytes, 32 + 4 * i, pointers[i], le);
        return bytes;
    }

    private static byte[] EncodeStrings(IReadOnlyDictionary<string, string> keywords, bool le)
    {
        using var ms = new MemoryStream();
        foreach (var (key, value) in keywords.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var text = Encoding.Latin1.GetBytes(value.Length > 0 ? key + " " + value : key);
            var len = 2 + text.Length + 1;
            var buf = new byte[len];
            W16(buf, 0, (ushort)len, le);
            text.CopyTo(buf, 2);
            ms.Write(buf);
        }
        ms.Write(new byte[2]); // end of string list
        return ms.ToArray();
    }

    private static byte[] EncodeSamples(double[] s, byte code, bool le)
    {
        switch (code)
        {
            case 1:
            {
                var b = new byte[2 * s.Length];
                for (var i = 0; i < s.Length; i++)
                {
                    var v = (short)Math.Clamp(Math.Round(s[i]), short.MinValue, short.MaxValue);
                    if (le) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2 * i), v); else BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(2 * i), v);
                }
                return b;
            }
            case 2:
            {
                var b = new byte[4 * s.Length];
                for (var i = 0; i < s.Length; i++)
                {
                    var v = (int)Math.Clamp(Math.Round(s[i]), int.MinValue, int.MaxValue);
                    if (le) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4 * i), v); else BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4 * i), v);
                }
                return b;
            }
            case 3:
            {
                if (s.Length % 4 != 0) throw new ArgumentException("The 20-bit encoding needs a multiple of 4 samples.");
                var b = new byte[s.Length / 4 * 10];
                for (var g = 0; g < s.Length / 4; g++)
                {
                    ushort exponents = 0;
                    var mantissas = new short[4];
                    for (var k = 0; k < 4; k++)
                    {
                        var v = Math.Round(s[4 * g + k]);
                        var e = 0;
                        while (e < 15 && Math.Abs(v) / (1 << e) > short.MaxValue) e++;
                        var m = (int)Math.Clamp(Math.Round(v / (1 << e)), -short.MaxValue, short.MaxValue);
                        if (m < 0) m -= 1; // two's complement → one's complement
                        mantissas[k] = (short)m;
                        exponents |= (ushort)(e << (4 * k));
                    }
                    var span = b.AsSpan(10 * g);
                    if (le) BinaryPrimitives.WriteUInt16LittleEndian(span, exponents); else BinaryPrimitives.WriteUInt16BigEndian(span, exponents);
                    for (var k = 0; k < 4; k++)
                    {
                        if (le) BinaryPrimitives.WriteInt16LittleEndian(span[(2 + 2 * k)..], mantissas[k]);
                        else BinaryPrimitives.WriteInt16BigEndian(span[(2 + 2 * k)..], mantissas[k]);
                    }
                }
                return b;
            }
            case 4:
            {
                var b = new byte[4 * s.Length];
                for (var i = 0; i < s.Length; i++)
                {
                    if (le) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4 * i), (float)s[i]); else BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4 * i), (float)s[i]);
                }
                return b;
            }
            default:
            {
                var b = new byte[8 * s.Length];
                for (var i = 0; i < s.Length; i++)
                {
                    if (le) BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(8 * i), s[i]); else BinaryPrimitives.WriteDoubleBigEndian(b.AsSpan(8 * i), s[i]);
                }
                return b;
            }
        }
    }

    private static void Pad4(MemoryStream ms)
    {
        while (ms.Position % 4 != 0) ms.WriteByte(0);
    }

    private static void W16(byte[] b, int o, ushort v, bool le)
    {
        if (le) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v); else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), v);
    }

    private static void W32(byte[] b, int o, uint v, bool le)
    {
        if (le) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v); else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v);
    }
}
