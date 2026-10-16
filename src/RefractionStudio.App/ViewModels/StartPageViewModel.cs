using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefractionStudio.App.Services;
using RefractionStudio.Infrastructure;
using RefractionStudio.Persistence;
using RefractionStudio.Synthetics;

namespace RefractionStudio.App.ViewModels;

/// <summary>Start screen: new, open, import, sample project, recent projects, version, licence and updates.</summary>
public sealed partial class StartPageViewModel(StudioContext ctx, MainWindowViewModel main, UpdateService updates) : ViewModelBase
{
    public ObservableCollection<RecentProject> Recent { get; } = [];

    public string Version => $"{AppInfo.ProductName} {AppInfo.Version}";

    [ObservableProperty] public partial string UpdateStatus { get; private set; } = "";
    [ObservableProperty] public partial bool UpdateAvailable { get; private set; }
    [ObservableProperty] public partial Licensing.ReleaseInfo? AvailableRelease { get; private set; }

    public bool HasRecent => Recent.Count > 0;

    public async Task RefreshAsync()
    {
        Recent.Clear();
        foreach (var r in await ctx.Catalog.RecentAsync()) Recent.Add(r);
        OnPropertyChanged(nameof(HasRecent));
    }

    [RelayCommand]
    private Task NewProject() => main.NewProjectCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task OpenProject() => main.OpenProjectCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task ImportSurvey() => main.ImportDataCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task OpenRecentAsync(RecentProject? recent)
    {
        if (recent is null) return;
        if (!File.Exists(recent.Path))
        {
            var choice = await ctx.Dialogs.AskAsync("Project not found", $"“{recent.Path}” no longer exists. Remove it from the recent list?", MessageKind.Question, "Remove", "Keep");
            if (choice == 0)
            {
                await ctx.Catalog.RemoveRecentAsync(recent.Path);
                await RefreshAsync();
            }
            return;
        }
        await main.OpenPathAsync(recent.Path);
    }

    [RelayCommand]
    private async Task TogglePinAsync(RecentProject? recent)
    {
        if (recent is null) return;
        await ctx.Catalog.SetPinnedAsync(recent.Path, !recent.Pinned);
        await RefreshAsync();
    }

    /// <summary>
    /// Opens the sample project: the copy installed with the application if present, otherwise it is
    /// generated (synthetic data; about 15 seconds) into the user's documents.
    /// </summary>
    [RelayCommand]
    private async Task OpenSampleAsync()
    {
        var installed = Path.Combine(AppContext.BaseDirectory, "Samples", "Refraction-Studio-Sample.rfsx");
        var userCopy = Path.Combine(AppPaths.DefaultProjectFolder, "Refraction-Studio-Sample.rfsx");
        if (!File.Exists(userCopy))
        {
            Directory.CreateDirectory(AppPaths.DefaultProjectFolder);
            if (File.Exists(installed))
            {
                File.Copy(installed, userCopy);
            }
            else
            {
                var ok = await main.RunGuardedAsync("Could not create the sample project", async () =>
                {
                    var work = Path.Combine(ctx.Paths.Root, "SampleWork");
                    await ctx.Busy.RunAsync("Generating the sample project…", async (progress, ct) =>
                    {
                        var ws = await SampleProjectBuilder.BuildAsync(work, new Core.WorkspaceOptions(AppInfo.Version, "Sample generator", Path.Combine(work, "recovery")),
                            new Progress<SampleProgress>(p => progress.Report((p.Fraction, p.Stage))), ct);
                        ws.Save(userCopy);
                        ws.Close();
                        return true;
                    });
                    if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
                });
                if (!ok || !File.Exists(userCopy)) return;
            }
        }
        await main.OpenPathAsync(userCopy);
    }

    [RelayCommand]
    public async Task CheckForUpdatesAsync(bool silent = false)
    {
        UpdateStatus = "Checking for updates…";
        var result = await updates.CheckAsync(ctx.Settings.Current.UpdateChannel, AppInfo.Version);
        ctx.Settings.Update(s => s with { LastUpdateCheck = DateTimeOffset.UtcNow });
        UpdateAvailable = result.Available;
        AvailableRelease = result.Release;
        UpdateStatus = result.Message;
        _manualInstall = result.RequiresManualInstall;
        if (result.Available) main.StatusMessage = result.Message + " See the start page.";
        if (result.Available && !silent) await InstallUpdateAsync();
    }

    private bool _manualInstall;

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (AvailableRelease is not { } release) return;
        if (_manualInstall)
        {
            if (await ctx.Dialogs.AskAsync("Manual installation required", UpdateStatus +
                    (string.IsNullOrWhiteSpace(release.ReleaseNotes) ? "" : "\n\nRelease notes:\n" + release.ReleaseNotes),
                    MessageKind.Information, "Open download", "Close") == 0)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.DownloadUrl) { UseShellExecute = true });
            return;
        }
        var notes = string.IsNullOrWhiteSpace(release.ReleaseNotes) ? "" : "\n\nRelease notes:\n" + release.ReleaseNotes;
        var question = release.Major
            ? $"Version {release.Version} is a major upgrade. Please read the release notes before installing.{notes}"
            : $"Install version {release.Version} now? Refraction Studio will close and restart; save your work first.{notes}";
        if (await ctx.Dialogs.AskAsync("Update available", question, MessageKind.Question, "Download and install", "Later") != 0) return;
        if (!await main.ConfirmCloseProjectAsync()) return;
        await main.RunGuardedAsync("Update failed", async () =>
        {
            var path = await ctx.Busy.RunAsync($"Downloading Refraction Studio {release.Version}…",
                (progress, ct) => updates.DownloadAsync(release, new Progress<double>(f => progress.Report((f, $"Downloading {f:P0}"))), ct));
            if (path is null) return;
            if (OperatingSystem.IsWindows())
            {
                UpdateService.LaunchInstaller(path);
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
            }
            else
            {
                await ctx.Dialogs.ShowAsync("Update downloaded", $"The verified installer was saved to {path}. Run it to update.");
            }
        });
    }
}
