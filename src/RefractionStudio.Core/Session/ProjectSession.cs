using RefractionStudio.Domain;

namespace RefractionStudio.Core.Session;

/// <summary>
/// A single undoable change: a pure function of the project state plus its audit description.
/// </summary>
/// <param name="Kind">Audit category.</param>
/// <param name="Operation">Machine-readable operation, e.g. "pick.set".</param>
/// <param name="Description">Human-readable description, shown in Undo/Redo menus and the history.</param>
/// <param name="Parameters">Audited parameters.</param>
/// <param name="Apply">The state transformation.</param>
public sealed record ProjectEdit(HistoryKind Kind, string Operation, string Description, ImmutableDictionary<string, string> Parameters, Func<ProjectState, ProjectState> Apply)
{
    public static ProjectEdit Create(HistoryKind kind, string operation, string description, Func<ProjectState, ProjectState> apply, params (string Key, string Value)[] parameters)
        => new(kind, operation, description, parameters.ToImmutableDictionary(p => p.Key, p => p.Value), apply);
}

/// <summary>Why the state changed.</summary>
public enum StateChangeKind
{
    Edit,
    Undo,
    Redo,
    Loaded,
}

/// <summary>Event data of <see cref="ProjectSession.StateChanged"/>.</summary>
public sealed class StateChangedEventArgs(ProjectState previous, ProjectState current, StateChangeKind kind, string description) : EventArgs
{
    public ProjectState Previous { get; } = previous;
    public ProjectState Current { get; } = current;
    public StateChangeKind Kind { get; } = kind;
    public string Description { get; } = description;
}

/// <summary>
/// Owns the current project state, application-wide undo/redo and the append-only audit history.
/// Not thread-safe: use from the UI thread; background work reads immutable snapshots of <see cref="State"/>.
/// </summary>
public sealed class ProjectSession
{
    /// <summary>Maximum number of undo steps kept.</summary>
    public const int UndoLimit = 500;

    private readonly LinkedList<(ProjectState State, string Description)> _undo = new();
    private readonly Stack<(ProjectState State, string Description)> _redo = new();
    private readonly List<HistoryEntry> _history;
    private readonly Dictionary<SnapshotId, ProjectState> _snapshots;
    private readonly TimeProvider _clock;
    private Gesture? _gesture;
    private long _savedRevision;

    public ProjectSession(ProjectState state, IEnumerable<HistoryEntry>? history, IReadOnlyDictionary<SnapshotId, ProjectState>? snapshots,
        string userName, string appVersion, TimeProvider? clock = null)
    {
        State = state;
        _history = [.. history ?? []];
        _snapshots = snapshots is null ? [] : new Dictionary<SnapshotId, ProjectState>(snapshots);
        UserName = userName;
        AppVersion = appVersion;
        _clock = clock ?? TimeProvider.System;
    }

    public ProjectState State { get; private set; }

    public string UserName { get; }

    public string AppVersion { get; }

    /// <summary>Increments on every state change (including undo/redo).</summary>
    public long Revision { get; private set; }

    public bool IsDirty => Revision != _savedRevision;

    public IReadOnlyList<HistoryEntry> History => _history;

    public IReadOnlyDictionary<SnapshotId, ProjectState> Snapshots => _snapshots;

    public bool CanUndo => _gesture is null && _undo.Count > 0;

    public bool CanRedo => _gesture is null && _redo.Count > 0;

    public string? UndoDescription => _undo.Last?.Value.Description;

    public string? RedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;

    public event EventHandler<StateChangedEventArgs>? StateChanged;

    /// <summary>Applies an edit. An edit that returns the same state is a no-op (nothing recorded).</summary>
    public void Execute(ProjectEdit edit)
    {
        var previous = State;
        var next = edit.Apply(previous);
        if (ReferenceEquals(next, previous) || next == previous) return;
        if (_gesture is not null)
        {
            _gesture.Last = edit;
        }
        else
        {
            PushUndo(previous, edit.Description);
            Record(edit.Kind, edit.Operation, edit.Description, edit.Parameters);
        }
        _redo.Clear();
        SetState(next, StateChangeKind.Edit, edit.Description);
    }

