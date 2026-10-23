using System.Globalization;
using RefractionStudio.Core.Session;
using RefractionStudio.Domain;
using RefractionStudio.Formats;
using RefractionStudio.Persistence;
using RefractionStudio.SignalProcessing;

namespace RefractionStudio.Core.Import;

/// <summary>How imported records are grouped into shot gathers.</summary>
public enum GatherGrouping
{
    /// <summary>One gather per record.</summary>
    Separate,

    /// <summary>Records at the same shot position are stacked (all polarity +1).</summary>
    StackSamePosition,

    /// <summary>
    /// SH: records at the same position are split by the sign of their correlation into two opposite
    /// impact directions and combined as (left − right)/2. A suggestion, labelled as such.
    /// </summary>
    ShOppositePairs,
}

/// <summary>Import choices made by the user in the preview.</summary>
/// <param name="ExistingLine">Add to this line; null creates a new line.</param>
/// <param name="NewLineName">Name of a new line.</param>
/// <param name="WaveType">Wave type of a new line.</param>
/// <param name="Profile">Spread profile (channel roles, default spacing).</param>
/// <param name="DefaultSpacing">Geophone spacing used when the headers do not record positions.</param>
/// <param name="Grouping">Gather grouping.</param>
/// <param name="SkipFiles">File names (approved fixes) not to import.</param>
/// <param name="SkipTraces">Traces (file name, channel) to import as excluded (approved fixes).</param>
public sealed record ImportOptions(
    LineId? ExistingLine,
    string NewLineName,
    WaveType WaveType,
    SpreadProfile? Profile,
    double? DefaultSpacing,
    GatherGrouping Grouping,
    IReadOnlySet<string> SkipFiles,
    IReadOnlySet<(string File, int Channel)> SkipTraces);

/// <summary>A prepared import: the edit to execute and the raw files to stage.</summary>
public sealed record ImportPlan(ProjectEdit Edit, IReadOnlyList<(SourceFile File, string Path)> RawFiles, ImmutableArray<string> Notes, LineId Line, int RecordCount, int GatherCount);

/// <summary>Turns validated, decoded files into project records, spreads and gathers.</summary>
public static class ImportService
{
    /// <summary>The readable files that would be imported: not skipped, not already in the project, one per identical file.</summary>
    public static List<InspectedFile> Importable(ProjectState state, IReadOnlyList<InspectedFile> files, IReadOnlySet<string> skipFiles)
    {
        var existingHashes = state.Survey.SourceFiles.Select(f => f.Sha256).ToHashSet();
        return files.Where(f => f.Decoded is not null && !skipFiles.Contains(f.FileName) && !existingHashes.Contains(f.Sha256))
            .GroupBy(f => f.Sha256).Select(g => g.First()).ToList();
    }

