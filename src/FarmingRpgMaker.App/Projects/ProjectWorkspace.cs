using Avalonia.Threading;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
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

    /// <summary>
    /// Why the last save failed (disk full, no permission…), or null while saving works. The
    /// edits stay open and pending; the next edit or <see cref="RetrySave"/> tries again.
    /// </summary>
    public string? SaveError { get; private set; }

    public event EventHandler<ProjectChangedEventArgs>? ProjectChanged;

    /// <summary>
    /// Shows an error a <see cref="ProjectChanged"/> handler (a view refreshing) threw, and
    /// returns true when it was shown (the editor's error screen); unhandled errors propagate.
    /// </summary>
    public Func<Exception, bool>? ViewErrorHandler { get; set; }

    /// <summary>
    /// Raised when saving starts failing and when it works again (web <c>useLocalKV</c>: the
    /// first failure is announced loudly, then nothing until a save succeeds). Read
    /// <see cref="SaveError"/> for the state.
    /// </summary>
    public event EventHandler? SaveStatusChanged;

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
        var opened = project.WithMode(project.Mode == "play" ? "tiles" : project.Mode);
        _document = Documents.Create(opened);
        if (save || !Store.Exists(project.Id))
        {
            _dirty = !TrySave(opened);
        }

        Settings.Update(s => s with { LastProjectId = project.Id });
        RaiseProjectChanged(ProjectChangeKind.Opened);
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

    /// <summary>The name the project list shows for <paramref name="id"/>, or null when it is not stored.</summary>
    public string? StoredName(string id) => Store.List().FirstOrDefault(summary => summary.Id == id)?.Name;

    /// <summary>
    /// After the project list renamed the open project on disk: takes the stored name as an
    /// undoable edit, so the open document and the next autosave keep it. False when the names
    /// already agree (or nothing is open).
    /// </summary>
    public bool AdoptStoredName()
    {
        if (Current is not { } project)
        {
            return false;
        }

        var stored = StoredName(project.Id);
        return stored is not null && stored != project.Name && !string.IsNullOrWhiteSpace(project.Name)
            && Apply(Edits.SetProjectInfo(stored, project.Version));
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
    /// An edit to editor state kept in the project that is not content (the tile brush): no
    /// undo entry, so picking a brush never uses up undo depth or answers Ctrl+Z (#45). Still
    /// autosaved. False when nothing changed.
    /// </summary>
    public bool ApplyWithoutHistory(Edit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (_document is null)
        {
            return false;
        }

        return Commit(Documents.ApplyWithoutHistory(_document, edit), ProjectChangeKind.Edited);
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
        var kept = project.WithMode("tiles");
        _document = _document is { } document
            ? Documents.Apply(document, Edits.ReplaceProject(kept))
            : Documents.Create(kept);
        SetCurrent(ProjectChangeKind.PlaytestKept);
        FlushPendingSave();
    }

    /// <summary>
    /// Writes pending edits now (exit, project switch). A failed write never throws: the edits
    /// stay pending and <see cref="SaveError"/> says why.
    /// </summary>
    public void FlushPendingSave()
    {
        _autosaveTimer?.Stop();
        if (_dirty && _document is { } document)
        {
            _dirty = !TrySave(document.Project);
        }
    }

    /// <summary>"Retry save": writes the open project now, pending edits or not. False when it failed again.</summary>
    public bool RetrySave()
    {
        _autosaveTimer?.Stop();
        if (_document is not { } document)
        {
            return false;
        }

        _dirty = !TrySave(document.Project);
        return !_dirty;
    }

    private bool TrySave(GameProject project)
    {
        string? error = null;
        try
        {
            Store.Save(project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            System.Diagnostics.Trace.TraceError($"Saving project {project.Id} failed: {ex}");
        }

        var changed = (error is null) != (SaveError is null);
        SaveError = error;
        if (changed)
        {
            SaveStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        return error is null;
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
        RaiseProjectChanged(kind);
    }

    /// <summary>
    /// Tells every view, one at a time: a view that throws goes to <see cref="ViewErrorHandler"/>
    /// and the others still hear about the change.
    /// </summary>
    private void RaiseProjectChanged(ProjectChangeKind kind)
    {
        var args = new ProjectChangedEventArgs(kind);
        foreach (var handler in ProjectChanged?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<ProjectChangedEventArgs>)handler)(this, args);
            }
#pragma warning disable CA1031 // Handed to the error screen, or rethrown.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (ViewErrorHandler?.Invoke(ex) != true)
                {
                    throw;
                }
            }
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
}
