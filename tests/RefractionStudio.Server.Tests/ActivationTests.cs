using System.Text;

namespace RefractionStudio.Server.Tests;

public sealed class ActivationTests(ServerFixture fx)
{
    private readonly HttpLicenseApi _api = fx.Server.CreateApi();
    private IServiceProvider Services => fx.Server.Services;

    private Task<ApiResult> ActivateAsync(string key, DeviceInfo device, string product = TestData.Product)
        => _api.ActivateAsync(new ActivationRequest(key, null, product, TestData.AppVersion, device), CancellationToken.None);

    private Task<ApiResult> ValidateAsync(string token, DeviceInfo device)
        => _api.ValidateAsync(new ValidationRequest(token, TestData.AppVersion, device), CancellationToken.None);

    private Task<ApiResult> DeactivateAsync(string token, DeviceInfo device)
        => _api.DeactivateAsync(new DeactivationRequest(token, device), CancellationToken.None);

    [Fact]
    public async Task Valid_key_activates_and_returns_a_verifiable_licence_token()
    {
        var (id, key) = await TestData.CreateLicenseAsync(Services, seats: 2);
        var device = TestData.NewDevice();
        var before = DateTimeOffset.UtcNow;

        var result = await ActivateAsync(key.ToLowerInvariant().Replace("-", " "), device);

        Assert.NotNull(result.Success);
        var claims = TestData.Verify(fx, result.Success.Token);
        Assert.Equal(LicenseKind.License, claims.Kind);
        Assert.Equal(id.ToString(), claims.LicenseId);
        Assert.Equal(TestData.Product, claims.Product);
        Assert.Equal(device.Fingerprint, claims.DeviceHash);
        Assert.Equal(key[^4..], claims.KeyHint);
        Assert.Equal("Geo Consulting Ltd", claims.LicensedTo);
        Assert.Equal(2, claims.Seats);
        Assert.Null(claims.ExpiresAt);
        Assert.InRange(claims.OfflineValidUntil, before.AddDays(30).AddSeconds(-5), DateTimeOffset.UtcNow.AddDays(30).AddSeconds(5));
        Assert.Contains("1 more computer", result.Success.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offline_window_never_extends_beyond_the_licence_expiry()
    {
        var expires = DateTimeOffset.UtcNow.AddDays(10);
        var (_, key) = await TestData.CreateLicenseAsync(Services, expiresAt: expires);

        var claims = TestData.Verify(fx, (await ActivateAsync(key, TestData.NewDevice())).Success!.Token);

        Assert.Equal(claims.ExpiresAt, claims.OfflineValidUntil);
        Assert.InRange(claims.ExpiresAt!.Value, expires.AddMilliseconds(-1), expires.AddMilliseconds(1));
    }

    [Fact]
    public async Task Unknown_or_mistyped_keys_are_refused_with_invalid_key()
    {
        var unknown = await ActivateAsync(LicenseKeys.Generate(), TestData.NewDevice());
        var mistyped = await ActivateAsync("RFST-0000-0000-0000-000", TestData.NewDevice());

        Assert.Equal(LicenseErrorCodes.InvalidKey, unknown.Error?.Code);
        Assert.Equal(LicenseErrorCodes.InvalidKey, mistyped.Error?.Code);
        Assert.False(unknown.NetworkFailure);
    }

    [Fact]
    public async Task Revoked_and_suspended_licences_are_refused()
    {
        var (revokedId, revokedKey) = await TestData.CreateLicenseAsync(Services);
        var (suspendedId, suspendedKey) = await TestData.CreateLicenseAsync(Services);
        await TestData.WithServiceAsync<LicenseService>(Services, s => s.RevokeAsync(revokedId, "Chargeback", TestData.TestAdmin, CancellationToken.None));
        await TestData.WithServiceAsync<LicenseService>(Services, s => s.SuspendAsync(suspendedId, "Payment review", TestData.TestAdmin, CancellationToken.None));

        var revoked = await ActivateAsync(revokedKey, TestData.NewDevice());
        var suspended = await ActivateAsync(suspendedKey, TestData.NewDevice());

        Assert.Equal(LicenseErrorCodes.Revoked, revoked.Error?.Code);
        Assert.Equal(LicenseErrorCodes.Suspended, suspended.Error?.Code);

        await TestData.WithServiceAsync<LicenseService>(Services, s => s.ReinstateAsync(suspendedId, "Resolved", TestData.TestAdmin, CancellationToken.None));
        Assert.NotNull((await ActivateAsync(suspendedKey, TestData.NewDevice())).Success);
    }

    [Fact]
    public async Task Expired_licence_is_refused_with_expired()
    {
        var (id, key) = await TestData.CreateLicenseAsync(Services, expiresAt: DateTimeOffset.UtcNow.AddDays(1));
        await using (var db = fx.CreateDbContext())
        {
            var license = await db.Licenses.SingleAsync(l => l.Id == id);
            license.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        var result = await ActivateAsync(key, TestData.NewDevice());

        Assert.Equal(LicenseErrorCodes.Expired, result.Error?.Code);
    }

    [Fact]
    public async Task Key_for_another_product_is_refused_with_product_mismatch()
    {
        await using (var db = fx.CreateDbContext())
        {
            if (!await db.LicenseProducts.AnyAsync(p => p.Code == "masw-studio"))
            {
                db.LicenseProducts.Add(new LicenseProduct { Code = "masw-studio", Name = "MASW Studio", KeyPrefix = "RFST", DefaultEdition = "Professional" });
                await db.SaveChangesAsync();
            }
        }
        var (_, key) = await TestData.CreateLicenseAsync(Services, product: "masw-studio");

        var result = await ActivateAsync(key, TestData.NewDevice());

        Assert.Equal(LicenseErrorCodes.ProductMismatch, result.Error?.Code);
    }

    [Fact]
    public async Task Activation_limit_refuses_a_second_computer_on_a_single_seat_licence()
    {
        var (_, key) = await TestData.CreateLicenseAsync(Services, seats: 1);
        Assert.NotNull((await ActivateAsync(key, TestData.NewDevice())).Success);

        var second = await ActivateAsync(key, TestData.NewDevice());

        Assert.Equal(LicenseErrorCodes.ActivationLimit, second.Error?.Code);
        Assert.Contains("1 computer", second.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reactivating_the_same_computer_does_not_consume_another_seat()
    {
        var (id, key) = await TestData.CreateLicenseAsync(Services, seats: 1);
        var device = TestData.NewDevice();

        var first = await ActivateAsync(key, device);
        var again = await ActivateAsync(key, device);

        Assert.NotNull(first.Success);
        Assert.NotNull(again.Success);
        Assert.Contains("already activated", again.Success.Message, StringComparison.Ordinal);
        await using var db = fx.CreateDbContext();
        Assert.Equal(1, await db.Activations.CountAsync(a => a.LicenseId == id && a.DeactivatedAt == null));
    }

    [Fact]
    public async Task Deactivation_frees_the_seat_and_sends_a_confirmation()
    {
        var (id, key) = await TestData.CreateLicenseAsync(Services, seats: 1);
        var a = TestData.NewDevice();
        var b = TestData.NewDevice();
        var tokenA = (await ActivateAsync(key, a)).Success!.Token;
        Assert.Equal(LicenseErrorCodes.ActivationLimit, (await ActivateAsync(key, b)).Error?.Code);

        var deactivated = await DeactivateAsync(tokenA, a);
        var activatedB = await ActivateAsync(key, b);
        var repeated = await DeactivateAsync(tokenA, a);

        Assert.NotNull(deactivated.Success);
        Assert.Contains("deactivated", deactivated.Success.Message, StringComparison.Ordinal);
        Assert.NotNull(activatedB.Success);
        Assert.Contains("already deactivated", repeated.Success!.Message, StringComparison.Ordinal);
        await using var db = fx.CreateDbContext();
        Assert.Equal(1, await db.EmailOutbox.CountAsync(m => m.LicenseId == id && m.Template == "device_deactivated"));
        Assert.Equal(1, await db.Activations.CountAsync(a2 => a2.LicenseId == id && a2.DeactivationReason == DeactivationReasons.User));
    }

    [Fact]
    public async Task Transfer_limit_refuses_activation_after_too_many_moves_in_30_days()
    {
        var (_, key) = await TestData.CreateLicenseAsync(Services, seats: 1, maxTransfers: 1);
        var a = TestData.NewDevice();
        var b = TestData.NewDevice();
        var c = TestData.NewDevice();

        var tokenA = (await ActivateAsync(key, a)).Success!.Token;
        Assert.NotNull((await DeactivateAsync(tokenA, a)).Success);
        var tokenB = (await ActivateAsync(key, b)).Success!.Token; // first transfer: allowed
        Assert.NotNull((await DeactivateAsync(tokenB, b)).Success);

        var third = await ActivateAsync(key, c);

        Assert.Equal(LicenseErrorCodes.TransferLimit, third.Error?.Code);
    }

    [Fact]
    public async Task Admin_reset_frees_seats_without_counting_as_transfers()
    {
        var (id, key) = await TestData.CreateLicenseAsync(Services, seats: 1, maxTransfers: 0);
        Assert.NotNull((await ActivateAsync(key, TestData.NewDevice())).Success);
        await TestData.WithServiceAsync<LicenseService>(Services, s => s.ResetActivationsAsync(id, TestData.TestAdmin, CancellationToken.None));

        var result = await ActivateAsync(key, TestData.NewDevice());

        Assert.NotNull(result.Success);
    }

    [Fact]
    public async Task Validation_refreshes_the_token_and_offline_window()
    {
        var (id, key) = await TestData.CreateLicenseAsync(Services);
        var device = TestData.NewDevice();
        var token = (await ActivateAsync(key, device)).Success!.Token;
        var original = TestData.Verify(fx, token);
        await Task.Delay(20);

        var result = await ValidateAsync(token, device);

        var refreshed = TestData.Verify(fx, result.Success!.Token);
        Assert.NotEqual(original.TokenId, refreshed.TokenId);
        Assert.True(refreshed.IssuedAt > original.IssuedAt);
        Assert.True(refreshed.OfflineValidUntil > original.OfflineValidUntil);
        Assert.Equal(original.LicenseId, refreshed.LicenseId);
        await using var db = fx.CreateDbContext();
        var activation = await db.Activations.SingleAsync(a => a.LicenseId == id);
        Assert.True(activation.LastValidatedAt >= refreshed.IssuedAt.AddSeconds(-1));
    }

    [Fact]
    public async Task Validation_after_revocation_returns_revoked()
    {
        var (id, key) = await TestData.CreateLicenseAsync(Services);
        var device = TestData.NewDevice();
        var token = (await ActivateAsync(key, device)).Success!.Token;
        await TestData.WithServiceAsync<LicenseService>(Services, s => s.RevokeAsync(id, "Refunded", TestData.TestAdmin, CancellationToken.None));

        var result = await ValidateAsync(token, device);

        Assert.Equal(LicenseErrorCodes.Revoked, result.Error?.Code);
        await using var db = fx.CreateDbContext();
        Assert.True(await db.LicenseEvents.AnyAsync(e => e.LicenseId == id && e.Type == EventTypes.ValidationRefused));
    }

    [Fact]
    public async Task Validation_from_another_computer_or_after_deactivation_is_refused()
    {
        var (_, key) = await TestData.CreateLicenseAsync(Services);
        var device = TestData.NewDevice();
        var token = (await ActivateAsync(key, device)).Success!.Token;

        var otherComputer = await ValidateAsync(token, TestData.NewDevice());
        Assert.NotNull((await DeactivateAsync(token, device)).Success);
        var afterDeactivation = await ValidateAsync(token, device);

        Assert.Equal(LicenseErrorCodes.DeviceMismatch, otherComputer.Error?.Code);
        Assert.Equal(LicenseErrorCodes.NotActivated, afterDeactivation.Error?.Code);
    }

    [Fact]
    public async Task Forged_or_foreign_tokens_are_rejected()
    {
        var (foreignPrivate, foreignPublic) = Es256.GenerateKeyPair();
        var device = TestData.NewDevice();
        var claims = new LicenseClaims(Guid.NewGuid().ToString("N"), LicenseKind.License, Guid.NewGuid().ToString(), TestData.Product, "Professional", null, null, null,
            device.Fingerprint, DateTimeOffset.UtcNow, null, null, DateTimeOffset.UtcNow.AddDays(30), null, null, [], 1);
        var forged = LicenseToken.Issue(claims, foreignPrivate, Es256.KeyId(foreignPublic));

        var result = await ValidateAsync(forged, device);

        Assert.Equal(LicenseErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public async Task Blocked_device_cannot_activate()
    {
        var (_, key) = await TestData.CreateLicenseAsync(Services, seats: 2);
        var device = TestData.NewDevice();
        Assert.NotNull((await ActivateAsync(key, device)).Success);
        Guid deviceId;
        await using (var db = fx.CreateDbContext()) deviceId = (await db.Devices.SingleAsync(d => d.Fingerprint == device.Fingerprint)).Id;
        await TestData.WithServiceAsync<DeviceService>(Services, s => s.BlockAsync(deviceId, "Abuse", TestData.TestAdmin, CancellationToken.None));

        var result = await ActivateAsync(key, device);

        Assert.Equal(LicenseErrorCodes.Revoked, result.Error?.Code);
    }

    [Fact]
    public async Task Malformed_requests_get_a_json_error_not_a_server_error()
    {
        using var http = fx.Server.CreateHttpsClient();
        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");

        using var response = await http.PostAsync("api/licenses/activate", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(LicenseJson.Options);
        Assert.Equal(LicenseErrorCodes.InvalidRequest, error?.Code);
    }

    [Fact]
    public async Task Raw_licence_keys_are_never_stored()
    {
        var (id, key) = await TestData.CreateLicenseAsync(Services);
        Assert.NotNull((await ActivateAsync(key, TestData.NewDevice())).Success);

        await using var db = fx.CreateDbContext();
        var license = await db.Licenses.SingleAsync(l => l.Id == id);
        Assert.Equal(LicenseKeys.Hash(key, fx.Pepper), license.LicenseKeyHash);
        Assert.Equal(key[^4..], license.KeyHint);
        var events = await db.LicenseEvents.Where(e => e.LicenseId == id).Select(e => e.Details).ToListAsync();
        var audit = await db.AuditLog.Where(a => a.EntityId == id.ToString()).Select(a => a.Details).ToListAsync();
        var mail = await db.EmailOutbox.Where(m => m.LicenseId == id).ToListAsync();
        var body = key.Replace("-", "", StringComparison.Ordinal);
        Assert.DoesNotContain(events.Concat(audit), d => d.Contains(key, StringComparison.Ordinal) || d.Contains(body, StringComparison.Ordinal));
        var keyMail = Assert.Single(mail, m => m.Template == "license_key");
        Assert.True(keyMail.Sensitive);
        Assert.StartsWith("enc:v1:", keyMail.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain(key, keyMail.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain(key, keyMail.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Append_only_tables_reject_updates_at_the_database_level()
    {
        var (id, _) = await TestData.CreateLicenseAsync(Services);
        await using var db = fx.CreateDbContext();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE license_events SET type = 'tampered' WHERE license_id = {id}"));

        Assert.Contains("append-only", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_desktop_LicenseManager_activates_and_deactivates_against_the_server()
    {
        var (_, key) = await TestData.CreateLicenseAsync(Services);
        var device = TestData.NewDevice();
        var options = new LicensingOptions(TestData.Product, "Refraction Studio", TestData.AppVersion, [fx.TokenKeys.PublicPem]);
        var manager = new LicenseManager(options, _api, new MemoryLicenseStore(), device);

        var trial = await manager.StartTrialAsync();
        var licensed = await manager.ActivateAsync(key, null, "RFST");
        var refreshed = await manager.RefreshAsync();
        var (deactivated, _) = await manager.DeactivateAsync();

        Assert.Equal(LicenseState.TrialActive, trial.State);
        Assert.Equal(LicenseState.Licensed, licensed.State);
        Assert.Equal("Geo Consulting Ltd", licensed.Claims?.LicensedTo);
        Assert.Equal(LicenseState.Licensed, refreshed.State);
        Assert.True(deactivated);
        Assert.Equal(LicenseState.NotActivated, manager.Evaluate().State);
    }
}
