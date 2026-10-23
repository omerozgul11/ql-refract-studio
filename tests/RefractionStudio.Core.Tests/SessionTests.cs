using RefractionStudio.Core.Session;
using RefractionStudio.Domain;

namespace RefractionStudio.Core.Tests;

public class SessionTests
{
    private static ProjectSession NewSession() => new(ProjectState.CreateNew("Test", "1.0.0", DateTimeOffset.UnixEpoch), [], null, "tester", "1.0.0");

    private static ProjectEdit Rename(string name) => Edits.SetMetadata(new ProjectMetadata(name));

    [Fact]
    public void Execute_undo_redo_and_history()
    {
        var s = NewSession();
        var events = 0;
        s.StateChanged += (_, _) => events++;
        s.Execute(Rename("A"));
        s.Execute(Rename("B"));
        Assert.Equal("B", s.State.Metadata.Name);
        Assert.True(s.CanUndo);
        Assert.Equal("Edit project information", s.UndoDescription);
        s.Undo();
        Assert.Equal("A", s.State.Metadata.Name);
        s.Redo();
        Assert.Equal("B", s.State.Metadata.Name);
        s.Undo();
        s.Undo();
        Assert.Equal("Test", s.State.Metadata.Name);
        Assert.False(s.CanUndo);
        Assert.Equal(6, events);
        Assert.Equal(["project.metadata", "project.metadata", "undo", "redo", "undo", "undo"], s.History.Select(h => h.Operation));
        Assert.All(s.History, h => Assert.Equal("tester", h.User));
    }

    [Fact]
    public void A_new_edit_clears_redo_and_no_op_edits_are_ignored()
    {
        var s = NewSession();
        s.Execute(Rename("A"));
        s.Undo();
        Assert.True(s.CanRedo);
        s.Execute(Rename("C"));
        Assert.False(s.CanRedo);
        var revision = s.Revision;
        s.Execute(ProjectEdit.Create(HistoryKind.Project, "noop", "No-op", st => st));
        Assert.Equal(revision, s.Revision);
        Assert.DoesNotContain(s.History, h => h.Operation == "noop");
    }

    [Fact]
    public void Gesture_is_one_undo_step_and_one_history_entry()
    {
        var s = NewSession();
        var ended = 0;
        s.GestureEnded += (_, _) => ended++;
        using (s.BeginGesture("Drag"))
        {
            for (var i = 0; i < 10; i++) s.Execute(Rename("drag " + i));
            Assert.False(s.CanUndo); // not available mid-gesture
        }
        Assert.Equal(1, ended); // the user interface refreshes Undo here
        Assert.True(s.CanUndo);
        Assert.Equal("drag 9", s.State.Metadata.Name);
        Assert.Single(s.History);
        s.Undo();
        Assert.Equal("Test", s.State.Metadata.Name);
    }

    [Fact]
    public void Dirty_tracking()
    {
        var s = NewSession();
        Assert.False(s.IsDirty);
        s.Execute(Rename("A"));
        Assert.True(s.IsDirty);
        s.MarkSaved();
        Assert.False(s.IsDirty);
        s.Undo();
        Assert.True(s.IsDirty);
    }

    [Fact]
    public void Undo_history_is_bounded()
    {
        var s = NewSession();
        for (var i = 0; i < ProjectSession.UndoLimit + 20; i++) s.Execute(Rename("n" + i));
        var count = 0;
        while (s.CanUndo) { s.Undo(); count++; }
        Assert.Equal(ProjectSession.UndoLimit, count);
    }

    [Fact]
    public void Snapshots_restore_content_and_are_undoable()
    {
        var s = NewSession();
        s.Execute(Rename("Before"));
        var id = s.CreateSnapshot("v1", "baseline");
        s.Execute(Rename("After"));
        s.RestoreSnapshot(id);
        Assert.Equal("Before", s.State.Metadata.Name);
        Assert.Single(s.State.Snapshots);
        s.Undo();
        Assert.Equal("After", s.State.Metadata.Name);
    }
}

public class EditTests
{
    private static (ProjectSession Session, LineId Line, PickKey Key) WithLine()
    {
        var state = ProjectState.CreateNew("T", "1", DateTimeOffset.UnixEpoch);
        var spread = Spread.Regular("S", 4, 0, 2);
        var gather = new ShotGather(GatherId.New(), "G1", -1, null, 0, spread.Id, CombineMode.Single, [new GatherMember(RecordId.New(), 1, SourceKind.Vertical)]);
        var line = new SurveyLine(LineId.New(), "L", WaveType.P, Topography.Flat, [spread], [gather]);
        state = state with { Survey = state.Survey with { Lines = [line] } };
        return (new ProjectSession(state, [], null, "u", "1"), line.Id, new PickKey(gather.Id, 2));
    }

