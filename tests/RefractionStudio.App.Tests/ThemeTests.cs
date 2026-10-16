using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using RefractionStudio.App.Services;
using RefractionStudio.App.ViewModels;
using RefractionStudio.Infrastructure;

namespace RefractionStudio.App.Tests;

public sealed class ThemeTests
{
    [AvaloniaFact]
    public async Task Dark_theme_renders_pages_without_problems()
    {
        var settings = RefractionStudio.App.App.Services.GetRequiredService<SettingsService>();
        var (window, vm) = UiHarness.CreateWindow();
        try
        {
            settings.Update(s => s with { Theme = ThemePreference.Dark });
            await UiHarness.OpenSampleAsync(vm);
            CollectingLogSink.Instance.Clear();
            foreach (var page in new[] { typeof(WaveformsPageViewModel), typeof(VelocityModelPageViewModel), typeof(QcPageViewModel), typeof(EngineeringPageViewModel) })
            {
                vm.SelectedNav = vm.NavItems.First(n => n.Page.GetType() == page);
                UiHarness.Pump();
                await Task.Delay(50);
                UiHarness.Screenshot(window, "dark-" + page.Name.Replace("PageViewModel", "", StringComparison.Ordinal));
            }
            Assert.Empty(CollectingLogSink.Instance.Problems);
        }
        finally
        {
            settings.Update(s => s with { Theme = ThemePreference.Light });
            vm.Shutdown();
            window.Close();
        }
    }
}
