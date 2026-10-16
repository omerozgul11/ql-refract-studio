namespace RefractionStudio.Domain;

/// <summary>Seismic wave type recorded on a line.</summary>
public enum WaveType
{
    /// <summary>Compressional wave: vertical impact source, vertical geophones.</summary>
    P,

    /// <summary>Horizontally polarised shear wave: horizontal (left/right) impacts, transverse horizontal geophones.</summary>
    SH,
}

/// <summary>An original file embedded in the project, byte-for-byte.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="FileName">Original file name.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the file content.</param>
/// <param name="Length">File size in bytes.</param>
/// <param name="FormatId">Reader that decoded it, e.g. "seg2".</param>
/// <param name="ImportedAt">When it was imported.</param>
/// <param name="OriginalPath">Absolute path at import time (informational).</param>
public sealed record SourceFile(
    SourceFileId Id,
    string FileName,
    string Sha256,
    long Length,
    string FormatId,
    DateTimeOffset ImportedAt,
    string? OriginalPath)
{
    /// <summary>Path of the embedded copy inside the project container.</summary>
    public string ContainerPath => $"raw/{Sha256}/{FileName}";
}

/// <summary>Header information of one trace as read from the file (never edited).</summary>
/// <param name="Channel">Channel number (1-based) as recorded.</param>
/// <param name="IndexInRecord">Zero-based index of the trace in the record.</param>
/// <param name="SampleCount">Number of samples.</param>
/// <param name="SampleInterval">Sample interval in seconds.</param>
/// <param name="Delay">Time of the first sample relative to the trigger, seconds (negative = pre-trigger).</param>
/// <param name="ReceiverX">Receiver in-line location from the header, if present (m).</param>
/// <param name="ReceiverElevation">Receiver elevation from the header, if present (m).</param>
/// <param name="SourceX">Source location from the header, if present (m).</param>
/// <param name="Stack">Number of vertically stacked shots, if recorded.</param>
/// <param name="SampleFormat">Human-readable encoding (e.g. "int32", "float32").</param>
/// <param name="Keywords">All descriptive strings of the trace header.</param>
public sealed record TraceHeader(
    int Channel,
    int IndexInRecord,
    int SampleCount,
    double SampleInterval,
    double Delay,
    double? ReceiverX,
    double? ReceiverElevation,
    double? SourceX,
    int? Stack,
    string SampleFormat,
    ImmutableDictionary<string, string> Keywords)
{
    /// <summary>Record time of sample <paramref name="index"/>.</summary>
    public double TimeOf(int index) => Delay + index * SampleInterval;

    /// <summary>Record time of the last sample.</summary>
    public double EndTime => Delay + (SampleCount - 1) * SampleInterval;
}

/// <summary>One field record: a set of traces recorded for one shot.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="SourceFileId">File it came from.</param>
/// <param name="RecordIndexInFile">Index of the record within a multi-record file (0 for SEG-2).</param>
/// <param name="Label">Display label, usually the file name or field record number.</param>
/// <param name="Traces">Trace headers in file order.</param>
/// <param name="Keywords">Record-level descriptive strings.</param>
/// <param name="AcquiredAt">Acquisition time, if recorded.</param>
/// <param name="FieldRecordNumber">Field record / shot sequence number, if recorded.</param>
/// <param name="SourceX">Source location from the header, if recorded (m).</param>
public sealed record SeismicRecord(
    RecordId Id,
    SourceFileId SourceFileId,
    int RecordIndexInFile,
    string Label,
    ImmutableArray<TraceHeader> Traces,
    ImmutableDictionary<string, string> Keywords,
    DateTimeOffset? AcquiredAt,
    int? FieldRecordNumber,
    double? SourceX)
{
    public TraceHeader? Trace(int channel)
    {
        foreach (var t in Traces)
            if (t.Channel == channel) return t;
        return null;
    }
}

/// <summary>A surveyed ground point along the line.</summary>
/// <param name="X">In-line coordinate (m).</param>
/// <param name="Elevation">Ground elevation (m, positive up).</param>
public sealed record TopographyPoint(double X, double Elevation);

/// <summary>Ground surface along the line, linearly interpolated between surveyed points.</summary>
public sealed record Topography(ImmutableArray<TopographyPoint> Points)
{
    /// <summary>Flat ground at elevation 0 (no topography supplied).</summary>
    public static Topography Flat { get; } = new([]);

    public bool IsFlat => Points.IsDefaultOrEmpty;

    /// <summary>Minimum and maximum surveyed x, or null for flat topography.</summary>
    public (double Min, double Max)? Extent => IsFlat ? null : (Points[0].X, Points[^1].X);

    /// <summary>True when <paramref name="x"/> lies inside the surveyed range (always true when flat).</summary>
    public bool Covers(double x, double tolerance = 1e-6)
        => IsFlat || (x >= Points[0].X - tolerance && x <= Points[^1].X + tolerance);

