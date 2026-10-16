using System.Globalization;
using System.Security.Cryptography;
using RefractionStudio.Domain;

namespace RefractionStudio.Formats;

/// <summary>Severity of an import finding.</summary>
public enum ImportSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>A correction the user may explicitly approve. Nothing is applied without approval.</summary>
public enum ImportFixKind
{
    /// <summary>Do not import this file.</summary>
    SkipFile,

    /// <summary>Import the trace but mark it as excluded (the raw file stays complete; the exclusion is recorded and reversible).</summary>
    SkipTrace,
}

/// <summary>A proposed correction.</summary>
public sealed record ImportFix(ImportFixKind Kind, string Description, string FileName, int? Channel = null);

/// <summary>A finding of import validation.</summary>
/// <param name="Severity">Error / Warning / Information.</param>
/// <param name="Code">Stable code, e.g. "sample-interval-mismatch".</param>
/// <param name="Message">Plain-language explanation and what to do.</param>
/// <param name="Details">Technical details for "Show details".</param>
/// <param name="FileName">Affected file.</param>
/// <param name="Channel">Affected channel.</param>
/// <param name="Fix">Correction the user may approve.</param>
public sealed record ImportIssue(ImportSeverity Severity, string Code, string Message, string? Details = null, string? FileName = null, int? Channel = null, ImportFix? Fix = null);

/// <summary>A file prepared for import.</summary>
/// <param name="Path">Full path.</param>
/// <param name="FileName">File name.</param>
/// <param name="Sha256">Content hash.</param>
/// <param name="Length">Size in bytes.</param>
/// <param name="Decoded">Decoded content, or null when the file could not be read.</param>
/// <param name="ReaderId">Reader used.</param>
/// <param name="ReadError">Error when it could not be read.</param>
public sealed record InspectedFile(string Path, string FileName, string Sha256, long Length, DecodedFile? Decoded, string? ReaderId, SeismicFormatException? ReadError);

/// <summary>Summary of the files being imported, shown in the import preview.</summary>
public sealed record ImportSummary(
    int FileCount,
    int ReadableFiles,
    int RecordCount,
    int TraceCount,
    ImmutableArray<double> SampleIntervals,
    ImmutableArray<int> SampleCounts,
    ImmutableArray<int> ChannelCounts,
    DateTimeOffset? FirstAcquisition,
    DateTimeOffset? LastAcquisition);

