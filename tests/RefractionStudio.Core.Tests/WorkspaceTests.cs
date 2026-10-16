using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RefractionStudio.Core.Data;
using RefractionStudio.Core.Import;
using RefractionStudio.Core.Services;
using RefractionStudio.Core.Session;
using RefractionStudio.Domain;
using RefractionStudio.Domain.Units;
using RefractionStudio.Persistence;
using RefractionStudio.Synthetics;

namespace RefractionStudio.Core.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rs-core-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ProjectWorkspace NewWorkspace() => new(new WorkspaceOptions("1.0.0-test", "tester", Path.Combine(_dir, "recovery")));

    private static readonly HorizontalLayersModel Model = new([500, 2000], [5]);

    private async Task<(ProjectWorkspace Ws, LineId Line, SyntheticSurvey Survey)> ImportedAsync(WaveType wave = WaveType.P, double[]? triggerErrors = null)
    {
        var ws = NewWorkspace();
        ws.NewProject("Test project");
        var survey = SurveyGenerator.Generate(Model, new SyntheticSurveyOptions(ShotPositions: [-24, -1, 23, 47, 70], NoiseLevel: 0.01, WaveType: wave, ShotTimeErrors: triggerErrors));
        var files = survey.WriteTo(Path.Combine(_dir, "data-" + wave));
        var (inspected, issues, _) = await ws.InspectAsync(files);
        Assert.DoesNotContain(issues, i => i.Severity == Formats.ImportSeverity.Error);
        var plan = ws.Import(inspected, new ImportOptions(null, "Line " + wave, wave, null, null,
            wave == WaveType.SH ? GatherGrouping.ShOppositePairs : GatherGrouping.Separate, new HashSet<string>(), new HashSet<(string, int)>()));
        return (ws, plan.Line, survey);
    }

    [Fact]
    public async Task Import_derives_spread_and_shot_positions_from_headers()
    {
        var (ws, lineId, survey) = await ImportedAsync();
        var line = ws.State.Survey.Line(lineId)!;
        Assert.Equal(5, line.Gathers.Length);
        var spread = Assert.Single(line.Spreads);
        Assert.Equal(0, spread.FirstReceiverX);
        Assert.Equal(2, spread.Spacing);
        Assert.Equal(survey.Options.ShotPositions!, line.Gathers.Select(g => g.ShotX).Order().ToArray());
        Assert.Equal(5, ws.State.Survey.SourceFiles.Length);
        Assert.Contains(ws.Session!.History, h => h.Operation == "import" && h.Parameters["records"] == "5");
        ws.Session.Undo();
        Assert.Empty(ws.State.Survey.Lines);
    }

    [Fact]
    public async Task Sh_records_are_paired_as_opposite_impacts_and_the_difference_cancels_p()
    {
        var (ws, lineId, _) = await ImportedAsync(WaveType.SH);
        var line = ws.State.Survey.Line(lineId)!;
        Assert.Equal(5, line.Gathers.Length);
        Assert.All(line.Gathers, g =>
        {
            Assert.Equal(CombineMode.Stack, g.Combine);
            Assert.Equal([1, -1], g.Members.Select(m => m.Polarity).Order().Reverse());
        });
        var gather = ws.Gather(lineId, line.Gathers[1].Id);
        Assert.Equal(2, gather.ShMembers.Length);
        // The combined trace equals (left − right)/2: verify on one channel against the members.
        var c = gather.Traces[5];
        var m0 = gather.ShMembers[0][5].Original;
        var m1 = gather.ShMembers[1][5].Original;
        for (var i = 0; i < c.Original.Length; i += 37) Assert.Equal(0.5 * (m0[i] + m1[i]), c.Original[i], 9);
    }

    [Fact]
    public async Task Save_and_load_round_trip_preserves_state_raw_bytes_and_history()
    {
        var (ws, lineId, _) = await ImportedAsync();
        await ws.AutoPickAsync(lineId, new PickerSettings());
        var path = Path.Combine(_dir, "p.rfsx");
        ws.Save(path);
        Assert.False(ws.Session!.IsDirty);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        var before = JsonSerializer.Serialize(ws.State, DomainJson.Options);

        var ws2 = NewWorkspace();
        var loaded = ws2.Open(path);
        Assert.Equal(before, JsonSerializer.Serialize(ws2.State, DomainJson.Options));
        Assert.Contains(ws2.Session!.History, h => h.Operation == "pick.automatic");
        foreach (var f in ws2.State.Survey.SourceFiles)
        {
            using var s = loaded.Open(f);
            Assert.Equal(f.Sha256, Convert.ToHexStringLower(SHA256.HashData(s)));
            Assert.Equal(File.ReadAllBytes(f.OriginalPath!), ws2.Raw.ReadAll(f));
        }
        // Gathers decode from the container.
        var g = ws2.Gather(lineId, ws2.State.Survey.Line(lineId)!.Gathers[0].Id);
        Assert.Equal(24, g.Traces.Length);
    }

    [Fact]
    public async Task Damaged_and_future_files_are_refused_with_explanations()
    {
        var (ws, _, _) = await ImportedAsync();
        var path = Path.Combine(_dir, "p.rfsx");
        ws.Save(path);
        // Tamper with project.json.
        var damaged = Path.Combine(_dir, "damaged.rfsx");
        File.Copy(path, damaged);
        using (var zip = ZipFile.Open(damaged, ZipArchiveMode.Update))
        {
            var e = zip.GetEntry("project.json")!;
            string text;
            using (var r = new StreamReader(e.Open())) text = r.ReadToEnd();
            e.Delete();
            var n = zip.CreateEntry("project.json");
            using var w = new StreamWriter(n.Open());
            w.Write(text.Replace("Test project", "Tampered"));
        }
        var ex = Assert.Throws<ProjectFileException>(() => NewWorkspace().Open(damaged));
        Assert.Contains("checksum", ex.Message);

        var text2 = Path.Combine(_dir, "notzip.rfsx");
        File.WriteAllText(text2, "hello");
        Assert.Contains("not a valid", Assert.Throws<ProjectFileException>(() => NewWorkspace().Open(text2)).Message);

        var future = new System.Text.Json.Nodes.JsonObject { ["schemaVersion"] = 99 };
        Assert.Contains("newer version", Assert.Throws<ProjectFileException>(() => ProjectMigrations.Upgrade(future)).Message);
    }

    [Fact]
    public async Task Never_saved_project_is_recovered_with_its_staged_raw_data()
    {
        var (ws, lineId, _) = await ImportedAsync();
        await ws.AutoPickAsync(lineId, new PickerSettings());
        var snapshot = ws.Session!.CreateSnapshot("Before review", null);
        Assert.True(ws.Checkpoint());
        var info = ws.Recovery.Recoverable(_ => false).Single();
        Assert.Null(info.ProjectPath);

        var ws2 = NewWorkspace();
        ws2.Recover(info);
        Assert.True(ws2.Session!.IsDirty);
        Assert.Equal(ws.State.Interpretation(lineId).Picks.Count, ws2.State.Interpretation(lineId).Picks.Count);
        Assert.Contains(ws2.Session.History, h => h.Operation == "project.recover");
        var g = ws2.Gather(lineId, ws2.State.Survey.Line(lineId)!.Gathers[0].Id);
        Assert.Equal(24, g.Traces.Length);
        // Snapshots survive recovery and the next save.
        Assert.True(ws2.Session.Snapshots.ContainsKey(snapshot));
        // Saving clears the checkpoint.
        var recovered = Path.Combine(_dir, "recovered.rfsx");
        ws2.Save(recovered);
        Assert.DoesNotContain(ws2.Recovery.Recoverable(_ => false), r => r.SessionId == info.SessionId);
        var ws3 = NewWorkspace();
        ws3.Open(recovered);
        Assert.True(ws3.Session!.Snapshots.ContainsKey(snapshot));
    }

    [Fact]
    public async Task Automatic_picks_match_truth_and_trigger_errors_are_corrected_by_manual_entries()
    {
        var errors = new[] { 0.0, 0.003, 0.0, -0.002, 0.0 };
        var (ws, lineId, survey) = await ImportedAsync(triggerErrors: errors);
        await ws.AutoPickAsync(lineId, new PickerSettings());
        var line = ws.State.Survey.Line(lineId)!;
        ws.Session!.Execute(Edits.SetShotTimeSettings(lineId, new ShotTimeSettings(ShotTimeMethod.Manual)));
        foreach (var g in line.Gathers)
        {
            var idx = Array.IndexOf(survey.Options.ShotPositions!, g.ShotX);
            ws.Session.Execute(Edits.SetShotTimeEntry(lineId, g.Id, new ShotTimeEntry(ManualCorrection: -errors[idx]), "Correct"));
        }
        var analysis = ws.Analyze();
        var obs = analysis.Line(lineId)!.Observations;
        var shots = survey.Shots;
        var errorsFound = new List<double>();
        foreach (var o in obs.Included)
        {
            var shot = shots.Single(s => s.ShotX == o.ShotX);
            errorsFound.Add(o.CorrectedTime - shot.TrueTimes[o.Channel]);
        }
        Assert.True(errorsFound.Count >= 100);
        Assert.True(errorsFound.Count(e => Math.Abs(e) < 0.001) > 0.95 * errorsFound.Count, $"median error {errorsFound.Order().ElementAt(errorsFound.Count / 2) * 1000:0.00} ms");
    }

    [Fact]
    public async Task Invalid_processing_steps_are_skipped_and_reported()
    {
        var (ws, lineId, _) = await ImportedAsync();
        ws.Session!.Execute(Edits.AddStep(lineId, new LowPassStep(StepId.New(), true, 10000, 4, true)));
        var g = ws.Gather(lineId, ws.State.Survey.Line(lineId)!.Gathers[0].Id);
        Assert.Contains(g.Problems, p => p.Contains("Nyquist") && p.Contains("skipped"));
        Assert.Single(PipelineRunner.Validate(ws.State.Interpretation(lineId).Pipeline, 0.000125));
    }

    [Fact]
    public async Task Workflow_status_reflects_progress()
    {
        var ws = NewWorkspace();
        ws.NewProject("Empty");
        var steps = WorkflowStatus.Compute(ws.State, null);
        Assert.Equal(StepState.NotStarted, steps.Single(s => s.Step == WorkflowStep.Import).State);
        var (ws2, lineId, _) = await ImportedAsync();
        await ws2.AutoPickAsync(lineId, new PickerSettings());
        var status = WorkflowStatus.Compute(ws2.State, ws2.Analyze());
        Assert.Equal(StepState.Complete, status.Single(s => s.Step == WorkflowStep.Import).State);
        Assert.Equal(StepState.NeedsAttention, status.Single(s => s.Step == WorkflowStep.PPicks).State); // automatic picks to review
        Assert.Equal(StepState.NotApplicable, status.Single(s => s.Step == WorkflowStep.SPicks).State);
    }

    [Fact]
    public async Task Tomography_result_survives_save_and_load_and_detects_staleness()
    {
        var (ws, lineId, _) = await ImportedAsync();
        await ws.AutoPickAsync(lineId, new PickerSettings());
        var settings = new TomographySettings(CellWidth: 1, CellHeight: 1, MaxDepth: 12, MaxIterations: 3, SecondaryNodes: 3);
        var r = await ws.RunTomographyAsync(lineId, settings);
        Assert.True(r.HasValue, r.Reason);
        Assert.False(ws.Analyze().Line(lineId)!.TomographyStale);
        var path = Path.Combine(_dir, "t.rfsx");
        ws.Save(path);
        var ws2 = NewWorkspace();
        ws2.Open(path);
        var line = ws2.Analyze().Line(lineId)!;
        Assert.NotNull(line.Tomography);
        Assert.False(line.TomographyStale);
        Assert.Equal(r.Value!.Model.Velocity, line.Tomography!.Model.Velocity);
        // Moving a pick makes it stale.
        var key = ws2.State.Interpretation(lineId).Picks.Keys.First();
        ws2.Session!.Execute(Edits.SetManualPick(lineId, key, ws2.State.Interpretation(lineId).Picks[key].Time + 0.001));
        Assert.True(ws2.Analyze().Line(lineId)!.TomographyStale);
    }
}

