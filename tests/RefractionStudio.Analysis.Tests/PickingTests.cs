using RefractionStudio.Analysis.Picking;
using RefractionStudio.Analysis.Quality;
using RefractionStudio.Analysis.TravelTimes;
using RefractionStudio.Domain;
using RefractionStudio.Formats;
using RefractionStudio.Synthetics;

namespace RefractionStudio.Analysis.Tests;

public class PickingTests
{
    private static readonly HorizontalLayersModel Model = new([500, 2000], [5]);

    private static (GatherData Gather, SyntheticShot Shot) Gather(SyntheticSurvey survey, int shotIndex, double timeOffset = 0)
    {
        var shot = survey.Shots[shotIndex];
        var file = Seg2Reader.Parse(shot.Bytes, shot.FileName, includeSamples: true);
        var traces = file.Records[0].Traces
            .Where(t => t.Header.Channel <= survey.Options.Channels)
            .Select(t => new GatherTrace(t.Header.Channel, survey.ReceiverX(t.Header.Channel), t.Samples, t.Header.SampleInterval, t.Header.Delay, timeOffset, false))
            .ToImmutableArray();
        return (new GatherData(GatherId.New(), shot.ShotX, traces, []), shot);
    }

    [Theory]
    [InlineData(PickerAlgorithm.Aic)]
    [InlineData(PickerAlgorithm.StaLta)]
    [InlineData(PickerAlgorithm.EnergyRatio)]
    public void Clean_data_is_picked_within_a_few_samples(PickerAlgorithm algorithm)
    {
        var survey = SurveyGenerator.Generate(Model, new SyntheticSurveyOptions(ShotPositions: [-1], Frequency: 80, NoiseLevel: 0.002));
        var (gather, shot) = Gather(survey, 0);
        var picks = new AutoPicker().PickGather(gather, new PickerSettings(algorithm));
        Assert.True(picks.Count >= 22);
        var dt = survey.Options.SampleInterval;
        // AIC (the default) is a precise onset estimator: 4 samples. STA/LTA and the energy ratio are
        // detectors whose onset estimate on an emergent wavelet can be up to ~1.5 ms early: allow 2 ms.
        var tolerance = algorithm == PickerAlgorithm.Aic ? 4 * dt : 0.002;
        foreach (var p in picks)
            Assert.InRange(p.Time - shot.TrueTimes[p.Channel], -tolerance, tolerance);
    }

    [Fact]
    public void Confidence_explains_noise_and_is_lower_for_noisy_data()
    {
        var clean = SurveyGenerator.Generate(Model, new SyntheticSurveyOptions(ShotPositions: [-1], NoiseLevel: 0.002, Seed: 3));
        var noisy = SurveyGenerator.Generate(Model, new SyntheticSurveyOptions(ShotPositions: [-1], NoiseLevel: 0.6, Seed: 3));
        var pc = new AutoPicker().PickGather(Gather(clean, 0).Gather, new PickerSettings());
        var pn = new AutoPicker().PickGather(Gather(noisy, 0).Gather, new PickerSettings());
        Assert.True(pn.Average(p => p.Confidence.Score) < pc.Average(p => p.Confidence.Score));
        Assert.All(pc.Concat(pn), p => Assert.Equal(4, p.Confidence.Factors.Length));
        Assert.Contains(pn, p => p.Confidence.Reasons.Any(r => r.Contains("signal-to-noise", StringComparison.OrdinalIgnoreCase) || r.Contains("disagree") || r.Contains("Inconsistent")));
        Assert.All(pc.Concat(pn), p => Assert.Equal(PickConfidence.LevelFor(p.Confidence.Score), p.Confidence.Level));
    }

    [Fact]
    public void Dead_traces_are_not_picked_and_are_flagged()
    {
        var survey = SurveyGenerator.Generate(Model, new SyntheticSurveyOptions(ShotPositions: [-1], DeadChannels: [7], NoiseLevel: 0.002));
        var (gather, _) = Gather(survey, 0);
        var picks = new AutoPicker().PickGather(gather, new PickerSettings());
        Assert.DoesNotContain(picks, p => p.Channel == 7);
        var flags = QualityControl.CheckTraces(LineId.New(), gather, null);
        Assert.Contains(flags, f => f.Code == "dead-trace" && f.Key!.Value.Channel == 7);
    }