/// <summary>Validates a batch of decoded files before import. Never modifies data.</summary>
public static class ImportInspector
{
    /// <summary>Reads and hashes the files, never throwing for a bad file (it becomes an Error issue).</summary>
    public static async Task<IReadOnlyList<InspectedFile>> ReadFilesAsync(FormatRegistry registry, IEnumerable<string> paths,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var list = paths.ToList();
        var result = new InspectedFile[list.Count];
        var done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, list.Count), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
            async (i, token) =>
            {
                result[i] = await ReadOneAsync(registry, list[i], token).ConfigureAwait(false);
                progress?.Report((Interlocked.Increment(ref done), list.Count));
            }).ConfigureAwait(false);
        return result;
    }

    public static async Task<InspectedFile> ReadOneAsync(FormatRegistry registry, string path, CancellationToken ct = default)
    {
        var name = System.IO.Path.GetFileName(path);
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InspectedFile(path, name, "", 0, null, null, new SeismicFormatException($"“{name}” could not be opened: {ex.Message}", ex.GetType().Name, ex));
        }
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var reader = registry.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, 4096)), name);
        if (reader is null)
            return new InspectedFile(path, name, sha, bytes.Length, null, null,
                new SeismicFormatException($"“{name}” is not in a supported format ({string.Join(", ", registry.Readers.Select(r => r.Name))})."));
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var decoded = await reader.ReadAsync(ms, name, ct).ConfigureAwait(false);
            return new InspectedFile(path, name, sha, bytes.Length, decoded, reader.Id, null);
        }
        catch (SeismicFormatException ex)
        {
            return new InspectedFile(path, name, sha, bytes.Length, null, reader.Id, ex);
        }
    }

    public static ImportSummary Summarize(IReadOnlyList<InspectedFile> files)
    {
        var records = files.Where(f => f.Decoded is not null).SelectMany(f => f.Decoded!.Records).ToList();
        var traces = records.SelectMany(r => r.Traces).ToList();
        var times = records.Where(r => r.AcquiredAt is not null).Select(r => r.AcquiredAt!.Value).OrderBy(t => t).ToList();
        return new ImportSummary(
            files.Count,
            files.Count(f => f.Decoded is not null),
            records.Count,
            traces.Count,
            [.. traces.Select(t => t.Header.SampleInterval).Distinct().Order()],
            [.. traces.Select(t => t.Header.SampleCount).Distinct().Order()],
            [.. records.Select(r => r.Traces.Length).Distinct().Order()],
            times.Count > 0 ? times[0] : null,
            times.Count > 0 ? times[^1] : null);
    }

    /// <summary>Runs all checks. <paramref name="existingHashes"/> are files already in the project.</summary>
    public static IReadOnlyList<ImportIssue> Validate(IReadOnlyList<InspectedFile> files, IReadOnlySet<string>? existingHashes = null)
    {
        var issues = new List<ImportIssue>();
        existingHashes ??= new HashSet<string>();

        foreach (var f in files.Where(f => f.ReadError is not null))
        {
            issues.Add(new ImportIssue(ImportSeverity.Error, "unreadable", f.ReadError!.Message, f.ReadError.TechnicalDetail, f.FileName,
                Fix: new ImportFix(ImportFixKind.SkipFile, "Skip this file", f.FileName)));
        }

        // Duplicate files.
        foreach (var group in files.Where(f => f.Sha256.Length > 0).GroupBy(f => f.Sha256).Where(g => g.Count() > 1))
        {
            var names = group.Select(f => f.FileName).ToList();
            foreach (var dup in group.Skip(1))
            {
                issues.Add(new ImportIssue(ImportSeverity.Warning, "duplicate-file",
                    $"“{dup.FileName}” is byte-for-byte identical to “{names[0]}”. Importing both would count the same shot twice.",
                    $"SHA-256 {group.Key}", dup.FileName, Fix: new ImportFix(ImportFixKind.SkipFile, "Skip the duplicate", dup.FileName)));
            }
        }
        foreach (var f in files.Where(f => existingHashes.Contains(f.Sha256)))
        {
            issues.Add(new ImportIssue(ImportSeverity.Warning, "already-imported",
                $"“{f.FileName}” is already part of this project.", $"SHA-256 {f.Sha256}", f.FileName,
                Fix: new ImportFix(ImportFixKind.SkipFile, "Skip this file", f.FileName)));
        }

        var readable = files.Where(f => f.Decoded is not null).ToList();
        var allTraces = readable.SelectMany(f => f.Decoded!.Records.SelectMany(r => r.Traces.Select(t => (File: f, Trace: t)))).ToList();
        if (allTraces.Count == 0) return issues;

        // Duplicate content under different names (same samples).
        var contentGroups = readable
            .SelectMany(f => f.Decoded!.Records.Select((r, i) => (File: f, Index: i, Hash: SampleHash(r))))
            .Where(x => x.Hash is not null)
            .GroupBy(x => x.Hash!)
            .Where(g => g.Select(x => x.File.Sha256).Distinct().Count() > 1);
        foreach (var g in contentGroups)
        {
            var first = g.First();
            foreach (var other in g.Skip(1).Where(x => x.File.Sha256 != first.File.Sha256))
            {
                issues.Add(new ImportIssue(ImportSeverity.Warning, "duplicate-record",
                    $"A record in “{other.File.FileName}” has exactly the same samples as one in “{first.File.FileName}”. It may be a copy of the same shot with different headers.",
                    null, other.File.FileName, Fix: new ImportFix(ImportFixKind.SkipFile, "Skip this file", other.File.FileName)));
            }
        }

        // Sample interval consistency (majority vs. outliers).
        var dtMajority = Majority(allTraces.Select(t => t.Trace.Header.SampleInterval), 1e-12);
        foreach (var f in readable)
        {
            var dts = f.Decoded!.Records.SelectMany(r => r.Traces).Select(t => t.Header.SampleInterval).Distinct().ToList();
            if (dts.Count > 1)
            {
                issues.Add(new ImportIssue(ImportSeverity.Warning, "mixed-sample-interval",
                    $"“{f.FileName}” contains traces with different sample intervals ({string.Join(", ", dts.Select(FormatInterval))}). Review the file before continuing.",
                    null, f.FileName));
            }
            else if (Math.Abs(dts[0] - dtMajority) > 1e-12)
            {
                issues.Add(new ImportIssue(ImportSeverity.Warning, "sample-interval-mismatch",
                    $"This record uses a different sample rate ({FormatRate(dts[0])}) from the other records ({FormatRate(dtMajority)}). Review the file before continuing; it cannot be stacked with the others.",
                    $"Sample interval {dts[0]:R} s vs {dtMajority:R} s", f.FileName));
            }
        }

        // Sample count consistency.
        var nsMajority = (int)Majority(allTraces.Select(t => (double)t.Trace.Header.SampleCount), 0.5);
        foreach (var f in readable)
        {
            var counts = f.Decoded!.Records.SelectMany(r => r.Traces).Select(t => t.Header.SampleCount).Distinct().ToList();
            if (counts.Any(c => c != nsMajority))
            {
                issues.Add(new ImportIssue(ImportSeverity.Information, "sample-count-mismatch",
                    $"“{f.FileName}” has records of {string.Join(", ", counts)} samples while most have {nsMajority}. Record lengths differ; analysis is unaffected but stacking requires equal lengths.",
                    null, f.FileName));
            }
        }

        // Channels.
        var channelMajority = (int)Majority(readable.SelectMany(f => f.Decoded!.Records).Select(r => (double)r.Traces.Length), 0.5);
        foreach (var f in readable)
        {
            foreach (var r in f.Decoded!.Records)
            {
                var channels = r.Traces.Select(t => t.Header.Channel).ToList();
                foreach (var dup in channels.GroupBy(c => c).Where(g => g.Count() > 1))
                {
                    issues.Add(new ImportIssue(ImportSeverity.Error, "duplicate-channel",
                        $"Channel {dup.Key} appears {dup.Count()} times in “{f.FileName}”. The channel mapping is ambiguous; correct the file headers or import only one of them.",
                        null, f.FileName, dup.Key));
                }
                var max = channels.Count > 0 ? channels.Max() : 0;
                var missing = Enumerable.Range(1, max).Except(channels).ToList();
                if (missing.Count > 0)
                {
                    issues.Add(new ImportIssue(ImportSeverity.Warning, "missing-channels",
                        $"“{f.FileName}” has no trace for channel(s) {string.Join(", ", missing)}. Those receiver positions will have no data.",
                        null, f.FileName));
                }
                else if (r.Traces.Length < channelMajority)
                {
                    issues.Add(new ImportIssue(ImportSeverity.Warning, "fewer-traces",
                        $"“{f.FileName}” has {r.Traces.Length} traces while most records have {channelMajority}. Some traces may be missing.",
                        null, f.FileName));
                }
                if (r.SourceX is null)
                {
                    issues.Add(new ImportIssue(ImportSeverity.Information, "no-source-location",
                        $"“{f.FileName}” does not record a source location. Enter the shot position in Geometry.",
                        null, f.FileName));
                }
            }
        }

        // Trace quality.
        foreach (var (file, trace) in allTraces)
        {
            var s = trace.Samples;
            if (s.Length == 0) continue;
            var h = trace.Header;
            var max = 0.0;
            var min = double.MaxValue;
            foreach (var v in s)
            {
                max = Math.Max(max, Math.Abs(v));
                min = Math.Min(min, v);
            }
            if (max == 0 || s.All(v => v == s[0]))
            {
                issues.Add(new ImportIssue(ImportSeverity.Warning, "dead-trace",
                    $"Channel {h.Channel} of “{file.FileName}” is flat (no signal). The geophone may be disconnected.",
                    null, file.FileName, h.Channel, new ImportFix(ImportFixKind.SkipTrace, "Do not import this trace", file.FileName, h.Channel)));
                continue;
            }
            var atMax = s.Count(v => Math.Abs(Math.Abs(v) - max) <= max * 1e-9);
            if (atMax >= Math.Max(5, s.Length / 200))
            {
                issues.Add(new ImportIssue(ImportSeverity.Warning, "clipped-trace",
                    $"Channel {h.Channel} of “{file.FileName}” appears clipped ({atMax} samples at the maximum amplitude). First breaks are usually still usable; amplitudes are not.",
                    null, file.FileName, h.Channel));
            }
            if (Math.Abs(h.Delay) > h.SampleCount * h.SampleInterval)
            {
                issues.Add(new ImportIssue(ImportSeverity.Warning, "suspicious-delay",
                    $"Channel {h.Channel} of “{file.FileName}” has a recording delay ({h.Delay * 1000:0.###} ms) longer than the record itself. Check the DELAY header.",
                    null, file.FileName, h.Channel));
            }
        }

        // Acquisition order versus file order.
        var timed = readable.SelectMany(f => f.Decoded!.Records.Select(r => (File: f, r.AcquiredAt))).Where(x => x.AcquiredAt is not null).ToList();
        for (var i = 1; i < timed.Count; i++)
        {
            if (timed[i].AcquiredAt < timed[i - 1].AcquiredAt)
            {
                issues.Add(new ImportIssue(ImportSeverity.Information, "acquisition-order",
                    $"“{timed[i].File.FileName}” was recorded before “{timed[i - 1].File.FileName}” although it comes later in the file list. Check the shot sequence when assigning positions.",
                    null, timed[i].File.FileName));
                break;
            }
        }

        var bySource = readable.SelectMany(f => f.Decoded!.Records.Select(r => (File: f, r.SourceX))).Where(x => x.SourceX is not null)
            .GroupBy(x => Math.Round(x.SourceX!.Value, 3)).Where(g => g.Count() > 1);
        foreach (var g in bySource)
        {
            issues.Add(new ImportIssue(ImportSeverity.Information, "repeated-source",
                $"{g.Count()} records share the source position {g.Key.ToString("0.###", CultureInfo.InvariantCulture)} m ({string.Join(", ", g.Select(x => x.File.FileName))}). They can be stacked, or paired as opposite SH impacts, in Geometry.",
                null, g.First().File.FileName));
        }

        return issues;
    }

    private static string? SampleHash(DecodedRecord r)
    {
        if (r.Traces.IsDefaultOrEmpty || r.Traces[0].Samples.Length == 0) return null;
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var t in r.Traces)
            h.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(t.Samples.AsSpan()));
        return Convert.ToHexString(h.GetHashAndReset());
    }

    private static double Majority(IEnumerable<double> values, double tolerance)
    {
        var groups = new List<(double Value, int Count)>();
        foreach (var v in values)
        {
            var idx = groups.FindIndex(g => Math.Abs(g.Value - v) <= tolerance);
            if (idx < 0) groups.Add((v, 1));
            else groups[idx] = (groups[idx].Value, groups[idx].Count + 1);
        }
        return groups.Count == 0 ? double.NaN : groups.OrderByDescending(g => g.Count).ThenBy(g => g.Value).First().Value;
    }

    private static string FormatInterval(double dt) => (dt * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " ms";

    private static string FormatRate(double dt)
    {
        var hz = 1 / dt;
        return hz >= 1000 ? (hz / 1000).ToString("0.###", CultureInfo.InvariantCulture) + " kHz" : hz.ToString("0.#", CultureInfo.InvariantCulture) + " Hz";
    }
}
