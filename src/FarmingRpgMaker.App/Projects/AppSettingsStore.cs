using System.Text.Json;
using System.Text.Json.Nodes;

namespace FarmingRpgMaker.App.Projects;

/// <summary>App-level preferences persisted between sessions.</summary>
public sealed record WorkspaceSettings
{
    /// <summary>Project reopened on launch.</summary>
    public string? LastProjectId { get; init; }
}

/// <summary>
/// Reads/writes the <c>"workspace"</c> section of the shared <c>settings.json</c> (the same file
/// the Update Center stores its <c>"updates"</c> section in — both stores preserve the other
/// top-level keys). Writes are atomic. Never throws on read.
/// </summary>
public sealed class AppSettingsStore
{
    public const string SectionName = "workspace";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _gate = new();

    public AppSettingsStore(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public WorkspaceSettings Load()
    {
        lock (_gate)
        {
            try
            {
                return ReadRoot()?[SectionName]?.Deserialize<WorkspaceSettings>(Options) ?? new WorkspaceSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return new WorkspaceSettings();
            }
        }
    }

    public void Save(WorkspaceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            JsonObject root;
            try
            {
                root = ReadRoot() ?? new JsonObject();
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                root = new JsonObject();
            }

            root[SectionName] = JsonSerializer.SerializeToNode(settings, Options);
            AtomicFile.WriteAllText(FilePath, root.ToJsonString(Options));
        }
    }

    public void Update(Func<WorkspaceSettings, WorkspaceSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Save(change(Load()));
    }

    private JsonObject? ReadRoot()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        var text = File.ReadAllText(FilePath);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
    }
}
