using RefractionStudio.Domain;

namespace RefractionStudio.Analysis.TravelTimes;

/// <summary>
/// One first-arrival observation with full geometry and timing provenance. Times: record time of the
/// pick (raw), shot-time correction, timing shifts from processing / trace statics, and the corrected
/// travel time used by every analysis method (corrected = raw + shot correction + time shift).
/// </summary>
public sealed record Observation(
    PickKey Key,
    int Channel,
    double ShotX,
    double SourceElevation,
    double ShotGroundElevation,
    double ShotDepth,
    double ReceiverX,
    double ReceiverElevation,
    double RawTime,
    double ShotCorrection,
    double TimeShift,
    double CorrectedTime,
    int Layer,
    bool Included,
    string? ExclusionReason,
    ArrivalPick Pick)
{
    public GatherId Gather => Key.Gather;

    /// <summary>Signed in-line offset (receiver − shot).</summary>
    public double Offset => ReceiverX - ShotX;

    public double AbsOffset => Math.Abs(Offset);

    /// <summary>Straight-line source–receiver distance including the elevation difference.</summary>
    public double Distance => Math.Sqrt(Offset * Offset + (ReceiverElevation - SourceElevation) * (ReceiverElevation - SourceElevation));

    /// <summary>−1 for receivers on the decreasing-x side of the shot, +1 otherwise.</summary>
    public int Side => Offset < 0 ? -1 : 1;
}

/// <summary>How the shot time of a gather was corrected.</summary>
/// <param name="Gather">Gather.</param>
/// <param name="Method">Method.</param>
/// <param name="ReferenceTime">Raw reference-geophone pick (s), if used.</param>
/// <param name="ExpectedReferenceTime">Expected reference arrival from geometry (s), if used.</param>
/// <param name="Correction">Correction added to all picks of the gather, or the reason it is unavailable.</param>
public sealed record ShotTimeInfo(GatherId Gather, ShotTimeMethod Method, double? ReferenceTime, double? ExpectedReferenceTime, Computed<double> Correction);

/// <summary>All observations of a line.</summary>
public sealed record LineObservations(SurveyLine Line, LineInterpretation Interpretation, ImmutableArray<Observation> All, ImmutableArray<ShotTimeInfo> ShotTimes)
{
    public IEnumerable<Observation> Included => All.Where(o => o.Included);

    public IEnumerable<Observation> ForGather(GatherId id) => All.Where(o => o.Gather == id);

    /// <summary>Typical geophone spacing of the line (median of spreads), or 1 m.</summary>
    public double TypicalSpacing
    {
        get
        {
            var spacings = Line.Spreads.Select(s => s.Spacing).Where(s => s > 0).Order().ToList();
            return spacings.Count > 0 ? spacings[spacings.Count / 2] : 1.0;
        }
    }
}

/// <summary>Computes shot-time corrections. Raw times are never modified.</summary>
public interface IShotTimeCorrector
{
    ShotTimeInfo Correct(SurveyLine line, ShotGather gather, LineInterpretation interpretation);
}

