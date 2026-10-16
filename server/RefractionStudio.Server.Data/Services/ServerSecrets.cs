using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RefractionStudio.Licensing;

namespace RefractionStudio.Server.Data.Services;

/// <summary>
/// Licensing configuration (section "Licensing"). Secrets come from environment variables or files
/// mounted from a secret store — never from committed configuration.
/// </summary>
public sealed class LicensingServerOptions
{
    public const string Section = "Licensing";

    /// <summary>PEM (PKCS#8) of the ES256 private key that signs licence and trial tokens.</summary>
    public string? SigningKeyPem { get; set; }

    /// <summary>Path of a file containing <see cref="SigningKeyPem"/>.</summary>
    public string? SigningKeyPath { get; set; }

    /// <summary>
    /// Public key(s) of retired signing keys whose tokens are still accepted by validation and deactivation
    /// (key rotation). Several PEM blocks may be concatenated.
    /// </summary>
    public string? PreviousSigningPublicKeyPem { get; set; }

    /// <summary>Path of a file containing <see cref="PreviousSigningPublicKeyPem"/>.</summary>
    public string? PreviousSigningPublicKeyPath { get; set; }

    /// <summary>PEM of the ES256 private key that signs update manifests (separate from the licence key).</summary>
    public string? ReleaseSigningKeyPem { get; set; }

    /// <summary>Path of a file containing <see cref="ReleaseSigningKeyPem"/>.</summary>
    public string? ReleaseSigningKeyPath { get; set; }

    /// <summary>Base64 server secret (at least 32 bytes) used to HMAC licence keys and IP addresses.</summary>
    public string? KeyPepper { get; set; }

    /// <summary>Path of a file containing <see cref="KeyPepper"/>.</summary>
    public string? KeyPepperPath { get; set; }

    /// <summary>Days a trial token can be used offline (capped at the trial end).</summary>
    public int TrialOfflineDays { get; set; } = 3;

    /// <summary>Apply EF Core migrations at startup.</summary>
    public bool AutoMigrate { get; set; }
}

/// <summary>
/// Loads and holds the server's secrets: the token signing key, the release signing key and the key pepper.
/// Keys are loaded on first use and validated; a missing or malformed secret fails with an explanation that
/// names the configuration setting (but never echoes its value).
/// </summary>
public sealed class ServerSecrets
{
    private readonly LicensingServerOptions _options;
    private readonly Lazy<byte[]> _pepper;
    private readonly Lazy<SigningKey> _signing;
    private readonly Lazy<SigningKey> _release;
    private readonly Lazy<IReadOnlyList<string>> _trusted;

    public ServerSecrets(IOptions<LicensingServerOptions> options)
    {
        _options = options.Value;
        _pepper = new(LoadPepper);
        _signing = new(() => LoadPrivateKey(_options.SigningKeyPem, _options.SigningKeyPath, "Licensing:SigningKeyPem", "Licensing:SigningKeyPath"));
        _release = new(() => LoadPrivateKey(_options.ReleaseSigningKeyPem, _options.ReleaseSigningKeyPath, "Licensing:ReleaseSigningKeyPem", "Licensing:ReleaseSigningKeyPath"));
        _trusted = new(LoadTrusted);
    }

    /// <summary>HMAC pepper for licence keys.</summary>
    public byte[] KeyPepper => _pepper.Value;

    /// <summary>Licence/trial token signing key.</summary>
    public SigningKey TokenSigningKey => _signing.Value;

    /// <summary>Update-manifest signing key.</summary>
    public SigningKey ReleaseSigningKey => _release.Value;

    /// <summary>Public keys whose tokens the server accepts (current first, then retired keys).</summary>
    public IReadOnlyList<string> TrustedTokenPublicKeys => _trusted.Value;

    /// <summary>Derives an independent 32-byte key for a purpose (IP hashing, outbox encryption…).</summary>
    public byte[] DeriveKey(string purpose)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, KeyPepper, 32, [], Encoding.UTF8.GetBytes("RefractionStudio.Server." + purpose));

    /// <summary>Forces loading of every secret needed by the licence server (fail fast at startup).</summary>
    public void ValidateForLicenseServer()
    {
        _ = KeyPepper;
        _ = TokenSigningKey;
        _ = ReleaseSigningKey;
        _ = TrustedTokenPublicKeys;
        if (TokenSigningKey.KeyId == ReleaseSigningKey.KeyId)
            throw new InvalidOperationException("The release signing key must be different from the licence token signing key.");
    }

    private byte[] LoadPepper()
    {
        var text = Resolve(_options.KeyPepper, _options.KeyPepperPath, "Licensing:KeyPepperPath")
                   ?? throw new InvalidOperationException("Licensing:KeyPepper (or Licensing:KeyPepperPath) is not configured. Generate one with: openssl rand -base64 32");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Licensing:KeyPepper must be base64 encoded.");
        }
        if (bytes.Length < 32) throw new InvalidOperationException("Licensing:KeyPepper must contain at least 32 random bytes.");
        return bytes;
    }

    private static SigningKey LoadPrivateKey(string? pem, string? path, string pemSetting, string pathSetting)
    {
        var text = Resolve(pem, path, pathSetting)
                   ?? throw new InvalidOperationException($"{pemSetting} (or {pathSetting}) is not configured.");
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(text);
            if (ec.KeySize != 256) throw new InvalidOperationException($"The key in {pemSetting} is not a P-256 key.");
            var privatePem = ec.ExportPkcs8PrivateKeyPem();
            var publicPem = ec.ExportSubjectPublicKeyInfoPem();
            return new SigningKey(privatePem, publicPem, Es256.KeyId(publicPem));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException($"The key in {pemSetting} could not be read as an ECDSA P-256 private key (PEM).", ex);
        }
    }

    private IReadOnlyList<string> LoadTrusted()
    {
        var list = new List<string> { TokenSigningKey.PublicPem };
        var previous = Resolve(_options.PreviousSigningPublicKeyPem, _options.PreviousSigningPublicKeyPath, "Licensing:PreviousSigningPublicKeyPath");
        if (previous is null) return list;
        const string begin = "-----BEGIN PUBLIC KEY-----";
        const string end = "-----END PUBLIC KEY-----";
        var index = 0;
        while ((index = previous.IndexOf(begin, index, StringComparison.Ordinal)) >= 0)
        {
            var stop = previous.IndexOf(end, index, StringComparison.Ordinal);
            if (stop < 0) break;
            var block = previous[index..(stop + end.Length)];
            try
            {
                Es256.KeyId(block);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException("Licensing:PreviousSigningPublicKeyPem contains a public key that could not be read.", ex);
            }
            list.Add(block);
            index = stop + end.Length;
        }
        return list;
    }

    private static string? Resolve(string? inline, string? path, string pathSetting)
    {
        if (!string.IsNullOrWhiteSpace(inline)) return inline.Replace("\\n", "\n", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!File.Exists(path)) throw new InvalidOperationException($"The file named by {pathSetting} does not exist.");
        return File.ReadAllText(path);
    }
}

/// <summary>A loaded ES256 key pair with its key id.</summary>
public sealed record SigningKey(string PrivatePem, string PublicPem, string KeyId);
