using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RefractionStudio.Analysis;
using RefractionStudio.Analysis.Picking;
using RefractionStudio.Analysis.Tomography;
using RefractionStudio.Analysis.TravelTimes;
using RefractionStudio.Core.Data;
using RefractionStudio.Core.Import;
using RefractionStudio.Core.Services;
using RefractionStudio.Core.Session;
using RefractionStudio.Domain;
using RefractionStudio.Formats;
using RefractionStudio.Persistence;

namespace RefractionStudio.Core;

/// <summary>Configuration of a workspace.</summary>
/// <param name="AppVersion">Application version recorded in files and history.</param>
/// <param name="UserName">Editor name recorded in history.</param>
/// <param name="RecoveryDirectory">Crash-recovery folder (per user).</param>
public sealed record WorkspaceOptions(string AppVersion, string UserName, string RecoveryDirectory);

/// <summary>
/// The open project and its services: session (undo/history), raw data, processed gathers, stored
/// results, analysis, import, save/load, autosave checkpoints and recovery. UI-independent: the
/// desktop app, the CLI and the tests use it the same way.
/// </summary>
public sealed class ProjectWorkspace
{
    private readonly ILogger _log;
    private readonly RecoveryStore _recovery;

    public ProjectWorkspace(WorkspaceOptions options, FormatRegistry? formats = null, ILogger<ProjectWorkspace>? logger = null)
    {
        Options = options;
        Formats = formats ?? FormatRegistry.CreateDefault();
        _log = (ILogger?)logger ?? NullLogger.Instance;
        _recovery = new RecoveryStore(options.RecoveryDirectory);
        Raw = new RawFileRepository();
        Traces = new TraceStore(Raw, Formats);
        Gathers = new GatherProvider(Traces);
        Analyzer.Traces = new WorkspaceTraceSource(Gathers);
    }

    public WorkspaceOptions Options { get; }
    public FormatRegistry Formats { get; }
    public RawFileRepository Raw { get; }
    public TraceStore Traces { get; }
    public GatherProvider Gathers { get; }
    public ResultStore Results { get; } = new();
    public RefractionAnalyzer Analyzer { get; } = new();
    public RecoveryStore Recovery => _recovery;

    public ProjectSession? Session { get; private set; }

    /// <summary>Saved file path; null for a project that was never saved.</summary>
    public string? FilePath { get; private set; }

    /// <summary>Identifies this editing session for recovery.</summary>
    public Guid SessionId { get; private set; } = Guid.NewGuid();

    public ProjectState State => Session?.State ?? throw new InvalidOperationException("No project is open.");

    /// <summary>Raised when a project is created, opened, recovered or closed.</summary>
    public event EventHandler? ProjectChanged;

