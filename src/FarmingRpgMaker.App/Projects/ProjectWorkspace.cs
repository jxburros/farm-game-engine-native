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
    private ProjectLock? _lock;

    /// <summary>The open project file as this editor last read or wrote it (<see cref="SaveConflict"/>).</summary>
    private FileStamp? _diskStamp;

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
        return new ProjectWorkspace(new ProjectStore(root), new AppSettingsStore(AppDataPaths.SettingsFile(root)));
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
    /// True when the last save was refused because the project file changed on disk since this
    /// editor read or wrote it (a text editor, a sync client, git, another window). Nothing is
    /// overwritten until the creator chooses: <see cref="OverwriteDiskVersion"/> (keep the
    /// editor's version) or <see cref="ReloadFromDisk"/> (take the file's).
    /// </summary>
    public bool SaveConflict { get; private set; }

    /// <summary>
    /// After <see cref="FlushPendingSave"/>: edits that exist only in memory because saving
    /// failed (<see cref="SaveError"/> says why). Closing, installing an update or opening
    /// another project would lose them.
    /// </summary>
    public bool HasUnsavedChanges => _dirty && _document is not null && SaveError is not null;

    /// <summary>
    /// Why the last save failed (disk full, no permission…), or null while saving works. The
    /// edits stay open and pending; the next edit or <see cref="RetrySave"/> tries again.
    /// </summary>
    public string? SaveError { get; private set; }

    /// <summary>
    /// A form's confirmation: "<paramref name="subject"/> saved<paramref name="detail"/>." while
    /// saving works, "… applied … (not yet saved to disk)." while it fails, so a form never says
    /// "saved" under the banner that says nothing can be saved.
    /// </summary>
    public string SavedText(string subject, string detail = "") => SaveError is null
        ? $"{subject} saved{detail}."
        : $"{subject} applied{detail} (not yet saved to disk).";

    public event EventHandler<ProjectChangedEventArgs>? ProjectChanged;

    /// <summary>How many handlers follow <see cref="ProjectChanged"/> (tests check that replaced editors let go).</summary>
    internal int ProjectChangedHandlerCount => ProjectChanged?.GetInvocationList().Length ?? 0;

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
        // Half-written temp files of an editor that was killed mid-save.
        Store.DeleteStaleTempFiles();
        if (Path.GetDirectoryName(Path.GetFullPath(Settings.FilePath)) is { } settingsDirectory)
        {
            FarmingRpgMaker.Updates.AtomicFile.DeleteStaleTempFiles(settingsDirectory, TimeSpan.FromMinutes(10));
        }

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

            if (_lock?.Id != id && ProjectLock.IsHeldElsewhere(Store, id))
            {
                errors.Add($"{StoredName(id) ?? id}: {OpenElsewhere}");
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
        TakeLock(project.Id);
        var opened = project.WithMode(project.Mode == "play" ? "tiles" : project.Mode);
        _document = Documents.Create(opened);
        // The previous project's state ends here (its unsaved edits were dealt with by the caller).
        _dirty = false;
        SaveConflict = false;
        _diskStamp = Store.Stamp(project.Id);
        if (save || !Store.Exists(project.Id))
        {
            _dirty = !TrySave(opened);
        }
        else
        {
            SetSaveError(null);
        }

        // Best effort: an unwritable settings.json must not leave the views on the old project.
        Settings.TryUpdate(s => s with { LastProjectId = project.Id });
        RaiseProjectChanged(ProjectChangeKind.Opened);
    }

    /// <summary>Why a project can't be opened while another window has it.</summary>
    public const string OpenElsewhere = "It is open in another Farming RPG Maker window. Close it there first, or open a copy (Duplicate in the project list).";

    /// <summary>Loads project <paramref name="id"/> from the store and opens it (refused while another window has it open).</summary>
    public ProjectLoadResult OpenById(string id)
    {
        if (_lock?.Id != id && ProjectLock.IsHeldElsewhere(Store, id))
        {
            return ProjectLoadResult.Fail(OpenElsewhere);
        }

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
    /// already agree (or nothing is open). <paramref name=storedName/> is the name read before
    /// something else rewrote the file (a kept playtest saves the pre-play name); null reads it now.
    /// </summary>
    public bool AdoptStoredName(string? storedName = null)
    {
        if (Current is not { } project)
        {
            return false;
        }

        var stored = storedName ?? StoredName(project.Id);
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

    /// <summary>
    /// The project file changed on disk (<see cref="SaveConflict"/>) and the creator keeps the
    /// editor's version: writes it over the file. False when the write fails.
    /// </summary>
    public bool OverwriteDiskVersion()
    {
        _autosaveTimer?.Stop();
        if (_document is not { } document)
        {
            return false;
        }

        _dirty = !TrySave(document.Project, overwriteChanges: true);
        return !_dirty;
    }

    /// <summary>
    /// The project file changed on disk (<see cref="SaveConflict"/>) and the creator takes the
    /// file's version: the editor's unsaved edits are dropped and the file is opened again.
    /// </summary>
    public ProjectLoadResult ReloadFromDisk()
    {
        if (Current is not { } project)
        {
            return ProjectLoadResult.Fail("No project is open.");
        }

        _autosaveTimer?.Stop();
        _dirty = false;
        return OpenById(project.Id);
    }

    /// <summary>
    /// The project list rewrote the open project's file (a rename): that version is this
    /// editor's own, not an outside change, so the next save may replace it.
    /// </summary>
    public void AcceptStoreChange()
    {
        if (Current is { } project)
        {
            _diskStamp = Store.Stamp(project.Id);
        }
    }

    /// <summary>
    /// Lets go of the open project's lock file (the window closed), so another window or the
    /// next launch can open it. Pending edits are written first.
    /// </summary>
    public void ReleaseLock()
    {
        FlushPendingSave();
        _lock?.Dispose();
        _lock = null;
    }

    private void TakeLock(string id)
    {
        if (_lock?.Id == id)
        {
            return;
        }

        _lock?.Dispose();
        _lock = null;
        var (result, taken) = ProjectLock.TryAcquire(Store, id);
        if (result == LockResult.Busy)
        {
            throw new InvalidOperationException(OpenElsewhere);
        }

        _lock = taken;
    }

    private bool TrySave(GameProject project, bool overwriteChanges = false)
    {
        string? error = null;
        var conflict = false;
        try
        {
            // Never silently overwrite a change made outside this editor.
            if (!overwriteChanges && _diskStamp is { } expected && Store.Stamp(project.Id) is { } actual && actual != expected)
            {
                conflict = true;
                error = "The project file was changed outside this editor (by another program or window).";
                System.Diagnostics.Trace.TraceWarning($"Not saving project {project.Id}: {Store.PathFor(project.Id)} changed on disk ({expected} → {actual}).");
            }
            else
            {
                Store.Save(project);
                _diskStamp = Store.Stamp(project.Id);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            System.Diagnostics.Trace.TraceError($"Saving project {project.Id} failed: {ex}");
        }

        var conflictChanged = conflict != SaveConflict;
        SaveConflict = conflict;
        SetSaveError(error, conflictChanged);
        return error is null;
    }

    private void SetSaveError(string? error, bool forceNotify = false)
    {
        var changed = forceNotify || (error is null) != (SaveError is null);
        SaveError = error;
        if (changed)
        {
            SaveStatusChanged?.Invoke(this, EventArgs.Empty);
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
