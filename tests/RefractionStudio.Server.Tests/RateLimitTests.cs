namespace RefractionStudio.Server.Tests;

public sealed class RateLimitTests(ServerFixture fx) : IAsyncDisposable
{
    private readonly LicenseServerFactory _server = new(fx, new Dictionary<string, string?>
    {
        ["RateLimits:ActivationPerMinute"] = "3",
        ["RateLimits:LicensePerMinute"] = "5",
        ["RateLimits:ActivationsPerKeyPerHour"] = "100",
    });

    [Fact]
    public async Task Activation_endpoint_returns_429_with_rate_limited_after_the_per_ip_limit()
    {
        var api = _server.CreateApi();
        var results = new List<ApiResult>();
        for (var i = 0; i < 4; i++)
            results.Add(await api.ActivateAsync(new ActivationRequest(LicenseKeys.Generate(), null, TestData.Product, TestData.AppVersion, TestData.NewDevice()), CancellationToken.None));

        Assert.All(results.Take(3), r => Assert.Equal(LicenseErrorCodes.InvalidKey, r.Error?.Code));
        Assert.Equal(LicenseErrorCodes.RateLimited, results[3].Error?.Code);
        Assert.False(results[3].NetworkFailure);
    }

    [Fact]
    public async Task Licence_endpoints_return_429_with_retry_after()
    {
        using var http = _server.CreateHttpsClient();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
        {
            last?.Dispose();
            last = await http.PostAsJsonAsync("api/trials/start", new TrialRequest(TestData.Product, TestData.AppVersion, TestData.NewDevice()), LicenseJson.Options);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.True(last.Headers.RetryAfter is not null);
        var error = await last.Content.ReadFromJsonAsync<ErrorResponse>(LicenseJson.Options);
        Assert.Equal(LicenseErrorCodes.RateLimited, error?.Code);
        last.Dispose();
    }

    [Fact]
    public async Task One_key_is_limited_across_addresses()
    {
        await using var server = new LicenseServerFactory(fx, new Dictionary<string, string?> { ["RateLimits:ActivationsPerKeyPerHour"] = "2" });
        var api = server.CreateApi();
        var key = LicenseKeys.Generate();
        var results = new List<ApiResult>();
        for (var i = 0; i < 3; i++)
            results.Add(await api.ActivateAsync(new ActivationRequest(i == 2 ? key.ToLowerInvariant() : key, null, TestData.Product, TestData.AppVersion, TestData.NewDevice()), CancellationToken.None));

        Assert.Equal(LicenseErrorCodes.InvalidKey, results[1].Error?.Code);
        Assert.Equal(LicenseErrorCodes.RateLimited, results[2].Error?.Code);
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
