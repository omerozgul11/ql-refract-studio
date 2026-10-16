namespace RefractionStudio.Licensing.Tests;

public sealed class DeviceFingerprintTests
{
    private static FixedIdentifierSource Source(string guid = "4c4c4544-0042-3510-8048-b4c04f4d4e31", string uuid = "11111111-2222-3333-4444-555555555555", string board = "Dell Inc./0XYZ12")
        => new(new MachineIdentifier("machine-guid", guid), new MachineIdentifier("smbios-uuid", uuid), new MachineIdentifier("baseboard", board));

    [Fact]
    public void Fingerprint_is_stable_for_the_same_identifiers()
    {
        var a = new DeviceFingerprint("salt-1", Source()).Compute(false);
        var b = new DeviceFingerprint("salt-1", Source()).Compute(false);

        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.Equal(a.ComponentHashes, b.ComponentHashes);
        Assert.Matches("^[0-9a-f]{64}$", a.Fingerprint);
    }

    [Fact]
    public void Fingerprint_depends_on_the_application_salt()
    {
        var a = new DeviceFingerprint("refraction-studio", Source()).Compute(false);
        var b = new DeviceFingerprint("masw-studio", Source()).Compute(false);

        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
        Assert.Empty(a.ComponentHashes.Intersect(b.ComponentHashes));
    }

    [Fact]
    public void Each_identifier_gets_its_own_component_hash()
    {
        var device = new DeviceFingerprint("salt", Source()).Compute(false);

        Assert.Equal(3, device.ComponentHashes.Count);
        Assert.Equal(3, device.ComponentHashes.Distinct().Count());
        Assert.All(device.ComponentHashes, h => Assert.Matches("^[0-9a-f]{64}$", h));
        Assert.DoesNotContain(device.ComponentHashes, h => h == device.Fingerprint);
    }

    [Fact]
    public void Changing_one_component_changes_only_that_component_hash()
    {
        var before = new DeviceFingerprint("salt", Source()).Compute(false);
        var boardChanged = new DeviceFingerprint("salt", Source(board: "ASUS/PRIME")).Compute(false);
        var uuidChanged = new DeviceFingerprint("salt", Source(uuid: "99999999-2222-3333-4444-555555555555")).Compute(false);

        // The fingerprint uses the first two identifiers only.
        Assert.Equal(before.Fingerprint, boardChanged.Fingerprint);
        Assert.Equal(2, before.ComponentHashes.Intersect(boardChanged.ComponentHashes).Count());
        Assert.NotEqual(before.Fingerprint, uuidChanged.Fingerprint);
        Assert.Equal(2, before.ComponentHashes.Intersect(uuidChanged.ComponentHashes).Count());
    }

    [Fact]
    public void Identifiers_are_normalised_before_hashing()
    {
        var a = new DeviceFingerprint("salt", Source(guid: "{abcdef00-1111-2222-3333-444455556666}")).Compute(false);
        var b = new DeviceFingerprint("salt", Source(guid: "  ABCDEF00-1111-2222-3333-444455556666 ")).Compute(false);

        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void Machine_name_is_included_only_when_allowed()
    {
        var fp = new DeviceFingerprint("salt", Source());

        Assert.Null(fp.Compute(false).MachineName);
        Assert.Equal(Environment.MachineName, fp.Compute(true).MachineName);
        Assert.False(string.IsNullOrWhiteSpace(fp.Compute(false).OsVersion));
    }

    [Fact]
    public void Raw_identifiers_never_appear_in_the_device_description()
    {
        var device = new DeviceFingerprint("salt", Source()).Compute(false);
        var all = string.Join("|", [device.Fingerprint, .. device.ComponentHashes]);

        Assert.DoesNotContain("4C4C4544", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dell", all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Computer_without_identifiers_is_reported()
    {
        var fp = new DeviceFingerprint("salt", new FixedIdentifierSource(new MachineIdentifier("machine-guid", "   ")));

        Assert.Throws<InvalidOperationException>(() => fp.Compute(false));
    }
}

public sealed class FileLicenseStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "store-tests", Guid.NewGuid().ToString("N"));

    private string PathFor(string name) => Path.Combine(_dir, name);

    private static StoredLicense Sample() => new("header.payload.signature", new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 5, 2, 9, 30, 0, TimeSpan.Zero), null, null);

    [Fact]
    public void Round_trip_preserves_the_data()
    {
        var store = new FileLicenseStore(PathFor("licence.dat"), "fingerprint-a");

        store.Save(Sample());

        Assert.Equal(Sample(), store.Load());
        Assert.False(File.Exists(PathFor("licence.dat.tmp")));
    }

    [Fact]
    public void File_is_encrypted()
    {
        var store = new FileLicenseStore(PathFor("licence.dat"), "fingerprint-a");
        store.Save(Sample());

        var bytes = File.ReadAllText(PathFor("licence.dat"), System.Text.Encoding.Latin1);

        Assert.DoesNotContain("header.payload", bytes, StringComparison.Ordinal);
    }

    [Fact]
    public void Tampered_file_loads_as_null()
    {
        var path = PathFor("licence.dat");
        new FileLicenseStore(path, "fingerprint-a").Save(Sample());
        var bytes = File.ReadAllBytes(path);
        bytes[^3] ^= 0x40;
        File.WriteAllBytes(path, bytes);

        Assert.Null(new FileLicenseStore(path, "fingerprint-a").Load());
    }

    [Fact]
    public void File_copied_to_another_computer_loads_as_null()
    {
        var path = PathFor("licence.dat");
        new FileLicenseStore(path, "fingerprint-a").Save(Sample());

        Assert.Null(new FileLicenseStore(path, "fingerprint-b").Load());
    }

    [Fact]
    public void Missing_or_truncated_file_loads_as_null_and_clear_removes_it()
    {
        var path = PathFor("licence.dat");
        var store = new FileLicenseStore(path, "fingerprint-a");
        Assert.Null(store.Load());

        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Null(store.Load());

        store.Save(Sample());
        store.Clear();
        Assert.False(File.Exists(path));
        Assert.Null(store.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
