using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FarmEngine.Json;

namespace FarmEngine.Schemas.Tests;

/// <summary>
/// The schema half of the TypeScript golden parity checks: project and save
/// migrations and the stable JSON writer, against the fixtures under
/// <c>Golden/</c> recorded from the TS engine by <c>tools/golden/generate.sh</c>.
/// Replays, content assembly and RNG streams are the engine's, checked by the
/// Rust tests (<c>crates/farm-sim/tests/golden_*.rs</c>).
/// </summary>
public class GoldenTests
{
    private static readonly string GoldenDir = Path.Combine(AppContext.BaseDirectory, "Golden");

    private static JsonElement Load(string relative) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenDir, relative))).RootElement;

    public static TheoryData<string> SaveNames()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(GoldenDir, "saves"), "*.json").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

    public static TheoryData<string> MigrationNames()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(GoldenDir, "migrations"), "project-v*.json").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(MigrationNames))]
    public void ProjectMigrationMatchesTypeScript(string name)
    {
        var fixture = Load($"migrations/{name}.json");
        var result = Migrations.MigrateProject(JsonNode.Parse(fixture.GetProperty("input").GetRawText()));
        var expected = fixture.GetProperty("result");
        Assert.Equal(expected.GetProperty("ok").GetBoolean(), result.Ok);
        Assert.Equal(expected.GetProperty("fromVersion").GetDouble(), result.FromVersion);
        var stable = StableJson.Stringify(result.Data);
        AssertSameStable(name, fixture.GetProperty("stable").GetString()!, stable);
        Assert.Equal(fixture.GetProperty("hash").GetString(), HashText(stable));
    }

    [Theory]
    [MemberData(nameof(SaveNames))]
    public void SaveMigrationMatchesTypeScript(string name)
    {
        var fixture = Load($"saves/{name}.json");
        var input = fixture.GetProperty("input");
        var result = SaveMigrations.MigrateGameState(input.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(input.GetRawText()));
        var expected = fixture.GetProperty("result");
        Assert.Equal(expected.GetProperty("ok").GetBoolean(), result.Ok);
        Assert.Equal(expected.GetProperty("fromVersion").GetDouble(), result.FromVersion);
        if (result.Ok)
        {
            AssertSameStable(name, fixture.GetProperty("stable").GetString()!, StableJson.Stringify(result.Data));
        }
    }

    [Fact]
    public void StableStringifyAndHashMatchTypeScript()
    {
        foreach (var entry in Load("hash.json").EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString();
            if (LoneSurrogate.IsMatch(entry.GetProperty("input").GetRawText()))
            {
                // System.Text.Json cannot hold strings with unpaired surrogates, so
                // no C# state can contain one; Js.QuoteString's escaping of them is
                // covered by JsSemanticsTests instead.
                continue;
            }
            var input = DecodeTagged(entry.GetProperty("input"));
            var stable = input is null ? "null" : StableJson.Stringify(JsonSerializer.SerializeToElement(input));
            if (IsUndefined(entry.GetProperty("input")))
            {
                // JSON.stringify(undefined) is undefined; nothing to compare.
                continue;
            }
            Assert.True(entry.GetProperty("stable").GetString() == stable, $"{name}: expected {entry.GetProperty("stable").GetString()}, got {stable}");
            Assert.Equal(entry.GetProperty("hash").GetString(), HashText(stable));
        }
    }

    /// <summary>
    /// hash.ts: FNV-1a 64-bit (as two 32-bit lanes) over the stable JSON text,
    /// the hash every golden fixture records.
    /// </summary>
    private static string HashText(string text)
    {
        unchecked
        {
            var h1 = 0x811c9dc5u;
            var h2 = 0xcbf29ce4u;
            foreach (var ch in text)
            {
                uint c = ch;
                h1 = (h1 ^ c) * 0x01000193u;
                h2 = (h2 ^ ((c << 1) | 1)) * 0x01000193u;
            }
            return h1.ToString("x8", CultureInfo.InvariantCulture) + h2.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex LoneSurrogate = new(
        @"\\ud[89ab][0-9a-f]{2}(?!\\ud[c-f])|(?<!\\ud[89ab][0-9a-f]{2})\\ud[c-f][0-9a-f]{2}",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsUndefined(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("$js", out var tag)
        && tag.GetString() == "undefined";

    /// <summary>
    /// Decode the generator's tagged encoding into what <c>JSON.stringify</c>
    /// would see: undefined object members vanish, undefined array slots and
    /// non-finite numbers become null, and -0 stays a number.
    /// </summary>
    private static JsonNode? DecodeTagged(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("$js", out var tag) && element.EnumerateObject().Count() == 1)
                {
                    return tag.GetString() switch
                    {
                        "-0" => JsonValue.Create(-0.0),
                        _ => null,
                    };
                }
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (IsUndefined(property.Value)) continue;
                    obj[property.Name] = DecodeTagged(property.Value);
                }
                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray()) array.Add(DecodeTagged(item));
                return array;
            default:
                return JsonNode.Parse(element.GetRawText());
        }
    }

    private static void AssertSameStable(string what, string expected, string actual)
    {
        if (expected == actual) return;
        var at = 0;
        while (at < expected.Length && at < actual.Length && expected[at] == actual[at]) at++;
        var from = Math.Max(0, at - 300);
        var sb = new StringBuilder();
        sb.AppendLine($"{what}: stable JSON differs at char {at}.");
        sb.AppendLine($"TS: …{expected.Substring(from, Math.Min(700, expected.Length - from))}");
        sb.AppendLine($"C#: …{actual.Substring(from, Math.Min(700, actual.Length - from))}");
        Assert.Fail(sb.ToString());
    }
}
