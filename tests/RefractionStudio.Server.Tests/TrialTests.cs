namespace RefractionStudio.Server.Tests;

public sealed class TrialTests(ServerFixture fx)
{
    private readonly HttpLicenseApi _api = fx.Server.CreateApi();

    private Task<ApiResult> StartAsync(DeviceInfo device) => _api.StartTrialAsync(new TrialRequest(TestData.Product, TestData.AppVersion, device), CancellationToken.None);

    [Fact]
    public async Task New_trial_issues_a_14_day_trial_token_valid_offline_for_3_days()
    {
        var device = TestData.NewDevice();
        var before = DateTimeOffset.UtcNow;

        var result = await StartAsync(device);

        Assert.NotNull(result.Success);
        var claims = TestData.Verify(fx, result.Success.Token);
        Assert.Equal(LicenseKind.Trial, claims.Kind);
        Assert.Equal(TestData.Product, claims.Product);
        Assert.Equal(device.Fingerprint, claims.DeviceHash);
        Assert.NotNull(claims.TrialStart);
        Assert.NotNull(claims.TrialEnd);
        Assert.InRange(claims.TrialEnd.Value - claims.TrialStart.Value, TimeSpan.FromDays(14) - TimeSpan.FromSeconds(1), TimeSpan.FromDays(14) + TimeSpan.FromSeconds(1));
        Assert.InRange(claims.OfflineValidUntil, before.AddDays(3).AddSeconds(-5), DateTimeOffset.UtcNow.AddDays(3).AddSeconds(5));
        Assert.Contains("started", result.Success.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Email_given_at_trial_start_is_recorded_for_the_reminder_and_invalid_addresses_are_refused()
    {
        var device = TestData.NewDevice();
        var invalid = await _api.StartTrialAsync(new TrialRequest(TestData.Product, TestData.AppVersion, device, "not-an-address"), CancellationToken.None);
        Assert.Equal(LicenseErrorCodes.InvalidRequest, invalid.Error?.Code);

        var ok = await _api.StartTrialAsync(new TrialRequest(TestData.Product, TestData.AppVersion, device, " eva@example.com "), CancellationToken.None);
        Assert.NotNull(ok.Success);
        await using var db = fx.CreateDbContext();
        Assert.Equal("eva@example.com", (await db.TrialDevices.SingleAsync(t => t.Fingerprint == device.Fingerprint)).Email);
    }

    [Fact]
    public async Task Restarting_after_reinstall_continues_the_same_trial()
    {
        var device = TestData.NewDevice();
        var first = TestData.Verify(fx, (await StartAsync(device)).Success!.Token);

        var second = await StartAsync(device);

        var claims = TestData.Verify(fx, second.Success!.Token);
        Assert.Equal(first.LicenseId, claims.LicenseId);
        Assert.Equal(first.TrialEnd, claims.TrialEnd);
        Assert.Equal(first.TrialStart, claims.TrialStart);
        Assert.Contains("continues", second.Success.Message, StringComparison.OrdinalIgnoreCase);
        await using var db = fx.CreateDbContext();
        Assert.Equal(1, await db.TrialDevices.CountAsync(t => t.Fingerprint == device.Fingerprint));
    }

    [Fact]
    public async Task One_changed_hardware_component_still_matches_the_existing_trial()
    {
        var device = TestData.NewDevice();
        var first = TestData.Verify(fx, (await StartAsync(device)).Success!.Token);
        var changed = TestData.WithOneComponentChanged(device);

        var result = await StartAsync(changed);

        var claims = TestData.Verify(fx, result.Success!.Token);
        Assert.Equal(first.LicenseId, claims.LicenseId);
        Assert.Equal(first.TrialEnd, claims.TrialEnd);
        Assert.Equal(changed.Fingerprint, claims.DeviceHash);
    }

    [Fact]
    public async Task A_device_sharing_only_one_component_gets_its_own_trial()
    {
        var device = TestData.NewDevice();
        var first = TestData.Verify(fx, (await StartAsync(device)).Success!.Token);
        var other = TestData.NewDevice() with { ComponentHashes = [device.ComponentHashes[0], TestData.RandomHash(), TestData.RandomHash()] };

        var claims = TestData.Verify(fx, (await StartAsync(other)).Success!.Token);

        Assert.NotEqual(first.LicenseId, claims.LicenseId);
    }

    [Fact]
    public async Task Expired_trial_is_refused_with_trial_expired()
    {
        var device = TestData.NewDevice();
        var token = (await StartAsync(device)).Success!.Token;
        var claims = TestData.Verify(fx, token);
        await using (var db = fx.CreateDbContext())
        {
            var trial = await db.TrialDevices.SingleAsync(t => t.Id == Guid.Parse(claims.LicenseId));
            trial.EndsAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var restart = await StartAsync(device);
        var afterHardwareChange = await StartAsync(TestData.WithOneComponentChanged(device));

        Assert.Equal(LicenseErrorCodes.TrialExpired, restart.Error?.Code);
        Assert.False(restart.NetworkFailure);
        Assert.Contains("ended", restart.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(LicenseErrorCodes.TrialExpired, afterHardwareChange.Error?.Code);
    }

    [Fact]
    public async Task Validating_a_trial_token_refreshes_it_and_reports_expiry_once_ended()
    {
        var device = TestData.NewDevice();
        var token = (await StartAsync(device)).Success!.Token;
        var original = TestData.Verify(fx, token);

        var refreshed = await _api.ValidateAsync(new ValidationRequest(token, TestData.AppVersion, device), CancellationToken.None);
        var claims = TestData.Verify(fx, refreshed.Success!.Token);
        Assert.Equal(original.LicenseId, claims.LicenseId);
        Assert.NotEqual(original.TokenId, claims.TokenId);

        await using (var db = fx.CreateDbContext())
        {
            var trial = await db.TrialDevices.SingleAsync(t => t.Id == Guid.Parse(original.LicenseId));
            trial.EndsAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        var expired = await _api.ValidateAsync(new ValidationRequest(token, TestData.AppVersion, device), CancellationToken.None);
        Assert.Equal(LicenseErrorCodes.TrialExpired, expired.Error?.Code);
    }

    [Fact]
    public async Task Trial_token_offline_window_never_exceeds_the_trial_end()
    {
        var device = TestData.NewDevice();
        var claims = TestData.Verify(fx, (await StartAsync(device)).Success!.Token);
        await using (var db = fx.CreateDbContext())
        {
            var trial = await db.TrialDevices.SingleAsync(t => t.Id == Guid.Parse(claims.LicenseId));
            trial.EndsAt = DateTimeOffset.UtcNow.AddHours(5);
            await db.SaveChangesAsync();
        }

        var again = TestData.Verify(fx, (await StartAsync(device)).Success!.Token);

        Assert.Equal(again.TrialEnd, again.OfflineValidUntil);
    }

    [Fact]
    public async Task Invalid_device_description_is_rejected()
    {
        var result = await StartAsync(new DeviceInfo("not-a-hash", [], null, "Windows"));

        Assert.Equal(LicenseErrorCodes.InvalidRequest, result.Error?.Code);
    }
}
