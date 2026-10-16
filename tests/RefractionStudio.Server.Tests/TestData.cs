using System.Security.Cryptography;

namespace RefractionStudio.Server.Tests;

/// <summary>Builders for test devices, customers and licences.</summary>
public static class TestData
{
    public const string Product = LicensingDbContext.RefractionStudioProduct;
    public const string AppVersion = "1.0.0";

    public static string RandomHash() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static DeviceInfo NewDevice(int components = 3, string? machineName = "SURVEY-LAPTOP")
        => new(RandomHash(), [.. Enumerable.Range(0, components).Select(_ => RandomHash())], machineName, "Microsoft Windows 10.0.22631 X64");

    /// <summary>The same computer after a hardware change: new fingerprint, one component replaced.</summary>
    public static DeviceInfo WithOneComponentChanged(DeviceInfo device)
        => device with { Fingerprint = RandomHash(), ComponentHashes = [.. device.ComponentHashes.Take(device.ComponentHashes.Count - 1), RandomHash()] };

    public static Actor TestAdmin { get; } = new("admin:tests@quakelogic.test", Guid.NewGuid(), null);

    /// <summary>Creates a customer and a licence through <see cref="LicenseService"/>; returns the licence id and its key.</summary>
    public static async Task<(Guid Id, string Key)> CreateLicenseAsync(IServiceProvider services, int seats = 1, DateTimeOffset? expiresAt = null,
        int maxTransfers = 3, string product = Product, int offlineDays = 30)
    {
        await using var scope = services.CreateAsyncScope();
        var customers = scope.ServiceProvider.GetRequiredService<CustomerService>();
        var db = scope.ServiceProvider.GetRequiredService<LicensingDbContext>();
        var customer = await customers.FindOrCreateAsync($"customer-{Guid.NewGuid():N}@example.com", "Jane Surveyor", "Geo Consulting Ltd", TestAdmin, CancellationToken.None);
        await db.SaveChangesAsync();
        var created = await scope.ServiceProvider.GetRequiredService<LicenseService>().CreateAsync(
            new CreateLicenseCommand(customer.Id, product, "Professional", seats, expiresAt, DateTimeOffset.UtcNow.AddYears(1),
                MaxTransfersPer30Days: maxTransfers, OfflineDays: offlineDays),
            TestAdmin, sendKeyEmail: true, CancellationToken.None);
        return (created.License.Id, created.Key);
    }

    public static async Task WithServiceAsync<T>(IServiceProvider services, Func<T, Task> action) where T : notnull
    {
        await using var scope = services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<T>());
    }

    /// <summary>Verifies a token with the test signing key and returns its claims.</summary>
    public static LicenseClaims Verify(ServerFixture fx, string token)
    {
        var (claims, error) = LicenseToken.Verify(token, [fx.TokenKeys.PublicPem]);
        Assert.Equal(TokenError.None, error);
        return claims!;
    }
}
