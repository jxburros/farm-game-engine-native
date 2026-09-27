using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Json;

/// <summary>
/// Serializer settings shared by every persisted shape. Property names are
/// camelCase so project/save JSON is interchangeable with the web version;
/// <c>null</c> means "absent" (TS <c>undefined</c>) and is omitted, except
/// for properties annotated <c>[JsonIgnore(Condition = Never)]</c> which
/// mirror zod <c>.nullable()</c> fields that are present-as-null.
/// </summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);
    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowOutOfOrderMetadataProperties = true,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // JSON.parse has no NaN/Infinity, and zod's z.number() rejects NaN: a project that
            // carried them could never be opened by the web version again.
            NumberHandling = JsonNumberHandling.Strict,
            WriteIndented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            RespectNullableAnnotations = false,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static string Serialize<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, indented ? Indented : Options);

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    /// <summary><c>JSON.parse(JSON.stringify(value))</c> — a structural deep copy.</summary>
    public static T DeepClone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Options), Options)!;
}