public class UnitConversionTests
{
    [Fact]
    public void Exact_factors()
    {
        Assert.Equal(3.280839895013123, UnitConversion.MetresToFeet(1), 12);
        Assert.Equal(16.018463373960138, ConversionFactors.KgPerM3PerPcf, 9);
        Assert.Equal(6894.757293168361, ConversionFactors.PascalsPerPsi, 6);
        Assert.Equal(47880.25898033584, ConversionFactors.PascalsPerKsf, 6);
        Assert.Equal(2.0, UnitConversion.KgM3ToGcm3(2000), 12);
    }

    [Theory]
    [InlineData(QuantityKind.Length, UnitSystem.USCustomary)]
    [InlineData(QuantityKind.Velocity, UnitSystem.USCustomary)]
    [InlineData(QuantityKind.Density, UnitSystem.USCustomary)]
    [InlineData(QuantityKind.Modulus, UnitSystem.USCustomary)]
    [InlineData(QuantityKind.Modulus, UnitSystem.SI)]
    [InlineData(QuantityKind.Time, UnitSystem.SI)]
    [InlineData(QuantityKind.Slowness, UnitSystem.USCustomary)]
    public void Display_conversion_round_trips(QuantityKind kind, UnitSystem system)
    {
        foreach (var v in new[] { 0.001, 1.5, 1234.5678, 3.2e8 })
        {
            var display = UnitConversion.ToDisplay(v, kind, system);
            Assert.Equal(v, UnitConversion.FromDisplay(display, kind, system), Math.Abs(v) * 1e-12);
        }
    }

    [Fact]
    public void Formatter_rounds_only_for_display()
    {
        var f = new UnitFormatter(UnitSystem.USCustomary, ModulusDisplayUnit.Ksf);
        Assert.Equal("1640 ft/s", f.Format(500, QuantityKind.Velocity));
        Assert.Equal("125 pcf", f.Format(2002.3, QuantityKind.Density));
        Assert.Equal("ksf", f.Symbol(QuantityKind.Modulus));
        Assert.Equal("12.35 ms", UnitFormatter.SI.Format(0.012345, QuantityKind.Time));
        Assert.Equal("—", UnitFormatter.SI.Format(double.NaN, QuantityKind.Depth));
        Assert.Equal(ModulusDisplayUnit.MPa, UnitConversion.EffectiveModulusUnit(UnitSystem.SI, ModulusDisplayUnit.Psi));
    }
}
