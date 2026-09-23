using Avalonia.Threading;
using FarmEngine.Content;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Projects;

/// <summary>Why <see cref="ProjectWorkspace.Current"/> changed.</summary>
public enum ProjectChangeKind
{
    /// <summary>A different project was opened (new/open/import/first launch).</summary>
    Opened,

    /// <summary>An editor edit (tile paint, undo/redo) — same project.</summary>
    Edited,

    /// <summary>Playtest results were kept (keep-changes exit).</summary>
    PlaytestKept,
}

public sealed class ProjectChangedEventArgs(ProjectChangeKind kind) : EventArgs
{
    public ProjectChangeKind Kind { get; } = kind;
}

/// <summary>
/// The open project and its persistence (web <c>useLocalKV</c> + projects.ts +
/// App.tsx history): opening/creating projects, editor edits with undo/redo, and debounced
/// autosave to the <see cref="ProjectStore"/>. Shared by the game surface (views) and the
/// File-menu project commands. UI-thread only.
/// </summary>
public sealed class ProjectWorkspace
{
    /// <summary>Undo depth (web <c>UNDO_LIMIT</c>).</summary>
    public const int UndoLimit = 50;

    private readonly List<GameProject> _past = [];
    private readonly List<GameProject> _future = [];
    private readonly TimeSpan _autosaveDelay;
    private DispatcherTimer? _autosaveTimer;
    private GameProject? _current;
    private bool _dirty;

    public ProjectWorkspace(ProjectStore store, AppSettingsStore settings, TimeSpan? autosaveDelay = null)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _autosaveDelay = autosaveDelay ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>Workspace over the default app-data folder (<see cref="AppDataPaths.DefaultRoot"/>).</summary>
    public static ProjectWorkspace CreateDefault(string? rootDirectory = null)
    {
        var root = rootDirectory ?? AppDataPaths.DefaultRoot;
        return new ProjectWorkspace(new ProjectStore(root), new AppSettingsStore(Path.Combine(root, "settings.json")));
    }

    public ProjectStore Store { get; }

    public AppSettingsStore Settings { get; }

    public GameProject? Current => _current;

    /// <summary>True while a playtest runs: the stored file stays the pre-play snapshot.</summary>
    public bool IsPlaytesting { get; set; }

    public bool CanUndo => _past.Count > 0;

    public bool CanRedo => _future.Count > 0;

    /// <summary>True when edits are waiting for the debounced autosave.</summary>
    public bool HasPendingSave => _dirty;

    public event EventHandler<ProjectChangedEventArgs>? ProjectChanged;

    /// <summary>
    /// First launch / startup: reopen the last project; else the most recent one; else create
    /// the Starter Farm sample. Projects that fail to load are skipped (the error is returned).
    /// </summary>
    public IReadOnlyList<string> OpenStartupProject()
    {
        var errors = new List<string>();
        var candidates = new List<string>();
        if (Settings.Load().LastProjectId is { } last)
        {
            candidates.Add(last);
        }

        candidates.AddRange(Store.List().Select(p => p.Id).Where(id => !candidates.Contains(id)));
        foreach (var id in candidates)
        {
            if (!Store.Exists(id))
            {
                continue;
            }

            var loaded = Store.Load(id);
            if (loaded.Ok)
            {
                Open(loaded.Project!, save: loaded.MigratedFrom is not null);
                return errors;
            }

            errors.Add($"{id}: {string.Join("; ", loaded.Errors)}");
        }

        Open(CreateProject(ProjectTemplates.Starter, "Starter Farm"));
        return errors;
    }

    /// <summary>A new project from a template with a fresh id (not yet opened).</summary>
    public GameProject CreateProject(string template, string name)
    {
        var id = Store.NewId();
        return Templates.CreateNewProject(template, string.IsNullOrWhiteSpace(name) ? "Untitled Game" : name.Trim(), id);
    }

