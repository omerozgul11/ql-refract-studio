using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using RefractionStudio.Licensing;

namespace RefractionStudio.Infrastructure;

/// <summary>Result of an update check.</summary>
/// <param name="Available">A newer verified release exists.</param>
/// <param name="Release">The release.</param>
/// <param name="Message">Explanation (also for "up to date" or errors).</param>
/// <param name="RequiresManualInstall">The installed version is older than the release's minimum version for
/// in-place updates: the user must download and run the installer manually (see the release notes).</param>
public sealed record UpdateCheck(bool Available, ReleaseInfo? Release, string Message, bool RequiresManualInstall = false);

/// <summary>
/// Automatic updates. The feed returns a manifest signed with the release key (ES256, separate from the
/// licence key); the manifest is verified with the embedded public key, the installer's SHA-256 is
/// verified after download, and on Windows the installer's Authenticode signature is checked before it
/// is started. Nothing is installed without the user's confirmation.
/// </summary>
public sealed class UpdateService(HttpClient http, AppPaths paths, IReadOnlyCollection<string> trustedReleaseKeys, ILogger<UpdateService> log)
{
    public async Task<UpdateCheck> CheckAsync(string channel, string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync($"api/updates/{AppInfo.ProductCode}/{Uri.EscapeDataString(channel)}?current={Uri.EscapeDataString(currentVersion)}", ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return new UpdateCheck(false, null, "Refraction Studio is up to date.");
            if (!response.IsSuccessStatusCode) return new UpdateCheck(false, null, $"The update service is unavailable ({(int)response.StatusCode}).");
            var manifest = await response.Content.ReadFromJsonAsync<SignedManifest>(LicenseJson.Options, ct).ConfigureAwait(false);
            if (manifest is null) return new UpdateCheck(false, null, "The update service returned an empty response.");
            var release = ManifestSigning.Verify(manifest, trustedReleaseKeys);
            if (release is null)
            {
                log.LogWarning("Update manifest failed signature verification (key {KeyId})", manifest.KeyId);
                return new UpdateCheck(false, null, "An update was offered but its signature could not be verified, so it was ignored.");
            }
            if (release.Product != AppInfo.ProductCode || !IsNewer(release.Version, currentVersion))
                return new UpdateCheck(false, null, "Refraction Studio is up to date.");
            if (!release.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return new UpdateCheck(false, null, "An update was offered over an insecure connection and was ignored.");
            if (release.MinimumVersion is { Length: > 0 } minimum && CompareVersions(currentVersion, minimum) < 0)
                return new UpdateCheck(true, release, $"Version {release.Version} is available. It cannot be installed over version {currentVersion} automatically: " +
                                                      $"download the installer and read the release notes first.", RequiresManualInstall: true);
            return new UpdateCheck(true, release, $"Version {release.Version} is available.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            return new UpdateCheck(false, null, "Could not check for updates (no connection to the update service).");
        }
    }

    public static bool IsNewer(string candidate, string current) => CompareVersions(candidate, current) > 0;

    /// <summary>
    /// Semantic Versioning 2.0 precedence: numeric major.minor.patch, then a version without a
    /// pre-release suffix ranks above one with it; pre-release identifiers are compared one by one
    /// (numeric ones numerically and below alphanumeric ones; fewer identifiers rank lower). Build
    /// metadata ("+…") is ignored.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        static (long[] Core, string[] Pre) Parse(string v)
        {
            var plus = v.IndexOf('+');
            if (plus >= 0) v = v[..plus];
            var dash = v.IndexOf('-');
            var core = dash >= 0 ? v[..dash] : v;
            var pre = dash >= 0 ? v[(dash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries) : [];
            return ([.. core.Split('.').Select(p => long.TryParse(p, out var n) ? n : 0)], pre);
        }
        var (ca, pa) = Parse(a.Trim());
        var (cb, pb) = Parse(b.Trim());
        for (var i = 0; i < Math.Max(ca.Length, cb.Length); i++)
        {
            var x = i < ca.Length ? ca[i] : 0;
            var y = i < cb.Length ? cb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        if (pa.Length == 0 || pb.Length == 0) return Math.Sign(pb.Length.CompareTo(pa.Length));
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var na = long.TryParse(pa[i], out var xa);
            var nb = long.TryParse(pb[i], out var xb);
            var c = (na, nb) switch
            {
                (true, true) => xa.CompareTo(xb),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(pa[i], pb[i]),
            };
            if (c != 0) return Math.Sign(c);
        }
        return pa.Length.CompareTo(pb.Length);
    }

    /// <summary>Downloads the installer and verifies its SHA-256 (and Authenticode on Windows). Returns the local path.</summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct = default)
    {
        var name = $"RefractionStudio-{release.Version}-setup.exe";
        var target = Path.Combine(paths.Updates, name);
        var temp = target + ".part";
        using (var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.Size;
            await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(temp);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hasher.AppendData(buffer, 0, read);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
            var hash = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                dst.Close();
                File.Delete(temp);
                throw new InvalidOperationException("The downloaded update does not match its published checksum and was deleted.");
            }
        }
        File.Move(temp, target, overwrite: true);
        if (OperatingSystem.IsWindows() && !Authenticode.IsTrusted(target))
        {
            File.Delete(target);
            throw new InvalidOperationException("The downloaded update is not signed with a trusted code-signing certificate and was deleted.");
        }
        log.LogInformation("Update {Version} downloaded and verified", release.Version);
        return target;
    }

    /// <summary>Starts the verified installer (Inno Setup, silent with progress) and lets the caller exit.</summary>
    [SupportedOSPlatform("windows")]
    public static void LaunchInstaller(string installerPath)
        => Process.Start(new ProcessStartInfo(installerPath, "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /SP-") { UseShellExecute = true });
}

/// <summary>Authenticode verification through WinVerifyTrust.</summary>
[SupportedOSPlatform("windows")]
public static class Authenticode
{
    public static bool IsTrusted(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2, // WTD_UI_NONE
                fdwRevocationChecks = 0x00000001, // WTD_REVOKE_WHOLECHAIN
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 0,
                dwProvFlags = 0x00000080, // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
