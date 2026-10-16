using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using RefractionStudio.Domain;
using RefractionStudio.Domain.Units;

namespace RefractionStudio.Infrastructure;

/// <summary>Per-user folders of the application (never inside project folders).</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "QuakeLogic", "RefractionStudio");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Recovery);
        Directory.CreateDirectory(Updates);
        Directory.CreateDirectory(Crashes);
    }

    public string Root { get; }
    public string Logs => Path.Combine(Root, "Logs");
    public string Recovery => Path.Combine(Root, "Recovery");
    public string Updates => Path.Combine(Root, "Updates");
    public string Crashes => Path.Combine(Root, "Crashes");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Catalog => Path.Combine(Root, "catalog.db");
    public string License => Path.Combine(Root, "license.dat");

    /// <summary>Default folder for new projects: Documents\Refraction Studio.</summary>
    public static string DefaultProjectFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Refraction Studio");
}

/// <summary>Application identity.</summary>
public static class AppInfo
{
    public const string ProductCode = "refraction-studio";
    public const string ProductName = "Refraction Studio";
    public const string Company = "QuakeLogic Inc.";
    public const string LicenseKeyPrefix = "RFST";

    /// <summary>Salt mixed into device fingerprints (not secret; makes fingerprints product-specific).</summary>
    public const string FingerprintSalt = "QuakeLogic.RefractionStudio.v1";

    public const string DefaultLicenseServer = "https://licensing.quakelogic.net/";

    public const string PrivacyPolicyUrl = "https://www.quakelogic.net/privacy";

    public const string SupportUrl = "https://www.quakelogic.net/support";

    public static string Version { get; } = (Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly)
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    /// <summary>Trusted public keys embedded in the application (private keys never ship).</summary>
    public static IReadOnlyCollection<string> LicensePublicKeys { get; } = [ReadKey("license-public")];

    public static IReadOnlyCollection<string> ReleasePublicKeys { get; } = [ReadKey("release-public")];

    private static string ReadKey(string name)
    {
        using var s = typeof(AppInfo).Assembly.GetManifestResourceStream($"RefractionStudio.Keys.{name}.pem")
            ?? throw new InvalidOperationException($"The embedded key {name} is missing from this build.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum UpdateMode
{
    Automatic,
    NotifyOnly,
    Manual,
}

public enum UserMode
{
    Guided,
    Expert,
}

/// <summary>User preferences (stored separately from projects).</summary>
public sealed record UserSettings
{
    // General
    public UserMode Mode { get; init; } = UserMode.Guided;
    public string? UserName { get; init; }
    public int AutosaveMinutes { get; init; } = 3;
    public bool ShowWelcome { get; init; } = true;
    public string? LastFolder { get; init; }

    // Appearance
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public string Colormap { get; init; } = "Viridis";
    public double UiScale { get; init; } = 1.0;

    // Units
    public UnitSystem Units { get; init; } = UnitSystem.SI;
    public ModulusDisplayUnit ModulusUnit { get; init; } = ModulusDisplayUnit.MPa;

    // Processing and picking defaults (applied to new lines)
    public ProcessingPipeline DefaultPipeline { get; init; } = ProcessingPipeline.Recommended;
    public PickerSettings DefaultPicker { get; init; } = new();
    public TomographySettings DefaultTomography { get; init; } = new();

    // Reporting
    public string PaperSize { get; init; } = "A4";
    public CompanyBranding Branding { get; init; } = new();

    // Licence
    public bool SendMachineName { get; init; }
    public string LicenseServer { get; init; } = AppInfo.DefaultLicenseServer;

    // Updates
    public UpdateMode Updates { get; init; } = UpdateMode.NotifyOnly;
    public string UpdateChannel { get; init; } = "stable";
    public DateTimeOffset? LastUpdateCheck { get; init; }

    // Advanced
    public bool VerboseLogging { get; init; }
    public int TraceCacheMegabytes { get; init; } = 320;
}

/// <summary>Loads and saves <see cref="UserSettings"/> as JSON; a damaged file falls back to defaults (and is kept as .bak).</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(DomainJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public UserSettings Load()
    {
        try
        {
            if (!File.Exists(path)) return new UserSettings();
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), Json) ?? new UserSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            try { File.Copy(path, path + ".bak", overwrite: true); } catch (IOException) { }
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, path, overwrite: true);
    }
}
