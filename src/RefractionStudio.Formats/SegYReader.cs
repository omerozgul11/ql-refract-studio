using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RefractionStudio.Domain;

namespace RefractionStudio.Formats;

/// <summary>
/// Reader for SEG-Y revisions 0, 1 and 2 (SEG Technical Standards Committee; rev 1 2002, rev 2.0 2017).
/// Traces are grouped into records by field record number (trace header bytes 9–12) in file order.
/// Supported sample formats: 1 (IBM float32), 2 (int32), 3 (int16), 5 (IEEE float32), 6 (IEEE float64),
/// 8 (int8). Byte order is big-endian unless the rev-2 byte-order constant or header plausibility
/// indicates little-endian. Coordinates use the coordinate and elevation scalars.
/// See docs/file-formats/segy.md.
/// </summary>
public sealed class SegYReader : ISeismicDataReader
{
    private const int TextHeaderLength = 3200;
    private const int BinaryHeaderLength = 400;
    private const int TraceHeaderLength = 240;

    public string Id => "segy";
    public string Name => "SEG-Y";
    public IReadOnlyList<string> SupportedExtensions { get; } = [".sgy", ".segy"];

    /// <summary>
    /// .sgy/.segy files are accepted when the binary header is plausible. Files with other extensions are
    /// recognised by content: a 3200-byte card-image textual header (EBCDIC or ASCII, cards starting with
    /// "C") followed by a valid binary header.
    /// </summary>
    public bool CanRead(ReadOnlySpan<byte> head, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".sgy" or ".segy") return head.Length < TextHeaderLength + BinaryHeaderLength || TryBinaryHeader(head, out _);
        return head.Length >= TextHeaderLength + BinaryHeaderLength && LooksLikeTextHeader(head[..TextHeaderLength]) && TryBinaryHeader(head, out _);
    }

    private static bool LooksLikeTextHeader(ReadOnlySpan<byte> text)
    {
        var cards = 0;
        for (var i = 0; i < 40; i++)
        {
            var c = text[i * 80];
            if (c is 0xC3 or (byte)'C') cards++; // "C" in EBCDIC or ASCII
        }
        return cards >= 30;
    }

    public Task<DecodedFile> ReadMetadataAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
        => ReadInternalAsync(stream, fileName, false, cancellationToken);

    public Task<DecodedFile> ReadAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
        => ReadInternalAsync(stream, fileName, true, cancellationToken);

    private static async Task<DecodedFile> ReadInternalAsync(Stream stream, string fileName, bool includeSamples, CancellationToken ct)
    {
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct).ConfigureAwait(false);
        return Parse(copy.ToArray(), fileName, includeSamples, ct);
    }

    private readonly record struct BinaryHeader(bool Little, int SampleIntervalMicro, int SamplesPerTrace, int FormatCode, int Revision, int ExtendedTextHeaders, bool FixedLength);

    private static bool TryBinaryHeader(ReadOnlySpan<byte> data, out BinaryHeader header)
    {
        header = default;
        if (data.Length < TextHeaderLength + BinaryHeaderLength) return false;
        var b = data.Slice(TextHeaderLength, BinaryHeaderLength);
        bool? little = null;
        var constant = BinaryPrimitives.ReadUInt32BigEndian(b[96..]);
        if (constant == 0x01020304) little = false;
        else if (constant == 0x04030201) little = true;
        foreach (var candidate in little is { } l ? new[] { l } : [false, true])
        {
            var format = I16(b, 24, candidate);
            // Sample interval (µs) and samples per trace are unsigned 16-bit values.
            int dt = (ushort)I16(b, 16, candidate);
            int ns = (ushort)I16(b, 20, candidate);
            if (format is 1 or 2 or 3 or 5 or 6 or 8 && dt > 0)
            {
                var rev = (ushort)I16(b, 300, candidate) >> 8; // bytes 3501–3502: major revision in the high byte
                var ext = I16(b, 304, candidate);
                var fixedFlag = I16(b, 302, candidate);
                // Revision 2: extended samples per trace (bytes 3269–3272) when the 16-bit field is zero.
                if (rev >= 2 && ns == 0 && b.Length >= 72) ns = Math.Max(0, I32(b, 68, candidate));
                header = new BinaryHeader(candidate, dt, ns, format, rev, Math.Max(0, (int)ext), fixedFlag == 1);
                return true;
            }
        }
        return false;
    }

    /// <summary>Parses a complete SEG-Y file held in memory.</summary>
    public static DecodedFile Parse(ReadOnlySpan<byte> data, string fileName, bool includeSamples, CancellationToken ct = default)
    {
        if (data.Length < TextHeaderLength + BinaryHeaderLength)
            throw new SeismicFormatException($"“{fileName}” is too short to be a SEG-Y file.");
        if (!TryBinaryHeader(data, out var bh))
            throw new SeismicFormatException($"“{fileName}” does not have a valid SEG-Y binary header (unsupported sample format or zero sample interval).",
                "Bytes 3217–3218 (sample interval) and 3225–3226 (format code 1, 2, 3, 5, 6 or 8) were checked in both byte orders.");

        var notices = ImmutableArray.CreateBuilder<string>();
        var textHeader = DecodeTextHeader(data[..TextHeaderLength]);
        var little = bh.Little;
        var bytesPerSample = bh.FormatCode switch { 1 or 2 or 5 => 4, 3 => 2, 6 => 8, _ => 1 };
        var pos = TextHeaderLength + BinaryHeaderLength + 3200 * bh.ExtendedTextHeaders;
        if (bh.ExtendedTextHeaders > 0) notices.Add($"{bh.ExtendedTextHeaders} extended textual header(s) skipped.");

        var groups = new List<(int Ffid, List<DecodedTrace> Traces, DateTimeOffset? Time, double? SourceX)>();
        var traceIndex = 0;
        while (pos + TraceHeaderLength <= data.Length)
        {
            ct.ThrowIfCancellationRequested();
            var th = data.Slice(pos, TraceHeaderLength);
            int ns = (ushort)I16(th, 114, little);
            if (bh.FixedLength || ns == 0) ns = bh.SamplesPerTrace;
            var dtMicro = (ushort)I16(th, 116, little);
            if (dtMicro == 0) dtMicro = (ushort)bh.SampleIntervalMicro;
            var payload = ns * bytesPerSample;
            if (pos + TraceHeaderLength + payload > data.Length)
                throw new SeismicFormatException($"“{fileName}” is truncated: trace {traceIndex + 1} extends beyond the end of the file.",
                    $"Trace header at byte {pos}, {ns} samples of {bytesPerSample} bytes.");

            var ffid = I32(th, 8, little);
            var channel = I32(th, 12, little);
            if (channel <= 0) channel = I32(th, 0, little);
            var scalarCoord = I16(th, 70, little);
            var scalarElev = I16(th, 68, little);
            var srcX = Scale(I32(th, 72, little), scalarCoord);
            var recX = Scale(I32(th, 80, little), scalarCoord);
            var recZ = Scale(I32(th, 40, little), scalarElev);
            // Delay recording time (bytes 109–110, ms); from revision 1 the time scalar (bytes 215–216)
            // applies to it (positive: multiply, negative: divide, zero: 1).
            double delayMs = I16(th, 108, little);
            if (bh.Revision >= 1)
            {
                var timeScalar = I16(th, 214, little);
                if (timeScalar > 0) delayMs *= timeScalar;
                else if (timeScalar < 0) delayMs /= -timeScalar;
            }
            var stack = I16(th, 30, little); // bytes 31–32: vertically summed traces

            var samples = includeSamples ? DecodeSamples(data.Slice(pos + TraceHeaderLength, payload), ns, bh.FormatCode, little) : [];
            if (includeSamples)
            {
                foreach (var v in samples)
                    if (!double.IsFinite(v))
                        throw new SeismicFormatException($"Trace {traceIndex + 1} of “{fileName}” contains invalid (non-finite) sample values.");
            }

            var keywords = ImmutableDictionary.CreateRange(StringComparer.Ordinal, new Dictionary<string, string>
            {
                ["FIELD_RECORD"] = ffid.ToString(CultureInfo.InvariantCulture),
                ["TRACE_IN_RECORD"] = channel.ToString(CultureInfo.InvariantCulture),
                ["SOURCE_X"] = srcX.ToString("R", CultureInfo.InvariantCulture),
                ["RECEIVER_X"] = recX.ToString("R", CultureInfo.InvariantCulture),
            });
            var formatName = bh.FormatCode switch { 1 => "IBM float32", 2 => "int32", 3 => "int16", 5 => "float32", 6 => "float64", _ => "int8" };

            var group = groups.Count > 0 && groups[^1].Ffid == ffid ? groups[^1] : default;
            if (group.Traces is null)
            {
                group = (ffid, new List<DecodedTrace>(), TraceTime(th, little), srcX);
                groups.Add(group);
            }
            var header = new TraceHeader(channel, group.Traces.Count, ns, dtMicro * 1e-6, delayMs * 1e-3,
                recX, scalarElev == 0 && recZ == 0 ? null : recZ, srcX, stack > 0 ? stack : null, formatName, keywords);
            group.Traces.Add(new DecodedTrace(header, samples));
            pos += TraceHeaderLength + payload;
            traceIndex++;
        }
        if (traceIndex == 0) throw new SeismicFormatException($"“{fileName}” contains no traces.");
        if (pos != data.Length) notices.Add($"{data.Length - pos} trailing byte(s) after the last trace were ignored.");

        var fileKw = ImmutableDictionary.CreateRange(StringComparer.Ordinal, new Dictionary<string, string> { ["TEXT_HEADER"] = textHeader });
        var records = groups.Select(g => new DecodedRecord(fileKw, [.. g.Traces], g.Time, g.Ffid, g.SourceX)).ToImmutableArray();
        var description = $"SEG-Y rev {bh.Revision}, {(little ? "little" : "big")}-endian, format code {bh.FormatCode}";
        return new DecodedFile("segy", description, records, notices.ToImmutable());
    }

    private static DateTimeOffset? TraceTime(ReadOnlySpan<byte> th, bool little)
    {
        var year = I16(th, 156, little);
        var day = I16(th, 158, little);
        var hour = I16(th, 160, little);
        var minute = I16(th, 162, little);
        var second = I16(th, 164, little);
        if (year <= 0 || day is <= 0 or > 366) return null;
        if (year < 100) year += year < 70 ? (short)2000 : (short)1900;
        try
        {
            return new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day - 1)
                .AddHours(Math.Clamp((int)hour, 0, 23)).AddMinutes(Math.Clamp((int)minute, 0, 59)).AddSeconds(Math.Clamp((int)second, 0, 59));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static double Scale(int value, short scalar)
        => scalar switch
        {
            0 or 1 => value,
            > 0 => value * (double)scalar,
            _ => value / (double)-scalar,
        };

    private static double[] DecodeSamples(ReadOnlySpan<byte> raw, int count, int format, bool little)
    {
        var s = new double[count];
        for (var i = 0; i < count; i++)
        {
            s[i] = format switch
            {
                1 => IbmToDouble(little ? BinaryPrimitives.ReadUInt32LittleEndian(raw[(4 * i)..]) : BinaryPrimitives.ReadUInt32BigEndian(raw[(4 * i)..])),
                2 => little ? BinaryPrimitives.ReadInt32LittleEndian(raw[(4 * i)..]) : BinaryPrimitives.ReadInt32BigEndian(raw[(4 * i)..]),
                3 => little ? BinaryPrimitives.ReadInt16LittleEndian(raw[(2 * i)..]) : BinaryPrimitives.ReadInt16BigEndian(raw[(2 * i)..]),
                5 => little ? BinaryPrimitives.ReadSingleLittleEndian(raw[(4 * i)..]) : BinaryPrimitives.ReadSingleBigEndian(raw[(4 * i)..]),
                6 => little ? BinaryPrimitives.ReadDoubleLittleEndian(raw[(8 * i)..]) : BinaryPrimitives.ReadDoubleBigEndian(raw[(8 * i)..]),
                _ => (sbyte)raw[i],
            };
        }
        return s;
    }

    /// <summary>Converts an IBM System/360 single-precision float: (−1)^s · 0.F · 16^(E−64).</summary>
    public static double IbmToDouble(uint bits)
    {
        var sign = (bits >> 31) != 0 ? -1.0 : 1.0;
        var exponent = (int)((bits >> 24) & 0x7F) - 64;
        var fraction = (bits & 0x00FFFFFF) / (double)(1 << 24);
        return fraction == 0 ? 0 : sign * fraction * Math.Pow(16, exponent);
    }

    /// <summary>Converts a double to IBM single precision (for test files).</summary>
    public static uint DoubleToIbm(double value)
    {
        if (value == 0) return 0;
        var sign = value < 0 ? 0x80000000u : 0;
        var v = Math.Abs(value);
        var exponent = 64;
        while (v >= 1) { v /= 16; exponent++; }
        while (v < 1.0 / 16) { v *= 16; exponent--; }
        var fraction = (uint)Math.Round(v * (1 << 24));
        if (fraction >= 1u << 24) { fraction >>= 4; exponent++; }
        return sign | ((uint)exponent << 24) | fraction;
    }

    private static string DecodeTextHeader(ReadOnlySpan<byte> text)
    {
        // EBCDIC headers start with 'C' (0xC3); ASCII with 'C' (0x43).
        var ebcdic = text.Length > 0 && text[0] == 0xC3;
        var chars = new char[text.Length];
        for (var i = 0; i < text.Length; i++) chars[i] = ebcdic ? EbcdicToAscii(text[i]) : (char)(text[i] is >= 32 and < 127 ? text[i] : (byte)' ');
        var sb = new StringBuilder();
        for (var line = 0; line < text.Length / 80; line++)
            sb.AppendLine(new string(chars, line * 80, 80).TrimEnd());
        return sb.ToString().TrimEnd();
    }

    private static char EbcdicToAscii(byte b)
    {
        // Printable subset of code page 037.
        return b switch
        {
            0x40 => ' ', 0x4B => '.', 0x4C => '<', 0x4D => '(', 0x4E => '+', 0x50 => '&', 0x5B => '$', 0x5C => '*', 0x5D => ')',
            0x5E => ';', 0x60 => '-', 0x61 => '/', 0x6B => ',', 0x6C => '%', 0x6D => '_', 0x6E => '>', 0x6F => '?', 0x7A => ':',
            0x7B => '#', 0x7C => '@', 0x7D => '\'', 0x7E => '=', 0x7F => '"',
            >= 0x81 and <= 0x89 => (char)('a' + b - 0x81),
            >= 0x91 and <= 0x99 => (char)('j' + b - 0x91),
            >= 0xA2 and <= 0xA9 => (char)('s' + b - 0xA2),
            >= 0xC1 and <= 0xC9 => (char)('A' + b - 0xC1),
            >= 0xD1 and <= 0xD9 => (char)('J' + b - 0xD1),
            >= 0xE2 and <= 0xE9 => (char)('S' + b - 0xE2),
            >= 0xF0 and <= 0xF9 => (char)('0' + b - 0xF0),
            _ => ' ',
        };
    }

    private static short I16(ReadOnlySpan<byte> d, int o, bool little)
        => little ? BinaryPrimitives.ReadInt16LittleEndian(d[o..]) : BinaryPrimitives.ReadInt16BigEndian(d[o..]);

    private static int I32(ReadOnlySpan<byte> d, int o, bool little)
        => little ? BinaryPrimitives.ReadInt32LittleEndian(d[o..]) : BinaryPrimitives.ReadInt32BigEndian(d[o..]);
}