    public static ImportPlan Plan(ProjectState state, IReadOnlyList<InspectedFile> files, ImportOptions options, DateTimeOffset now)
    {
        var notes = ImmutableArray.CreateBuilder<string>();
        var accepted = Importable(state, files, options.SkipFiles);
        if (accepted.Count == 0) throw new InvalidOperationException("No files remain to import.");

        var sourceFiles = new List<SourceFile>();
        var records = new List<(SeismicRecord Record, InspectedFile File, DecodedRecord Decoded)>();
        foreach (var f in accepted)
        {
            var sf = new SourceFile(SourceFileId.New(), f.FileName, f.Sha256, f.Length, f.ReaderId!, now, f.Path);
            sourceFiles.Add(sf);
            for (var i = 0; i < f.Decoded!.Records.Length; i++)
            {
                var d = f.Decoded.Records[i];
                var label = f.Decoded.Records.Length > 1 ? $"{Path.GetFileNameWithoutExtension(f.FileName)} #{d.FieldRecordNumber ?? i + 1}" : Path.GetFileNameWithoutExtension(f.FileName);
                var rec = new SeismicRecord(RecordId.New(), sf.Id, i, label, [.. d.Traces.Select(t => t.Header)], d.Keywords, d.AcquiredAt, d.FieldRecordNumber, d.SourceX);
                records.Add((rec, f, d));
            }
        }

        var line = options.ExistingLine is { } existingId ? state.Survey.Line(existingId) ?? throw new InvalidOperationException("The target line no longer exists.") : null;
        var wave = line?.WaveType ?? options.WaveType;
        var lineId = line?.Id ?? LineId.New();

        // Spreads: one per distinct receiver layout.
        var spreads = new List<Spread>(line?.Spreads ?? []);
        var spreadOf = new Dictionary<RecordId, SpreadId>();
        var assumedLayout = false;
        foreach (var (rec, _, _) in records)
        {
            var (spread, assumed) = DeriveSpread(rec, options, spreads.Count + 1);
            assumedLayout |= assumed;
            var match = spreads.FirstOrDefault(s => SameLayout(s, spread));
            if (match is null)
            {
                spreads.Add(spread);
                match = spread;
            }
            spreadOf[rec.Id] = match.Id;
        }
        if (assumedLayout)
            notes.Add($"Receiver positions are not recorded in the file headers: a regular spread from 0 m with {(options.Profile?.Spacing ?? options.DefaultSpacing ?? 1.0).ToString("0.##", CultureInfo.InvariantCulture)} m spacing was assumed. Check and correct it in Geometry.");
        if (spreads.Count > (line?.Spreads.Length ?? 0) + 1) notes.Add($"{spreads.Count - (line?.Spreads.Length ?? 0)} different receiver layouts were found (roll-along); a spread was created for each.");

        // Topography from receiver elevations in headers.
        var topography = line?.Topography ?? Topography.Flat;
        var elevated = records.SelectMany(r => r.Record.Traces).Where(t => t.ReceiverX is not null && t.ReceiverElevation is not null)
            .GroupBy(t => Math.Round(t.ReceiverX!.Value, 3)).Select(g => new TopographyPoint(g.Key, g.First().ReceiverElevation!.Value)).ToList();
        if (topography.IsFlat && elevated.Count >= 2 && elevated.Any(p => p.Elevation != elevated[0].Elevation))
        {
            topography = Topography.Create(elevated);
            notes.Add($"Topography was taken from receiver elevations in the headers ({elevated.Count} points).");
        }

        // Gathers.
        var gathers = new List<ShotGather>();
        var number = (line?.Gathers.Length ?? 0) + 1;
        var groups = records.GroupBy(r => (Position: r.Record.SourceX is { } x ? Math.Round(x, 3) : double.NaN, Spread: spreadOf[r.Record.Id], Missing: r.Record.SourceX is null ? r.Record.Id.Value : Guid.Empty)).ToList();
        foreach (var group in groups)
        {
            var members = group.ToList();
            var position = group.Key.Position;
            var grouping = options.Grouping;
            if (members.Count == 1 || double.IsNaN(position) || grouping == GatherGrouping.Separate)
            {
                foreach (var m in members) gathers.Add(NewGather(number++, m.Record, spreadOf[m.Record.Id], wave));
                continue;
            }
            if (grouping == GatherGrouping.ShOppositePairs)
            {
                var signs = members.Select(m => m == members[0] ? 1 : Math.Sign(Correlate(members[0].Decoded, m.Decoded))).ToList();
                var r = members.Skip(1).Select(m => Correlate(members[0].Decoded, m.Decoded)).ToList();
                if (signs.Any(sg => sg < 0))
                {
                    var gm = members.Select((m, i) => new GatherMember(m.Record.Id, signs[i] < 0 ? -1 : 1, signs[i] < 0 ? SourceKind.ShRight : SourceKind.ShLeft)).ToImmutableArray();
                    gathers.Add(new ShotGather(GatherId.New(), Label(number++, position), position, null, 0, group.Key.Spread, CombineMode.Stack, gm, true,
                        $"Opposite impacts identified by correlation (r = {string.Join(", ", r.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)))}); the first record is treated as the “left” impact. Verify."));
                    notes.Add($"x = {position:0.##} m: {members.Count} records combined as opposite SH impacts (polarity from correlation; suggestion).");
                    continue;
                }
                notes.Add($"x = {position:0.##} m: the records are not of opposite polarity (r = {string.Join(", ", r.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)))}); they were stacked instead. Review in Geometry.");
            }
            var stackMembers = members.Select(m => new GatherMember(m.Record.Id, 1, wave == WaveType.SH ? SourceKind.Unknown : SourceKind.Vertical)).ToImmutableArray();
            gathers.Add(new ShotGather(GatherId.New(), Label(number++, position), position, null, 0, group.Key.Spread, CombineMode.Stack, stackMembers, true,
                $"Vertical stack of {members.Count} records at the same position."));
        }
        var missingPositions = gathers.Count(g => double.IsNaN(g.ShotX));
        if (missingPositions > 0) notes.Add($"{missingPositions} shot(s) have no recorded source position and are excluded until their position is entered in Geometry.");

        // Skipped traces become excluded traces (auditable, reversible).
        var excluded = new List<(GatherId, int)>();
        foreach (var g in gathers)
        {
            foreach (var m in g.Members)
            {
                var (rec, file, _) = records.First(r => r.Record.Id == m.Record);
                foreach (var t in rec.Traces)
                    if (options.SkipTraces.Contains((file.FileName, t.Channel))) excluded.Add((g.Id, t.Channel));
            }
        }

        var newLine = line is null
            ? new SurveyLine(lineId, options.NewLineName, wave, topography, [.. spreads], [.. gathers])
            : line with { Spreads = [.. spreads], Gathers = line.Gathers.AddRange(gathers), Topography = topography };
        var rawFiles = accepted.Select(f => (sourceFiles.First(s => s.Sha256 == f.Sha256), f.Path)).ToList();
        var description = $"Import {accepted.Count} file(s), {records.Count} record(s) into {newLine.Name}";
        var edit = ProjectEdit.Create(HistoryKind.Import, "import", description, s =>
        {
            var survey = s.Survey with
            {
                SourceFiles = s.Survey.SourceFiles.AddRange(sourceFiles),
                Records = s.Survey.Records.AddRange(records.Select(r => r.Record)),
                Lines = line is null ? s.Survey.Lines.Add(newLine) : [.. s.Survey.Lines.Select(l => l.Id == newLine.Id ? newLine : l)],
            };
            var next = s with { Survey = survey };
            return next.WithLineInterpretation(newLine.Id, i =>
            {
                var adjustments = i.Adjustments.ToBuilder();
                foreach (var (g, c) in excluded)
                    adjustments[new PickKey(g, c)] = new TraceAdjustment(Excluded: true, Note: "Skipped at import (approved correction)");
                var picker = wave == WaveType.SH ? i.Picker with { Algorithm = Domain.PickerAlgorithm.ShPolarityDivergence } : i.Picker;
                return i with { Adjustments = adjustments.ToImmutable(), Picker = line is null ? picker : i.Picker };
            });
        }, ("files", string.Join(";", accepted.Select(f => f.FileName))), ("sha256", string.Join(";", accepted.Select(f => f.Sha256))),
           ("records", records.Count.ToString(CultureInfo.InvariantCulture)), ("line", newLine.Name), ("grouping", options.Grouping.ToString()),
           ("excludedTraces", excluded.Count.ToString(CultureInfo.InvariantCulture)));
        return new ImportPlan(edit, rawFiles, notes.ToImmutable(), newLine.Id, records.Count, gathers.Count);
    }

    private static string Label(int number, double x)
        => double.IsNaN(x) ? $"S{number}" : $"S{number} ({x.ToString("0.##", CultureInfo.InvariantCulture)} m)";

    private static ShotGather NewGather(int number, SeismicRecord record, SpreadId spread, WaveType wave)
    {
        var x = record.SourceX ?? double.NaN;
        var kind = wave == WaveType.P ? SourceKind.Vertical : SourceKind.Unknown;
        return new ShotGather(GatherId.New(), Label(number, x), x, null, 0, spread, CombineMode.Single, [new GatherMember(record.Id, 1, kind)],
            Included: !double.IsNaN(x), Comment: double.IsNaN(x) ? "Shot position not recorded; enter it in Geometry." : null);
    }

    /// <summary>Spread from receiver positions in the trace headers, or a regular assumed layout.</summary>
    private static (Spread Spread, bool Assumed) DeriveSpread(SeismicRecord record, ImportOptions options, int number)
    {
        var profile = options.Profile;
        var channels = record.Traces.Select(t => t.Channel).Order().ToList();
        ChannelRole RoleOf(int c) => profile?.Channels.FirstOrDefault(a => a.Channel == c)?.Role ?? ChannelRole.Receiver;
        var assignments = channels.Select(c => new ChannelAssignment(c, RoleOf(c))).ToImmutableArray();
        var receivers = record.Traces.Where(t => RoleOf(t.Channel) == ChannelRole.Receiver).OrderBy(t => t.Channel).ToList();
        var xs = receivers.Select(t => t.ReceiverX).ToList();
        if (receivers.Count >= 2 && xs.All(x => x is not null))
        {
            var diffs = xs.Zip(xs.Skip(1), (a, b) => b!.Value - a!.Value).ToList();
            var median = diffs.Order().ElementAt(diffs.Count / 2);
            if (Math.Abs(median) > 1e-6 && diffs.All(d => Math.Abs(d - median) <= 1e-3 * Math.Max(1, Math.Abs(median))))
            {
                return (new Spread(SpreadId.New(), $"Spread {number}", xs[0]!.Value, Math.Abs(median),
                    median > 0 ? ChannelDirection.Increasing : ChannelDirection.Decreasing, assignments), false);
            }
            // Irregular but recorded: keep positions as per-channel overrides.
            var withOverrides = assignments.Select(a => a.Role == ChannelRole.Receiver
                ? a with { XOverride = record.Traces.First(t => t.Channel == a.Channel).ReceiverX } : a).ToImmutableArray();
            return (new Spread(SpreadId.New(), $"Spread {number}", xs[0]!.Value, Math.Abs(median) > 0 ? Math.Abs(median) : 1, ChannelDirection.Increasing, withOverrides), false);
        }
        var spacing = profile?.Spacing ?? options.DefaultSpacing ?? 1.0;
        return (new Spread(SpreadId.New(), $"Spread {number}", 0, spacing, profile?.Direction ?? ChannelDirection.Increasing, assignments), true);
    }

    private static bool SameLayout(Spread a, Spread b)
        => Math.Abs(a.FirstReceiverX - b.FirstReceiverX) < 1e-6 && Math.Abs(a.Spacing - b.Spacing) < 1e-6 && a.Direction == b.Direction
           && a.Channels.Length == b.Channels.Length && a.Channels.Zip(b.Channels).All(p => p.First == p.Second);

    /// <summary>Mean zero-lag correlation of corresponding traces of two records.</summary>
    private static double Correlate(DecodedRecord a, DecodedRecord b)
    {
        var values = new List<double>();
        foreach (var ta in a.Traces)
        {
            var tb = b.Traces.FirstOrDefault(t => t.Header.Channel == ta.Header.Channel);
            if (tb is null || tb.Samples.Length != ta.Samples.Length || ta.Samples.Length < 4) continue;
            values.Add(Correlation.Pearson(ta.Samples, tb.Samples));
        }
        return values.Count == 0 ? 0 : values.Average();
    }
}
