using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RefractionStudio.Domain;

namespace RefractionStudio.Formats;

/// <summary>
/// Reader for SEG-2 (Pullan, S.E., 1990, "Recommended standard for seismic (/radar) data files in the
/// personal computer environment", Geophysics 55(9), 1260–1271). Supports both byte orders and data
/// format codes 1 (int16), 2 (int32), 3 (20-bit packed floating point), 4 (IEEE float32) and
/// 5 (IEEE float64). Samples are multiplied by DESCALING_FACTOR when present.
/// See docs/file-formats/seg2.md.
/// </summary>
public sealed class Seg2Reader : ISeismicDataReader
{
    public const ushort FileDescriptorId = 0x3a55;
    public const ushort TraceDescriptorId = 0x4422;

    public string Id => "seg2";
    public string Name => "SEG-2";
    public IReadOnlyList<string> SupportedExtensions { get; } = [".sg2", ".seg2", ".dat"];

    public bool CanRead(ReadOnlySpan<byte> head, string fileName)
        => head.Length >= 2 && ((head[0] == 0x55 && head[1] == 0x3a) || (head[0] == 0x3a && head[1] == 0x55));

    public Task<DecodedFile> ReadMetadataAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
        => ReadInternalAsync(stream, fileName, includeSamples: false, cancellationToken);

    public Task<DecodedFile> ReadAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
        => ReadInternalAsync(stream, fileName, includeSamples: true, cancellationToken);

