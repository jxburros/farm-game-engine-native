using System.Text.Json;
using FarmingRpgMaker.Updates;

namespace FarmingRpgMaker.App.Projects;

/// <summary>App-level preferences persisted between sessions.</summary>
public sealed record WorkspaceSettings
{
    /// <summary>Project reopened on launch.</summary>
    public string? LastProjectId { get; init; }

    /// <summary>Output folder of the last Export Game.</summary>
    public string? LastExportFolder { get; init; }

    /// <summary>Whether Export Game last wrote zip / tar.gz archives (default on).</summary>
    public bool? ExportArchives { get; init; }

    /// <summary>The editor's language (Help → Language); null follows the system.</summary>
    public string? EditorLanguage { get; init; }

    /// <summary>The first-run welcome tour was dismissed (Help → Welcome Tour reopens it).</summary>
    public bool? WelcomeSeen { get; init; }
}

/// <summary>
/// Reads/writes the <c>"workspace"</c> section of the shared <c>settings.json</c> (the same file
/// the Update Center stores its <c>"updates"</c> section in). Both stores go through
/// <see cref="SettingsFile"/>: other sections are preserved, writes are atomic with a
/// <c>.bak</c>, and a file that can't be read is never overwritten. Never throws on read.
/// </summary>
public sealed class AppSettingsStore
{
    public const string SectionName = "workspace";

    /// <summary>Shared with the Update Center's store, which writes the same file.</summary>
    private static JsonSerializerOptions Options => JsonSettingsStore.JsonOptions;

    private readonly SettingsFile _file;

    public AppSettingsStore(string filePath)
    {
        _file = new SettingsFile(filePath);
    }

    public string FilePath => _file.FilePath;

    public WorkspaceSettings Load()
    {
        try
        {
            return _file.ReadSection(SectionName)?.Deserialize<WorkspaceSettings>(Options) ?? new WorkspaceSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException)
        {
            return new WorkspaceSettings();
        }
    }

    /// <summary>
    /// Saves the section. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>
    /// when the file can't be read or written (nothing is written then).
    /// </summary>
    public void Save(WorkspaceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _file.WriteSection(SectionName, JsonSerializer.SerializeToNode(settings, Options));
    }

    public void Update(Func<WorkspaceSettings, WorkspaceSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Save(change(Load()));
    }

    /// <summary>
    /// <see cref="Update"/> for preferences that are nice to keep but must never stop what the
    /// creator is doing (the last project, the export folder, the language, the welcome tour):
    /// a failed write is logged and returns false.
    /// </summary>
    public bool TryUpdate(Func<WorkspaceSettings, WorkspaceSettings> change)
    {
        try
        {
            Update(change);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not save the editor settings to {FilePath}: {ex.Message}");
            return false;
        }
    }
}
