using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RefractionStudio.Domain;

namespace RefractionStudio.Persistence;

/// <summary>Container manifest (manifest.json).</summary>
/// <param name="Format">Always <see cref="ProjectContainer.FormatId"/>.</param>
/// <param name="SchemaVersion">Schema of project.json.</param>
/// <param name="AppVersion">Application version that wrote the file.</param>
/// <param name="CreatedAt">Project creation time.</param>
/// <param name="SavedAt">Time of this save.</param>
/// <param name="ProjectSha256">Hash of project.json.</param>
/// <param name="RawFiles">Hash → container path of every embedded raw file.</param>
public sealed record ContainerManifest(string Format, int SchemaVersion, string AppVersion, DateTimeOffset CreatedAt, DateTimeOffset SavedAt, string ProjectSha256, ImmutableDictionary<string, string> RawFiles);

/// <summary>Everything stored in a project container.</summary>
/// <param name="State">Project state.</param>
/// <param name="History">Audit history.</param>
/// <param name="Snapshots">Snapshot states by id.</param>
/// <param name="Results">Derived result blobs by id (e.g. tomography grids).</param>
public sealed record ProjectDocument(
    ProjectState State,
    ImmutableArray<HistoryEntry> History,
    ImmutableDictionary<SnapshotId, ProjectState> Snapshots,
    ImmutableDictionary<Guid, byte[]> Results);

/// <summary>Provides raw file bytes by SHA-256 for saving.</summary>
public interface IRawFileSource
{
    /// <summary>Opens the content of an embedded or staged raw file.</summary>
    Stream Open(SourceFile file);
}

/// <summary>A loaded container: the document plus lazy access to the embedded raw files.</summary>
public sealed class LoadedProject : IRawFileSource
{
    internal LoadedProject(string path, ProjectDocument document, ContainerManifest manifest, ImmutableArray<string> migrationNotes)
    {
        Path = path;
        Document = document;
        Manifest = manifest;
        MigrationNotes = migrationNotes;
    }

    public string Path { get; }

    public ProjectDocument Document { get; }

    public ContainerManifest Manifest { get; }

    /// <summary>Messages describing schema upgrades applied while loading.</summary>
    public ImmutableArray<string> MigrationNotes { get; }

