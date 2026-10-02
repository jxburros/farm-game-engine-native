using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmingRpgMaker.Updates;

/// <summary>
/// Stores settings in <c>%APPDATA%/FarmingRpgMaker/settings.json</c> (on Linux/macOS:
/// <c>~/.config/FarmingRpgMaker/settings.json</c>). Update settings live under the
/// <c>"updates"</c> key; any other top-level keys (written by other parts of the app)
/// are preserved on save. The file is shared through <see cref="SettingsFile"/>: atomic,
/// flushed writes with a <c>.bak</c>, and an unreadable file is never overwritten.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    public const string SectionName = "updates";

    /// <summary>
    /// The serializer options for <c>settings.json</c>. Every store that writes a section of the
    /// shared file uses these: a save re-serializes the whole file, so stores with different
    /// options would rewrite each other's sections (escaping, indentation, enum names).
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // human-readable local file ("+00:00", not "\u002B00:00")
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SettingsFile _file;

    public JsonSettingsStore(string? filePath = null)
    {
        _file = new SettingsFile(filePath ?? DefaultFilePath);
    }

    /// <summary><c>&lt;ApplicationData&gt;/FarmingRpgMaker/settings.json</c>.</summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "FarmingRpgMaker",
        "settings.json");

    public string FilePath => _file.FilePath;

    public UpdateSettings Load()
    {
        try
        {
            return _file.ReadSection(SectionName)?.Deserialize<UpdateSettings>(JsonOptions) ?? new UpdateSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException)
        {
            return new UpdateSettings();
        }
    }

    /// <summary>
    /// Saves the <c>"updates"</c> section. Throws <see cref="IOException"/> (and writes nothing)
    /// when the existing file can't be read, so the other sections are never lost.
    /// </summary>
    public void Save(UpdateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _file.WriteSection(SectionName, JsonSerializer.SerializeToNode(settings, JsonOptions));
    }
}
