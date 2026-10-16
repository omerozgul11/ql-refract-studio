using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using RefractionStudio.App.Controls;
using RefractionStudio.App.Services;
using RefractionStudio.App.ViewModels;
using RefractionStudio.App.Views;
using RefractionStudio.Domain;
using RefractionStudio.Licensing;

namespace RefractionStudio.App.Tests;

/// <summary>Mouse and keyboard interaction and the import workflow, driven like a user would.</summary>
public sealed class InteractionTests
{
    private static async Task EnsureTrialAsync(MainWindowViewModel vm)
    {
        var license = RefractionStudio.App.App.Services.GetRequiredService<LicenseService>();
        if (license.Status.CanEdit) return;
        await vm.LicensePanel.StartTrialCommand.ExecuteAsync(null);
        await UiHarness.WaitUntilAsync(() => license.Status.CanEdit, 10, "the trial to start");
    }

    [AvaloniaFact]
    public async Task Clicking_a_trace_places_a_manual_pick_and_arrow_keys_nudge_it()
    {
        var (window, vm) = UiHarness.CreateWindow();
        try
        {
            await EnsureTrialAsync(vm);
            await UiHarness.OpenSampleAsync(vm);
            vm.SelectedNav = vm.NavItems.First(n => n.Page is PicksPageViewModel p && p.Step == Core.Services.WorkflowStep.PPicks);
            UiHarness.Pump();
            await Task.Delay(50);
            UiHarness.Pump();

            var viewerView = UiHarness.Find<GatherViewerView>(window).Single();
            var plot = UiHarness.Find<PlotView>(viewerView).Single();
            var viewer = (GatherViewerViewModel)viewerView.DataContext!;
            var model = ((GatherPlot)plot.Plot!).Model;
            var vp = plot.CurrentViewport!;
            var trace = model.Traces.First(t => t.Channel == 10);
            const double clickTime = 0.150;
            var local = new Point(vp.X(trace.X), vp.Y(clickTime));
            var point = plot.TranslatePoint(local, window)!.Value;

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            UiHarness.Pump();

            var key = new PickKey(viewer.Gather!.Id, 10);
            var line = viewer.Line!;
            var pick = RefractionStudio.App.App.Services.GetRequiredService<Core.ProjectWorkspace>().State.Interpretation(line.Id).PickOf(key);
            Assert.NotNull(pick);
            Assert.True(pick!.IsManual);
            var pixelTime = Math.Abs(vp.DataY((float)local.Y + 1) - vp.DataY((float)local.Y));
            Assert.InRange(pick.Time, clickTime - 2 * pixelTime - 0.001, clickTime + 2 * pixelTime + 0.001);
            Assert.Equal(10, viewer.SelectedChannel);

            var dt = viewer.Processed!.Traces.First(t => t.Channel == 10).SampleInterval;
            var before = pick.Time;
            plot.Focus();
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            UiHarness.Pump();
            var after = RefractionStudio.App.App.Services.GetRequiredService<Core.ProjectWorkspace>().State.Interpretation(line.Id).PickOf(key)!.Time;
            Assert.Equal(before + dt, after, 9);

            vm.UndoCommand.Execute(null);
            vm.UndoCommand.Execute(null);
            UiHarness.Pump();
            var restored = RefractionStudio.App.App.Services.GetRequiredService<Core.ProjectWorkspace>().State.Interpretation(line.Id).PickOf(key);
            Assert.False(restored?.IsManual == true && Math.Abs(restored.Time - clickTime) < 0.01, "Undo did not remove the manual pick.");
        }
        finally
        {
            vm.Shutdown();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Seg2_files_are_inspected_and_imported_as_a_new_line()
    {
        var (window, vm) = UiHarness.CreateWindow();
        try
        {
            await EnsureTrialAsync(vm);
            await vm.NewProjectCommand.ExecuteAsync(null);
            vm.SelectedNav = vm.NavItems.First(n => n.Page is ImportPageViewModel);
            var import = vm.Import;
            var folder = Path.Combine(TestApp.DataDirectory, "import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            // Shot records written by the application's own SEG-2 writer from a synthetic survey.
            var files = SyntheticFiles.Write(folder, 4);
            await import.AddPathsAsync(files);
            await UiHarness.WaitUntilAsync(() => import.HasFiles, 30, "the files to be inspected");
            Assert.Equal(4, import.Files.Count);
            Assert.All(import.Files, f => Assert.True(f.Readable));
            Assert.Equal(0, import.ErrorCount);
            UiHarness.Screenshot(window, "import-inspected");

            import.LineName = "Test line";
            await import.ImportCommand.ExecuteAsync(null);
            UiHarness.Pump();
            var state = RefractionStudio.App.App.Services.GetRequiredService<Core.ProjectWorkspace>().State;
            var line = Assert.Single(state.Survey.Lines);
            Assert.Equal("Test line", line.Name);
            Assert.Equal(4, line.Gathers.Length);
            Assert.Contains("Imported 4 record(s)", import.ResultText, StringComparison.Ordinal);
            UiHarness.Screenshot(window, "import-done");

            import.GoToGeometryCommand.Execute(null);
            UiHarness.Pump();
            Assert.IsType<GeometryPageViewModel>(vm.SelectedNav!.Page);
        }
        finally
        {
            vm.Shutdown();
            window.Close();
        }
    }
}
