using System.Text.RegularExpressions;

namespace RefractionStudio.Server.Tests;

public sealed partial class AdminPortalTests(ServerFixture fx)
{
    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();

    [GeneratedRegex("id=\"license-key\">([A-Z0-9-]+)<")]
    private static partial Regex RevealedKey();

    private static async Task<string> TokenAsync(HttpClient http, string path)
    {
        var html = await http.GetStringAsync(path);
        var match = AntiforgeryToken().Match(html);
        Assert.True(match.Success, $"No antiforgery token on {path}");
        return match.Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient http, string path, IDictionary<string, string> fields)
    {
        var token = await TokenAsync(http, path);
        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        using var content = new FormUrlEncodedContent(form);
        return await http.PostAsync(path, content);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient http, string email, string password)
        => PostFormAsync(http, "Account/Login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = password });

    [Theory]
    [InlineData("/")]
    [InlineData("/Licenses")]
    [InlineData("/Licenses/Create")]
    [InlineData("/Audit")]
    [InlineData("/Admins")]
    public async Task Unauthenticated_requests_are_redirected_to_the_login_page(string path)
    {
        using var http = fx.Admin.CreateHttpsClient();

        using var response = await http.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login", response.Headers.Location!.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stylesheet_and_health_are_served_without_login()
    {
        using var http = fx.Admin.CreateHttpsClient();

        using var css = await http.GetAsync("css/site.css");
        using var health = await http.GetAsync("health");
        using var login = await http.GetAsync("Account/Login");

        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal("DENY", login.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", login.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Posts_without_an_antiforgery_token_are_rejected()
    {
        using var http = fx.Admin.CreateHttpsClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = fx.AdminEmail, ["Password"] = fx.AdminPassword });

        using var response = await http.PostAsync("Account/Login", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_then_create_licence_shows_the_key_once_and_the_key_activates()
    {
        using var http = fx.Admin.CreateHttpsClient();
        using var login = await LoginAsync(http, fx.AdminEmail, fx.AdminPassword);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location!.OriginalString);

        var dashboard = await http.GetStringAsync("/");
        Assert.Contains("Active licences", dashboard, StringComparison.Ordinal);

        var email = $"portal-{Guid.NewGuid():N}@example.com";
        using var created = await PostFormAsync(http, "Licenses/Create", new Dictionary<string, string>
        {
            ["Email"] = email,
            ["CustomerName"] = "Alex Field",
            ["Company"] = "Portal Surveys",
            ["Product"] = TestData.Product,
            ["Seats"] = "2",
            ["MaintenanceUntil"] = DateTime.UtcNow.AddYears(1).ToString("yyyy-MM-dd"),
            ["MaxTransfers"] = "3",
            ["OfflineDays"] = "30",
            ["SendKeyEmail"] = "true",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Contains("no-store", created.Headers.CacheControl?.ToString() ?? "", StringComparison.Ordinal);
        var html = await created.Content.ReadAsStringAsync();
        var match = RevealedKey().Match(html);
        Assert.True(match.Success, "The created key is displayed");
        var key = match.Groups[1].Value;
        Assert.Equal(key, LicenseKeys.Normalize(key));

        Guid licenseId;
        await using (var db = fx.CreateDbContext())
        {
            var license = await db.Licenses.Include(l => l.Customer).SingleAsync(l => l.Customer!.Email == email);
            licenseId = license.Id;
            Assert.Equal(2, license.SeatCount);
            Assert.Equal(LicenseKeys.Hash(key, fx.Pepper), license.LicenseKeyHash);
            var admin = await db.AdminUsers.SingleAsync(u => u.Email == fx.AdminEmail);
            Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "license.create" && a.EntityId == license.Id.ToString() && a.AdminUserId == admin.Id));
            Assert.True(await db.EmailOutbox.AnyAsync(m => m.LicenseId == license.Id && m.Template == "license_key"));
        }

        var details = await http.GetStringAsync($"Licenses/Details/{licenseId}");
        Assert.DoesNotContain(key, details, StringComparison.Ordinal);
        Assert.Contains("…" + key[^4..], details, StringComparison.Ordinal);

        var activation = await fx.Server.CreateApi().ActivateAsync(new ActivationRequest(key, email, TestData.Product, TestData.AppVersion, TestData.NewDevice()), CancellationToken.None);
        Assert.NotNull(activation.Success);
    }

    [Fact]
    public async Task Admin_actions_on_a_licence_are_audited_with_the_admin_id()
    {
        var (id, _) = await TestData.CreateLicenseAsync(fx.Server.Services);
        using var http = fx.Admin.CreateHttpsClient();
        using var login = await LoginAsync(http, fx.AdminEmail, fx.AdminPassword);

        using var response = await PostFormAsync(http, $"Licenses/Details/{id}?handler=Suspend", new Dictionary<string, string> { ["Reason"] = "Card dispute" });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var db = fx.CreateDbContext();
        Assert.Equal(LicenseRecordStatus.Suspended, (await db.Licenses.SingleAsync(l => l.Id == id)).Status);
        var admin = await db.AdminUsers.SingleAsync(u => u.Email == fx.AdminEmail);
        Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "license.suspend" && a.EntityId == id.ToString() && a.AdminUserId == admin.Id));
    }

    [Fact]
    public async Task Five_failed_logins_lock_the_account_for_15_minutes()
    {
        var email = $"support-{Guid.NewGuid():N}@quakelogic.test";
        const string password = "Another-Strong-Pass-77";
        await TestData.WithServiceAsync<AdminUserService>(fx.Admin.Services, s => s.CreateAsync(email, "Support", password, AdminRoles.Support, Actor.System, CancellationToken.None));
        using var http = fx.Admin.CreateHttpsClient();

        for (var i = 0; i < 5; i++)
        {
            using var failed = await LoginAsync(http, email, "wrong-password-" + i);
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        }
        using var correct = await LoginAsync(http, email, password);

        Assert.Equal(HttpStatusCode.OK, correct.StatusCode);
        Assert.Contains("locked", await correct.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var db = fx.CreateDbContext();
        var user = await db.AdminUsers.SingleAsync(u => u.Email == email);
        Assert.NotNull(user.LockedUntil);
        Assert.InRange(user.LockedUntil.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "admin.locked_out" && a.AdminUserId == user.Id));
    }

    [Fact]
    public async Task Viewers_cannot_change_data()
    {
        var email = $"viewer-{Guid.NewGuid():N}@quakelogic.test";
        const string password = "Viewer-Strong-Pass-99";
        await TestData.WithServiceAsync<AdminUserService>(fx.Admin.Services, s => s.CreateAsync(email, "Viewer", password, AdminRoles.Viewer, Actor.System, CancellationToken.None));
        var (id, _) = await TestData.CreateLicenseAsync(fx.Server.Services);
        using var http = fx.Admin.CreateHttpsClient();
        using var login = await LoginAsync(http, email, password);

        using var revoke = await PostFormAsync(http, $"Licenses/Details/{id}?handler=Revoke", new Dictionary<string, string> { ["Reason"] = "Not allowed" });
        using var admins = await http.GetAsync("Admins");

        Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", revoke.Headers.Location!.PathAndQuery, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, admins.StatusCode);
        await using var db = fx.CreateDbContext();
        Assert.Equal(LicenseRecordStatus.Active, (await db.Licenses.SingleAsync(l => l.Id == id)).Status);
    }
}