    /// <summary>Reads an embedded raw file (and verifies its hash).</summary>
    public Stream Open(SourceFile file)
    {
        using var zip = ZipFile.OpenRead(Path);
        var entry = zip.GetEntry(file.ContainerPath)
            ?? throw new ProjectFileException($"The project does not contain the raw file “{file.FileName}”.", $"Missing entry {file.ContainerPath}");
        using var s = entry.Open();
        var ms = new MemoryStream();
        s.CopyTo(ms);
        var hash = Convert.ToHexStringLower(SHA256.HashData(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
        if (hash != file.Sha256)
            throw new ProjectFileException($"The embedded raw file “{file.FileName}” is damaged (its checksum does not match).", $"Expected {file.Sha256}, found {hash}");
        ms.Position = 0;
        return ms;
    }
}

/// <summary>A project file could not be read or written. The message is written for users.</summary>
public sealed class ProjectFileException(string message, string? technicalDetail = null, Exception? inner = null) : Exception(message, inner)
{
    public string? TechnicalDetail { get; } = technicalDetail;
}

/// <summary>
/// Reads and writes .rfsx containers (ZIP). Saving writes a complete new container next to the target
/// and then replaces it, so a crash or full disk never leaves a half-written project.
/// </summary>
public static class ProjectContainer
{
    public const string FormatId = "quakelogic.refraction-studio.project";
    public const string Extension = ".rfsx";

    public static void Save(string path, ProjectDocument document, IRawFileSource rawSource, string appVersion, TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var full = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var temp = System.IO.Path.Combine(dir, "." + System.IO.Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var projectJson = JsonSerializer.SerializeToUtf8Bytes(document.State, DomainJson.Options);
                    var raw = ImmutableDictionary.CreateBuilder<string, string>();
                    foreach (var file in document.State.Survey.SourceFiles)
                    {
                        if (raw.ContainsKey(file.Sha256)) continue;
                        var entry = zip.CreateEntry(file.ContainerPath, CompressionLevel.NoCompression);
                        using var src = rawSource.Open(file);
                        using var dst = entry.Open();
                        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[81920];
                        int read;
                        while ((read = src.Read(buffer)) > 0)
                        {
                            hasher.AppendData(buffer, 0, read);
                            dst.Write(buffer, 0, read);
                        }
                        var hash = Convert.ToHexStringLower(hasher.GetHashAndReset());
                        if (hash != file.Sha256)
                            throw new ProjectFileException($"The raw file “{file.FileName}” changed since it was imported; the project was not saved.", $"Expected {file.Sha256}, found {hash}");
                        raw[file.Sha256] = file.ContainerPath;
                    }
                    var manifest = new ContainerManifest(FormatId, document.State.SchemaVersion, appVersion, document.State.Metadata.CreatedAt, now,
                        Convert.ToHexStringLower(SHA256.HashData(projectJson)), raw.ToImmutable());
                    Write(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, DomainJson.Options));
                    Write(zip, "project.json", projectJson);
                    Write(zip, "history.json", JsonSerializer.SerializeToUtf8Bytes(document.History, DomainJson.Options));
                    foreach (var (id, snapshot) in document.Snapshots)
                        Write(zip, $"snapshots/{id}.json", JsonSerializer.SerializeToUtf8Bytes(snapshot, DomainJson.Options));
                    foreach (var (id, blob) in document.Results)
                    {
                        var e = zip.CreateEntry($"results/{id:N}.bin", CompressionLevel.Fastest);
                        using var s = e.Open();
                        s.Write(blob);
                    }
                }
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, full, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new ProjectFileException($"The project could not be saved to “{full}”: {ex.Message}", ex.GetType().Name, ex);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static LoadedProject Load(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full)) throw new ProjectFileException($"The project file “{full}” does not exist.");
        try
        {
            using var zip = ZipFile.OpenRead(full);
            var manifest = Read<ContainerManifest>(zip, "manifest.json")
                ?? throw new ProjectFileException("This file is not a Refraction Studio project (manifest missing).");
            if (manifest.Format != FormatId)
                throw new ProjectFileException("This file is not a Refraction Studio project.", $"Format id '{manifest.Format}'.");
            var projectEntry = zip.GetEntry("project.json") ?? throw new ProjectFileException("The project file is damaged (project.json missing).");
            byte[] projectBytes;
            using (var s = projectEntry.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                projectBytes = ms.ToArray();
            }
            if (Convert.ToHexStringLower(SHA256.HashData(projectBytes)) != manifest.ProjectSha256)
                throw new ProjectFileException("The project file is damaged: project.json does not match its checksum.", "project.json SHA-256 mismatch");
            var (state, notes) = ProjectMigrations.Upgrade(JsonNode.Parse(projectBytes)!.AsObject());
            var history = Read<ImmutableArray<HistoryEntry>>(zip, "history.json");
            var snapshots = ImmutableDictionary.CreateBuilder<SnapshotId, ProjectState>();
            var results = ImmutableDictionary.CreateBuilder<Guid, byte[]>();
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.StartsWith("snapshots/", StringComparison.Ordinal) && entry.FullName.EndsWith(".json", StringComparison.Ordinal))
                {
                    var id = new SnapshotId(Guid.Parse(System.IO.Path.GetFileNameWithoutExtension(entry.Name)));
                    using var s = entry.Open();
                    var (snap, _) = ProjectMigrations.Upgrade(JsonNode.Parse(s)!.AsObject());
                    snapshots[id] = snap;
                }
                else if (entry.FullName.StartsWith("results/", StringComparison.Ordinal) && entry.FullName.EndsWith(".bin", StringComparison.Ordinal))
                {
                    using var s = entry.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    results[Guid.Parse(System.IO.Path.GetFileNameWithoutExtension(entry.Name))] = ms.ToArray();
                }
            }
            foreach (var f in state.Survey.SourceFiles)
                if (zip.GetEntry(f.ContainerPath) is null)
                    throw new ProjectFileException($"The project is incomplete: the raw file “{f.FileName}” is missing.", f.ContainerPath);
            var doc = new ProjectDocument(state, history.IsDefault ? [] : history, snapshots.ToImmutable(), results.ToImmutable());
            return new LoadedProject(full, doc, manifest, notes);
        }
        catch (InvalidDataException ex)
        {
            throw new ProjectFileException("The file is not a valid Refraction Studio project (it is not a readable archive).", ex.Message, ex);
        }
        catch (JsonException ex)
        {
            throw new ProjectFileException("The project file is damaged (its data could not be read).", ex.Message, ex);
        }
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        s.Write(bytes);
    }

    private static T? Read<T>(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e is null) return default;
        using var s = e.Open();
        return JsonSerializer.Deserialize<T>(s, DomainJson.Options);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Upgrades older project.json schemas and normalises defaults.</summary>