    [Fact]
    public void Manual_pick_keeps_the_automatic_suggestion_and_can_restore_it()
    {
        var (s, line, key) = WithLine();
        var confidence = new PickConfidence(0.8, ConfidenceLevel.High, [], []);
        s.Execute(Edits.ApplyAutomaticPicks(line, new Dictionary<PickKey, Analysis.Picking.AutoPick> { [key] = new(2, 0.010, confidence, 12, "AIC") }, new PickerSettings()));
        Assert.Equal(ArrivalOrigin.Automatic, s.State.Interpretation(line).PickOf(key)!.Origin);
        s.Execute(Edits.SetManualPick(line, key, 0.0123));
        var manual = s.State.Interpretation(line).PickOf(key)!;
        Assert.Equal(ArrivalOrigin.Manual, manual.Origin);
        Assert.Equal(0.0123, manual.Time);
        Assert.Equal(0.010, manual.Suggestion!.Time);
        // Re-running automatic picking never overwrites a manual pick.
        s.Execute(Edits.ApplyAutomaticPicks(line, new Dictionary<PickKey, Analysis.Picking.AutoPick> { [key] = new(2, 0.011, confidence, 12, "AIC") }, new PickerSettings()));
        Assert.Equal(0.0123, s.State.Interpretation(line).PickOf(key)!.Time);
        Assert.Equal(0.011, s.State.Interpretation(line).PickOf(key)!.Suggestion!.Time);
        s.Execute(Edits.RestoreAutomaticPick(line, key));
        Assert.Equal(0.011, s.State.Interpretation(line).PickOf(key)!.Time);
        Assert.Equal(ArrivalOrigin.Automatic, s.State.Interpretation(line).PickOf(key)!.Origin);
        s.Execute(Edits.AcceptPicks(line, [key]));
        Assert.Equal(ArrivalOrigin.AcceptedSuggestion, s.State.Interpretation(line).PickOf(key)!.Origin);
        s.Undo(); s.Undo(); s.Undo();
        Assert.Equal(0.0123, s.State.Interpretation(line).PickOf(key)!.Time);
    }

    [Fact]
    public void Pipeline_steps_can_be_added_moved_toggled_and_removed()
    {
        var (s, line, _) = WithLine();
        var bp = new BandPassStep(StepId.New(), true, 10, 100, 4, true);
        var agc = new AgcStep(StepId.New(), true, 0.05);
        s.Execute(Edits.AddStep(line, bp));
        s.Execute(Edits.AddStep(line, agc));
        var steps = s.State.Interpretation(line).Pipeline.Steps;
        Assert.Equal(3, steps.Length); // recommended demean + 2
        s.Execute(Edits.MoveStep(line, agc.Id, 0, agc.DisplayName));
        Assert.IsType<AgcStep>(s.State.Interpretation(line).Pipeline.Steps[0]);
        s.Execute(Edits.SetStepEnabled(line, bp, false));
        Assert.DoesNotContain(s.State.Interpretation(line).Pipeline.EnabledSteps, x => x.Id == bp.Id);
        s.Execute(Edits.RemoveStep(line, agc.Id, agc.DisplayName));
        Assert.Equal(2, s.State.Interpretation(line).Pipeline.Steps.Length);
        Assert.Contains(s.History, h => h.Operation == "processing.add" && h.Parameters["lowHz"] == "10");
        while (s.CanUndo) s.Undo();
        Assert.Single(s.State.Interpretation(line).Pipeline.Steps);
    }

    [Fact]
    public void Trace_exclusion_and_layer_overrides_are_undoable()
    {
        var (s, line, key) = WithLine();
        s.Execute(Edits.SetTraceAdjustment(line, key, new TraceAdjustment(Excluded: true, Note: "noisy"), "Exclude"));
        Assert.True(s.State.Interpretation(line).AdjustmentOf(key).Excluded);
        s.Execute(Edits.SetPickLayer(line, key, 2));
        Assert.Equal(2, s.State.Interpretation(line).Segments.Overrides[key]);
        s.Undo();
        s.Undo();
        Assert.False(s.State.Interpretation(line).AdjustmentOf(key).Excluded);
        Assert.Empty(s.State.Interpretation(line).Segments.Overrides);
    }

    [Fact]
    public void Scenarios_share_raw_data_and_switching_is_undoable()
    {
        var (s, line, key) = WithLine();
        s.Execute(Edits.SetManualPick(line, key, 0.01));
        s.Execute(Edits.DuplicateScenario("B", DateTimeOffset.UnixEpoch));
        Assert.Equal(2, s.State.Scenarios.Length);
        s.Execute(Edits.SetManualPick(line, key, 0.02));
        var a = s.State.Scenarios[0];
        Assert.Equal(0.01, a.Lines[line].Picks[key].Time);
        Assert.Equal(0.02, s.State.Interpretation(line).PickOf(key)!.Time);
        Assert.Same(s.State.Survey, s.State.Survey); // one survey for all scenarios
        s.Execute(Edits.SwitchScenario(a.Id, a.Name));
        Assert.Equal(0.01, s.State.Interpretation(line).PickOf(key)!.Time);
        s.Undo();
        Assert.Equal(0.02, s.State.Interpretation(line).PickOf(key)!.Time);
    }
}