    [Fact]
    public void Strong_traces_near_the_shot_are_not_flagged_but_a_noisy_trace_is()
    {
        var survey = SurveyGenerator.Generate(Model, new SyntheticSurveyOptions(ShotPositions: [-1], NoiseLevel: 0.002));
        var (gather, _) = Gather(survey, 0);
        Assert.DoesNotContain(QualityControl.CheckTraces(LineId.New(), gather, null), f => f.Code == "noisy-trace");

        var rng = new Random(5);
        var traces = gather.Traces.Select(t => t.Channel == 15
            ? t with { Samples = [.. t.Samples.Select(v => v + (rng.NextDouble() - 0.5) * 400 * t.Samples.Select(Math.Abs).Max())] }
            : t).ToImmutableArray();
        var flags = QualityControl.CheckTraces(LineId.New(), gather with { Traces = traces }, null);
        var noisy = Assert.Single(flags, f => f.Code == "noisy-trace");
        Assert.Equal(15, noisy.Key!.Value.Channel);
    }

    [Fact]
    public void Reversed_polarity_channel_is_suggested()
    {
        var survey = SurveyGenerator.Generate(Model, new SyntheticSurveyOptions(ShotPositions: [-1], ReversedChannels: [12], NoiseLevel: 0.002));
        var (gather, shot) = Gather(survey, 0);
        var flags = QualityControl.CheckTraces(LineId.New(), gather, shot.TrueTimes);
        Assert.Contains(flags, f => f.Code == "polarity" && f.Key!.Value.Channel == 12);
        Assert.DoesNotContain(flags, f => f.Code == "polarity" && f.Key!.Value.Channel != 12);
    }

    [Fact]
    public void Sh_polarity_divergence_finds_the_shear_onset_not_the_p_wave()
    {
        var shModel = new HorizontalLayersModel([200, 700], [5]);
        var survey = SurveyGenerator.Generate(shModel, new SyntheticSurveyOptions(ShotPositions: [-1], WaveType: WaveType.SH, Frequency: 40, Samples: 2048, NoiseLevel: 0.002));
        var left = Gather(survey, 0).Gather;
        var right = Gather(survey, 1).Gather;
        // Polarity-weighted members: left +1, right −1 → S aligned.
        var weightedRight = right.Traces.Select(t => t with { Samples = [.. t.Samples.Select(v => -v)] }).ToImmutableArray();
        var combined = left.Traces.Select((t, i) => t with { Samples = [.. t.Samples.Select((v, k) => 0.5 * (v + weightedRight[i].Samples[k]))] }).ToImmutableArray();
        var gather = new GatherData(GatherId.New(), -1, combined, [left.Traces, weightedRight]);
        var picks = new AutoPicker().PickGather(gather, new PickerSettings(PickerAlgorithm.ShPolarityDivergence, ShortWindow: 0.004, MinApparentVelocity: 100));
        Assert.True(picks.Count >= 20);
        var truth = survey.Shots[0].TrueTimes;
        var good = picks.Count(p => Math.Abs(p.Time - truth[p.Channel]) < 0.004);
        Assert.True(good >= picks.Count * 0.85, $"{good}/{picks.Count} within 4 ms");
        Assert.All(picks, p => Assert.Contains(p.Confidence.Factors, f => f.Name == "Opposite-impact agreement"));
    }

    [Fact]
    public void Cross_correlation_transfers_a_pick_to_the_neighbouring_trace()
    {
        var survey = SurveyGenerator.Generate(Model, new SyntheticSurveyOptions(ShotPositions: [-1], NoiseLevel: 0.002));
        var (gather, shot) = Gather(survey, 0);
        var a = gather.Traces.First(t => t.Channel == 3);
        var b = gather.Traces.First(t => t.Channel == 4);
        var r = new CrossCorrelationAnalyzer().Transfer(a, shot.TrueTimes[3], b, 0.002, 0.008, 0.006)!;
        Assert.InRange(r.Time - shot.TrueTimes[4], -0.0003, 0.0003);
        Assert.True(r.Coefficient > 0.9);
        Assert.NotEmpty(r.Curve);
    }
}

