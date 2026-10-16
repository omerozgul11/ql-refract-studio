using RefractionStudio.Domain;
using RefractionStudio.Formats;
using RefractionStudio.Persistence;

namespace RefractionStudio.Core.Data;

/// <summary>
/// Locates raw file bytes: staged copies of new imports (recovery folder), the embedded copies in the
/// open project container, or in-memory content (tests, sample generation).
/// </summary>
public sealed class RawFileRepository : IRawFileSource
{
    private readonly Dictionary<string, string> _stagedPaths = [];
    private readonly Dictionary<string, byte[]> _memory = [];
    private readonly Lock _lock = new();
    private LoadedProject? _container;

    /// <summary>Uses an opened container as a source of embedded files.</summary>
    public void Attach(LoadedProject? container)
    {
        lock (_lock) _container = container;
    }

    public void AddStaged(string sha256, string path)
    {
        lock (_lock) _stagedPaths[sha256] = path;
    }

    public void AddInMemory(string sha256, byte[] bytes)
    {
        lock (_lock) _memory[sha256] = bytes;
    }

    public bool Contains(SourceFile file)
    {
        lock (_lock)
            return _memory.ContainsKey(file.Sha256) || _stagedPaths.ContainsKey(file.Sha256) || _container is not null;
    }

    public Stream Open(SourceFile file)
    {
        lock (_lock)
        {
            if (_memory.TryGetValue(file.Sha256, out var bytes)) return new MemoryStream(bytes, writable: false);
            if (_stagedPaths.TryGetValue(file.Sha256, out var path) && File.Exists(path)) return File.OpenRead(path);
            if (_container is not null) return _container.Open(file);
        }
        throw new ProjectFileException($"The raw data of “{file.FileName}” is not available.", file.Sha256);
    }

    public byte[] ReadAll(SourceFile file)
    {
        using var s = Open(file);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>Raw samples of traces, decoded lazily from the original files.</summary>
public interface ITraceStore
{
    /// <summary>Raw samples of a channel of a record (physical units), or null if the channel is absent.</summary>
    double[]? Samples(Survey survey, RecordId record, int channel);
}

/// <summary>
/// Decodes source files on demand with the registered format readers and keeps decoded files in a
/// bounded least-recently-used cache. Raw arrays are shared read-only; callers must copy before modifying.
/// </summary>
public sealed class TraceStore(RawFileRepository repository, FormatRegistry formats, long maxCachedSamples = 40_000_000) : ITraceStore
{
    private readonly Dictionary<SourceFileId, (DecodedFile File, long Samples)> _cache = [];
    private readonly LinkedList<SourceFileId> _lru = new();
    private readonly Lock _lock = new();
    private long _cachedSamples;

    public double[]? Samples(Survey survey, RecordId record, int channel)
    {
        var rec = survey.Record(record);
        if (rec is null) return null;
        var decoded = Decode(survey, rec.SourceFileId);
        if (rec.RecordIndexInFile >= decoded.Records.Length) return null;
        var r = decoded.Records[rec.RecordIndexInFile];
        foreach (var t in r.Traces)
            if (t.Header.Channel == channel) return t.Samples;
        return null;
    }

    public DecodedFile Decode(Survey survey, SourceFileId id)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(id, out var hit))
            {
                _lru.Remove(id);
                _lru.AddFirst(id);
                return hit.File;
            }
        }
        var file = survey.SourceFile(id) ?? throw new InvalidOperationException($"Unknown source file {id}.");
        var reader = formats.ById(file.FormatId) ?? throw new SeismicFormatException($"No reader is available for the format “{file.FormatId}” of “{file.FileName}”.");
        DecodedFile decoded;
        using (var s = repository.Open(file))
        {
            decoded = reader.ReadAsync(s, file.FileName).GetAwaiter().GetResult();
        }
        var count = decoded.Records.Sum(r => r.Traces.Sum(t => (long)t.Samples.Length));
        lock (_lock)
        {
            if (!_cache.ContainsKey(id))
            {
                _cache[id] = (decoded, count);
                _lru.AddFirst(id);
                _cachedSamples += count;
                while (_cachedSamples > maxCachedSamples && _lru.Count > 1)
                {
                    var last = _lru.Last!.Value;
                    _lru.RemoveLast();
                    _cachedSamples -= _cache[last].Samples;
                    _cache.Remove(last);
                }
            }
        }
        return decoded;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lru.Clear();
            _cachedSamples = 0;
        }
    }
}