    /// <summary>Makes <paramref name="project"/> current (saving it) and remembers it for next launch.</summary>
    public void Open(GameProject project, bool save = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        FlushPendingSave();
        _past.Clear();
        _future.Clear();
        _current = project with { Mode = project.Mode == "play" ? "tiles" : project.Mode };
        if (save || !Store.Exists(project.Id))
        {
            Store.Save(_current);
        }

        Settings.Update(s => s with { LastProjectId = project.Id });
        ProjectChanged?.Invoke(this, new ProjectChangedEventArgs(ProjectChangeKind.Opened));
    }

    /// <summary>Loads project <paramref name="id"/> from the store and opens it.</summary>
    public ProjectLoadResult OpenById(string id)
    {
        var loaded = Store.Load(id);
        if (loaded.Ok)
        {
            Open(loaded.Project!, save: loaded.MigratedFrom is not null);
        }

        return loaded;
    }

    /// <summary>
    /// Editor mutation with undo capture (web <c>editProject</c>). No-ops (same instance
    /// returned) are not recorded. Schedules an autosave.
    /// </summary>
    public void Edit(Func<GameProject, GameProject> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (_current is null)
        {
            return;
        }

        var next = change(_current);
        if (ReferenceEquals(next, _current))
        {
            return;
        }

        PushHistory(_current);
        _future.Clear();
        SetCurrent(next, ProjectChangeKind.Edited);
    }

    /// <summary>Starts a drag stroke: the returned token commits ONE undo entry for all its edits.</summary>
    public IDisposable BeginStroke()
    {
        var baseProject = _current;
        var depth = _past.Count;
        return new Stroke(() =>
        {
            // Collapse every entry the stroke pushed into a single one (the stroke's start).
            if (baseProject is not null && _past.Count > depth)
            {
                _past.RemoveRange(depth, _past.Count - depth);
                PushHistory(baseProject);
            }
        });
    }

    public bool Undo()
    {
        if (_current is null || _past.Count == 0)
        {
            return false;
        }

        var previous = _past[^1];
        _past.RemoveAt(_past.Count - 1);
        _future.Add(_current);
        SetCurrent(previous, ProjectChangeKind.Edited);
        return true;
    }

    public bool Redo()
    {
        if (_current is null || _future.Count == 0)
        {
            return false;
        }

        var next = _future[^1];
        _future.RemoveAt(_future.Count - 1);
        PushHistory(_current);
        SetCurrent(next, ProjectChangeKind.Edited);
        return true;
    }

    /// <summary>Keep-changes playtest exit: the synced project replaces the current one (saved).</summary>
    public void KeepPlaytestResult(GameProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_current is not null)
        {
            PushHistory(_current);
            _future.Clear();
        }

        SetCurrent(project with { Mode = "tiles" }, ProjectChangeKind.PlaytestKept);
        FlushPendingSave();
    }

    /// <summary>Writes pending edits now (exit, project switch).</summary>
    public void FlushPendingSave()
    {
        _autosaveTimer?.Stop();
        if (_dirty && _current is not null)
        {
            _dirty = false;
            Store.Save(_current);
        }
    }

    private void SetCurrent(GameProject project, ProjectChangeKind kind)
    {
        _current = project;
        ScheduleSave();
        ProjectChanged?.Invoke(this, new ProjectChangedEventArgs(kind));
    }

    private void PushHistory(GameProject project)
    {
        _past.Add(project);
        if (_past.Count > UndoLimit)
        {
            _past.RemoveAt(0);
        }
    }

    private void ScheduleSave()
    {
        _dirty = true;
        if (_autosaveDelay <= TimeSpan.Zero)
        {
            FlushPendingSave();
            return;
        }

        if (_autosaveTimer is null)
        {
            _autosaveTimer = new DispatcherTimer { Interval = _autosaveDelay };
            _autosaveTimer.Tick += (_, _) => FlushPendingSave();
        }

        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    private sealed class Stroke(Action onEnd) : IDisposable
    {
        private Action? _onEnd = onEnd;

        public void Dispose()
        {
            _onEnd?.Invoke();
            _onEnd = null;
        }
    }
}
