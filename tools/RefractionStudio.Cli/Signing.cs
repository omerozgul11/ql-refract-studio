using System.Text.Json;
using RefractionStudio.Licensing;

namespace RefractionStudio.Cli;

/// <summary>Key generation and release-manifest signing. Private keys never belong in the repository or the app.</summary>
public static class Signing
{
    public static Task<int> KeysAsync(Args a)
    {
        var folder = Path.GetFullPath(a.Option("out"));
        Directory.CreateDirectory(folder);
        var (priv, pub) = Es256.GenerateKeyPair();
        var kid = Es256.KeyId(pub);
        var privPath = Path.Combine(folder, $"signing-{kid}.private.pem");
        var pubPath = Path.Combine(folder, $"signing-{kid}.public.pem");
        if (File.Exists(privPath)) throw new InvalidOperationException($"{privPath} already exists; refusing to overwrite a key.");
        File.WriteAllText(privPath, priv);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(privPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(pubPath, pub);
        Console.WriteLine($"Key id {kid}");
        Console.WriteLine($"Private key: {privPath}  (store in the secret manager; never commit or ship it)");
        Console.WriteLine($"Public key:  {pubPath}  (embed in the application)");
        return Task.FromResult(0);
    }

    public static Task<int> SignManifestAsync(Args a)
    {
        var release = JsonSerializer.Deserialize<ReleaseInfo>(File.ReadAllText(a.Positional(0, "manifest")), LicenseJson.Options)
            ?? throw new ArgumentException("The manifest is empty.");
        var priv = File.ReadAllText(a.Option("key"));
        using var ec = System.Security.Cryptography.ECDsa.Create();
        ec.ImportFromPem(priv);
        var kid = Es256.KeyId(ec.ExportSubjectPublicKeyInfoPem());
        var signed = ManifestSigning.Sign(release, priv, kid);
        File.WriteAllText(a.Option("out"), JsonSerializer.Serialize(signed, LicenseJson.Options));
        Console.WriteLine($"Signed {release.Product} {release.Version} ({release.Channel}) with key {kid}.");
        return Task.FromResult(0);
    }
}
