using RefractionStudio.App.Services;
using RefractionStudio.Infrastructure;
using RefractionStudio.Licensing;

namespace RefractionStudio.App.Tests;

/// <summary>The full version (licensing not enforced) edits without a trial, licence or server.</summary>
public sealed class LicensingModeTests
{
    [Fact]
    public void Full_version_permits_editing_without_a_licence()
    {
        var device = new DeviceFingerprint("ui-tests", new FixedIdentifierSource(new MachineIdentifier("machine-guid", "FULL-VERSION"))).Compute(false);
        var manager = new LicenseManager(new LicensingOptions("refraction-studio", "Refraction Studio", "1.0.0", [FakeLicenseApi.Keys.PublicPem]),
            TestApp.LicenseApi, new MemoryLicenseStore(), device);
        Assert.False(manager.Evaluate().CanEdit);

        var service = new LicenseService(manager, enforced: false);
        Assert.False(service.IsEnforced);
        Assert.True(service.Status.CanEdit);
        service.Reevaluate();
        Assert.True(service.Status.CanEdit);
    }

    [Fact]
    public void Shipping_build_is_the_full_version() => Assert.False(AppInfo.LicensingEnforced);
}
