using Microsoft.Extensions.Time.Testing;

namespace RefractionStudio.Licensing.Tests;

/// <summary>A scripted licence server that signs real tokens with a test key.</summary>
internal sealed class FakeLicenseApi(TimeProvider clock) : ILicenseApi
{
    public static readonly (string PrivatePem, string PublicPem) Keys = Es256.GenerateKeyPair();

    public const string Product = "refraction-studio";

    public List<string> Calls { get; } = [];

    /// <summary>When set, every call returns this instead of the default behaviour.</summary>
    public ApiResult? NextResult { get; set; }

    public DateTimeOffset? TrialEndOverride { get; set; }

    public int LicenseOfflineDays { get; set; } = 30;

    public DateTimeOffset? LicenseExpiresAt { get; set; }

    public string Sign(LicenseClaims claims) => LicenseToken.Issue(claims, Keys.PrivatePem, Es256.KeyId(Keys.PublicPem));

    public string TrialToken(string device, DateTimeOffset start, DateTimeOffset end, DateTimeOffset now)
        => Sign(new LicenseClaims(Guid.NewGuid().ToString("N"), LicenseKind.Trial, "trial-1", Product, "Professional", null, null, null, device,
            now, end, end, end < now.AddDays(3) ? end : now.AddDays(3), start, end, [], 1));

    public string IssueLicenseToken(string device, DateTimeOffset now, string product = Product)
    {
        var offline = now.AddDays(LicenseOfflineDays);
        if (LicenseExpiresAt is { } exp && exp < offline) offline = exp;
        return Sign(new LicenseClaims(Guid.NewGuid().ToString("N"), LicenseKind.License, "lic-1", product, "Professional", "Geo Consulting Ltd", "jane@geo.example",
            "ABCD", device, now, LicenseExpiresAt, now.AddYears(1), offline, null, null, [], 1));
    }

    public Task<ApiResult> StartTrialAsync(TrialRequest request, CancellationToken ct)
    {
        Calls.Add("trial");
        var now = clock.GetUtcNow();
        return Task.FromResult(NextResult ?? ApiResult.Ok(new TokenResponse(TrialToken(request.Device.Fingerprint, now, TrialEndOverride ?? now.AddDays(14), now), "Trial started.")));
    }

    public Task<ApiResult> ActivateAsync(ActivationRequest request, CancellationToken ct)
    {
        Calls.Add("activate:" + request.LicenseKey);
        return Task.FromResult(NextResult ?? ApiResult.Ok(new TokenResponse(IssueLicenseToken(request.Device.Fingerprint, clock.GetUtcNow()), "Activation successful.")));
    }

    public Task<ApiResult> ValidateAsync(ValidationRequest request, CancellationToken ct)
    {
        Calls.Add("validate");
        return Task.FromResult(NextResult ?? ApiResult.Ok(new TokenResponse(IssueLicenseToken(request.Device.Fingerprint, clock.GetUtcNow()), "Licence verified.")));
    }

    public Task<ApiResult> DeactivateAsync(DeactivationRequest request, CancellationToken ct)
    {
        Calls.Add("deactivate");
        return Task.FromResult(NextResult ?? ApiResult.Ok(new TokenResponse("", "This computer has been deactivated.")));
    }
}

