using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using RefractionStudio.App.Services;
using RefractionStudio.App.ViewModels;
using RefractionStudio.Licensing;

namespace RefractionStudio.App.Tests;

/// <summary>
/// End-to-end checks of the desktop user interface in the headless platform: every page renders with
/// the sample project without binding errors, the licence gate works, and core commands behave.
/// Screenshots are written to the test output folder (screenshots/) for visual review.
/// </summary>
public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void Start_page_is_shown_without_a_project()
    {
        var (window, vm) = UiHarness.CreateWindow();
        try
        {
            Assert.False(vm.HasProject);
            Assert.Null(vm.SelectedNav);
            Assert.Contains(UiHarness.Find<TextBlock>(window), t => t.Text == "Explore the sample");
            UiHarness.Screenshot(window, "00-start");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Every_page_renders_the_sample_project_without_binding_errors()
    {
        var (window, vm) = UiHarness.CreateWindow();
        try
        {
            await UiHarness.OpenSampleAsync(vm);
            CollectingLogSink.Instance.Clear();
            var index = 1;
            foreach (var nav in vm.NavItems.ToList())
            {
                vm.SelectedNav = nav;
                UiHarness.Pump();
                await Task.Delay(50);
                UiHarness.Pump();
                Assert.DoesNotContain(UiHarness.Find<TextBlock>(window), t => t.Text?.StartsWith("View not found", StringComparison.Ordinal) == true);
                UiHarness.Screenshot(window, $"{index++:00}-{string.Concat(nav.Title.Where(char.IsLetterOrDigit))}");
            }
            var problems = CollectingLogSink.Instance.Problems;
            Assert.True(problems.Count == 0, "Avalonia reported problems:\n" + string.Join("\n", problems.Distinct().Take(40)));

            // Commands bound through $parent in item templates resolve once the views are attached.
            vm.SelectedNav = vm.NavItems.First(n => n.Page is ProjectPageViewModel);
            UiHarness.Pump();
            var activate = UiHarness.Find<Button>(window).Where(b => b.Content as string == "Activate").ToList();
            Assert.NotEmpty(activate);
            Assert.All(activate, b => Assert.NotNull(b.Command));

            vm.SelectedNav = vm.NavItems.First(n => n.Page is PicksPageViewModel);
            UiHarness.Pump();
            var picks = (PicksPageViewModel)vm.SelectedNav.Page;
            var viewer = UiHarness.Find<RefractionStudio.App.Views.GatherViewerView>(window).Single();
            picks.SnapToOnset = true;
            UiHarness.Pump();
            Assert.True(viewer.SnapToOnset);
        }
        finally
        {
            vm.Shutdown();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Overlays_open_render_and_close()
    {
        var (window, vm) = UiHarness.CreateWindow();
        try
        {
            CollectingLogSink.Instance.Clear();
            vm.OpenSettingsCommand.Execute(null);
            foreach (var section in vm.Settings.Sections)
            {
                vm.Settings.Section = section;
                UiHarness.Pump();
                UiHarness.Screenshot(window, "settings-" + string.Concat(section.Where(char.IsLetterOrDigit)));
            }
            vm.CloseOverlaysCommand.Execute(null);
            Assert.False(vm.IsSettingsOpen);

            vm.OpenHelpCommand.Execute(null);
            UiHarness.Pump();
            UiHarness.Screenshot(window, "help-topic");
            vm.Help.OpenGuideCommand.Execute(null);
            UiHarness.Pump();
            UiHarness.Screenshot(window, "help-guide");
            vm.CloseOverlaysCommand.Execute(null);

            vm.OpenLicenseCommand.Execute(null);
            UiHarness.Pump();
            UiHarness.Screenshot(window, "license");
            vm.CloseOverlaysCommand.Execute(null);
            Assert.False(vm.IsLicenseOpen);

            vm.OpenPaletteCommand.Execute(null);
            vm.PaletteQuery = "report";
            UiHarness.Pump();
            Assert.Contains(vm.PaletteResults, c => c.Title == "Generate report");
            Assert.DoesNotContain(vm.PaletteResults, c => c.Title == "New project");
            vm.PaletteQuery = "Go to S picks";
            UiHarness.Pump();
            Assert.Equal("Go to S picks", vm.PaletteResults[0].Title);
            UiHarness.Screenshot(window, "palette");
            vm.CloseOverlaysCommand.Execute(null);
            Assert.False(vm.IsPaletteOpen);

            var problems = CollectingLogSink.Instance.Problems;
            Assert.True(problems.Count == 0, "Avalonia reported problems:\n" + string.Join("\n", problems.Distinct().Take(40)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Editing_requires_a_licence_and_is_undoable()
    {
        var (window, vm) = UiHarness.CreateWindow();
        var license = RefractionStudio.App.App.Services.GetRequiredService<LicenseService>();
        try
        {
            await UiHarness.OpenSampleAsync(vm);
            if (license.Status.State == LicenseState.NotActivated)
            {
                // Viewing works without a licence, editing does not.
                Assert.False(license.Status.CanEdit);
                await vm.LicensePanel.StartTrialCommand.ExecuteAsync(null);
                await UiHarness.WaitUntilAsync(() => license.Status.CanEdit, 10, "the trial to start");
            }
            Assert.Equal(LicenseState.TrialActive, license.Status.State);

            vm.SelectedNav = vm.NavItems.First(n => n.Page is ProjectPageViewModel);
            var page = (ProjectPageViewModel)vm.SelectedNav.Page;
            var original = page.Name;
            page.Name = original + " (edited)";
            page.ApplyMetadataCommand.Execute(null);
            UiHarness.Pump();
            Assert.Equal(original + " (edited)", vm.ProjectTitle);
            Assert.True(vm.IsDirty);
            Assert.True(vm.UndoCommand.CanExecute(null));

            vm.UndoCommand.Execute(null);
            UiHarness.Pump();
            Assert.Equal(original, vm.ProjectTitle);
            vm.RedoCommand.Execute(null);
            UiHarness.Pump();
            Assert.Equal(original + " (edited)", vm.ProjectTitle);
        }
        finally
        {
            vm.Shutdown();
            window.Close();
        }
    }
}
