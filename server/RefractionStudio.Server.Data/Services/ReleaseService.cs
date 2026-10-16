using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.Licensing;
using RefractionStudio.Server.Data.Entities;

namespace RefractionStudio.Server.Data.Services;

/// <summary>Semantic version "major.minor.patch[-prerelease][+build]" with SemVer 2.0 precedence.</summary>
public sealed record SemanticVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = new SemanticVersion(0, 0, 0, null);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('v', 'V');
        var plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) s = s[..plus];
        string? pre = null;
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0 || pre.Split('.').Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) return false;
        }
        var parts = s.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        var numbers = new int[3];
        for (var i = 0; i < Math.Min(parts.Length, 3); i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }
        if (parts.Length == 4 && !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out _)) return false;
        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bn = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = (an, bn) switch
            {
                (true, true) => ai.CompareTo(bi),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Prerelease is null ? "" : "-" + Prerelease);
}

/// <summary>A new release entry.</summary>
public sealed record CreateReleaseCommand(string Product, string Version, string Channel, DateTimeOffset ReleasedAt, string DownloadUrl, string Sha256, long Size,
    string ReleaseNotes, bool Major, string? MinimumVersion, bool Publish);

/// <summary>
/// The update feed. Manifests are signed at request time with the release signing key — a key separate
/// from the licence token key — so a compromise of one does not affect the other.
/// </summary>
public sealed class ReleaseService(LicensingDbContext db, ServerSecrets secrets, TimeProvider clock)
{
    public static readonly IReadOnlyList<string> Channels = ["stable", "beta"];

    /// <summary>
    /// The newest published release newer than <paramref name="currentVersion"/> for the product and channel,
    /// signed; null when the application is up to date. The beta channel also receives stable releases.
    /// </summary>
    public async Task<SignedManifest?> GetLatestAsync(string product, string channel, string? currentVersion, CancellationToken ct)
    {
        var release = await FindLatestAsync(product, channel, currentVersion, ct).ConfigureAwait(false);
        if (release is null) return null;
        var info = new ReleaseInfo(release.Product, release.Version, release.Channel, release.ReleasedAt, release.DownloadUrl, release.Sha256, release.Size,
            release.ReleaseNotes, release.Major, release.MinimumVersion);
        var key = secrets.ReleaseSigningKey;
        return ManifestSigning.Sign(info, key.PrivatePem, key.KeyId);
    }

    internal async Task<SoftwareVersion?> FindLatestAsync(string product, string channel, string? currentVersion, CancellationToken ct)
    {
        channel = channel.ToLowerInvariant();
        if (!Channels.Contains(channel)) throw new ArgumentException("Unknown update channel.", nameof(channel));
        SemanticVersion? current = null;
        if (!string.IsNullOrWhiteSpace(currentVersion))
        {
            if (!SemanticVersion.TryParse(currentVersion, out var parsed)) throw new ArgumentException("The current version is not a valid version number.", nameof(currentVersion));
            current = parsed;
        }
        var channels = channel == "beta" ? new[] { "stable", "beta" } : ["stable"];
        var now = clock.GetDbNow();
        var candidates = await db.SoftwareVersions.AsNoTracking()
            .Where(v => v.Product == product && v.Published && channels.Contains(v.Channel) && v.ReleasedAt <= now)
            .ToListAsync(ct).ConfigureAwait(false);
        return candidates
            .Select(v => (Release: v, Parsed: SemanticVersion.TryParse(v.Version, out var p) ? p : null))
            .Where(x => x.Parsed is not null && (current is null || x.Parsed > current))
            .OrderByDescending(x => x.Parsed)
            .Select(x => x.Release)
            .FirstOrDefault();
    }

    /// <summary>Creates a release entry (validated). Saves changes.</summary>
    public async Task<SoftwareVersion> CreateAsync(CreateReleaseCommand c, Actor actor, CancellationToken ct)
    {
        if (!await db.LicenseProducts.AnyAsync(p => p.Code == c.Product, ct).ConfigureAwait(false)) throw new LicensingOperationException("Unknown product.");
        if (!SemanticVersion.TryParse(c.Version, out var version)) throw new LicensingOperationException("The version must look like 1.2.3 (optionally 1.2.3-beta.1).");
        var channel = c.Channel.Trim().ToLowerInvariant();
        if (!Channels.Contains(channel)) throw new LicensingOperationException("The channel must be “stable” or “beta”.");
        if (c.MinimumVersion is { Length: > 0 } min && !SemanticVersion.TryParse(min, out _)) throw new LicensingOperationException("The minimum version must look like 1.2.3.");
        if (!Uri.TryCreate(c.DownloadUrl?.Trim(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) throw new LicensingOperationException("The download URL must be an absolute https:// URL.");
        var sha = c.Sha256?.Trim().ToLowerInvariant() ?? "";
        if (!RequestValidation.IsSha256Hex(sha)) throw new LicensingOperationException("The SHA-256 must be 64 hexadecimal characters.");
        if (c.Size <= 0) throw new LicensingOperationException("The installer size must be a positive number of bytes.");
        var normalized = version.ToString();
        if (await db.SoftwareVersions.AnyAsync(v => v.Product == c.Product && v.Version == normalized, ct).ConfigureAwait(false))
            throw new LicensingOperationException($"Version {normalized} already exists.");
        var now = clock.GetDbNow();
        var release = new SoftwareVersion
        {
            Id = Guid.NewGuid(),
            Product = c.Product,
            Version = normalized,
            Channel = channel,
            ReleasedAt = c.ReleasedAt.ToUniversalTime(),
            DownloadUrl = url.ToString(),
            Sha256 = sha,
            Size = c.Size,
            ReleaseNotes = c.ReleaseNotes ?? "",
            Major = c.Major,
            MinimumVersion = string.IsNullOrWhiteSpace(c.MinimumVersion) ? null : c.MinimumVersion.Trim(),
            Published = c.Publish,
            CreatedAt = now,
        };
        db.SoftwareVersions.Add(release);
        db.AddAudit(now, actor, "release.create", "release", release.Id.ToString(), new { release.Product, release.Version, release.Channel, release.Published });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return release;
    }

    /// <summary>Publishes or withdraws a release.</summary>
    public async Task SetPublishedAsync(Guid id, bool published, Actor actor, CancellationToken ct)
    {
        var release = await db.SoftwareVersions.SingleOrDefaultAsync(v => v.Id == id, ct).ConfigureAwait(false)
                      ?? throw new LicensingOperationException("The release does not exist.");
        release.Published = published;
        db.AddAudit(clock.GetDbNow(), actor, published ? "release.publish" : "release.unpublish", "release", release.Id.ToString(), new { release.Product, release.Version });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