    /// <summary>
    /// Ground elevation at <paramref name="x"/>. Outside the surveyed range the nearest end
    /// elevation is used; geometry validation reports every position where that happens.
    /// </summary>
    public double ElevationAt(double x)
    {
        if (IsFlat) return 0.0;
        var pts = Points;
        if (x <= pts[0].X) return pts[0].Elevation;
        if (x >= pts[^1].X) return pts[^1].Elevation;
        int lo = 0, hi = pts.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (pts[mid].X <= x) lo = mid; else hi = mid;
        }
        var a = pts[lo];
        var b = pts[hi];
        var span = b.X - a.X;
        return span <= 0 ? a.Elevation : a.Elevation + (b.Elevation - a.Elevation) * (x - a.X) / span;
    }

    /// <summary>Creates topography from points, sorting by x and rejecting duplicates.</summary>
    public static Topography Create(IEnumerable<TopographyPoint> points)
    {
        var sorted = points.OrderBy(p => p.X).ToImmutableArray();
        for (var i = 1; i < sorted.Length; i++)
        {
            if (Math.Abs(sorted[i].X - sorted[i - 1].X) < 1e-9)
                throw new ArgumentException($"Topography has two points at x = {sorted[i].X} m.");
        }
        foreach (var p in sorted)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Elevation))
                throw new ArgumentException("Topography contains a non-finite coordinate.");
        }
        return new Topography(sorted);
    }
}

/// <summary>Role of a recorded channel.</summary>
public enum ChannelRole
{
    /// <summary>A geophone of the refraction spread.</summary>
    Receiver,

    /// <summary>Reference / trigger / shot ("spy") geophone used for shot-time correction.</summary>
    Reference,

    /// <summary>Auxiliary channel (e.g. trigger pulse); not used for analysis.</summary>
    Auxiliary,

    /// <summary>Unused or disconnected channel.</summary>
    Unused,
}

/// <summary>Direction in which channel numbers advance along the line.</summary>
public enum ChannelDirection
{
    /// <summary>Channel 1 at the first receiver position, increasing x with channel number.</summary>
    Increasing,

    /// <summary>Channel 1 at the first receiver position, decreasing x with channel number.</summary>
    Decreasing,
}

/// <summary>Per-channel mapping and overrides of a spread.</summary>
/// <param name="Channel">Channel number.</param>
/// <param name="Role">Role of the channel.</param>
/// <param name="XOverride">Explicit in-line position (m); null = from the regular layout.</param>
/// <param name="ElevationOverride">Explicit elevation (m); null = from topography.</param>
/// <param name="InvertPolarity">True when the geophone is known to be wired with reversed polarity.</param>
public sealed record ChannelAssignment(
    int Channel,
    ChannelRole Role,
    double? XOverride = null,
    double? ElevationOverride = null,
    bool InvertPolarity = false);

/// <summary>Receiver layout of a spread: a regular geophone array with per-channel overrides.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="Name">Display name, e.g. "Spread 1".</param>
/// <param name="FirstReceiverX">Position of the first receiver channel (m).</param>
/// <param name="Spacing">Geophone interval (m, &gt; 0).</param>
/// <param name="Direction">Direction in which receiver channels advance.</param>
/// <param name="Channels">All channels of the spread with their roles.</param>
public sealed record Spread(
    SpreadId Id,
    string Name,
    double FirstReceiverX,
    double Spacing,
    ChannelDirection Direction,
    ImmutableArray<ChannelAssignment> Channels)
{
    public ChannelAssignment? Channel(int channel)
    {
        foreach (var c in Channels)
            if (c.Channel == channel) return c;
        return null;
    }

    /// <summary>Receiver channels ordered by channel number.</summary>
    public IEnumerable<ChannelAssignment> ReceiverChannels
        => Channels.Where(c => c.Role == ChannelRole.Receiver).OrderBy(c => c.Channel);

    /// <summary>
    /// In-line position of a channel. Receivers are laid out at regular spacing in channel order,
    /// counting only receiver channels; an explicit override always wins. Reference channels without an
    /// override are located at the shot (returns null, the caller substitutes the shot position).
    /// </summary>
    public double? PositionOf(int channel)
    {
        var assignment = Channel(channel);
        if (assignment is null) return null;
        if (assignment.XOverride is { } x) return x;
        if (assignment.Role != ChannelRole.Receiver) return null;
        var ordinal = 0;
        foreach (var c in ReceiverChannels)
        {
            if (c.Channel == channel) break;
            ordinal++;
        }
        var sign = Direction == ChannelDirection.Increasing ? 1.0 : -1.0;
        return FirstReceiverX + sign * ordinal * Spacing;
    }

    /// <summary>Creates a regular spread where channels 1..receiverCount are receivers.</summary>
    public static Spread Regular(string name, int channelCount, double firstX, double spacing,
        ChannelDirection direction = ChannelDirection.Increasing)
    {
        var channels = Enumerable.Range(1, channelCount)
            .Select(c => new ChannelAssignment(c, ChannelRole.Receiver))
            .ToImmutableArray();
        return new Spread(SpreadId.New(), name, firstX, spacing, direction, channels);
    }
}

/// <summary>Kind of impact for a member record of a gather.</summary>
public enum SourceKind
{
    /// <summary>Vertical impact or explosive (P).</summary>
    Vertical,