    public void NewProject(string name, TimeProvider? clock = null)
    {
        Reset();
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        Session = new ProjectSession(ProjectState.CreateNew(name, Options.AppVersion, now), [], null, Options.UserName, Options.AppVersion, clock);
        Session.Record(HistoryKind.Project, "project.create", $"Create project “{name}”", ImmutableDictionary<string, string>.Empty.Add("version", Options.AppVersion));
        Session.MarkDirty();
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    public LoadedProject Open(string path) => Attach(Load(path));

    /// <summary>Reads and verifies a project file without opening it (safe on a background thread).</summary>
    public static LoadedProject Load(string path) => ProjectContainer.Load(path);

    /// <summary>
    /// Makes a loaded project the open project and raises <see cref="ProjectChanged"/>. Call it on the
    /// thread that owns the user interface.
    /// </summary>
    public LoadedProject Attach(LoadedProject loaded)
    {
        var path = loaded.Path;
        Reset();
        Raw.Attach(loaded);
        Results.Load(loaded.Document.Results);
        Session = new ProjectSession(loaded.Document.State, loaded.Document.History, loaded.Document.Snapshots, Options.UserName, Options.AppVersion);
        Session.Record(HistoryKind.Project, "project.open", $"Open project ({loaded.Manifest.AppVersion} file)",
            ImmutableDictionary<string, string>.Empty.Add("path", Path.GetFileName(path)).Add("fileVersion", loaded.Manifest.AppVersion));
        Session.MarkSaved();
        FilePath = loaded.Path;
        _log.LogInformation("Opened project {File}", Path.GetFileName(path));
        ProjectChanged?.Invoke(this, EventArgs.Empty);
        return loaded;
    }

    /// <summary>Saves to <paramref name="path"/> (or the current file) atomically and clears the recovery checkpoint.</summary>
    public void Save(string? path = null)
    {
        var session = Session ?? throw new InvalidOperationException("No project is open.");
        var target = path ?? FilePath ?? throw new InvalidOperationException("Choose a file name for the project.");
        session.Record(HistoryKind.Project, "project.save", "Save project", ImmutableDictionary<string, string>.Empty.Add("file", Path.GetFileName(target)));
        var doc = Document();
        ProjectContainer.Save(target, doc, Raw, Options.AppVersion);
        // Re-attach the new container so that raw files are served from the saved copy.
        var reloaded = ProjectContainer.Load(target);
        Raw.Attach(reloaded);
        FilePath = Path.GetFullPath(target);
        session.MarkSaved();
        _recovery.ClearCheckpoint(SessionId);
        _log.LogInformation("Saved project {File}", Path.GetFileName(target));
    }

    /// <summary>The document as it would be saved.</summary>
    public ProjectDocument Document()
    {
        var session = Session ?? throw new InvalidOperationException("No project is open.");
        var snapshots = session.Snapshots.Where(kv => session.State.Snapshots.Any(h => h.Id == kv.Key)).ToImmutableDictionary();
        var results = Results.Export(snapshots.Values.Prepend(session.State));
        return new ProjectDocument(session.State, [.. session.History], snapshots, results);
    }

    /// <summary>Writes an autosave checkpoint if the project has unsaved changes.</summary>
    public bool Checkpoint()
    {
        var session = Session;
        if (session is null || !session.IsDirty) return false;
        _recovery.WriteCheckpoint(new RecoveryInfo(SessionId, session.State.Metadata.Name, FilePath, DateTimeOffset.UtcNow, Options.AppVersion, Environment.ProcessId),
            session.State, session.History, session.Snapshots.Where(kv => session.State.Snapshots.Any(h => h.Id == kv.Key)).ToImmutableDictionary());
        return true;
    }

    /// <summary>Restores an unsaved project left by a previous session.</summary>
    public void Recover(RecoveryInfo info)
    {
        var (state, history, snapshots) = _recovery.Load(info.SessionId);
        Reset();
        SessionId = info.SessionId;
        LoadedProject? saved = null;
        if (info.ProjectPath is { } p && File.Exists(p))
        {
            saved = ProjectContainer.Load(p);
            Raw.Attach(saved);
            Results.Load(saved.Document.Results);
            FilePath = p;
        }
        foreach (var f in state.Survey.SourceFiles)
            if (_recovery.StagedRawFile(info.SessionId, f) is { } staged) Raw.AddStaged(f.Sha256, staged);
        var missing = state.Survey.SourceFiles.Where(f => _recovery.StagedRawFile(info.SessionId, f) is null && FilePath is null).ToList();
        if (missing.Count > 0)
            throw new ProjectFileException($"The recovered project refers to {missing.Count} raw file(s) that are no longer available.", string.Join(", ", missing.Select(m => m.FileName)));
        // Tomography results computed after the last save are not part of the checkpoint; references to them are cleared.
        state = ClearUnavailableResults(state);
        // Snapshots: those in the checkpoint, completed from the saved project for older checkpoints.
        if (saved is not null)
            foreach (var kv in saved.Document.Snapshots)
                if (!snapshots.ContainsKey(kv.Key)) snapshots = snapshots.Add(kv.Key, kv.Value);
        snapshots = snapshots.Where(kv => state.Snapshots.Any(h => h.Id == kv.Key)).ToImmutableDictionary();
        Session = new ProjectSession(state, history, snapshots, Options.UserName, Options.AppVersion);
        Session.Record(HistoryKind.Project, "project.recover", "Recovered unsaved changes after an unexpected exit", ImmutableDictionary<string, string>.Empty);
        Session.MarkDirty();
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    private ProjectState ClearUnavailableResults(ProjectState state)
        => state with
        {
            Scenarios = [.. state.Scenarios.Select(sc => sc with
            {
                Lines = sc.Lines.ToImmutableDictionary(kv => kv.Key, kv => kv.Value.TomographyResult is { } r && Results.Get(r) is null ? kv.Value with { TomographyResult = null } : kv.Value),
            })],
        };

    public void Close()
    {
        if (Session is not null) _recovery.Discard(SessionId);
        Reset();
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Reset()
    {
        Session = null;
        FilePath = null;
        SessionId = Guid.NewGuid();
        Raw.Attach(null);
        Traces.Clear();
        Gathers.Clear();
        Results.Load(ImmutableDictionary<Guid, byte[]>.Empty);
    }

    // ───────────────────────────── Import ─────────────────────────────

    /// <summary>Reads and validates files for the import preview.</summary>
    public async Task<(IReadOnlyList<InspectedFile> Files, IReadOnlyList<ImportIssue> Issues, ImportSummary Summary)> InspectAsync(IEnumerable<string> paths,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var files = await ImportInspector.ReadFilesAsync(Formats, paths, progress, ct).ConfigureAwait(false);
        var existing = Session?.State.Survey.SourceFiles.Select(f => f.Sha256).ToHashSet() ?? [];
        return (files, ImportInspector.Validate(files, existing), ImportInspector.Summarize(files));
    }

    /// <summary>Stages raw files (so recovery works before the first save) and executes the import edit.</summary>
    public ImportPlan Import(IReadOnlyList<InspectedFile> files, ImportOptions options) => ApplyImport(PrepareImport(files, options));

    /// <summary>
    /// Plans an import and stages the raw files; does not change the project (safe on a background
    /// thread). Apply the plan with <see cref="ApplyImport"/>.
    /// </summary>
    public ImportPlan PrepareImport(IReadOnlyList<InspectedFile> files, ImportOptions options)
    {
        var session = Session ?? throw new InvalidOperationException("No project is open.");
        var plan = ImportService.Plan(session.State, files, options, DateTimeOffset.UtcNow);
        foreach (var (file, path) in plan.RawFiles)
        {
            var bytes = File.ReadAllBytes(path);
            var staged = _recovery.StageRawFile(SessionId, file, bytes);
            Raw.AddStaged(file.Sha256, staged);
        }
        return plan;
    }

    /// <summary>Executes a prepared import (one undo step).</summary>
    public ImportPlan ApplyImport(ImportPlan plan)
    {
        var session = Session ?? throw new InvalidOperationException("No project is open.");
        session.Execute(plan.Edit);
        foreach (var n in plan.Notes) _log.LogInformation("Import note: {Note}", n);
        return plan;
    }

    // ───────────────────────────── Analysis ─────────────────────────────

    public AnalysisResult Analyze(CancellationToken ct = default) => Analyzer.Analyze(State, Results, ct);

    public ProcessedGather Gather(LineId line, GatherId gather) => Gathers.Get(State, line, gather);

    /// <summary>Runs automatic picking on the gathers of a line and stores the picks (one undo step).</summary>
    public async Task<int> AutoPickAsync(LineId lineId, PickerSettings settings, IReadOnlyCollection<GatherId>? gathers = null, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var state = State;
        var line = state.Survey.Line(lineId) ?? throw new ArgumentException("Line not found.");
        var targets = line.Gathers.Where(g => g.Included && (gathers is null || gathers.Contains(g.Id))).ToList();
        var picker = new AutoPicker();
        var result = new System.Collections.Concurrent.ConcurrentDictionary<PickKey, AutoPick>();
        var done = 0;
        await Parallel.ForEachAsync(targets, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, (g, token) =>
        {
            var processed = Gathers.Get(state, lineId, g.Id);
            var picks = picker.PickGather(processed.ToGatherData(), settings, token);
            foreach (var p in picks)
            {
                var trace = processed.Traces.First(t => t.Channel == p.Channel);
                result[new PickKey(g.Id, p.Channel)] = p with { Time = trace.ToRecordTime(p.Time) };
            }
            progress?.Report((Interlocked.Increment(ref done), targets.Count));
            return ValueTask.CompletedTask;
        });
        // The edit is applied on the caller's context (the UI thread in the application) so that state
        // change notifications reach the user interface on its own thread.
        Session!.Execute(Edits.ApplyAutomaticPicks(lineId, result, settings));
        return result.Count;
    }

    /// <summary>
    /// Transfers a pick from one trace to its neighbours by cross-correlation, stepping outwards while
    /// the correlation coefficient stays at or above <paramref name="minCoefficient"/>.
    /// </summary>
    public int PropagatePick(LineId lineId, GatherId gatherId, int fromChannel, double minCoefficient = 0.7, double windowBefore = 0.002, double windowAfter = 0.008, double maxLag = 0.004)
    {
        var state = State;
        var interp = state.Interpretation(lineId);
        var key = new PickKey(gatherId, fromChannel);
        var source = interp.PickOf(key) ?? throw new InvalidOperationException("The reference trace has no pick.");
        var gather = Gathers.Get(state, lineId, gatherId);
        var traces = gather.Traces.Where(t => !t.Excluded).OrderBy(t => t.X).ToList();
        var index = traces.FindIndex(t => t.Channel == fromChannel);
        if (index < 0) return 0;
        var analyzer = new CrossCorrelationAnalyzer();
        var transferred = new Dictionary<PickKey, (double, double)>();
        foreach (var step in new[] { -1, 1 })
        {
            var current = traces[index];
            var time = current.ToDisplayTime(source.Time);
            for (var i = index + step; i >= 0 && i < traces.Count; i += step)
            {
                var next = traces[i];
                var a = new GatherTrace(current.Channel, current.X, current.Processed, current.SampleInterval, current.StartTime, 0, false);
                var b = new GatherTrace(next.Channel, next.X, next.Processed, next.SampleInterval, next.StartTime, 0, false);
                var r = analyzer.Transfer(a, time, b, windowBefore, windowAfter, maxLag);
                if (r is null || r.Coefficient < minCoefficient) break;
                transferred[new PickKey(gatherId, next.Channel)] = (next.ToRecordTime(r.Time), r.Coefficient);
                current = next;
                time = r.Time;
            }
        }
        if (transferred.Count > 0)
            Session!.Execute(Edits.ApplyCorrelationPicks(lineId, transferred, $"Transfer pick from channel {fromChannel} to {transferred.Count} trace(s) by cross-correlation"));
        return transferred.Count;
    }

    /// <summary>Automatically picks the reference (shot) geophone of every gather; stored as automatic reference picks.</summary>
    public int AutoPickReferences(LineId lineId, PickerSettings settings)
    {
        var state = State;
        var line = state.Survey.Line(lineId)!;
        var interp = state.Interpretation(lineId);
        var entries = new List<(GatherId, ShotTimeEntry)>();
        foreach (var g in line.Gathers.Where(g => g.Included))
        {
            var processed = Gathers.Get(state, lineId, g.Id);
            var reference = processed.ReferenceTraces.FirstOrDefault();
            if (reference is null) continue;
            // The trigger pulse is picked on the unprocessed trace: filtering (zero-phase in particular)
            // would move energy ahead of the true onset.
            var dt = reference.OriginalSampleInterval;
            var start = (int)Math.Max(0, Math.Floor((-0.005 - reference.StartTime) / dt));
            var end = (int)Math.Min(reference.Original.Length - 1, Math.Ceiling((0.05 - reference.StartTime) / dt));
            var cand = new AicPicker().Pick(reference.Original, start, end, dt, settings);
            if (cand is null) continue;
            var t = reference.StartTime + cand.Index * dt;
            interp.ShotTimes.TryGetValue(g.Id, out var existing);
            if (existing?.ReferencePick is { IsManual: true }) continue;
            entries.Add((g.Id, (existing ?? new ShotTimeEntry()) with { ReferencePick = new ArrivalPick(t, ArrivalOrigin.Automatic, "AIC") }));
        }
        if (entries.Count == 0) return 0;
        Session!.Execute(ProjectEdit.Create(HistoryKind.Processing, "shottime.autopick", $"Automatic reference-geophone picks ({entries.Count} shots)",
            s => s.WithLineInterpretation(lineId, i => i with { ShotTimes = i.ShotTimes.SetItems(entries.Select(e => KeyValuePair.Create(e.Item1, e.Item2))) }),
            ("count", entries.Count.ToString(CultureInfo.InvariantCulture))));
        return entries.Count;
    }

    /// <summary>Runs the tomographic inversion of a line and stores the result (one undo step).</summary>
    public async Task<Computed<TomographyResult>> RunTomographyAsync(LineId lineId, TomographySettings settings, IProgress<TomographyProgress>? progress = null, CancellationToken ct = default)
    {
        var state = State;
        var line = state.Survey.Line(lineId) ?? throw new ArgumentException("Line not found.");
        var interp = state.Interpretation(lineId);
        var obs = ObservationBuilder.Build(line, interp);
        Analysis.Layered.LayeredModel? layered = null;
        if (settings.StartingModel == StartingModelKind.Layered)
        {
            var lm = Analyzer.Method(interp.Layered.Method).Interpret(obs, interp.Layered, Math.Max(2, interp.Segments.LayerCount));
            if (!lm.HasValue) return Computed<TomographyResult>.Unavailable("The layered starting model is not available: " + lm.Reason);
            layered = lm.Value;
        }
        // Continue on the caller's context: the result is stored by an edit (see AutoPickAsync).
        var result = await new TomographyEngine().RunAsync(obs, settings, layered, progress, ct);
        if (!result.HasValue) return result;
        var r = result.Value!;
        var id = Results.Add(r);
        var reference = new TomographyResultRef(id, r.InputFingerprint, DateTimeOffset.UtcNow, Options.AppVersion, r.Iterations.Length - 1, r.FinalRms, r.FinalChi2);
        Session!.Execute(Edits.SetTomographyResult(lineId, reference, settings));
        _log.LogInformation("Tomography {Line}: {Iterations} iterations, RMS {Rms:0.000} ms", line.Name, reference.Iterations, reference.RmsSeconds * 1000);
        return result;
    }

    // ───────────────────────────── Archive / audit ─────────────────────────────

    /// <summary>
    /// Writes a project archive: the saved project file, a machine-readable audit record and a readme.
    /// The project must have been saved.
    /// </summary>
    public void Archive(string zipPath, string auditJson)
    {
        if (FilePath is null) throw new InvalidOperationException("Save the project before archiving it.");
        var temp = zipPath + ".tmp";
        using (var fs = File.Create(temp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(FilePath, Path.GetFileName(FilePath), CompressionLevel.Optimal);
            var a = zip.CreateEntry("audit.json");
            using (var s = a.Open()) s.Write(System.Text.Encoding.UTF8.GetBytes(auditJson));
            var readme = zip.CreateEntry("README.txt");
            using var w = new StreamWriter(readme.Open());
            w.WriteLine($"Refraction Studio project archive — {State.Metadata.Name}");
            w.WriteLine($"Created {DateTimeOffset.UtcNow:u} with Refraction Studio {Options.AppVersion} by {Options.UserName}.");
            w.WriteLine("Contents: the project file (raw data embedded byte-for-byte, processing history, picks, models) and audit.json.");
        }
        File.Move(temp, zipPath, overwrite: true);
    }

    /// <summary>Serialises any value with the project JSON settings (used by exports).</summary>
    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, DomainJson.Options);
}
