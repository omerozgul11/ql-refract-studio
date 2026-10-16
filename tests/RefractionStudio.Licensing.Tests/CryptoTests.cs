using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RefractionStudio.Licensing.Tests;

public sealed partial class LicenseKeyTests
{
    [GeneratedRegex("^RFST-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$")]
    private static partial Regex KeyFormat();

    [Fact]
    public void Generated_keys_have_the_documented_format_and_validate()
    {
        for (var i = 0; i < 200; i++)
        {
            var key = LicenseKeys.Generate();
            Assert.Matches(KeyFormat(), key);
            Assert.Equal(key, LicenseKeys.Normalize(key));
        }
    }

    [Fact]
    public void Ten_thousand_keys_are_unique_and_use_the_whole_alphabet()
    {
        var keys = Enumerable.Range(0, 10_000).Select(_ => LicenseKeys.Generate()).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        var bodies = keys.Select(k => k[5..].Replace("-", "", StringComparison.Ordinal)).ToList();
        // Every random position uses every alphabet character (10 000 draws of 32 symbols).
        for (var pos = 0; pos < 15; pos++)
            Assert.Equal(32, bodies.Select(b => b[pos]).Distinct().Count());
        // Rough uniformity: each symbol's share of the random characters is within ±20 % of 1/32.
        var counts = bodies.SelectMany(b => b[..15]).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        var expected = 10_000 * 15 / 32.0;
        Assert.All(counts.Values, n => Assert.InRange(n, expected * 0.8, expected * 1.2));
    }

    [Fact]
    public void Custom_prefix_is_supported()
    {
        var key = LicenseKeys.Generate("MASW");

        Assert.StartsWith("MASW-", key, StringComparison.Ordinal);
        Assert.Equal(key, LicenseKeys.Normalize(key, "MASW"));
        Assert.Null(LicenseKeys.Normalize(key.Replace("MASW-", "RFST-", StringComparison.Ordinal) + "X", "MASW"));
    }

    [Fact]
    public void Normalize_accepts_lowercase_spaces_missing_prefix_and_confusable_letters()
    {
        var key = KeyWith('0', '1');
        var body = key[5..];

        Assert.Equal(key, LicenseKeys.Normalize(key.ToLowerInvariant()));
        Assert.Equal(key, LicenseKeys.Normalize("  " + key.Replace("-", " ", StringComparison.Ordinal) + " "));
        Assert.Equal(key, LicenseKeys.Normalize(body));
        Assert.Equal(key, LicenseKeys.Normalize(key.Replace('0', 'O')));
        Assert.Equal(key, LicenseKeys.Normalize(key[..5] + body.Replace('0', 'o').Replace('1', 'I')));
        Assert.Equal(key, LicenseKeys.Normalize(key[..5] + body.Replace('1', 'l')));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("RFST-1234")]
    [InlineData("RFST-0000-0000-0000-00000")]
    [InlineData("RFST-UUUU-UUUU-UUUU-UUUU")]
    public void Normalize_rejects_malformed_input(string? input) => Assert.Null(LicenseKeys.Normalize(input));

    [Fact]
    public void Any_change_to_the_check_character_is_rejected()
    {
        for (var i = 0; i < 300; i++)
        {
            var key = LicenseKeys.Generate();
            foreach (var c in LicenseKeys.Alphabet.Where(c => c != key[^1]))
                Assert.Null(LicenseKeys.Normalize(key[..^1] + c));
        }
    }

    [Fact]
    public void Every_single_character_typo_is_rejected_except_zero_for_z()
    {
        // The check is a weighted sum modulo 31 with weights 1…15; values 0 (“0”) and 31 (“Z”) are the only
        // pair that differ by a multiple of 31 (documented in docs/licensing/LICENSE-SERVER.md).
        for (var n = 0; n < 300; n++)
        {
            var body = LicenseKeys.Generate()[5..].Replace("-", "", StringComparison.Ordinal);
            for (var pos = 0; pos < 16; pos++)
            {
                foreach (var c in LicenseKeys.Alphabet.Where(c => c != body[pos]))
                {
                    var typo = body[..pos] + c + body[(pos + 1)..];
                    var undetectable = pos < 15 && ((body[pos] == '0' && c == 'Z') || (body[pos] == 'Z' && c == '0'));
                    if (!undetectable) Assert.Null(LicenseKeys.Normalize(typo));
                }
            }
        }
    }

