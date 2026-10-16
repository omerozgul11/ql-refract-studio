using RefractionStudio.Analysis.TravelTimes;
using RefractionStudio.Domain;
using RefractionStudio.Synthetics;

namespace RefractionStudio.Analysis.Tests;

/// <summary>Builds survey lines with picks equal to known travel times (no waveforms).</summary>
public static class TestLines
{
    public sealed record Built(SurveyLine Line, LineInterpretation Interpretation, ImmutableArray<ShotGather> Gathers)
    {
        public LineObservations Observations => ObservationBuilder.Build(Line, Interpretation);

        public ShotGather At(double x) => Gathers.Single(g => Math.Abs(g.ShotX - x) < 1e-9);
    }

    /// <summary>
    /// A line of <paramref name="channels"/> geophones at <paramref name="spacing"/> starting at x = 0,
    /// shots at <paramref name="shots"/>, picks = model first arrivals (+ optional per-shot trigger error),
    /// layer assignment by the automatic segment assigner.
    /// </summary>
    public static Built Create(ISyntheticModel model, double[] shots, int channels = 24, double spacing = 2, int layerCount = 2,
        double[]? triggerErrors = null, Topography? topography = null, Func<int, int, double>? perturb = null, WaveType wave = WaveType.P)
    {
        var topo = topography ?? Topography.Flat;
        var spread = Spread.Regular("Spread 1", channels, 0, spacing);
        var gathers = shots.Select((x, i) => new ShotGather(GatherId.New(), $"S{i + 1}", x, null, 0, spread.Id, CombineMode.Single,
            [new GatherMember(RecordId.New(), 1, wave == WaveType.P ? SourceKind.Vertical : SourceKind.ShLeft)])).ToImmutableArray();
        var line = new SurveyLine(LineId.New(), "Test line", wave, topo, [spread], gathers);
        var picks = ImmutableDictionary.CreateBuilder<PickKey, ArrivalPick>();
        for (var s = 0; s < gathers.Length; s++)
        {
            var g = gathers[s];
            var receivers = Enumerable.Range(1, channels).Select(c => (X: spread.PositionOf(c)!.Value, Z: topo.ElevationAt(spread.PositionOf(c)!.Value))).ToList();
            var times = model.FirstArrivals((g.ShotX, topo.ElevationAt(g.ShotX)), receivers);
            for (var c = 1; c <= channels; c++)
            {
                if (Math.Abs(receivers[c - 1].X - g.ShotX) < 1e-9) continue;
                var t = times[c - 1] + (triggerErrors is { } e ? e[s] : 0) + (perturb?.Invoke(s, c) ?? 0);
                picks[new PickKey(g.Id, c)] = new ArrivalPick(t, ArrivalOrigin.Manual);
            }
        }
        var interp = LineInterpretation.Create() with { Picks = picks.ToImmutable() };
        interp = interp with { Segments = interp.Segments with { LayerCount = layerCount } };
        var built = new Built(line, interp, gathers);
        var suggested = new SegmentAssigner().Suggest(built.Observations, layerCount);
        return built with { Interpretation = interp with { Segments = suggested } };
    }

    public static Built WithLayered(this Built b, LayeredSettings settings) => b with { Interpretation = b.Interpretation with { Layered = settings } };
}
