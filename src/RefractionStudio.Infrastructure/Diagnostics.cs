using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace RefractionStudio.Infrastructure;

/// <summary>Structured local logging (Serilog compact JSON, daily files, 14 days retained).</summary>
public static partial class Logging
{
    public static ILoggerFactory Create(AppPaths paths, bool verbose)
    {
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .Enrich.WithProperty("App", AppInfo.ProductName)
            .Enrich.WithProperty("Version", AppInfo.Version)
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(paths.Logs, "refraction-studio-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, fileSizeLimitBytes: 20_000_000, rollOnFileSizeLimit: true)
            .CreateLogger();
        return LoggerFactory.Create(b => b.AddSerilog(logger, dispose: true));
    }

    /// <summary>Removes licence keys, tokens and e-mail addresses from text written to diagnostics.</summary>
    public static string Scrub(string text)
    {
        text = LicenseKeyPattern().Replace(text, "RFST-****-****-****-****");
        text = TokenPattern().Replace(text, "[token removed]");
        return EmailPattern().Replace(text, "[e-mail removed]");
    }

    [GeneratedRegex(@"RFST-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}", RegexOptions.IgnoreCase)]
    private static partial Regex LicenseKeyPattern();

    [GeneratedRegex(@"ey[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();
}

/// <summary>A crash report written before the application exits.</summary>
public sealed record CrashReport(DateTimeOffset Time, string Version, string Exception, string Message, string StackTrace, bool RecoveryCheckpointWritten);

/// <summary>
/// Global crash handling: unhandled exceptions are logged, a recovery checkpoint of the open project is
/// written, and a crash report is stored so that the next start can explain what happened. Nothing is
/// uploaded.
/// </summary>
public sealed class CrashHandler(AppPaths paths, Func<bool> writeRecoveryCheckpoint, Microsoft.Extensions.Logging.ILogger log)
{
    private int _handling;

    public void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Handle(e.ExceptionObject as Exception ?? new InvalidOperationException("Unknown fatal error"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    /// <summary>Handles a fatal exception (also called by the UI dispatcher's unhandled-exception hook).</summary>
    public void Handle(Exception ex)
    {
        if (Interlocked.Exchange(ref _handling, 1) == 1) return;
        var checkpoint = false;
        try
        {
            log.LogCritical(ex, "Unhandled exception");
            try
            {
                checkpoint = writeRecoveryCheckpoint();
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.LogError(inner, "Could not write the recovery checkpoint");
            }
            var report = new CrashReport(DateTimeOffset.UtcNow, AppInfo.Version, ex.GetType().FullName ?? "", Logging.Scrub(ex.Message), Logging.Scrub(ex.ToString()), checkpoint);
            File.WriteAllText(Path.Combine(paths.Crashes, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"), JsonSerializer.Serialize(report));
        }
        catch (IOException)
        {
            // Last resort: nothing more can be done safely.
        }
        finally
        {
            Interlocked.Exchange(ref _handling, 0);
        }
    }

    /// <summary>Crash reports not yet acknowledged by the user.</summary>
    public IReadOnlyList<(string Path, CrashReport Report)> PendingReports()
    {
        var list = new List<(string, CrashReport)>();
        foreach (var f in Directory.EnumerateFiles(paths.Crashes, "crash-*.json").Order())
        {
            try
            {
                if (JsonSerializer.Deserialize<CrashReport>(File.ReadAllText(f)) is { } r) list.Add((f, r));
            }
            catch (JsonException)
            {
            }
        }
        return list;
    }

    public void Acknowledge(string path)
    {
        var dest = Path.Combine(paths.Crashes, "acknowledged");
        Directory.CreateDirectory(dest);
        File.Move(path, Path.Combine(dest, Path.GetFileName(path)), overwrite: true);
    }
}

/// <summary>
/// Settings → Advanced → Export diagnostic package: logs, version, operating system and basic hardware information.
/// Survey/project files are included only when the user explicitly selects them.
/// </summary>
public static class DiagnosticPackage
{
    public static void Create(string zipPath, AppPaths paths, UserSettings settings, IReadOnlyList<string> userSelectedFiles)
    {
        var temp = zipPath + ".tmp";
        using (var fs = File.Create(temp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var info = new StringBuilder();
            info.AppendLine($"{AppInfo.ProductName} {AppInfo.Version}");
            info.AppendLine($"Created: {DateTimeOffset.UtcNow:u}");
            info.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            info.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
            info.AppendLine($"Process: {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} logical processors");
            info.AppendLine($"Memory available to the process: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)} MB");
            info.AppendLine($"Culture: {System.Globalization.CultureInfo.CurrentCulture.Name}");
            info.AppendLine($"Theme: {settings.Theme}, units: {settings.Units}, mode: {settings.Mode}, updates: {settings.Updates}/{settings.UpdateChannel}");
            Add(zip, "system.txt", info.ToString());
            var safeSettings = settings with { Branding = settings.Branding with { LogoPngBase64 = null }, UserName = null, LastFolder = null };
            Add(zip, "settings.json", Logging.Scrub(JsonSerializer.Serialize(safeSettings, new JsonSerializerOptions { WriteIndented = true })));
            foreach (var log in Directory.EnumerateFiles(paths.Logs, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(7))
            {
                string text;
                using (var s = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(s)) text = r.ReadToEnd();
                Add(zip, "logs/" + Path.GetFileName(log), Logging.Scrub(text));
            }
            foreach (var crash in Directory.EnumerateFiles(paths.Crashes, "*.json", SearchOption.AllDirectories).Take(20))
                Add(zip, "crashes/" + Path.GetFileName(crash), File.ReadAllText(crash));
            foreach (var file in userSelectedFiles)
                zip.CreateEntryFromFile(file, "user-files/" + Path.GetFileName(file), CompressionLevel.Optimal);
        }
        File.Move(temp, zipPath, overwrite: true);
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
