using System.Text.Json;
using RefractionStudio.Domain;

namespace RefractionStudio.Persistence;

/// <summary>Metadata of a recovery checkpoint.</summary>
/// <param name="SessionId">Editing session.</param>
/// <param name="ProjectName">Project name.</param>
/// <param name="ProjectPath">Saved project file, or null for a never-saved project.</param>
/// <param name="CheckpointAt">Time of the checkpoint.</param>
/// <param name="AppVersion">Application version.</param>
/// <param name="ProcessId">Process that wrote it (a running process's checkpoint is not offered).</param>
public sealed record RecoveryInfo(Guid SessionId, string ProjectName, string? ProjectPath, DateTimeOffset CheckpointAt, string AppVersion, int ProcessId);

/// <summary>
/// Crash-recovery storage: unsaved state checkpoints plus staged copies of imported raw files that
/// are not yet inside a saved project. Recovery data never leaves the computer.
/// </summary>
public sealed class RecoveryStore
{
    private readonly string _root;

    public RecoveryStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public string SessionDirectory(Guid session) => Path.Combine(_root, session.ToString("N"));

    /// <summary>Copies an imported raw file into the session's staging area (idempotent).</summary>
    public string StageRawFile(Guid session, SourceFile file, ReadOnlySpan<byte> content)
    {
        var path = Path.Combine(SessionDirectory(session), file.ContainerPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, content.ToArray());
            File.Move(temp, path, overwrite: true);
        }
        return path;
    }

    /// <summary>Path of a staged raw file, or null.</summary>
    public string? StagedRawFile(Guid session, SourceFile file)
    {
        var path = Path.Combine(SessionDirectory(session), file.ContainerPath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    /// <summary>Writes a checkpoint atomically.</summary>
    public void WriteCheckpoint(RecoveryInfo info, ProjectState state, IReadOnlyList<HistoryEntry> history,
        IReadOnlyDictionary<SnapshotId, ProjectState>? snapshots = null)
    {
        var dir = SessionDirectory(info.SessionId);
        Directory.CreateDirectory(dir);
        WriteAtomic(Path.Combine(dir, "state.json"), JsonSerializer.SerializeToUtf8Bytes(state, DomainJson.Compact));
        WriteAtomic(Path.Combine(dir, "history.json"), JsonSerializer.SerializeToUtf8Bytes(history, DomainJson.Compact));
        WriteAtomic(Path.Combine(dir, "snapshots.json"), JsonSerializer.SerializeToUtf8Bytes(
            (snapshots ?? ImmutableDictionary<SnapshotId, ProjectState>.Empty).ToImmutableDictionary(), DomainJson.Compact));
        WriteAtomic(Path.Combine(dir, "info.json"), JsonSerializer.SerializeToUtf8Bytes(info, DomainJson.Compact));
    }

    /// <summary>Checkpoints left by sessions that are no longer running.</summary>
    public IReadOnlyList<RecoveryInfo> Recoverable(Func<int, bool>? isProcessAlive = null)
    {
        isProcessAlive ??= IsAlive;
        var list = new List<RecoveryInfo>();
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var infoPath = Path.Combine(dir, "info.json");
            if (!File.Exists(infoPath) || !File.Exists(Path.Combine(dir, "state.json"))) continue;
            try
            {
                var info = JsonSerializer.Deserialize<RecoveryInfo>(File.ReadAllBytes(infoPath), DomainJson.Options);
                if (info is not null && (info.ProcessId == Environment.ProcessId || !isProcessAlive(info.ProcessId))) list.Add(info);
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
        }
        return [.. list.OrderByDescending(i => i.CheckpointAt)];
    }

    /// <summary>Loads a checkpoint.</summary>
    public (ProjectState State, ImmutableArray<HistoryEntry> History, ImmutableDictionary<SnapshotId, ProjectState> Snapshots) Load(Guid session)
    {
        var dir = SessionDirectory(session);
        var state = JsonSerializer.Deserialize<ProjectState>(File.ReadAllBytes(Path.Combine(dir, "state.json")), DomainJson.Options)
            ?? throw new ProjectFileException("The recovery data is empty.");
        var history = JsonSerializer.Deserialize<ImmutableArray<HistoryEntry>>(File.ReadAllBytes(Path.Combine(dir, "history.json")), DomainJson.Options);
        // Checkpoints written before snapshots were included have no snapshots file.
        var snapshotFile = Path.Combine(dir, "snapshots.json");
        var snapshots = File.Exists(snapshotFile)
            ? JsonSerializer.Deserialize<ImmutableDictionary<SnapshotId, ProjectState>>(File.ReadAllBytes(snapshotFile), DomainJson.Options) ?? ImmutableDictionary<SnapshotId, ProjectState>.Empty
            : ImmutableDictionary<SnapshotId, ProjectState>.Empty;
        return (ProjectMigrations.Normalize(state), history.IsDefault ? [] : history,
            snapshots.ToImmutableDictionary(kv => kv.Key, kv => ProjectMigrations.Normalize(kv.Value)));
    }

    /// <summary>Deletes a session's recovery data (after a successful save, a discard, or a clean exit).</summary>
    public void Discard(Guid session)
    {
        var dir = SessionDirectory(session);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>Removes only the checkpoint of a session, keeping staged raw files (used after saving).</summary>
    public void ClearCheckpoint(Guid session)
    {
        foreach (var name in new[] { "state.json", "history.json", "info.json" })
        {
            var p = Path.Combine(SessionDirectory(session), name);
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
