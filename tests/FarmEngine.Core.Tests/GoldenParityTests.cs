using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Golden;

/// <summary>
/// Parity with the TypeScript reference engine. Every fixture under
/// <c>Golden/</c> was recorded from the TS engine by <c>tools/golden/generate.sh</c>;
/// the C# port must reproduce each state hash, effect list and final state
/// byte for byte. The first diverging step is reported with both stable
/// JSON encodings so the offending system is easy to find.
/// </summary>
public class GoldenParityTests
{
    private static readonly string GoldenDir = Path.Combine(AppContext.BaseDirectory, "Golden");

    private static JsonElement Load(string relative) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenDir, relative))).RootElement;

    public static TheoryData<string> ReplayNames()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(GoldenDir, "replays"), "*.json").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name != "index") data.Add(name);
        }
        return data;
    }

    public static TheoryData<string> ContentNames()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(GoldenDir, "content"), "*.json").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

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
    [MemberData(nameof(ReplayNames))]
    public void ReplayMatchesTypeScript(string name)
    {
        var fixture = Load($"replays/{name}.json");
        var project = fixture.GetProperty("project").Deserialize<GameProject>(JsonDefaults.Options)!;

        var content = EngineState.CreateContentFromProject(project);
        Assert.Equal(fixture.GetProperty("contentHash").GetString(), Hash.HashState(content));
        var ctx = new EngineContext(content);

        var seed = fixture.GetProperty("seed");
        var created = seed.ValueKind == JsonValueKind.Null
            ? EngineState.CreateGameState(project)
            : EngineState.CreateGameState(project, seed.GetString());
        AssertSameJson("created state", fixture.GetProperty("createdHash").GetString()!, null, created);

        var state = fixture.GetProperty("autoStartQuests").GetBoolean() ? Quests.AutoStartQuests(ctx, created) : created;
        AssertSameJson("initial state", fixture.GetProperty("initialHash").GetString()!, fixture.GetProperty("initialState"), state);

        var index = 0;
        foreach (var step in fixture.GetProperty("steps").EnumerateArray())
        {
            var input = step.GetProperty("input").Deserialize<ReplayInput>(JsonDefaults.Options)!;
            var before = state;
            var result = input switch
            {
                CommandInput c => Engine.ApplyCommand(ctx, state, c.Command),
                TickInput t => Engine.AdvanceTick(ctx, state, t.Ticks),
                _ => throw new InvalidOperationException("unknown replay input"),
            };
            state = result.State;

            var expectedHash = step.GetProperty("hash").GetString()!;
            var actualHash = Hash.HashState(state);
            if (actualHash != expectedHash)
            {
                Assert.Fail(
                    $"{name}: state diverged at step {index} ({step.GetProperty("input").GetRawText()}).\n" +
                    $"expected hash {expectedHash}, got {actualHash}.\n" +
                    $"state before step: {Hash.StableStringify(before)[..Math.Min(4000, Hash.StableStringify(before).Length)]}\n" +
                    $"C# effects: {StableJson.Stringify(JsonDefaults.ToElement(result.Effects))}\n" +
                    $"TS effects: {StableJson.Stringify(step.GetProperty("effects"))}");
            }

            var expectedEffects = StableJson.Stringify(step.GetProperty("effects"));
            var actualEffects = StableJson.Stringify(JsonDefaults.ToElement(result.Effects));
            Assert.True(expectedEffects == actualEffects,
                $"{name}: effects differ at step {index} ({step.GetProperty("input").GetRawText()}).\nTS: {expectedEffects}\nC#: {actualEffects}");
            index++;
        }

        Assert.Equal(fixture.GetProperty("stepCount").GetInt32(), index);
        AssertSameJson("final state", fixture.GetProperty("finalHash").GetString()!, fixture.GetProperty("finalState"), state);

        var finalProject = EngineState.ApplyStateToProject(project, state);
        AssertSameStable("final project", StableJson.Stringify(fixture.GetProperty("finalProject")), Hash.StableStringify(finalProject));
    }

    [Theory]
    [MemberData(nameof(ContentNames))]
    public void ContentAssemblyMatchesTypeScript(string name)
    {
        var fixture = Load($"content/{name}.json");
        var project = fixture.GetProperty("project").Deserialize<GameProject>(JsonDefaults.Options)!;
        var content = EngineState.CreateContentFromProject(project);
        AssertSameStable($"{name} content", fixture.GetProperty("stable").GetString()!, Hash.StableStringify(content));
        Assert.Equal(fixture.GetProperty("contentHash").GetString(), Hash.HashState(content));
        var state = EngineState.CreateGameState(project, $"content:{name}");
        Assert.Equal(fixture.GetProperty("stateHash").GetString(), Hash.HashState(state));
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
        AssertSameStable(name, fixture.GetProperty("stable").GetString()!, StableJson.Stringify(result.Data));
        Assert.Equal(fixture.GetProperty("hash").GetString(), Hash.HashState(result.Data));
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
    public void RngStreamsMatchTypeScript()
    {
        var fixture = Load("rng.json");
        foreach (var vector in fixture.GetProperty("hashStringToU32").EnumerateArray())
        {
            Assert.Equal(vector.GetProperty("value").GetDouble(), RngMath.HashStringToU32(RawString(vector.GetProperty("input"))));
        }

        foreach (var entry in fixture.GetProperty("seeds").EnumerateArray())
        {
            var seedElement = entry.GetProperty("seed");
            var initial = seedElement.ValueKind == JsonValueKind.String
                ? RngMath.CreateRngState(RawString(seedElement))
                : RngMath.CreateRngState(seedElement.GetDouble());
            var label = seedElement.GetRawText();
            AssertSameStable($"seed {label} initial", StableJson.Stringify(entry.GetProperty("initial")), StableJson.Stringify(initial));

            var s = initial;
            var index = 0;
            foreach (var expected in entry.GetProperty("u32").EnumerateArray())
            {
                var (value, next) = RngMath.NextU32(s);
                Assert.True(expected.GetDouble() == value, $"seed {label} u32[{index++}]");
                s = next;
            }
            AssertSameStable($"seed {label} u32End", StableJson.Stringify(entry.GetProperty("u32End")), StableJson.Stringify(s));

            s = initial;
            foreach (var expected in entry.GetProperty("float").EnumerateArray())
            {
                var (value, next) = RngMath.NextFloat(s);
                Assert.Equal(expected.GetDouble(), value);
                s = next;
            }
            s = initial;
            foreach (var expected in entry.GetProperty("intDie").EnumerateArray())
            {
                var (value, next) = RngMath.NextInt(s, 1, 6);
                Assert.Equal(expected.GetDouble(), value);
                s = next;
            }
            s = initial;
            foreach (var expected in entry.GetProperty("intSigned").EnumerateArray())
            {
                var (value, next) = RngMath.NextInt(s, -5, 5);
                Assert.Equal(expected.GetDouble(), value);
                s = next;
            }

            var rng = new Rng(initial);
            foreach (var table in entry.GetProperty("weighted").EnumerateArray())
            {
                var weights = table.GetProperty("weights").EnumerateArray().Select(w => w.GetDouble()).ToList();
                foreach (var pick in table.GetProperty("picks").EnumerateArray())
                {
                    Assert.Equal(pick.GetDouble(), rng.Weighted(weights));
                }
            }
            AssertSameStable($"seed {label} after weighted", StableJson.Stringify(entry.GetProperty("rngAfterWeighted")), StableJson.Stringify(rng.State));
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
            Assert.Equal(entry.GetProperty("hash").GetString(), Hash.HashText(stable));
        }
    }

    /// <summary>
    /// Decode a JSON string token allowing lone surrogates (JS strings may hold
    /// them; System.Text.Json refuses to materialize them).
    /// </summary>
    private static string RawString(JsonElement element)
    {
        var raw = element.GetRawText();
        var sb = new StringBuilder();
        for (var i = 1; i < raw.Length - 1; i++)
        {
            var c = raw[i];
            if (c != '\\') { sb.Append(c); continue; }
            var e = raw[++i];
            switch (e)
            {
                case 'u':
                    sb.Append((char)Convert.ToInt32(raw.Substring(i + 1, 4), 16));
                    i += 4;
                    break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                default: sb.Append(e); break;
            }
        }
        return sb.ToString();
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

    private static void AssertSameJson<T>(string what, string expectedHash, JsonElement? expectedJson, T actual)
    {
        var actualStable = Hash.StableStringify(actual);
        if (expectedJson is { } json)
        {
            AssertSameStable(what, StableJson.Stringify(json), actualStable);
        }
        Assert.Equal(expectedHash, Hash.HashText(actualStable));
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