/// <inheritdoc />
public sealed class ShotTimeCorrector : IShotTimeCorrector
{
    public ShotTimeInfo Correct(SurveyLine line, ShotGather gather, LineInterpretation interpretation)
    {
        var settings = interpretation.ShotTime;
        interpretation.ShotTimes.TryGetValue(gather.Id, out var entry);
        switch (settings.Method)
        {
            case ShotTimeMethod.None:
                return new ShotTimeInfo(gather.Id, settings.Method, null, null, Computed<double>.Ok(0.0));
            case ShotTimeMethod.Manual:
                return new ShotTimeInfo(gather.Id, settings.Method, null, null,
                    entry?.ManualCorrection is { } m ? Computed<double>.Ok(m) : Computed<double>.Ok(0.0, "No manual correction entered for this shot; 0 ms used."));
            default:
            {
                if (entry?.ReferencePick is not { } refPick)
                    return new ShotTimeInfo(gather.Id, settings.Method, null, null,
                        Computed<double>.Unavailable($"Shot {gather.Label}: no reference-geophone pick, so its shot time cannot be corrected."));
                var spread = line.Spread(gather.SpreadId);
                var refChannel = spread?.Channels.FirstOrDefault(c => c.Role == ChannelRole.Reference);
                double expected = 0;
                if (settings.ReferenceVelocity is { } v && v > 0 && refChannel?.XOverride is { } refX)
                {
                    var refZ = refChannel.ElevationOverride ?? line.Topography.ElevationAt(refX);
                    var dx = refX - gather.ShotX;
                    var dz = refZ - line.ShotSourceElevation(gather);
                    expected = Math.Sqrt(dx * dx + dz * dz) / v;
                }
                return new ShotTimeInfo(gather.Id, settings.Method, refPick.Time, expected, Computed<double>.Ok(expected - refPick.Time));
            }
        }
    }
}

/// <summary>Builds observations of a line from the project state.</summary>
public static class ObservationBuilder
{
    public static LineObservations Build(ProjectState state, LineId lineId, IShotTimeCorrector? corrector = null)
    {
        var line = state.Survey.Line(lineId) ?? throw new ArgumentException($"Line {lineId} does not exist.", nameof(lineId));
        return Build(line, state.Interpretation(lineId), corrector);
    }

    public static LineObservations Build(SurveyLine line, LineInterpretation interp, IShotTimeCorrector? corrector = null)
    {
        corrector ??= new ShotTimeCorrector();
        var pipelineShift = interp.Pipeline.TotalTimeShift;
        var shotTimes = new Dictionary<GatherId, ShotTimeInfo>();
        foreach (var g in line.Gathers) shotTimes[g.Id] = corrector.Correct(line, g, interp);

        var list = ImmutableArray.CreateBuilder<Observation>();
        foreach (var (key, pick) in interp.Picks)
        {
            var gather = line.Gather(key.Gather);
            if (gather is null) continue; // pick of another line
            var spread = line.Spread(gather.SpreadId);
            var assignment = spread?.Channel(key.Channel);
            var rx = spread?.PositionOf(key.Channel);
            string? reason = null;
            if (assignment is null || rx is null) reason = $"Channel {key.Channel} is not part of the spread layout.";
            else if (assignment.Role != ChannelRole.Receiver) reason = $"Channel {key.Channel} is not a receiver channel ({assignment.Role}).";
            var x = rx ?? double.NaN;
            var rz = assignment?.ElevationOverride ?? (double.IsNaN(x) ? double.NaN : line.Topography.ElevationAt(x));
            var adjustment = interp.AdjustmentOf(key);
            var st = shotTimes[gather.Id];
            var correction = st.Correction.HasValue ? st.Correction.Value : double.NaN;
            reason ??= !gather.Included ? "The shot is excluded."
                : adjustment.Excluded ? "The trace is excluded."
                : !pick.Included ? "The pick is excluded."
                : !st.Correction.HasValue ? st.Correction.Reason
                : null;
            var shift = pipelineShift + adjustment.TimeShift;
            var corrected = pick.Time + (double.IsNaN(correction) ? 0 : correction) + shift;
            var offset = x - gather.ShotX;
            var layer = interp.Segments.LayerOf(key, offset);
            list.Add(new Observation(key, key.Channel, gather.ShotX, line.ShotSourceElevation(gather), line.ShotGroundElevation(gather), gather.ShotDepth,
                x, rz, pick.Time, correction, shift, corrected, layer, reason is null, reason, pick));
        }
        var ordered = list.OrderBy(o => o.ShotX).ThenBy(o => o.Gather.Value).ThenBy(o => o.ReceiverX).ToImmutableArray();
        return new LineObservations(line, interp, ordered, [.. line.Gathers.Select(g => shotTimes[g.Id])]);
    }
}
