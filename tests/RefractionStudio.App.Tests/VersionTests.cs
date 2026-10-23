using RefractionStudio.Infrastructure;

namespace RefractionStudio.App.Tests;

public sealed class VersionTests
{
    [Theory]
    [InlineData("1.0.1", "1.0.0")]
    [InlineData("1.10.0", "1.9.9")]
    [InlineData("1.2.0", "1.2.0-beta.10")]
    [InlineData("1.2.0-beta.10", "1.2.0-beta.2")]
    [InlineData("1.2.0-beta", "1.2.0-alpha.5")]
    [InlineData("1.2.0-beta.2.1", "1.2.0-beta.2")]
    [InlineData("1.2.0-rc.1", "1.2.0-beta.11")]
    [InlineData("1.2.0-alpha.beta", "1.2.0-alpha.1")]
    public void Semantic_version_precedence(string newer, string older)
    {
        Assert.True(UpdateService.IsNewer(newer, older));
        Assert.False(UpdateService.IsNewer(older, newer));
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("1.2.0+build.5", "1.2.0")]
    [InlineData("1.2", "1.2.0")]
    public void Equal_versions_are_not_newer(string a, string b)
    {
        Assert.Equal(0, UpdateService.CompareVersions(a, b));
        Assert.False(UpdateService.IsNewer(a, b));
    }
}

/// <summary>The full version reads the signed manifest in the repository download folder.</summary>
public sealed class StaticUpdateFeedTests
{
    private sealed class FeedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static (UpdateService Service, FeedHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond, IReadOnlyCollection<string> keys)
    {
        var handler = new FeedHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri(AppInfo.ReleaseFeedUrl) };
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "rs-feed-tests-" + Guid.NewGuid().ToString("N")));
        return (new UpdateService(http, paths, keys, Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateService>.Instance, staticFeed: true), handler);
    }

    [Fact]
    public async Task Signed_release_manifest_offers_a_newer_version()
    {
        var keys = RefractionStudio.Licensing.Es256.GenerateKeyPair();
        var release = new RefractionStudio.Licensing.ReleaseInfo(AppInfo.ProductCode, "1.0.1", "stable", DateTimeOffset.UtcNow,
            "https://github.com/omerozgul11/ql-refract-studio/raw/main/download/RefractionStudio-1.0.1-win-x64-setup.exe", new string('a', 64), 1000, "", false, null);
        var manifest = RefractionStudio.Licensing.ManifestSigning.Sign(release, keys.PrivatePem, RefractionStudio.Licensing.Es256.KeyId(keys.PublicPem));
        var (service, handler) = Create(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = System.Net.Http.Json.JsonContent.Create(manifest, options: RefractionStudio.Licensing.LicenseJson.Options),
        }, [keys.PublicPem]);

        var check = await service.CheckAsync("stable", "1.0.0");

        Assert.True(check.Available);
        Assert.Equal("1.0.1", check.Release!.Version);
        Assert.Equal(new Uri(AppInfo.ReleaseFeedUrl + "release-stable.json"), Assert.Single(handler.Requests));
        Assert.False((await service.CheckAsync("stable", "1.0.1")).Available);
    }

    [Fact]
    public async Task Missing_release_is_reported_as_unavailable()
    {
        var (service, _) = Create(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound), []);
        var check = await service.CheckAsync("stable", "1.0.0");
        Assert.False(check.Available);
        Assert.Contains("unavailable", check.Message);
    }
}