    private static async Task<DecodedFile> ReadInternalAsync(Stream stream, string fileName, bool includeSamples, CancellationToken ct)
    {
        byte[] bytes;
        if (stream is MemoryStream ms && ms.TryGetBuffer(out var seg) && seg.Offset == 0 && ms.Position == 0)
        {
            bytes = seg.Array!.AsSpan(0, (int)ms.Length).ToArray();
        }
        else
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, ct).ConfigureAwait(false);
            bytes = copy.ToArray();
        }
        ct.ThrowIfCancellationRequested();
        return Parse(bytes, fileName, includeSamples, ct);
    }

    /// <summary>Parses a complete SEG-2 file held in memory.</summary>
    public static DecodedFile Parse(ReadOnlySpan<byte> data, string fileName, bool includeSamples, CancellationToken ct = default)
    {
        if (data.Length < 32)
            throw new SeismicFormatException($"“{fileName}” is too short to be a SEG-2 file ({data.Length} bytes).");

        bool little;
        if (data[0] == 0x55 && data[1] == 0x3a) little = true;
        else if (data[0] == 0x3a && data[1] == 0x55) little = false;
        else
            throw new SeismicFormatException($"“{fileName}” is not a SEG-2 file (the file descriptor block identifier is missing).",
                $"First bytes: {data[0]:X2} {data[1]:X2}; expected 55 3A (little-endian) or 3A 55 (big-endian).");

        var revision = U16(data, 2, little);
        var pointerBlockSize = U16(data, 4, little);
        var traceCount = U16(data, 6, little);
        var stringTermSize = data[8];
        var lineTermSize = data[11];
        if (stringTermSize is not (1 or 2))
            throw new SeismicFormatException($"“{fileName}” has an invalid SEG-2 header (string terminator size {stringTermSize}).",
                "Byte 8 of the file descriptor block must be 1 or 2.");
        if (lineTermSize > 2)
            throw new SeismicFormatException($"“{fileName}” has an invalid SEG-2 header (line terminator size {lineTermSize}).",
                "Byte 11 of the file descriptor block must be 0, 1 or 2.");
        var stringTerm = data.Slice(9, stringTermSize).ToArray();
        var lineTerm = data.Slice(12, lineTermSize).ToArray();

        if (traceCount == 0)
            throw new SeismicFormatException($"“{fileName}” contains no traces.");
        if (traceCount * 4 > pointerBlockSize)
            throw new SeismicFormatException($"“{fileName}” has an inconsistent SEG-2 header: {traceCount} traces but room for only {pointerBlockSize / 4} trace pointers.",
                $"N = {traceCount}, M = {pointerBlockSize}.");
        if (32 + pointerBlockSize > data.Length)
            throw new SeismicFormatException($"“{fileName}” is truncated: the trace pointer table extends beyond the end of the file.",
                $"Pointer table ends at byte {32 + pointerBlockSize}, file length {data.Length}.");

        var pointers = new uint[traceCount];
        for (var i = 0; i < traceCount; i++) pointers[i] = U32(data, 32 + 4 * i, little);

        var notices = ImmutableArray.CreateBuilder<string>();
        if (revision != 1) notices.Add($"SEG-2 revision number is {revision}; the file was read with the revision 1 rules.");

        var stringsStart = 32 + pointerBlockSize;
        var firstTrace = pointers.Min();
        var fileStringsEnd = (int)Math.Min(firstTrace, (uint)data.Length);
        var fileKeywords = fileStringsEnd > stringsStart
            ? ParseStrings(data[stringsStart..fileStringsEnd], little, stringTerm, lineTerm)
            : ImmutableDictionary<string, string>.Empty;

        var traces = ImmutableArray.CreateBuilder<DecodedTrace>(traceCount);
        for (var i = 0; i < traceCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            traces.Add(ReadTrace(data, (int)pointers[i], i, fileName, little, stringTerm, lineTerm, includeSamples));
        }

        var acquired = ParseAcquisition(fileKeywords);
        int? shot = TryInt(fileKeywords, "SHOT_SEQUENCE_NUMBER");
        double? sourceX = traces.Count > 0 ? traces[0].Header.SourceX : null;
        if (sourceX is null && fileKeywords.TryGetValue("SOURCE_LOCATION", out var sl)) sourceX = FirstNumber(sl);

        var record = new DecodedRecord(fileKeywords, traces.MoveToImmutable(), acquired, shot, sourceX);
        var description = $"SEG-2 rev {revision}, {(little ? "little" : "big")}-endian";
        return new DecodedFile("seg2", description, [record], notices.ToImmutable());
    }

    private static DecodedTrace ReadTrace(ReadOnlySpan<byte> data, int offset, int index, string fileName, bool little,
        byte[] stringTerm, byte[] lineTerm, bool includeSamples)
    {
        var label = $"trace {index + 1} of “{fileName}”";
        if (offset < 32 || offset + 32 > data.Length)
            throw new SeismicFormatException($"The file is truncated or damaged: {label} points outside the file.",
                $"Trace pointer {offset}, file length {data.Length}.");
        var id = U16(data, offset, little);
        if (id != TraceDescriptorId)
            throw new SeismicFormatException($"The file is damaged: {label} does not start with a SEG-2 trace descriptor.",
                $"At byte {offset}: identifier 0x{id:X4}, expected 0x4422.");
        var blockSize = U16(data, offset + 2, little);
        var dataSize = U32(data, offset + 4, little);
        var sampleCount = U32(data, offset + 8, little);
        var formatCode = data[offset + 12];
        if (blockSize < 32 || offset + blockSize > data.Length)
            throw new SeismicFormatException($"The file is damaged: the descriptor of {label} has an invalid size.",
                $"Descriptor size {blockSize} at byte {offset}.");

        var keywords = ParseStrings(data.Slice(offset + 32, blockSize - 32), little, stringTerm, lineTerm);
        var (bytesPerSample, formatName) = formatCode switch
        {
            1 => (2.0, "int16"),
            2 => (4.0, "int32"),
            3 => (2.5, "20-bit float"),
            4 => (4.0, "float32"),
            5 => (8.0, "float64"),
            _ => throw new SeismicFormatException($"{Capitalize(label)} uses an unsupported sample encoding (data format code {formatCode}).",
                "SEG-2 defines codes 1–5."),
        };
        if (formatCode == 3 && sampleCount % 4 != 0)
            throw new SeismicFormatException($"{Capitalize(label)} uses the 20-bit encoding with a sample count ({sampleCount}) that is not a multiple of 4.");
        var needed = (long)Math.Ceiling(sampleCount * bytesPerSample);
        var dataStart = offset + blockSize;
        if (needed > dataSize)
            throw new SeismicFormatException($"{Capitalize(label)} declares {sampleCount} samples but its data block holds only {dataSize} bytes.");
        if (dataStart + needed > data.Length)
            throw new SeismicFormatException($"The file is truncated: the samples of {label} extend beyond the end of the file.",
                $"Samples end at byte {dataStart + needed}, file length {data.Length}.");

        var dt = TryDouble(keywords, "SAMPLE_INTERVAL")
            ?? throw new SeismicFormatException($"{Capitalize(label)} has no SAMPLE_INTERVAL; the time axis cannot be established.",
                "The SAMPLE_INTERVAL keyword is required by the SEG-2 standard.");
        if (!(dt > 0) || !double.IsFinite(dt))
            throw new SeismicFormatException($"{Capitalize(label)} has an invalid sample interval ({dt}).");
        var delay = TryDouble(keywords, "DELAY") ?? 0.0;
        var channel = TryInt(keywords, "CHANNEL_NUMBER") ?? index + 1;
        var descale = TryDouble(keywords, "DESCALING_FACTOR");
        var stack = TryInt(keywords, "STACK");

        double? recX = null, recZ = null, srcX = null;
        if (keywords.TryGetValue("RECEIVER_LOCATION", out var rl))
        {
            var nums = Numbers(rl);
            if (nums.Count >= 1) recX = nums[0];
            if (nums.Count >= 3) recZ = nums[2];
        }
        if (keywords.TryGetValue("SOURCE_LOCATION", out var sls)) srcX = FirstNumber(sls);

        double[] samples = [];
        if (includeSamples)
        {
            samples = DecodeSamples(data.Slice(dataStart, (int)needed), (int)sampleCount, formatCode, little);
            if (descale is { } f && f != 0 && f != 1)
                for (var i = 0; i < samples.Length; i++) samples[i] *= f;
            foreach (var v in samples)
                if (!double.IsFinite(v))
                    throw new SeismicFormatException($"{Capitalize(label)} contains invalid (non-finite) sample values.");
        }

        var header = new TraceHeader(channel, index, (int)sampleCount, dt, delay, recX, recZ, srcX, stack, formatName, keywords);
        return new DecodedTrace(header, samples);
    }

    private static double[] DecodeSamples(ReadOnlySpan<byte> raw, int count, byte code, bool little)
    {
        var s = new double[count];
        switch (code)
        {
            case 1:
                for (var i = 0; i < count; i++)
                    s[i] = little ? BinaryPrimitives.ReadInt16LittleEndian(raw[(2 * i)..]) : BinaryPrimitives.ReadInt16BigEndian(raw[(2 * i)..]);
                break;
            case 2:
                for (var i = 0; i < count; i++)
                    s[i] = little ? BinaryPrimitives.ReadInt32LittleEndian(raw[(4 * i)..]) : BinaryPrimitives.ReadInt32BigEndian(raw[(4 * i)..]);
                break;
            case 3:
                // Groups of 10 bytes = one 16-bit exponent word (4-bit exponent per sample, first sample
                // in the least-significant nibble) followed by four 16-bit one's-complement mantissas.
                for (var g = 0; g < count / 4; g++)
                {
                    var b = raw.Slice(10 * g, 10);
                    var exponents = little ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);
                    for (var k = 0; k < 4; k++)
                    {
                        int m = little ? BinaryPrimitives.ReadInt16LittleEndian(b[(2 + 2 * k)..]) : BinaryPrimitives.ReadInt16BigEndian(b[(2 + 2 * k)..]);
                        if (m < 0) m += 1; // one's complement → two's complement
                        var e = (exponents >> (4 * k)) & 0xF;
                        s[4 * g + k] = m * (double)(1 << e);
                    }
                }
                break;
            case 4:
                for (var i = 0; i < count; i++)
                    s[i] = little ? BinaryPrimitives.ReadSingleLittleEndian(raw[(4 * i)..]) : BinaryPrimitives.ReadSingleBigEndian(raw[(4 * i)..]);
                break;
            case 5:
                for (var i = 0; i < count; i++)
                    s[i] = little ? BinaryPrimitives.ReadDoubleLittleEndian(raw[(8 * i)..]) : BinaryPrimitives.ReadDoubleBigEndian(raw[(8 * i)..]);
                break;
        }
        return s;
    }

    /// <summary>
    /// Parses a SEG-2 string list: each entry is a 2-byte offset to the next entry (including the
    /// offset itself) followed by "KEYWORD value" and the string terminator; a zero offset ends the list.
    /// Keywords are upper-cased; repeated keywords are concatenated with a line break.
    /// </summary>
    public static ImmutableDictionary<string, string> ParseStrings(ReadOnlySpan<byte> block, bool little, byte[] stringTerm, byte[] lineTerm)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var pos = 0;
        while (pos + 2 <= block.Length)
        {
            var len = little ? BinaryPrimitives.ReadUInt16LittleEndian(block[pos..]) : BinaryPrimitives.ReadUInt16BigEndian(block[pos..]);
            if (len == 0) break;
            if (len < 2 || pos + len > block.Length)
            {
                // A damaged length: stop parsing strings rather than reading garbage.
                break;
            }
            var body = block.Slice(pos + 2, len - 2);
            var end = IndexOf(body, stringTerm);
            if (end >= 0) body = body[..end];
            var text = Encoding.Latin1.GetString(body);
            if (lineTerm.Length > 0) text = text.Replace(Encoding.Latin1.GetString(lineTerm), "\n");
            text = text.Trim('\0', ' ', '\t', '\r', '\n');
            if (text.Length > 0)
            {
                var split = text.IndexOfAny([' ', '\t']);
                var key = (split < 0 ? text : text[..split]).ToUpperInvariant();
                var value = split < 0 ? "" : text[(split + 1)..].Trim();
                result[key] = result.TryGetValue(key, out var existing) ? existing + "\n" + value : value;
            }
            pos += len;
        }
        return result.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, byte[] needle)
        => needle.Length == 0 ? -1 : haystack.IndexOf(needle);

    internal static DateTimeOffset? ParseAcquisition(ImmutableDictionary<string, string> kw)
    {
        if (!kw.TryGetValue("ACQUISITION_DATE", out var date)) return null;
        kw.TryGetValue("ACQUISITION_TIME", out var time);
        string[] formats = ["d/MMM/yyyy", "dd/MMM/yyyy", "d/M/yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "d-MMM-yyyy", "MM/dd/yyyy"];
        if (!DateTime.TryParseExact(date.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)) return null;
        if (time is not null && TimeSpan.TryParse(time.Trim(), CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1))
            d = d.Date + t;
        return new DateTimeOffset(d, TimeSpan.Zero);
    }

    internal static double? TryDouble(ImmutableDictionary<string, string> kw, string key)
        => kw.TryGetValue(key, out var v) ? FirstNumber(v) : null;

    internal static int? TryInt(ImmutableDictionary<string, string> kw, string key)
    {
        var d = TryDouble(kw, key);
        return d is { } x && Math.Abs(x - Math.Round(x)) < 1e-9 && Math.Abs(x) < int.MaxValue ? (int)Math.Round(x) : null;
    }

    internal static double? FirstNumber(string text)
    {
        var n = Numbers(text);
        return n.Count > 0 ? n[0] : null;
    }

    internal static List<double> Numbers(string text)
    {
        var list = new List<double>();
        foreach (var part in text.Split([' ', '\t', ',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)) list.Add(v);
            else break;
        }
        return list;
    }

    private static ushort U16(ReadOnlySpan<byte> d, int o, bool little)
        => little ? BinaryPrimitives.ReadUInt16LittleEndian(d[o..]) : BinaryPrimitives.ReadUInt16BigEndian(d[o..]);

    private static uint U32(ReadOnlySpan<byte> d, int o, bool little)
        => little ? BinaryPrimitives.ReadUInt32LittleEndian(d[o..]) : BinaryPrimitives.ReadUInt32BigEndian(d[o..]);

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