    [Fact]
    public void Adjacent_transpositions_are_rejected_except_zero_and_z()
    {
        for (var n = 0; n < 1000; n++)
        {
            var body = LicenseKeys.Generate()[5..].Replace("-", "", StringComparison.Ordinal);
            for (var pos = 0; pos < 15; pos++)
            {
                if (body[pos] == body[pos + 1]) continue;
                var pair = string.Concat(body[pos], body[pos + 1]);
                if (pos < 14 && pair is "0Z" or "Z0") continue;
                var swapped = body[..pos] + body[pos + 1] + body[pos] + body[(pos + 2)..];
                Assert.Null(LicenseKeys.Normalize(swapped));
            }
        }
    }

    [Fact]
    public void Hash_is_a_keyed_hmac_and_hint_is_the_last_four_characters()
    {
        var key = LicenseKeys.Generate();
        var pepper1 = Enumerable.Repeat((byte)1, 32).ToArray();
        var pepper2 = Enumerable.Repeat((byte)2, 32).ToArray();

        var h1 = LicenseKeys.Hash(key, pepper1);

        Assert.Matches("^[0-9a-f]{64}$", h1);
        Assert.Equal(h1, LicenseKeys.Hash(key, pepper1));
        Assert.NotEqual(h1, LicenseKeys.Hash(key, pepper2));
        Assert.DoesNotContain(key, h1, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(key[^4..], LicenseKeys.Hint(key));
    }

    /// <summary>A valid key whose body contains the given characters (generated until it does).</summary>
    private static string KeyWith(params char[] required)
    {
        while (true)
        {
            var key = LicenseKeys.Generate();
            if (required.All(c => key[5..].Contains(c, StringComparison.Ordinal))) return key;
        }
    }
}

public sealed class TokenTests
{
    private static readonly (string PrivatePem, string PublicPem) Keys = Es256.GenerateKeyPair();

    internal static LicenseClaims Claims(string device = "device-hash", LicenseKind kind = LicenseKind.License) => new(
        "tok-1", kind, "lic-1", "refraction-studio", "Professional", "Geo Ltd", "a@b.c", "ABCD", device,
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero), null, null, ["tomography"], 2);

    [Fact]
    public void Issued_token_verifies_and_round_trips_the_claims()
    {
        var claims = Claims();
        var token = LicenseToken.Issue(claims, Keys.PrivatePem, Es256.KeyId(Keys.PublicPem));

        var (verified, error) = LicenseToken.Verify(token, [Keys.PublicPem]);

        Assert.Equal(TokenError.None, error);
        Assert.NotNull(verified);
        Assert.Equal(claims with { Features = verified.Features }, verified);
        Assert.Equal(["tomography"], verified.Features);
        Assert.Equal(3, token.Split('.').Length);
    }

    [Fact]
    public void Tampered_payload_fails_verification()
    {
        var token = LicenseToken.Issue(Claims(), Keys.PrivatePem, "kid");
        var parts = token.Split('.');
        var tampered = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(Claims() with { Seats = 500 }, LicenseJson.Options));

        var (claims, error) = LicenseToken.Verify(parts[0] + "." + tampered + "." + parts[2], [Keys.PublicPem]);

