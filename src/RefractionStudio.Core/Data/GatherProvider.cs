using System.Globalization;
using System.Text;
using RefractionStudio.Analysis.Picking;
using RefractionStudio.Analysis.TravelTimes;
using RefractionStudio.Domain;
using RefractionStudio.SignalProcessing;

namespace RefractionStudio.Core.Data;

/// <summary>One trace of a gather as displayed: original (combined) and processed samples.</summary>
/// <param name="Channel">Channel.</param>
/// <param name="X">Position (m); NaN when unknown (reference channel at the shot uses the shot position).</param>
/// <param name="Role">Channel role.</param>
/// <param name="Original">Combined members, geometry polarity applied, no processing.</param>
/// <param name="Processed">Pipeline and trace adjustments applied.</param>
/// <param name="OriginalSampleInterval">Sample interval of <paramref name="Original"/>.</param>
/// <param name="SampleInterval">Sample interval of <paramref name="Processed"/>.</param>
/// <param name="StartTime">Record time of sample 0 (both arrays share the time origin).</param>
/// <param name="TotalShift">Timing shift applied by processing and the trace adjustment (display − record time).</param>
/// <param name="Adjustment">Trace adjustment.</param>
public sealed record ProcessedTrace(int Channel, double X, ChannelRole Role, double[] Original, double[] Processed, double OriginalSampleInterval,
    double SampleInterval, double StartTime, double TotalShift, TraceAdjustment Adjustment)
{
    public bool Excluded => Adjustment.Excluded;

    /// <summary>Record (raw) time of a time read on the processed display.</summary>
    public double ToRecordTime(double displayTime) => displayTime - TotalShift;

    /// <summary>Display time of a record time.</summary>
    public double ToDisplayTime(double recordTime) => recordTime + TotalShift;
}

/// <summary>A gather ready for display and picking.</summary>
public sealed record ProcessedGather(
    LineId Line,
    GatherId Gather,
    string Label,
    double ShotX,
    ShotTimeInfo ShotTime,
    ImmutableArray<ProcessedTrace> Traces,
    ImmutableArray<ProcessedTrace> ReferenceTraces,
    ImmutableArray<ImmutableArray<ProcessedTrace>> ShMembers,
    ImmutableArray<string> Problems)
{
    /// <summary>Shot correction (0 when unavailable).</summary>
    public double Correction => ShotTime.Correction.HasValue ? ShotTime.Correction.Value : 0;

    /// <summary>Picker input (processed traces, display time axis).</summary>
    public GatherData ToGatherData() => new(Gather, ShotX,
        [.. Traces.Select(t => new GatherTrace(t.Channel, t.X, t.Processed, t.SampleInterval, t.StartTime, Correction, t.Excluded))],
        [.. ShMembers.Select(m => m.Select(t => new GatherTrace(t.Channel, t.X, t.Processed, t.SampleInterval, t.StartTime, Correction, t.Excluded)).ToImmutableArray())]);
}

/// <summary>
/// Builds displayed gathers: combines member records (stack / SH pairs), applies channel polarity,
/// the processing pipeline and trace adjustments, and caches the result by a fingerprint of everything
/// that affects it. Raw data are never modified.
/// </summary>
public sealed class GatherProvider(ITraceStore traces, int capacity = 64)
{
    private readonly Dictionary<string, ProcessedGather> _cache = [];
    private readonly LinkedList<string> _lru = new();
    private readonly Lock _lock = new();