    /// <summary>Horizontal impact toward the "left" (negative transverse) side.</summary>
    ShLeft,

    /// <summary>Horizontal impact toward the "right" side.</summary>
    ShRight,

    /// <summary>Not recorded.</summary>
    Unknown,
}

/// <summary>A record contributing to a gather.</summary>
/// <param name="Record">Record id.</param>
/// <param name="Polarity">+1 or -1. Applied before combining (SH right impacts are usually -1).</param>
/// <param name="Kind">Impact kind.</param>
public sealed record GatherMember(RecordId Record, int Polarity, SourceKind Kind);

/// <summary>How the members of a gather are combined into the picked traces.</summary>
public enum CombineMode
{
    /// <summary>One member only (the first).</summary>
    Single,

    /// <summary>Mean of polarity-weighted members (vertical stack; for opposite SH impacts with
    /// polarities +1/−1 this is the difference trace (L − R)/2, which cancels P energy).</summary>
    Stack,
}

/// <summary>
/// A shot gather: the traces recorded by one spread for one shot position, possibly combined from
/// several records. This is the unit that is displayed and picked.
/// </summary>
/// <param name="Id">Identifier.</param>
/// <param name="Label">Display label.</param>
/// <param name="ShotX">In-line source position (m).</param>
/// <param name="ShotElevation">Source elevation (m); null = ground elevation from topography.</param>
/// <param name="ShotDepth">Depth of a buried source below ground (m, ≥ 0).</param>
/// <param name="SpreadId">Receiver layout used.</param>
/// <param name="Combine">Combination of members.</param>
/// <param name="Members">Contributing records.</param>
/// <param name="Included">False to exclude the whole gather from analysis.</param>
/// <param name="Comment">User comment.</param>
public sealed record ShotGather(
    GatherId Id,
    string Label,
    double ShotX,
    double? ShotElevation,
    double ShotDepth,
    SpreadId SpreadId,
    CombineMode Combine,
    ImmutableArray<GatherMember> Members,
    bool Included = true,
    string? Comment = null);

/// <summary>Optional geographic placement of the line (informational; analysis uses x).</summary>
/// <param name="StartEasting">Easting of x = 0.</param>
/// <param name="StartNorthing">Northing of x = 0.</param>
/// <param name="AzimuthDegrees">Line azimuth, degrees clockwise from grid north.</param>
/// <param name="CoordinateSystem">CRS description, e.g. "EPSG:32611".</param>
public sealed record LineLocation(double StartEasting, double StartNorthing, double AzimuthDegrees, string CoordinateSystem);

/// <summary>One survey line for one wave type.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="Name">Display name, e.g. "Line 1 – P".</param>
/// <param name="WaveType">P or SH.</param>
/// <param name="Topography">Ground surface.</param>
/// <param name="Spreads">Receiver layouts.</param>
/// <param name="Gathers">Shot gathers.</param>
/// <param name="Location">Optional map placement.</param>
/// <param name="Notes">Free text.</param>
public sealed record SurveyLine(
    LineId Id,
    string Name,
    WaveType WaveType,
    Topography Topography,
    ImmutableArray<Spread> Spreads,
    ImmutableArray<ShotGather> Gathers,
    LineLocation? Location = null,
    string? Notes = null)
{
    public Spread? Spread(SpreadId id)
    {
        foreach (var s in Spreads)
            if (s.Id == id) return s;
        return null;
    }

    public ShotGather? Gather(GatherId id)
    {
        foreach (var g in Gathers)
            if (g.Id == id) return g;
        return null;
    }

    /// <summary>Elevation of the source of a gather (top of a buried charge is not modelled; the
    /// source point is at ground elevation minus <see cref="ShotGather.ShotDepth"/>).</summary>
    public double ShotSourceElevation(ShotGather gather)
        => (gather.ShotElevation ?? Topography.ElevationAt(gather.ShotX)) - gather.ShotDepth;

    /// <summary>Ground elevation at the shot.</summary>
    public double ShotGroundElevation(ShotGather gather)
        => gather.ShotElevation ?? Topography.ElevationAt(gather.ShotX);
}

/// <summary>All acquisition data of a project.</summary>
public sealed record Survey(
    ImmutableArray<SourceFile> SourceFiles,
    ImmutableArray<SeismicRecord> Records,
    ImmutableArray<SurveyLine> Lines)
{
    public static Survey Empty { get; } = new([], [], []);

    public SeismicRecord? Record(RecordId id)
    {
        foreach (var r in Records)
            if (r.Id == id) return r;
        return null;
    }

    public SurveyLine? Line(LineId id)
    {
        foreach (var l in Lines)
            if (l.Id == id) return l;
        return null;
    }

    public SourceFile? SourceFile(SourceFileId id)
    {
        foreach (var f in SourceFiles)
            if (f.Id == id) return f;
        return null;
    }

    /// <summary>Finds the line containing a gather.</summary>
    public (SurveyLine Line, ShotGather Gather)? FindGather(GatherId id)
    {
        foreach (var l in Lines)
        {
            var g = l.Gather(id);
            if (g is not null) return (l, g);
        }
        return null;
    }
}