        Assert.Null(claims);
        Assert.Equal(TokenError.BadSignature, error);
    }

    [Fact]
    public void Token_signed_with_another_key_fails_verification()
    {
        var other = Es256.GenerateKeyPair();
        var token = LicenseToken.Issue(Claims(), other.PrivatePem, "kid");

        Assert.Equal(TokenError.BadSignature, LicenseToken.Verify(token, [Keys.PublicPem]).Error);
    }

    [Fact]
    public void Any_trusted_key_verifies_supporting_key_rotation()
    {
        var next = Es256.GenerateKeyPair();
        var oldToken = LicenseToken.Issue(Claims(), Keys.PrivatePem, "old");
        var newToken = LicenseToken.Issue(Claims(), next.PrivatePem, "new");

        Assert.Equal(TokenError.None, LicenseToken.Verify(oldToken, [Keys.PublicPem, next.PublicPem]).Error);
        Assert.Equal(TokenError.None, LicenseToken.Verify(newToken, [Keys.PublicPem, next.PublicPem]).Error);
    }

    [Fact]
    public void Wrong_token_type_is_rejected_even_when_correctly_signed()
    {
        var header = Base64Url.Encode(Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\"x\"}"));
        var payload = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(Claims(), LicenseJson.Options));
        var signature = Base64Url.Encode(Es256.Sign(Encoding.ASCII.GetBytes(header + "." + payload), Keys.PrivatePem));

        Assert.Equal(TokenError.WrongType, LicenseToken.Verify(header + "." + payload + "." + signature, [Keys.PublicPem]).Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a.b")]
    [InlineData("!!!.???.***")]
    public void Malformed_tokens_are_reported_as_malformed(string token)
        => Assert.Equal(TokenError.Malformed, LicenseToken.Verify(token, [Keys.PublicPem]).Error);

    [Fact]
    public void Alg_none_is_rejected()
    {
        var header = Base64Url.Encode(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"RSLIC\",\"kid\":\"x\"}"));
        var payload = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(Claims(), LicenseJson.Options));

        Assert.Equal(TokenError.Malformed, LicenseToken.Verify(header + "." + payload + ".", [Keys.PublicPem]).Error);
    }

    [Fact]
    public void PeekUnverified_reads_claims_without_a_key()
    {
        var token = LicenseToken.Issue(Claims(), Keys.PrivatePem, "kid");

        Assert.Equal("lic-1", LicenseToken.PeekUnverified(token)?.LicenseId);
        Assert.Null(LicenseToken.PeekUnverified("garbage"));
    }

    [Fact]
    public void Key_id_is_stable_and_differs_between_keys()
    {
        var other = Es256.GenerateKeyPair();

        Assert.Equal(Es256.KeyId(Keys.PublicPem), Es256.KeyId(Keys.PublicPem));
        Assert.Matches("^[0-9a-f]{16}$", Es256.KeyId(Keys.PublicPem));
        Assert.NotEqual(Es256.KeyId(Keys.PublicPem), Es256.KeyId(other.PublicPem));
    }

    [Fact]
    public void Base64Url_round_trips_and_rejects_invalid_length()
    {
        byte[] data = [0xfb, 0xff, 0x00, 0x10, 0x3e];

        var encoded = Base64Url.Encode(data);

        Assert.DoesNotContain('=', encoded);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.Equal(data, Base64Url.Decode(encoded));
        Assert.Throws<FormatException>(() => Base64Url.Decode("abcde"));
    }
}

public sealed class ManifestSigningTests
{
    private static readonly ReleaseInfo Release = new("refraction-studio", "1.2.0", "stable", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        "https://downloads.quakelogic.net/rs/1.2.0.exe", new string('a', 64), 104_857_600, "Fixes.", false, "1.0.0");

    [Fact]
    public void Signed_manifest_verifies_with_the_release_key_only()
    {
        var release = Es256.GenerateKeyPair();
        var licence = Es256.GenerateKeyPair();

        var manifest = ManifestSigning.Sign(Release, release.PrivatePem, Es256.KeyId(release.PublicPem));

        Assert.Equal(Release, ManifestSigning.Verify(manifest, [release.PublicPem]));
        Assert.Null(ManifestSigning.Verify(manifest, [licence.PublicPem]));
        Assert.Equal(Es256.KeyId(release.PublicPem), manifest.KeyId);
    }

    [Fact]
    public void Tampered_manifest_is_rejected()
    {
        var keys = Es256.GenerateKeyPair();
        var manifest = ManifestSigning.Sign(Release, keys.PrivatePem, "kid");
        var evil = Release with { DownloadUrl = "https://evil.example/installer.exe" };

        var tampered = manifest with { Payload = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(evil, LicenseJson.Options)) };

        Assert.Null(ManifestSigning.Verify(tampered, [keys.PublicPem]));
        Assert.Null(ManifestSigning.Verify(manifest with { Signature = "AAAA" }, [keys.PublicPem]));
        Assert.Null(ManifestSigning.Verify(manifest with { Payload = "%%%" }, [keys.PublicPem]));
    }
}