/// <summary>A clock that can also move backwards (for clock-rollback tests).</summary>
internal sealed class AdjustableClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class LicenseManagerTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly DeviceInfo _device = new DeviceFingerprint("tests", new FixedIdentifierSource(
        new MachineIdentifier("machine-guid", "A1B2C3"), new MachineIdentifier("smbios-uuid", "D4E5F6"), new MachineIdentifier("baseboard", "Board"))).Compute(false);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly MemoryLicenseStore _store = new();
    private readonly FakeLicenseApi _api;
    private readonly LicenseManager _manager;

    public LicenseManagerTests()
    {
        _api = new FakeLicenseApi(_clock);
        _manager = Create(_clock);
    }

    private LicenseManager Create(TimeProvider clock, ILicenseApi? api = null)
        => new(new LicensingOptions(FakeLicenseApi.Product, "Refraction Studio", "1.0.0", [FakeLicenseApi.Keys.PublicPem]), api ?? _api, _store, _device, clock);

    private const string ValidKeyInput = "rfst 0000 0000 0000 000";

    /// <summary>Any well-formed key: the fake server accepts it.</summary>
    private static string ValidKey() => LicenseKeys.Generate();

    [Fact]
    public void Fresh_install_is_not_activated_and_cannot_edit()
    {
        var status = _manager.Evaluate();

        Assert.Equal(LicenseState.NotActivated, status.State);
        Assert.False(status.CanEdit);
        Assert.Contains("14-day free trial", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_trial_is_active_for_14_days()
    {
        LicenseStatus? raised = null;
        _manager.StatusChanged += (_, s) => raised = s;

        var status = await _manager.StartTrialAsync();

        Assert.Equal(LicenseState.TrialActive, status.State);
        Assert.Equal(14, status.DaysRemaining);
        Assert.True(status.CanEdit);
        Assert.Equal("Refraction Studio Trial — 14 days remaining", status.Title);
        Assert.Equal(LicenseState.TrialActive, raised?.State);
        Assert.Equal(LicenseState.TrialActive, _manager.Evaluate().State);
    }

    [Fact]
    public async Task Trial_with_three_days_or_less_left_is_expiring()
    {
        _api.TrialEndOverride = Start.AddDays(14);
        await _manager.StartTrialAsync();
        _clock.Advance(TimeSpan.FromDays(11));
        // Online again on day 11: the server continues the same trial with a fresh token.
        await _manager.StartTrialAsync();

        var status = _manager.Evaluate();

        Assert.Equal(LicenseState.TrialExpiring, status.State);
        Assert.Equal(3, status.DaysRemaining);
        Assert.True(status.CanEdit);
        Assert.Equal("Refraction Studio Trial — 3 days remaining", status.Title);
    }

    [Fact]
    public async Task Trial_with_one_day_left_uses_the_singular()
    {
        _api.TrialEndOverride = Start.AddHours(20);

        var status = await _manager.StartTrialAsync();

        Assert.Equal(LicenseState.TrialExpiring, status.State);
        Assert.Equal("Refraction Studio Trial — 1 day remaining", status.Title);
    }

    [Fact]
    public async Task Trial_past_its_end_is_expired_and_read_only()
    {
        _api.TrialEndOverride = Start.AddDays(2);
        await _manager.StartTrialAsync();
        _clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromMinutes(1));

        var status = _manager.Evaluate();

        Assert.Equal(LicenseState.TrialExpired, status.State);
        Assert.False(status.CanEdit);
        Assert.Contains("can still be opened and viewed", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_refusing_the_trial_reports_trial_expired()
    {
        _api.NextResult = ApiResult.Fail(new ErrorResponse(LicenseErrorCodes.TrialExpired, "The 14-day trial on this computer ended on 1 March 2026."));

        var status = await _manager.StartTrialAsync();

        Assert.Equal(LicenseState.TrialExpired, status.State);
        Assert.Equal("The 14-day trial on this computer ended on 1 March 2026.", status.Message);
        Assert.False(status.CanEdit);
    }

    [Fact]
    public async Task Trial_offline_beyond_three_days_requires_validation()
    {
        await _manager.StartTrialAsync();
        _clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));

        var status = _manager.Evaluate();

        Assert.Equal(LicenseState.NetworkValidationRequired, status.State);
        Assert.False(status.CanEdit);
    }

    [Fact]
    public async Task Valid_licence_activation_is_licensed()
    {
        var status = await _manager.ActivateAsync(ValidKey(), "jane@geo.example", "RFST");

        Assert.Equal(LicenseState.Licensed, status.State);
        Assert.True(status.CanEdit);
        Assert.Equal("Refraction Studio Professional", status.Title);
        Assert.Equal("Licensed to Geo Consulting Ltd.", status.Message);
        Assert.Equal(Start, status.LastValidated);
        Assert.Equal("License verified today.", status.LastVerifiedText(_clock.GetUtcNow()));
    }

    [Fact]
    public async Task Key_is_normalised_before_it_is_sent()
    {
        var key = ValidKey();

        await _manager.ActivateAsync(key.ToLowerInvariant().Replace('-', ' '), null, "RFST");

        Assert.Equal("activate:" + key, Assert.Single(_api.Calls));
    }

    [Theory]
    [InlineData("RFST-1234")]
    [InlineData(ValidKeyInput)]
    [InlineData("")]
    public async Task Invalid_key_format_is_rejected_without_contacting_the_server(string input)
    {
        var status = await _manager.ActivateAsync(input, null, "RFST");

        Assert.Equal(LicenseState.NotActivated, status.State);
        Assert.Contains("is not a valid licence key", status.Message, StringComparison.Ordinal);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Revoked_licence_is_reported_and_remembered()
    {
        await _manager.ActivateAsync(ValidKey(), null, "RFST");
        _api.NextResult = ApiResult.Fail(new ErrorResponse(LicenseErrorCodes.Revoked, "This licence has been revoked."));

        var status = await _manager.RefreshAsync();

        Assert.Equal(LicenseState.LicenseRevoked, status.State);
        Assert.False(status.CanEdit);
        Assert.Equal("This licence has been revoked.", status.Message);
        Assert.Equal(LicenseState.LicenseRevoked, _manager.Evaluate().State);
    }

    [Fact]
    public async Task Computer_deactivated_on_the_server_loses_its_stored_licence()
    {
        await _manager.ActivateAsync(ValidKey(), null, "RFST");
        Assert.Equal(LicenseState.Licensed, _manager.Evaluate().State);
        _api.NextResult = ApiResult.Fail(new ErrorResponse(LicenseErrorCodes.NotActivated, "This computer is not activated for this licence."));

        var status = await _manager.RefreshAsync();

        Assert.Equal(LicenseState.NotActivated, status.State);
        Assert.False(status.CanEdit);
        Assert.Null(_store.Load());
        Assert.Equal(LicenseState.NotActivated, _manager.Evaluate().State);
    }

    [Fact]
    public async Task Suspended_licence_is_treated_as_revoked()
    {
        _api.NextResult = ApiResult.Fail(new ErrorResponse(LicenseErrorCodes.Suspended, "This licence is suspended."));

        var status = await _manager.ActivateAsync(ValidKey(), null, "RFST");

        Assert.Equal(LicenseState.LicenseRevoked, status.State);
    }

    [Fact]
    public async Task Expired_licence_is_read_only()
    {
        _api.LicenseExpiresAt = Start.AddDays(10);
        await _manager.ActivateAsync(ValidKey(), null, "RFST");
        _clock.Advance(TimeSpan.FromDays(10));

        var status = _manager.Evaluate();

        Assert.Equal(LicenseState.LicenseExpired, status.State);
        Assert.False(status.CanEdit);
    }

    [Fact]
    public async Task Offline_within_the_grace_period_keeps_working()
    {
        await _manager.ActivateAsync(ValidKey(), null, "RFST");
        _clock.Advance(TimeSpan.FromDays(5));

        var status = _manager.Evaluate();

        Assert.Equal(LicenseState.LicenseOfflineGrace, status.State);
        Assert.True(status.CanEdit);
        Assert.Contains("1 April 2026", status.Message, StringComparison.Ordinal);
        Assert.Equal("License last verified 5 days ago.", status.LastVerifiedText(_clock.GetUtcNow()));
    }

    [Fact]
    public async Task Network_failure_during_refresh_keeps_the_offline_evaluation()
    {
        await _manager.ActivateAsync(ValidKey(), null, "RFST");
        _clock.Advance(TimeSpan.FromDays(1));
        _api.NextResult = ApiResult.Offline("The licence server could not be reached.");

        var status = await _manager.RefreshAsync();

        Assert.Equal(LicenseState.Licensed, status.State);
        Assert.True(status.CanEdit);
    }

    [Fact]
    public async Task Offline_beyond_the_offline_window_requires_network_validation()
    {
        await _manager.ActivateAsync(ValidKey(), null, "RFST");
        _clock.Advance(TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));

        var status = _manager.Evaluate();

        Assert.Equal(LicenseState.NetworkValidationRequired, status.State);
        Assert.False(status.CanEdit);
        Assert.Contains("your projects can still be opened", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_refresh_after_a_long_offline_period_restores_the_licence()
    {
        await _manager.ActivateAsync(ValidKey(), null, "RFST");
        _clock.Advance(TimeSpan.FromDays(40));

        var status = await _manager.RefreshAsync();

        Assert.Equal(LicenseState.Licensed, status.State);
        Assert.Equal(_clock.GetUtcNow(), status.LastValidated);
    }

    [Fact]
    public async Task Clock_rollback_of_more_than_24_hours_requires_network_validation()
    {
        var clock = new AdjustableClock(Start);
        var api = new FakeLicenseApi(clock);
        var manager = Create(clock, api);
        await manager.ActivateAsync(ValidKey(), null, "RFST");
        clock.Now = Start.AddDays(5);
        Assert.Equal(LicenseState.LicenseOfflineGrace, manager.Evaluate().State);

        clock.Now = Start.AddDays(3);
        var status = manager.Evaluate();

        Assert.Equal(LicenseState.NetworkValidationRequired, status.State);
        Assert.StartsWith("Your system clock changed significantly. Connect to the internet to verify your license.", status.Message, StringComparison.Ordinal);
        Assert.False(status.CanEdit);
    }

    [Fact]
    public async Task Clock_rollback_within_24_hours_is_tolerated()
    {
        var clock = new AdjustableClock(Start);
        var manager = Create(clock, new FakeLicenseApi(clock));
        await manager.ActivateAsync(ValidKey(), null, "RFST");
        clock.Now = Start.AddHours(30);
        manager.Evaluate();

        clock.Now = Start.AddHours(10);

        Assert.Equal(LicenseState.Licensed, manager.Evaluate().State);
    }

    [Fact]
    public async Task Server_unreachable_on_first_activation_explains_that_internet_is_needed()
    {
        _api.NextResult = ApiResult.Offline("The licence server could not be reached. Check the internet connection.");

        var status = await _manager.ActivateAsync(ValidKey(), null, "RFST");

        Assert.Equal(LicenseState.NotActivated, status.State);
        Assert.EndsWith("An internet connection is needed for the first activation.", status.Message, StringComparison.Ordinal);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task Activation_limit_is_reported()
    {
        _api.NextResult = ApiResult.Fail(new ErrorResponse(LicenseErrorCodes.ActivationLimit, "This licence is already active on 1 computer."));

        var status = await _manager.ActivateAsync(ValidKey(), null, "RFST");

        Assert.Equal(LicenseState.ActivationLimitReached, status.State);
        Assert.Equal("This licence is already active on 1 computer.", status.Message);
        Assert.False(status.CanEdit);
    }

    [Fact]
    public async Task Deactivation_clears_the_local_licence()
    {
        await _manager.ActivateAsync(ValidKey(), null, "RFST");

        var (success, message) = await _manager.DeactivateAsync();

        Assert.True(success);
        Assert.Equal("This computer has been deactivated.", message);
        Assert.Null(_store.Load());
        Assert.Equal(LicenseState.NotActivated, _manager.Evaluate().State);
    }

    [Fact]
    public async Task Deactivation_needs_the_server()
    {
        await _manager.ActivateAsync(ValidKey(), null, "RFST");
        _api.NextResult = ApiResult.Offline("offline");

        var (success, message) = await _manager.DeactivateAsync();

        Assert.False(success);
        Assert.Contains("requires an internet connection", message, StringComparison.Ordinal);
        Assert.NotNull(_store.Load());
        Assert.Equal(LicenseState.Licensed, _manager.Evaluate().State);
    }

    [Fact]
    public async Task Deactivating_a_computer_that_is_not_activated_is_refused_locally()
    {
        var (success, message) = await _manager.DeactivateAsync();

        Assert.False(success);
        Assert.Equal("This computer is not activated.", message);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Token_for_another_computer_or_product_is_not_accepted()
    {
        _api.NextResult = ApiResult.Ok(new TokenResponse(_api.IssueLicenseToken("another-device", Start), null));
        var otherDevice = await _manager.ActivateAsync(ValidKey(), null, "RFST");
        _api.NextResult = ApiResult.Ok(new TokenResponse(_api.IssueLicenseToken(_device.Fingerprint, Start, product: "masw-studio"), null));
        var otherProduct = await _manager.ActivateAsync(ValidKey(), null, "RFST");

        Assert.Equal(LicenseState.NotActivated, otherDevice.State);
        Assert.Contains("could not be verified", otherDevice.Message, StringComparison.Ordinal);
        Assert.Equal(LicenseState.NotActivated, otherProduct.State);
    }

    [Fact]
    public async Task Token_signed_by_an_untrusted_key_is_not_accepted()
    {
        var rogue = Es256.GenerateKeyPair();
        var claims = LicenseToken.PeekUnverified(_api.IssueLicenseToken(_device.Fingerprint, Start))!;
        _api.NextResult = ApiResult.Ok(new TokenResponse(LicenseToken.Issue(claims, rogue.PrivatePem, "rogue"), null));

        var status = await _manager.ActivateAsync(ValidKey(), null, "RFST");

        Assert.Equal(LicenseState.NotActivated, status.State);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task Licence_store_copied_from_another_computer_is_ignored()
    {
        await _manager.ActivateAsync(ValidKey(), null, "RFST");
        var otherComputer = new DeviceFingerprint("tests", new FixedIdentifierSource(new MachineIdentifier("machine-guid", "OTHER"), new MachineIdentifier("smbios-uuid", "PC"))).Compute(false);
        var manager = new LicenseManager(new LicensingOptions(FakeLicenseApi.Product, "Refraction Studio", "1.0.0", [FakeLicenseApi.Keys.PublicPem]), _api, _store, otherComputer, _clock);

        var status = manager.Evaluate();

        Assert.Equal(LicenseState.NotActivated, status.State);
        Assert.Contains("could not be verified on this computer", status.Message, StringComparison.Ordinal);
    }
}
