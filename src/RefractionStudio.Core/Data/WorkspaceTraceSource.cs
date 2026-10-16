using RefractionStudio.Analysis;
using RefractionStudio.Domain;
using RefractionStudio.Formats;
using RefractionStudio.Persistence;

namespace RefractionStudio.Core.Data;

/// <summary>
/// Gives the analysis access to processed waveforms (for trace-level QC). Gathers come from the shared
/// <see cref="GatherProvider"/> cache; picks are converted to the processed (display) time base.
/// </summary>
public sealed class WorkspaceTraceSource(GatherProvider gathers) : ITraceDataSource
{
    public TraceQcInput? Get(ProjectState project, LineId line, GatherId gather)
    {
        ProcessedGather processed;
        try
        {
            processed = gathers.Get(project, line, gather);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or KeyNotFoundException or SeismicFormatException or ProjectFileException)
        {
            // Raw data not available (e.g. a file could not be read): the other checks still run.
            return null;
        }
        var picks = project.Interpretation(line).Picks;
        var times = new Dictionary<int, double>();
        foreach (var t in processed.Traces)
        {
            if (picks.TryGetValue(new PickKey(gather, t.Channel), out var pick)) times[t.Channel] = t.ToDisplayTime(pick.Time);
        }
        return new TraceQcInput(processed.ToGatherData(), times);
    }
}