    public ProcessedGather Get(ProjectState state, LineId lineId, GatherId gatherId)
    {
        var line = state.Survey.Line(lineId) ?? throw new ArgumentException("Line not found.", nameof(lineId));
        var gather = line.Gather(gatherId) ?? throw new ArgumentException("Gather not found.", nameof(gatherId));
        var interp = state.Interpretation(lineId);
        var spread = line.Spread(gather.SpreadId) ?? throw new InvalidOperationException($"Shot {gather.Label} refers to a missing spread.");
        var shotTime = new ShotTimeCorrector().Correct(line, gather, interp);
        var key = Fingerprint(gather, spread, interp, shotTime);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                _lru.Remove(key);
                _lru.AddFirst(key);
                return hit;
            }
        }
        var result = Build(state.Survey, line, gather, spread, interp, shotTime);
        lock (_lock)
        {
            if (!_cache.ContainsKey(key))
            {
                _cache[key] = result;
                _lru.AddFirst(key);
                while (_lru.Count > capacity)
                {
                    _cache.Remove(_lru.Last!.Value);
                    _lru.RemoveLast();
                }
            }
        }
        return result;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lru.Clear();
        }
    }

    private ProcessedGather Build(Survey survey, SurveyLine line, ShotGather gather, Spread spread, LineInterpretation interp, ShotTimeInfo shotTime)
    {
        var problems = ImmutableArray.CreateBuilder<string>();
        var members = gather.Combine == CombineMode.Single ? gather.Members.Take(1).ToList() : gather.Members.ToList();
        var receivers = ImmutableArray.CreateBuilder<ProcessedTrace>();
        var references = ImmutableArray.CreateBuilder<ProcessedTrace>();
        var memberTraces = members.Select(_ => ImmutableArray.CreateBuilder<ProcessedTrace>()).ToList();
        var isShPair = members.Count >= 2 && members.Select(m => m.Kind).Distinct().Count() >= 2 && members.Any(m => m.Polarity < 0);

        foreach (var ch in spread.Channels.OrderBy(c => c.Channel))
        {
            if (ch.Role is ChannelRole.Unused or ChannelRole.Auxiliary) continue;
            var headers = members.Select(m => survey.Record(m.Record)?.Trace(ch.Channel)).ToList();
            if (headers.Any(h => h is null))
            {
                if (headers.Any(h => h is not null)) problems.Add($"Channel {ch.Channel} is missing from some records of shot {gather.Label}; it is not shown.");
                continue;
            }
            var h0 = headers[0]!;
            if (headers.Any(h => h!.SampleCount != h0.SampleCount || Math.Abs(h!.SampleInterval - h0.SampleInterval) > 1e-12 || Math.Abs(h.Delay - h0.Delay) > 1e-9))
            {
                problems.Add($"Channel {ch.Channel}: the records of shot {gather.Label} differ in sampling or delay and cannot be combined.");
                continue;
            }
            var geometrySign = ch.InvertPolarity ? -1.0 : 1.0;
            var weighted = new List<double[]>();
            for (var m = 0; m < members.Count; m++)
            {
                var raw = traces.Samples(survey, members[m].Record, ch.Channel)!;
                var sign = members[m].Polarity * geometrySign;
                weighted.Add(raw.Select(v => v * sign).ToArray());
            }
            var combined = new double[h0.SampleCount];
            foreach (var w in weighted)
                for (var i = 0; i < combined.Length; i++) combined[i] += w[i] / weighted.Count;

            var key = new PickKey(gather.Id, ch.Channel);
            var adjustment = interp.AdjustmentOf(key);
            var x = spread.PositionOf(ch.Channel) ?? (ch.Role == ChannelRole.Reference ? gather.ShotX : double.NaN);
            var processed = Process(combined, h0, interp, adjustment, problems, ch.Channel);
            var trace = new ProcessedTrace(ch.Channel, x, ch.Role, combined, processed.Samples, h0.SampleInterval, processed.SampleInterval, h0.Delay,
                interp.Pipeline.TotalTimeShift + adjustment.TimeShift, adjustment);
            if (ch.Role == ChannelRole.Reference) references.Add(trace);
            else receivers.Add(trace);

            if (isShPair && ch.Role == ChannelRole.Receiver)
            {
                for (var m = 0; m < members.Count; m++)
                {
                    var p = Process(weighted[m], h0, interp, adjustment, null, ch.Channel);
                    memberTraces[m].Add(trace with { Original = weighted[m], Processed = p.Samples, SampleInterval = p.SampleInterval });
                }
            }
        }
        var label = gather.Label;
        return new ProcessedGather(line.Id, gather.Id, label, gather.ShotX, shotTime, receivers.ToImmutable(), references.ToImmutable(),
            isShPair ? [.. memberTraces.Select(b => b.ToImmutable())] : [], problems.Distinct().ToImmutableArray());
    }

    private static PipelineOutput Process(double[] combined, TraceHeader header, LineInterpretation interp, TraceAdjustment adjustment, ImmutableArray<string>.Builder? problems, int channel)
    {
        var output = PipelineRunner.Run(combined, header.SampleInterval, header.Delay, interp.Pipeline);
        if (problems is not null)
            foreach (var p in output.Problems) problems.Add(p);
        var samples = output.Samples;
        var factor = adjustment.Gain * (adjustment.InvertPolarity ? -1 : 1);
        if (factor != 1) samples = TraceOps.Scale(samples, factor);
        if (adjustment.TimeShift != 0) samples = TraceOps.Shift(samples, adjustment.TimeShift, output.SampleInterval);
        return output with { Samples = samples };
    }

    private static string Fingerprint(ShotGather g, Spread spread, LineInterpretation interp, ShotTimeInfo shotTime)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{g.Id}|{g.Combine}|{g.ShotX:R}|");
        foreach (var m in g.Members) sb.Append(CultureInfo.InvariantCulture, $"{m.Record}:{m.Polarity}:{m.Kind};");
        sb.Append('|');
        foreach (var c in spread.Channels) sb.Append(CultureInfo.InvariantCulture, $"{c.Channel}:{c.Role}:{c.InvertPolarity}:{c.XOverride:R};");
        sb.Append(CultureInfo.InvariantCulture, $"|{spread.FirstReceiverX:R}|{spread.Spacing:R}|{spread.Direction}|{interp.Pipeline.Fingerprint()}|");
        foreach (var (k, a) in interp.Adjustments.Where(a => a.Key.Gather == g.Id).OrderBy(a => a.Key.Channel))
            sb.Append(CultureInfo.InvariantCulture, $"{k.Channel}:{a.Fingerprint()}:{a.Excluded};");
        sb.Append(CultureInfo.InvariantCulture, $"|{shotTime.Method}|{(shotTime.Correction.HasValue ? shotTime.Correction.Value.ToString("R", CultureInfo.InvariantCulture) : "na")}");
        return sb.ToString();
    }
}