public static class ProjectMigrations
{
    public static (ProjectState State, ImmutableArray<string> Notes) Upgrade(JsonObject json)
    {
        var notes = ImmutableArray.CreateBuilder<string>();
        var version = json["schemaVersion"]?.GetValue<int>() ?? 0;
        if (version > ProjectState.CurrentSchemaVersion)
            throw new ProjectFileException($"This project was created by a newer version of Refraction Studio (schema {version}). Update the application to open it.");
        if (version < 1)
            throw new ProjectFileException("This project uses an unknown schema version.", $"schemaVersion = {version}");
        // Future: `if (version == 1) { …transform json…; version = 2; notes.Add(...); }`
        var state = json.Deserialize<ProjectState>(DomainJson.Options) ?? throw new ProjectFileException("The project data is empty.");
        return (Normalize(state), notes.ToImmutable());
    }

    /// <summary>Replaces default (null) immutable arrays from older or hand-edited files with empty ones.</summary>
    public static ProjectState Normalize(ProjectState s)
    {
        static ImmutableArray<T> E<T>(ImmutableArray<T> a) => a.IsDefault ? [] : a;
        var survey = s.Survey with
        {
            SourceFiles = E(s.Survey.SourceFiles),
            Records = E(s.Survey.Records),
            Lines = [.. E(s.Survey.Lines).Select(l => l with
            {
                Spreads = [.. E(l.Spreads).Select(sp => sp with { Channels = E(sp.Channels) })],
                Gathers = [.. E(l.Gathers).Select(g => g with { Members = E(g.Members) })],
                Topography = new Topography(E(l.Topography.Points)),
            })],
        };
        var scenarios = E(s.Scenarios).Select(sc => sc with
        {
            Settings = sc.Settings with
            {
                Engineering = sc.Settings.Engineering with { ProfilePositions = E(sc.Settings.Engineering.ProfilePositions) },
                Density = sc.Settings.Density with { Intervals = E(sc.Settings.Density.Intervals) },
                Vs30 = sc.Settings.Vs30 with { Positions = E(sc.Settings.Vs30.Positions), CustomIntervals = E(sc.Settings.Vs30.CustomIntervals) },
                VelocityClasses = E(sc.Settings.VelocityClasses),
                IsolineVelocities = E(sc.Settings.IsolineVelocities),
            },
            Lines = sc.Lines.ToImmutableDictionary(kv => kv.Key, kv => kv.Value with
            {
                Pipeline = new ProcessingPipeline(E(kv.Value.Pipeline.Steps)),
                Layered = kv.Value.Layered with { FixedVelocities = E(kv.Value.Layered.FixedVelocities) },
            }),
        }).ToImmutableArray();
        return s with { Survey = survey, Scenarios = scenarios, Snapshots = E(s.Snapshots), Report = s.Report with { Sections = E(s.Report.Sections) } };
    }
}
