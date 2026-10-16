using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(RefractionStudio.Server.Tests.ServerFixture))]

namespace RefractionStudio.Server.Tests;

/// <summary>
/// One PostgreSQL container (Testcontainers) for the whole test assembly, migrated once, plus ephemeral
/// signing keys and secrets. Tests create their own customers, licences and devices, so they can run in
/// parallel against the same database.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("licensing_tests")
        .WithUsername("licensing")
        .WithPassword("licensing-test-password")
        .Build();

    private LicenseServerFactory? _server;
    private AdminPortalFactory? _admin;

    public (string PrivatePem, string PublicPem) TokenKeys { get; } = Es256.GenerateKeyPair();

    public (string PrivatePem, string PublicPem) ReleaseKeys { get; } = Es256.GenerateKeyPair();

    public byte[] Pepper { get; } = RandomNumberGenerator.GetBytes(32);

    public string StripeSecret { get; } = "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    public string AdminEmail => "admin@quakelogic.test";

    public string AdminPassword => "Correct-Horse-Battery-42";

    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>Licence server with generous rate limits (functional tests).</summary>
    public LicenseServerFactory Server => _server ??= new LicenseServerFactory(this, new Dictionary<string, string?>());

    /// <summary>Admin portal on the same database.</summary>
    public AdminPortalFactory Admin => _admin ??= new AdminPortalFactory(this);

    /// <summary>Configuration shared by both applications.</summary>
    public Dictionary<string, string?> BaseSettings() => new()
    {
        ["ConnectionStrings:Licensing"] = ConnectionString,
        ["Licensing:SigningKeyPem"] = TokenKeys.PrivatePem,
        ["Licensing:ReleaseSigningKeyPem"] = ReleaseKeys.PrivatePem,
        ["Licensing:KeyPepper"] = Convert.ToBase64String(Pepper),
        ["Licensing:AutoMigrate"] = "true",
        ["Stripe:WebhookSecret"] = StripeSecret,
        ["RateLimits:LicensePerMinute"] = "100000",
        ["RateLimits:ActivationPerMinute"] = "100000",
        ["RateLimits:ActivationsPerKeyPerHour"] = "100000",
        ["Workers:EmailPollSeconds"] = "3600",
        ["Admin:BootstrapEmail"] = AdminEmail,
        ["Admin:BootstrapPassword"] = AdminPassword,
        ["Admin:LoginAttemptsPerMinute"] = "100000",
    };

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        if (_admin is not null) await _admin.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public LicensingDbContext CreateDbContext()
        => new(LicensingDbContext.CreateOptions(ConnectionString));
}

/// <summary>The licence server under test (HTTPS base address, "Testing" environment).</summary>
public sealed class LicenseServerFactory(ServerFixture fixture, IDictionary<string, string?> overrides)
    : WebApplicationFactory<RefractionStudio.LicenseServer.Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in fixture.BaseSettings()) builder.UseSetting(key, value);
        foreach (var (key, value) in overrides) builder.UseSetting(key, value);
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/"), AllowAutoRedirect = false });

    /// <summary>The desktop client's own HTTP API implementation, pointed at the test server.</summary>
    public HttpLicenseApi CreateApi() => new(CreateHttpsClient());
}

/// <summary>The admin portal under test.</summary>
public sealed class AdminPortalFactory(ServerFixture fixture) : WebApplicationFactory<RefractionStudio.AdminPortal.Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in fixture.BaseSettings()) builder.UseSetting(key, value);
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/"), AllowAutoRedirect = false, HandleCookies = true });
}