    /// <summary>
    /// Starts a continuous gesture (dragging a pick, a crossover, a slider). All edits until the
    /// returned handle is disposed form one undo step and one history entry (the final edit's).
    /// </summary>
    public IDisposable BeginGesture(string description)
    {
        if (_gesture is not null) throw new InvalidOperationException("A gesture is already in progress.");
        _gesture = new Gesture(this, State, description);
        return _gesture;
    }

    private void EndGesture(Gesture g)
    {
        _gesture = null;
        if (g.Last is null || ReferenceEquals(g.Start, State)) return;
        PushUndo(g.Start, g.Last.Description);
        Record(g.Last.Kind, g.Last.Operation, g.Last.Description, g.Last.Parameters);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var (state, description) = _undo.Last!.Value;
        _undo.RemoveLast();
        _redo.Push((State, description));
        Record(HistoryKind.Undo, "undo", "Undo: " + description, ImmutableDictionary<string, string>.Empty);
        SetState(state, StateChangeKind.Undo, description);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var (state, description) = _redo.Pop();
        PushUndo(State, description);
        Record(HistoryKind.Redo, "redo", "Redo: " + description, ImmutableDictionary<string, string>.Empty);
        SetState(state, StateChangeKind.Redo, description);
    }

    /// <summary>Records the current revision as saved.</summary>
    public void MarkSaved() => _savedRevision = Revision;

    /// <summary>Marks the project as changed (e.g. after recovery) without an undo entry.</summary>
    public void MarkDirty() => _savedRevision = -1;

    /// <summary>Appends an audit entry that does not change the state (e.g. report generated, project saved).</summary>
    public void Record(HistoryKind kind, string operation, string description, ImmutableDictionary<string, string> parameters)
        => _history.Add(new HistoryEntry(_clock.GetUtcNow(), kind, operation, description, parameters, UserName, AppVersion, State.ActiveScenarioId));

    /// <summary>Creates a named snapshot of the current state (undoable: the header is part of the state).</summary>
    public SnapshotId CreateSnapshot(string name, string? note)
    {
        var id = SnapshotId.New();
        var header = new SnapshotHeader(id, name, _clock.GetUtcNow(), note);
        _snapshots[id] = State;
        Execute(ProjectEdit.Create(HistoryKind.Project, "snapshot.create", $"Create snapshot “{name}”",
            s => s with { Snapshots = s.Snapshots.Add(header) }, ("name", name)));
        return id;
    }

    /// <summary>Restores the content of a snapshot (keeping the snapshot list). Undoable.</summary>
    public void RestoreSnapshot(SnapshotId id)
    {
        if (!_snapshots.TryGetValue(id, out var snapshot)) throw new ArgumentException("Unknown snapshot.", nameof(id));
        var header = State.Snapshots.FirstOrDefault(h => h.Id == id);
        Execute(ProjectEdit.Create(HistoryKind.Project, "snapshot.restore", $"Restore snapshot “{header?.Name ?? id.ToString()}”",
            s => snapshot with { Snapshots = s.Snapshots }, ("snapshot", id.ToString())));
    }

    private void PushUndo(ProjectState state, string description)
    {
        _undo.AddLast((state, description));
        while (_undo.Count > UndoLimit) _undo.RemoveFirst();
    }

    private void SetState(ProjectState next, StateChangeKind kind, string description)
    {
        var previous = State;
        State = next;
        Revision++;
        StateChanged?.Invoke(this, new StateChangedEventArgs(previous, next, kind, description));
    }

    private sealed class Gesture(ProjectSession owner, ProjectState start, string description) : IDisposable
    {
        public ProjectState Start { get; } = start;
        public string Description { get; } = description;
        public ProjectEdit? Last { get; set; }
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.EndGesture(this);
        }
    }
}
