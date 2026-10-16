using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RefractionStudio.Licensing;

/// <summary>Base64url without padding (RFC 4648 §5).</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 1 => throw new FormatException("Invalid base64url length."), _ => "" };
        return Convert.FromBase64String(s);
    }
}

/// <summary>ECDSA P-256 / SHA-256 (ES256) signing and verification with PEM keys.</summary>
public static class Es256
{
    public static (string PrivatePem, string PublicPem) GenerateKeyPair()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ec.ExportPkcs8PrivateKeyPem(), ec.ExportSubjectPublicKeyInfoPem());
    }

    public static byte[] Sign(ReadOnlySpan<byte> data, string privatePem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(privatePem);
        return ec.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, string publicPem)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(publicPem);
            return ec.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Key id: first 16 hex characters of the SHA-256 of the public key DER.</summary>
    public static string KeyId(string publicPem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(publicPem);
        return Convert.ToHexStringLower(SHA256.HashData(ec.ExportSubjectPublicKeyInfo()))[..16];
    }
}

/// <summary>Why a token was rejected.</summary>
public enum TokenError
{
    None,
    Malformed,
    BadSignature,
    WrongType,
}

/// <summary>
/// Compact signed tokens: base64url(header).base64url(claims).base64url(ES256 signature) — the JWS
/// compact serialisation with a "RSLIC" type. The desktop application verifies with the public key only.
/// </summary>
public static class LicenseToken
{
    public const string TokenType = "RSLIC";

    private sealed record Header(string Alg, string Typ, string Kid);

    public static string Issue(LicenseClaims claims, string privatePem, string keyId)
    {
        var header = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new Header("ES256", TokenType, keyId), LicenseJson.Options));
        var payload = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(claims, LicenseJson.Options));
        var signingInput = Encoding.ASCII.GetBytes(header + "." + payload);
        return header + "." + payload + "." + Base64Url.Encode(Es256.Sign(signingInput, privatePem));
    }

    /// <summary>Verifies the signature with any of the trusted public keys and returns the claims.</summary>
    public static (LicenseClaims? Claims, TokenError Error) Verify(string token, IReadOnlyCollection<string> trustedPublicPems)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return (null, TokenError.Malformed);
        try
        {
            var header = JsonSerializer.Deserialize<Header>(Base64Url.Decode(parts[0]), LicenseJson.Options);
            if (header is null || header.Alg != "ES256") return (null, TokenError.Malformed);
            if (header.Typ != TokenType) return (null, TokenError.WrongType);
            var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            var signature = Base64Url.Decode(parts[2]);
            if (!trustedPublicPems.Any(pem => Es256.Verify(signingInput, signature, pem))) return (null, TokenError.BadSignature);
            var claims = JsonSerializer.Deserialize<LicenseClaims>(Base64Url.Decode(parts[1]), LicenseJson.Options);
            return claims is null ? (null, TokenError.Malformed) : (claims, TokenError.None);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return (null, TokenError.Malformed);
        }
    }

    /// <summary>Reads claims WITHOUT verifying (display/diagnostics only).</summary>
    public static LicenseClaims? PeekUnverified(string token)
    {
        try
        {
            var parts = token.Split('.');
            return parts.Length == 3 ? JsonSerializer.Deserialize<LicenseClaims>(Base64Url.Decode(parts[1]), LicenseJson.Options) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>Signs and verifies update manifests (separate key from licences).</summary>
public static class ManifestSigning
{
    public static SignedManifest Sign(ReleaseInfo release, string privatePem, string keyId)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(release, LicenseJson.Options);
        return new SignedManifest(Base64Url.Encode(payload), Base64Url.Encode(Es256.Sign(payload, privatePem)), keyId);
    }

    public static ReleaseInfo? Verify(SignedManifest manifest, IReadOnlyCollection<string> trustedPublicPems)
    {
        try
        {
            var payload = Base64Url.Decode(manifest.Payload);
            var signature = Base64Url.Decode(manifest.Signature);
            if (!trustedPublicPems.Any(p => Es256.Verify(payload, signature, p))) return null;
            return JsonSerializer.Deserialize<ReleaseInfo>(payload, LicenseJson.Options);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Licence keys "RFST-XXXX-XXXX-XXXX-XXXX": 15 random Crockford base32 characters (75 bits from a
/// cryptographic RNG) plus one check character, so typing errors are caught before contacting the server.
/// </summary>
public static class LicenseKeys
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32 (no I, L, O, U)

    public static string Generate(string prefix = "RFST")
    {
        Span<char> body = stackalloc char[16];
        for (var i = 0; i < 15; i++) body[i] = Alphabet[RandomNumberGenerator.GetInt32(32)];
        body[15] = Check(body[..15]);
        return $"{prefix}-{new string(body[..4])}-{new string(body[4..8])}-{new string(body[8..12])}-{new string(body[12..16])}";
    }

    /// <summary>Normalises user input (case, spaces, confusable letters) to the canonical form, or null if invalid.</summary>
    public static string? Normalize(string? input, string prefix = "RFST")
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = new string([.. input.ToUpperInvariant().Where(char.IsLetterOrDigit)]);
        if (s.StartsWith(prefix, StringComparison.Ordinal)) s = s[prefix.Length..];
        s = s.Replace('O', '0').Replace('I', '1').Replace('L', '1');
        if (s.Length != 16 || s.Any(c => !Alphabet.Contains(c))) return null;
        if (Check(s.AsSpan(0, 15)) != s[15]) return null;
        return $"{prefix}-{s[..4]}-{s[4..8]}-{s[8..12]}-{s[12..16]}";
    }

    /// <summary>
    /// Check character: Σ vᵢ·(i + 1) modulo the prime 31, where vᵢ is the base-32 value of body character i.
    /// Every weight is invertible modulo 31, so a single substitution or an adjacent transposition changes
    /// the check unless the two characters differ by 31 (only “0” ↔ “Z”). The check is never “Z”, so any
    /// error in the check character itself is detected.
    /// </summary>
    private static char Check(ReadOnlySpan<char> body)
    {
        var sum = 0;
        for (var i = 0; i < body.Length; i++) sum = (sum + Alphabet.IndexOf(body[i]) * (i + 1)) % 31;
        return Alphabet[sum];
    }

    /// <summary>Server-side storage form: HMAC-SHA256 of the canonical key with a server secret (hex).</summary>
    public static string Hash(string canonicalKey, byte[] pepper)
        => Convert.ToHexStringLower(HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(canonicalKey)));

    /// <summary>Last four characters for display ("…ABCD").</summary>
    public static string Hint(string canonicalKey) => canonicalKey[^4..];
}
