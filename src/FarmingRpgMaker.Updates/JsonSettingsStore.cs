using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FarmingRpgMaker.Updates;

/// <summary>
/// Stores settings in <c>%APPDATA%/FarmingRpgMaker/settings.json</c> (on Linux/macOS:
/// <c>~/.config/FarmingRpgMaker/settings.json</c>). Update settings live under the
/// <c>"updates"</c> key; any other top-level keys (written by other parts of the app)
/// are preserved on save. Writes go to a temp file first and are then moved over the
/// target, so a crash mid-write never leaves a truncated file.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    public const string SectionName = "updates";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // human-readable local file ("+00:00", not "\u002B00:00")
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Lock _gate = new();

    public JsonSettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? DefaultFilePath;
    }

    /// <summary><c>&lt;ApplicationData&gt;/FarmingRpgMaker/settings.json</c>.</summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "FarmingRpgMaker",
        "settings.json");

    public string FilePath { get; }

    public UpdateSettings Load()
    {
        lock (_gate)
        {
            try
            {
                var root = ReadRoot();
                var section = root?[SectionName];
                return section?.Deserialize<UpdateSettings>(JsonOptions) ?? new UpdateSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException)
            {
                return new UpdateSettings();
            }
        }
    }

    public void Save(UpdateSettings settings)
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

            root[SectionName] = JsonSerializer.SerializeToNode(settings, JsonOptions);

            var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
            Directory.CreateDirectory(directory);
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tempPath, root.ToJsonString(JsonOptions));
                File.Move(tempPath, FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
    }

    private JsonObject? ReadRoot()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        var text = File.ReadAllText(FilePath);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return JsonNode.Parse(text) as JsonObject;
    }
}
