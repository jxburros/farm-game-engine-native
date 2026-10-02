namespace FarmingRpgMaker.App.Projects;

/// <summary>
/// "This project is open in an editor window": a hidden lock file next to the project file,
/// held open with no sharing for as long as the project is open. A second editor window (or a
/// second instance of the app) can't take it, so two windows never edit the same file. The
/// operating system releases it when the process ends, crashed or not.
/// </summary>
public sealed class ProjectLock : IDisposable
{
    private readonly FileStream _stream;

    private ProjectLock(string id, string path, FileStream stream)
    {
        Id = id;
        Path = path;
        _stream = stream;
    }

    /// <summary>The locked project.</summary>
    public string Id { get; }

    /// <summary>The lock file.</summary>
    public string Path { get; }

    /// <summary>
    /// Takes the lock on project <paramref name="id"/>. <see cref="LockResult.Busy"/> when another
    /// window holds it; <see cref="LockResult.Unavailable"/> when the folder doesn't allow a lock
    /// file (read-only), in which case the project opens unlocked.
    /// </summary>
    public static (LockResult Result, ProjectLock? Lock) TryAcquire(ProjectStore store, string id)
    {
        ArgumentNullException.ThrowIfNull(store);
        var path = store.LockPathFor(id);
        try
        {
            Directory.CreateDirectory(store.ProjectsDirectory);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return (LockResult.Acquired, new ProjectLock(id, path, stream));
        }
        catch (IOException) when (File.Exists(path))
        {
            // The file is there but can't be opened exclusively: someone holds it.
            return (LockResult.Busy, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not lock project {id} ({path}): {ex.Message}");
            return (LockResult.Unavailable, null);
        }
    }

    /// <summary>True when another window holds project <paramref name="id"/> (checked by briefly taking the lock).</summary>
    public static bool IsHeldElsewhere(ProjectStore store, string id)
    {
        var (result, held) = TryAcquire(store, id);
        held?.Dispose();
        return result == LockResult.Busy;
    }

    /// <summary>Releases the lock and removes the lock file.</summary>
    public void Dispose()
    {
        _stream.Dispose();
        try
        {
            File.Delete(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another window took it in the meantime, or the folder is read-only: harmless.
        }
    }
}

/// <summary>Outcome of <see cref="ProjectLock.TryAcquire"/>.</summary>
public enum LockResult
{
    Acquired,

    /// <summary>Another editor window has the project open.</summary>
    Busy,

    /// <summary>No lock file could be made (read-only folder); the project is used without one.</summary>
    Unavailable,
}
