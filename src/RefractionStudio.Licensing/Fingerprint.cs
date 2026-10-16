using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RefractionStudio.Licensing;

/// <summary>A raw machine identifier (never transmitted; only salted hashes leave the computer).</summary>
public sealed record MachineIdentifier(string Name, string Value);

/// <summary>Reads stable machine identifiers.</summary>
public interface IMachineIdentifierSource
{
    IReadOnlyList<MachineIdentifier> Read();
}

/// <summary>
/// Privacy-preserving device fingerprint. Identifiers are normalised (trimmed, upper-cased) and only
/// salted SHA-256 hashes are used: fingerprint = SHA-256(salt | machine id | firmware UUID); each
/// component is also hashed separately so the server can recognise the computer after a single
/// hardware change (adding RAM changes none of the identifiers used). MAC and IP addresses are not used.
/// </summary>
public sealed class DeviceFingerprint
{
    private readonly string _salt;
    private readonly IMachineIdentifierSource _source;

    public DeviceFingerprint(string applicationSalt, IMachineIdentifierSource? source = null)
    {
        _salt = applicationSalt;
        _source = source ?? DefaultSource();
    }

    public static IMachineIdentifierSource DefaultSource()
        => OperatingSystem.IsWindows() ? new WindowsIdentifierSource() : new UnixIdentifierSource();

    public DeviceInfo Compute(bool includeMachineName)
    {
        var ids = _source.Read().Select(i => i with { Value = Normalize(i.Value) }).Where(i => i.Value.Length > 0).ToList();
        if (ids.Count == 0) throw new InvalidOperationException("No stable machine identifier could be read on this computer.");
        var primary = ids.Take(2).Select(i => i.Value);
        var fingerprint = Hash(_salt + "|" + string.Join("|", primary));
        var components = ids.Select(i => Hash(_salt + "|" + i.Name + "|" + i.Value)).ToList();
        var os = RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture;
        return new DeviceInfo(fingerprint, components, includeMachineName ? Environment.MachineName : null, os);
    }

    private static string Normalize(string s) => s.Trim().Trim('{', '}').ToUpperInvariant();

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}

/// <summary>Windows: MachineGuid (registry), SMBIOS system UUID (firmware table), baseboard and system volume serial.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsIdentifierSource : IMachineIdentifierSource
{
    public IReadOnlyList<MachineIdentifier> Read()
    {
        var list = new List<MachineIdentifier>();
        var guid = RegistryValue(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid");
        if (guid is not null) list.Add(new("machine-guid", guid));
        var uuid = SmbiosUuid();
        if (uuid is not null) list.Add(new("smbios-uuid", uuid));
        var board = RegistryValue(@"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardManufacturer") + "/" + RegistryValue(@"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct");
        if (board.Length > 1) list.Add(new("baseboard", board));
        var serial = VolumeSerial(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (serial is not null) list.Add(new("system-volume", serial));
        return list;
    }

    private static string? RegistryValue(string path, string name)
    {
        // Avoids a dependency on Microsoft.Win32.Registry by using the advapi32 API directly.
        const uint HKEY_LOCAL_MACHINE = 0x80000002;
        const int KEY_READ_WOW64_64 = 0x20019 | 0x0100;
        if (RegOpenKeyEx(new IntPtr(unchecked((int)HKEY_LOCAL_MACHINE)), path, 0, KEY_READ_WOW64_64, out var key) != 0) return null;
        try
        {
            var size = 512;
            var buffer = new byte[size];
            if (RegQueryValueEx(key, name, IntPtr.Zero, out _, buffer, ref size) != 0) return null;
            return Encoding.Unicode.GetString(buffer, 0, Math.Max(0, size - 2)).TrimEnd('\0');
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static string? SmbiosUuid()
    {
        const uint RSMB = 0x52534D42;
        var size = GetSystemFirmwareTable(RSMB, 0, IntPtr.Zero, 0);
        if (size == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetSystemFirmwareTable(RSMB, 0, buffer, size) != size) return null;
            var data = new byte[size];
            Marshal.Copy(buffer, data, 0, (int)size);
            // RawSMBIOSData: 8-byte header, then structures. Type 1 (System Information) holds the UUID at offset 8.
            var pos = 8;
            while (pos + 4 < data.Length)
            {
                var type = data[pos];
                var length = data[pos + 1];
                if (type == 1 && length >= 0x18 && pos + 8 + 16 <= data.Length)
                {
                    var uuid = data.AsSpan(pos + 8, 16);
                    if (uuid.ToArray().All(b => b == 0) || uuid.ToArray().All(b => b == 0xFF)) return null;
                    return Convert.ToHexString(uuid);
                }
                // Skip the formatted area and the string set (terminated by a double NUL).
                var next = pos + length;
                while (next + 1 < data.Length && !(data[next] == 0 && data[next + 1] == 0)) next++;
                pos = next + 2;
                if (type == 127) break;
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? VolumeSerial(string windowsDirectory)
    {
        var root = Path.GetPathRoot(windowsDirectory);
        if (root is null) return null;
        return GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0) ? serial.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
    private static extern int RegOpenKeyEx(IntPtr hKey, string subKey, int options, int samDesired, out IntPtr result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
    private static extern int RegQueryValueEx(IntPtr hKey, string valueName, IntPtr reserved, out int type, byte[] data, ref int dataSize);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);

    [DllImport("kernel32.dll")]
    private static extern uint GetSystemFirmwareTable(uint providerSignature, uint tableId, IntPtr buffer, uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string rootPathName, StringBuilder? volumeName, int volumeNameSize, out uint serialNumber,
        out uint maxComponentLength, out uint fileSystemFlags, StringBuilder? fileSystemName, int fileSystemNameSize);
}

/// <summary>Linux/macOS (development and future ports): machine-id and DMI product UUID when readable.</summary>
public sealed class UnixIdentifierSource : IMachineIdentifierSource
{
    public IReadOnlyList<MachineIdentifier> Read()
    {
        var list = new List<MachineIdentifier>();
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            if (TryRead(path) is { } id) { list.Add(new("machine-id", id)); break; }
        }
        if (TryRead("/sys/class/dmi/id/product_uuid") is { } uuid) list.Add(new("dmi-uuid", uuid));
        if (TryRead("/sys/class/dmi/id/board_name") is { } board) list.Add(new("baseboard", (TryRead("/sys/class/dmi/id/board_vendor") ?? "") + "/" + board));
        return list;
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: > 0 } s ? s : null : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Fixed identifiers (tests).</summary>
public sealed class FixedIdentifierSource(params MachineIdentifier[] ids) : IMachineIdentifierSource
{
    public IReadOnlyList<MachineIdentifier> Read() => ids;
}