public class ShotTimeAndQcTests
{
    private static readonly HorizontalLayersModel Model = new([500, 2000], [5]);

    [Fact]
    public void Reference_geophone_correction_removes_trigger_errors_and_keeps_raw_times()
    {
        var errors = new[] { 0.0, 0.004, -0.003, 0.0, 0.002 };
        var b = TestLines.Create(Model, [-24, -1, 23, 47, 70], triggerErrors: errors);
        var shotTimes = b.Gathers.Select((g, i) => KeyValuePair.Create(g.Id, new ShotTimeEntry(ReferencePick: new ArrivalPick(errors[i], ArrivalOrigin.Manual))));
        var interp = b.Interpretation with
        {
            ShotTime = new ShotTimeSettings(ShotTimeMethod.ReferenceGeophone),
            ShotTimes = ImmutableDictionary.CreateRange(shotTimes),
        };
        var obs = ObservationBuilder.Build(b.Line, interp);
        var truth = TestLines.Create(Model, [-24, -1, 23, 47, 70]).Observations;
        foreach (var o in obs.All)
        {
            var t = truth.All.Single(x => Math.Abs(x.ShotX - o.ShotX) < 1e-9 && x.Channel == o.Channel);
            Assert.Equal(t.CorrectedTime, o.CorrectedTime, 12);
            Assert.Equal(t.CorrectedTime + o.Pick.Time - t.Pick.Time, o.RawTime, 12);
            Assert.Equal(-o.ShotCorrection, o.RawTime - t.CorrectedTime, 12);
        }
    }

    [Fact]
    public void Missing_reference_pick_excludes_the_shot_with_a_reason()
    {
        var b = TestLines.Create(Model, [-1, 47]);
        var interp = b.Interpretation with { ShotTime = new ShotTimeSettings(ShotTimeMethod.ReferenceGeophone) };
        var obs = ObservationBuilder.Build(b.Line, interp);
        Assert.All(obs.All, o => Assert.False(o.Included));
        Assert.All(obs.All, o => Assert.Contains("no reference-geophone pick", o.ExclusionReason));
    }

    [Fact]
    public void Trigger_errors_and_reciprocity_violations_are_flagged()
    {
        var b = TestLines.Create(Model, [-1, 23, 47], triggerErrors: [0, 0.006, 0]);
        var obs = b.Observations;
        var fits = SegmentAssigner.FitAll(obs, RegressionMode.Ordinary);
        var reciprocals = QualityControl.ReciprocalPairs(obs, 1.1);
        var flags = QualityControl.CheckLine(obs, new QcThresholds(), fits, reciprocals, null, null, false);
        var center = b.At(23).Id;
        Assert.Contains(flags, f => f.Code == "trigger" && f.Gather == center);
        Assert.Contains(flags, f => f.Code == "reciprocity");
        Assert.DoesNotContain(flags, f => f.Code == "trigger" && f.Gather != center);
    }

    [Fact]
    public void Clean_line_has_no_problems()
    {
        var b = TestLines.Create(Model, [-24, -1, 23, 47, 70]);
        var obs = b.Observations;
        var fits = SegmentAssigner.FitAll(obs, RegressionMode.Ordinary);
        var flags = QualityControl.CheckLine(obs, new QcThresholds(), fits, QualityControl.ReciprocalPairs(obs, 1.1), null, null, false);
        Assert.DoesNotContain(flags, f => f.Level == QcLevel.Problem);
        Assert.DoesNotContain(flags, f => f.Code is "reciprocity" or "trigger" or "time-reversal" or "segment-residual");
    }

    [Fact]
    public void Air_wave_picks_are_suspected()
    {
        var b = TestLines.Create(new HorizontalLayersModel([340, 2000], [30]), [-1]);
        var obs = b.Observations;
        var flags = QualityControl.CheckLine(obs, new QcThresholds(), SegmentAssigner.FitAll(obs, RegressionMode.Ordinary), [], null, null, false);
        Assert.Contains(flags, f => f.Code == "air-wave");
    }
}
