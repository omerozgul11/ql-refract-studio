using Avalonia;
using Avalonia.Headless;
using Microsoft.Extensions.DependencyInjection;
using RefractionStudio.App;
using RefractionStudio.App.Services;
using RefractionStudio.Licensing;

[assembly: AvaloniaTestApplication(typeof(RefractionStudio.App.Tests.TestApp))]

namespace RefractionStudio.App.Tests;

/// <summary>
/// Starts the real application in the headless platform with Skia rendering (so frames can be
/// captured), an isolated data folder and an in-process licence server.
/// </summary>
public static class TestApp
{
    public static string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "rs-ui-tests-" + Environment.ProcessId);

    public static FakeLicenseApi LicenseApi { get; } = new();

    public static AppBuilder BuildAvaloniaApp()
    {
        Directory.CreateDirectory(DataDirectory);
        Environment.SetEnvironmentVariable(RefractionStudio.App.App.DataDirectoryVariable, DataDirectory);
        RefractionStudio.App.App.ConfigureServices = services =>
        {
            var device = new DeviceFingerprint("ui-tests", new FixedIdentifierSource(new MachineIdentifier("machine-guid", "UI-TESTS"))).Compute(false);
            services.AddSingleton(_ => new LicenseService(new LicenseManager(
                new LicensingOptions("refraction-studio", "Refraction Studio", "1.0.0", [FakeLicenseApi.Keys.PublicPem]),
                LicenseApi, new MemoryLicenseStore(), device)));
        };
        return AppBuilder.Configure<RefractionStudio.App.App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

/// <summary>In-process licence server issuing tokens signed with a key generated for the test run.</summary>
public sealed class FakeLicenseApi : ILicenseApi
{
    public static readonly (string PrivatePem, string PublicPem) Keys = Es256.GenerateKeyPair();

    private string Sign(LicenseClaims claims) => LicenseToken.Issue(claims, Keys.PrivatePem, Es256.KeyId(Keys.PublicPem));

    public Task<ApiResult> StartTrialAsync(TrialRequest request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var end = now.AddDays(14);
        var token = Sign(new LicenseClaims(Guid.NewGuid().ToString("N"), LicenseKind.Trial, "trial-ui", "refraction-studio", "Professional", null, null, null,
            request.Device.Fingerprint, now, end, end, now.AddDays(3), now, end, [], 1));
        return Task.FromResult(ApiResult.Ok(new TokenResponse(token, "Trial started.")));
    }

    public Task<ApiResult> ActivateAsync(ActivationRequest request, CancellationToken ct) => Task.FromResult(ApiResult.Fail(new ErrorResponse("invalid_key", "Unknown licence key.")));

    public Task<ApiResult> ValidateAsync(ValidationRequest request, CancellationToken ct) => Task.FromResult(ApiResult.Offline("offline in tests"));

    public Task<ApiResult> DeactivateAsync(DeactivationRequest request, CancellationToken ct) => Task.FromResult(ApiResult.Ok(new TokenResponse("", "Deactivated.")));
}
