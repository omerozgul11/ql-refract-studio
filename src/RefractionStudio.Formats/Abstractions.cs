using RefractionStudio.Domain;

namespace RefractionStudio.Formats;

/// <summary>A decoded trace: header plus samples (physical units after any descaling).</summary>
public sealed record DecodedTrace(TraceHeader Header, double[] Samples);

/// <summary>A decoded field record.</summary>
/// <param name="Keywords">Record-level descriptive strings.</param>
/// <param name="Traces">Traces (headers only when produced by a metadata read: samples are then empty).</param>
/// <param name="AcquiredAt">Acquisition time, if available.</param>
/// <param name="FieldRecordNumber">Field record / shot number, if available.</param>
/// <param name="SourceX">Source position from headers, if available.</param>
public sealed record DecodedRecord(
    ImmutableDictionary<string, string> Keywords,
    ImmutableArray<DecodedTrace> Traces,
    DateTimeOffset? AcquiredAt,
    int? FieldRecordNumber,
    double? SourceX);

/// <summary>A decoded file: one or more records.</summary>
/// <param name="FormatId">Reader id.</param>
/// <param name="FormatDescription">Format and variant, e.g. "SEG-2 rev 1, little-endian".</param>
/// <param name="Records">Records in file order.</param>
/// <param name="Notices">Informational notes about how the file was interpreted.</param>
public sealed record DecodedFile(string FormatId, string FormatDescription, ImmutableArray<DecodedRecord> Records, ImmutableArray<string> Notices);

/// <summary>
/// A plug-in that reads one seismic data format. Implementations must be stateless and thread-safe,
/// must never modify the input, and must throw <see cref="SeismicFormatException"/> (never anything
/// else) for invalid or unsupported content.
/// </summary>
public interface ISeismicDataReader
{
    /// <summary>Stable identifier stored in projects, e.g. "seg2".</summary>
    string Id { get; }

    /// <summary>User-facing name, e.g. "SEG-2".</summary>
    string Name { get; }

    /// <summary>File extensions including the dot, lower case.</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>Quick check from the first bytes of a file (at least 4 KiB when available).</summary>
    bool CanRead(ReadOnlySpan<byte> head, string fileName);

    /// <summary>Reads headers only (sample arrays are empty).</summary>
    Task<DecodedFile> ReadMetadataAsync(Stream stream, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Reads headers and samples.</summary>
    Task<DecodedFile> ReadAsync(Stream stream, string fileName, CancellationToken cancellationToken = default);
}

/// <summary>A file could not be read. <see cref="Exception.Message"/> is written for users.</summary>
public sealed class SeismicFormatException : Exception
{
    public SeismicFormatException(string userMessage, string? technicalDetail = null, Exception? inner = null)
        : base(userMessage, inner) => TechnicalDetail = technicalDetail;

    /// <summary>Byte offsets, field values etc. for the "Show details" panel.</summary>
    public string? TechnicalDetail { get; }
}

/// <summary>The set of available format readers.</summary>
public sealed class FormatRegistry
{
    private readonly List<ISeismicDataReader> _readers;

    public FormatRegistry(IEnumerable<ISeismicDataReader> readers) => _readers = [.. readers];

    /// <summary>Registry with the built-in readers.</summary>
    public static FormatRegistry CreateDefault() => new([new Seg2Reader(), new SegYReader()]);

    public IReadOnlyList<ISeismicDataReader> Readers => _readers;

    public ISeismicDataReader? ById(string id) => _readers.FirstOrDefault(r => r.Id == id);

    /// <summary>All supported extensions (for file dialogs and folder import).</summary>
    public IReadOnlyList<string> AllExtensions => _readers.SelectMany(r => r.SupportedExtensions).Distinct().ToList();

    /// <summary>Finds a reader by content first, then by extension.</summary>
    public ISeismicDataReader? Detect(ReadOnlySpan<byte> head, string fileName)
    {
        foreach (var r in _readers)
            if (r.CanRead(head, fileName)) return r;
        return null;
    }

    /// <summary>Detects the format of a file on disk.</summary>
    public async Task<ISeismicDataReader?> DetectAsync(string path, CancellationToken ct = default)
    {
        var buffer = new byte[4096];
        await using var fs = File.OpenRead(path);
        var read = await fs.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        return Detect(buffer.AsSpan(0, read), Path.GetFileName(path));
    }
}
