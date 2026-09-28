using Avalonia.Threading;
using FarmEngine.Authoring;
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
/// <remarks>
/// The project and its history live in an F# <see cref="Document"/> (docs/LANGUAGES.md: "F#
/// understands the project"). Every change is an <see cref="Edit"/> applied through
/// <see cref="Documents"/>; this class only holds the current document, fires events and saves.
/// </remarks>
public sealed class ProjectWorkspace
{
    private readonly TimeSpan _autosaveDelay;
    private DispatcherTimer? _autosaveTimer;
    private Document? _document;
    private bool _dirty;

    public ProjectWorkspace(ProjectStore store, AppSettingsStore settings, TimeSpan? autosaveDelay = null)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _autosaveDelay = autosaveDelay ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>Undo depth (web <c>UNDO_LIMIT</c>), owned by the F# document.</summary>
    public static int UndoLimit => Documents.UndoLimit;

    /// <summary>Workspace over the default app-data folder (<see cref="AppDataPaths.DefaultRoot"/>).</summary>
    public static ProjectWorkspace CreateDefault(string? rootDirectory = null)
    {
        var root = rootDirectory ?? AppDataPaths.DefaultRoot;
        return new ProjectWorkspace(new ProjectStore(root), new AppSettingsStore(Path.Combine(root, "settings.json")));
    }

    public ProjectStore Store { get; }

    public AppSettingsStore Settings { get; }

    public GameProject? Current => _document?.Project;

    /// <summary>The open document (project + history); null before the first <see cref="Open"/>.</summary>
    public Document? Document => _document;

    /// <summary>True while a playtest runs: the stored file stays the pre-play snapshot.</summary>
    public bool IsPlaytesting { get; set; }

    public bool CanUndo => _document is { } document && Documents.CanUndo(document);

    public bool CanRedo => _document is { } document && Documents.CanRedo(document);

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

        Open(CreateProject("starter", "Starter Farm"));
        return errors;
    }

    /// <summary>A new project from a template with a fresh id (not yet opened).</summary>
    public GameProject CreateProject(string template, string name)
    {
        var id = Store.NewId();
        return ProjectCatalog.CreateNewProject(template, string.IsNullOrWhiteSpace(name) ? "Untitled Game" : name.Trim(), id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Makes <paramref name="project"/> current (saving it) and remembers it for next launch.</summary>
    public void Open(GameProject project, bool save = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        FlushPendingSave();
        var opened = project with { Mode = project.Mode == "play" ? "tiles" : project.Mode };
        _document = Documents.Create(opened);
        if (save || !Store.Exists(project.Id))
        {
            Store.Save(opened);
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
    /// Editor mutation with undo capture (web <c>editProject</c>): applies an F# <see cref="Edit"/>.
    /// No-ops are not recorded and return false. Schedules an autosave.
    /// </summary>
    public bool Apply(Edit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (_document is null)
        {
            return false;
        }

        return Commit(Documents.Apply(_document, edit), ProjectChangeKind.Edited);
    }

    /// <summary>
    /// A drag-stroke edit (web <c>beginPaintStroke</c>/<c>endPaintStroke</c>): every edit with the
    /// same <paramref name="strokeId"/> lands in ONE undo entry. Use a fresh id per pointer press.
    /// </summary>
    public bool ApplyInStroke(string strokeId, Edit edit)
    {
        ArgumentNullException.ThrowIfNull(strokeId);
        ArgumentNullException.ThrowIfNull(edit);
        if (_document is null)
        {
            return false;
        }

        return Commit(Documents.ApplyInStroke(_document, strokeId, edit), ProjectChangeKind.Edited);
    }

    /// <summary>Ends the current drag stroke, so the next stroke edit starts a new undo entry.</summary>
    public void EndStroke()
    {
        if (_document is { } document)
        {
            _document = Documents.EndStroke(document);
        }
    }

    public bool Undo()
    {
        if (_document is null || !Documents.CanUndo(_document))
        {
            return false;
        }

        return Commit(Documents.Undo(_document), ProjectChangeKind.Edited);
    }

    public bool Redo()
    {
        if (_document is null || !Documents.CanRedo(_document))
        {
            return false;
        }

        return Commit(Documents.Redo(_document), ProjectChangeKind.Edited);
    }

    /// <summary>Keep-changes playtest exit: the synced project replaces the current one (saved), as one undo step.</summary>
    public void KeepPlaytestResult(GameProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var kept = project with { Mode = "tiles" };
        _document = _document is { } document
            ? Documents.Apply(document, Edits.ReplaceProject(kept))
            : Documents.Create(kept);
        SetCurrent(ProjectChangeKind.PlaytestKept);
        FlushPendingSave();
    }

    /// <summary>Writes pending edits now (exit, project switch).</summary>
    public void FlushPendingSave()
    {
        _autosaveTimer?.Stop();
        if (_dirty && _document is { } document)
        {
            _dirty = false;
            Store.Save(document.Project);
        }
    }

    private bool Commit(Document next, ProjectChangeKind kind)
    {
        // A no-op may still hand back a new document (a stroke ended); only a project change counts.
        var changed = !ReferenceEquals(next.Project, _document?.Project);
        _document = next;
        if (!changed)
        {
            return false;
        }

        SetCurrent(kind);
        return true;
    }

    private void SetCurrent(ProjectChangeKind kind)
    {
        ScheduleSave();
        ProjectChanged?.Invoke(this, new ProjectChangedEventArgs(kind));
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
}
