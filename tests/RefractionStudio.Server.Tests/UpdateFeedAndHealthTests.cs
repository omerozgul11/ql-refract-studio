namespace RefractionStudio.Server.Tests;

public sealed class UpdateFeedAndHealthTests(ServerFixture fx)
{
    private const string FeedProduct = "feed-test-studio";

    private async Task SeedAsync()
    {
        await using var db = fx.CreateDbContext();
        if (await db.LicenseProducts.AnyAsync(p => p.Code == FeedProduct)) return;
        db.LicenseProducts.Add(new LicenseProduct { Code = FeedProduct, Name = "Feed Test Studio", KeyPrefix = "FEED" });
        await db.SaveChangesAsync();
        await using var scope = fx.Server.Services.CreateAsyncScope();
        var releases = scope.ServiceProvider.GetRequiredService<ReleaseService>();
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        foreach (var (version, channel, published) in new[] { ("1.0.0", "stable", true), ("1.1.0", "stable", true), ("1.2.0-beta.1", "beta", true), ("1.3.0", "stable", false) })
        {
            await releases.CreateAsync(new CreateReleaseCommand(FeedProduct, version, channel, at, $"https://downloads.quakelogic.net/feed-test/{version}.exe",
                new string('a', 64), 123_456_789, $"Notes for {version}", false, "1.0.0", published), TestData.TestAdmin, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Update_feed_returns_a_manifest_signed_with_the_release_key()
    {
        await SeedAsync();
        using var http = fx.Server.CreateHttpsClient();

        var manifest = await http.GetFromJsonAsync<SignedManifest>($"api/updates/{FeedProduct}/stable?current=1.0.0", LicenseJson.Options);

        Assert.NotNull(manifest);
        var release = ManifestSigning.Verify(manifest, [fx.ReleaseKeys.PublicPem]);
        Assert.NotNull(release);
        Assert.Equal("1.1.0", release.Version);
        Assert.Equal("stable", release.Channel);
        Assert.Equal(Es256.KeyId(fx.ReleaseKeys.PublicPem), manifest.KeyId);
        Assert.Null(ManifestSigning.Verify(manifest, [fx.TokenKeys.PublicPem]));
    }

    [Fact]
    public async Task Update_feed_returns_204_when_up_to_date_and_includes_betas_on_the_beta_channel()
    {
        await SeedAsync();
        using var http = fx.Server.CreateHttpsClient();

        using var upToDate = await http.GetAsync($"api/updates/{FeedProduct}/stable?current=1.1.0");
        var beta = await http.GetFromJsonAsync<SignedManifest>($"api/updates/{FeedProduct}/beta?current=1.1.0", LicenseJson.Options);
        using var badVersion = await http.GetAsync($"api/updates/{FeedProduct}/stable?current=banana");
        using var badChannel = await http.GetAsync($"api/updates/{FeedProduct}/nightly");

        Assert.Equal(HttpStatusCode.NoContent, upToDate.StatusCode);
        Assert.Equal("1.2.0-beta.1", ManifestSigning.Verify(beta!, [fx.ReleaseKeys.PublicPem])!.Version);
        Assert.Equal(HttpStatusCode.BadRequest, badVersion.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badChannel.StatusCode);
    }

    [Fact]
    public async Task Health_reports_database_connectivity()
    {
        using var http = fx.Server.CreateHttpsClient();

        using var response = await http.GetAsync("api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"database\":\"ok\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_http_requests_are_redirected_to_https_outside_development()
    {
        using var http = fx.Server.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost/") });

        using var response = await http.GetAsync("api/health");

        Assert.True(response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect, $"Status was {response.StatusCode}");
        Assert.True(response.Headers.Contains("Strict-Transport-Security") || response.Headers.Location?.Scheme == "https");
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.0.0-beta.2", "2.0.0-beta.10", -1)]
    [InlineData("2.0.0-beta", "2.0.0", -1)]
    [InlineData("2.0.0-alpha", "2.0.0-beta", -1)]
    [InlineData("v1.2", "1.2.0", 0)]
    [InlineData("1.2.3+build.7", "1.2.3", 0)]
    public void Semantic_versions_compare_by_precedence(string a, string b, int expected)
    {
        Assert.True(SemanticVersion.TryParse(a, out var va));
        Assert.True(SemanticVersion.TryParse(b, out var vb));
        Assert.Equal(expected, Math.Sign(va.CompareTo(vb)));
    }
}
