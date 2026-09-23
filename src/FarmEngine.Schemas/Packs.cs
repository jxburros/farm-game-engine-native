using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FarmEngine.Json;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/packs.ts.
//
// Content packs (M5) — the modding unit. A pack is a JSON document (zip and
// folder loaders can wrap this later) with a manifest and declared content.
// Everything a mod can add is validated by these schemas; malformed packs
// produce actionable errors, never crashes or silent partial loads.

public sealed record PackPermissions
{
    /// <summary>Hook names the pack's plugins may subscribe to (user-approved at install).</summary>
    public List<string> Hooks { get; init; } = [];
    /// <summary>May contribute content definitions (the normal case).</summary>
    public bool ContentInject { get; init; } = true;
    /// <summary>Reserved: declarative UI panels (not yet implemented).</summary>
    public bool UiPanels { get; init; } = false;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record PackDependency
{
    public string PackId { get; init; } = "";
    public string? Version { get; init; }
}

/// <summary>
/// A sandboxed plugin: <c>source</c> is the body of <c>function (api) { ... }</c> and
/// registers handlers with <c>api.on(hookName, fn)</c>. Handlers return an array
/// of mutations (validated as <see cref="PluginMutation"/>) — never raw state access.
/// </summary>
public sealed record PackPlugin
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    /// <summary>Hooks this plugin wants; effective set = intersection with manifest permissions.hooks.</summary>
    public List<string> Hooks { get; init; } = [];
    public string Source { get; init; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record PackManifest
{
    /// <summary>Must match <c>^[a-z0-9][a-z0-9-]*$</c> (see <see cref="PacksSchema.PackIdPattern"/>).</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string? Description { get; init; }
    public string? Author { get; init; }
    /// <summary>Semver range: '*', exact '1.2.3', '^1.2.3' or '>=1.2.3'.</summary>
    public string EngineCompatibility { get; init; } = "*";
    /// <summary>
    /// Base packs (content-default) keep their plain IDs; all other packs get
    /// their definitions namespaced to <c>packId:localId</c> at load time.
    /// </summary>
    public bool Base { get; init; } = false;
    public List<PackDependency> Dependencies { get; init; } = [];
    /// <summary>
    /// Fully-qualified IDs this pack intentionally replaces. Redefining an
    /// existing ID without declaring it here is a conflict surfaced in the
    /// Problems panel (the earlier definition wins).
    /// </summary>
    public List<string> Overrides { get; init; } = [];
    public PackPermissions Permissions { get; init; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>TS <c>PackPlayerStartSchema.inventory</c> item (inline object).</summary>
public sealed record PackStartItem
{
    public string ItemId { get; init; } = "";
    /// <summary>int, min 1.</summary>
    public double Quantity { get; init; }
}

/// <summary>Optional player-start block so a base pack can express the whole starter game.</summary>
public sealed record PackPlayerStart
{
    public string? SceneId { get; init; }
    /// <summary>int.</summary>
    public double? X { get; init; }
    /// <summary>int.</summary>
    public double? Y { get; init; }
    public double? Money { get; init; }
    public List<PackStartItem> Inventory { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record PackContent
{
    public List<CropDefinition> Crops { get; init; } = [];
    public List<Item> Items { get; init; } = [];
    public List<RecipeDefinition> Recipes { get; init; } = [];
    public List<MachineTypeDefinition> MachineTypes { get; init; } = [];
    public List<NodeTypeDefinition> NodeTypes { get; init; } = [];
    public List<AnimalSpeciesDefinition> AnimalSpecies { get; init; } = [];
    public List<FishTable> FishTables { get; init; } = [];
    public List<WeatherTypeDefinition> WeatherTypes { get; init; } = [];
    public List<Npc> Npcs { get; init; } = [];
    public List<Dialogue> Dialogues { get; init; } = [];
    public List<Scene> Scenes { get; init; } = [];
    public List<GameEvent> Events { get; init; } = [];
    public List<Quest> Quests { get; init; } = [];
    public List<ShopDefinition> Shops { get; init; } = [];
    public List<ActionDef> Actions { get; init; } = [];
    public List<MinigameDef> Minigames { get; init; } = [];
    public PackPlayerStart? PlayerStart { get; init; }
    /// <summary>
    /// Per-locale string tables for game text (M7 i18n). Keys address content
    /// fields: <c>item:{id}:name</c>, <c>item:{id}:description</c>, <c>dialogue:{id}:text</c>,
    /// <c>quest:{id}:name</c>, <c>quest:{id}:description</c>. The authored text is the
    /// fallback locale.
    /// </summary>
    public OrderedDictionary<string, OrderedDictionary<string, string>> Strings { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record ContentPack
{
    public PackManifest Manifest { get; init; } = new();
    public PackContent Content { get; init; } = new();
    public List<PackPlugin> Plugins { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>How a project stores an installed pack. Array order = load order.</summary>
public sealed record PackInstallation
{
    public ContentPack Pack { get; init; } = new();
    public bool Enabled { get; init; } = true;
}

public sealed record PackValidationResult
{
    public bool Ok { get; init; }
    public ContentPack? Pack { get; init; }
    public List<string> Errors { get; init; } = [];
}

/// <summary>
/// TS <c>PluginMutationSchema</c> — the only things a plugin hook may do:
/// declared, validated mutations that flow through the command pipeline (so
/// replays stay deterministic). Reference targets that don't exist fail soft
/// with an error message — never a crash.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GiveItemMutation), "giveItem")]
[JsonDerivedType(typeof(TakeItemMutation), "takeItem")]
[JsonDerivedType(typeof(GiveMoneyMutation), "giveMoney")]
[JsonDerivedType(typeof(TakeMoneyMutation), "takeMoney")]
[JsonDerivedType(typeof(SetFlagMutation), "setFlag")]
[JsonDerivedType(typeof(MessageMutation), "message")]
[JsonDerivedType(typeof(SetWeatherMutation), "setWeather")]
[JsonDerivedType(typeof(ModifyFriendshipMutation), "modifyFriendship")]
[JsonDerivedType(typeof(GrantXpMutation), "grantXp")]
[JsonDerivedType(typeof(ModifyEnergyMutation), "modifyEnergy")]
[JsonDerivedType(typeof(StartQuestMutation), "startQuest")]
[JsonDerivedType(typeof(WarpPlayerMutation), "warpPlayer")]
[JsonDerivedType(typeof(StartDialogueMutation), "startDialogue")]
[JsonDerivedType(typeof(PlaySoundMutation), "playSound")]
[JsonDerivedType(typeof(PerformActionMutation), "performAction")]
[JsonDerivedType(typeof(StartMinigameMutation), "startMinigame")]
public abstract record PluginMutation
{
    [JsonIgnore]
    public abstract string Type { get; }
}

public sealed record GiveItemMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "giveItem";
    public string ItemId { get; init; } = "";
    /// <summary>int, 1..999.</summary>
    public double Quantity { get; init; }
}

public sealed record TakeItemMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "takeItem";
    public string ItemId { get; init; } = "";
    /// <summary>int, 1..999.</summary>
    public double Quantity { get; init; }
}

public sealed record GiveMoneyMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "giveMoney";
    /// <summary>int, 1..1_000_000.</summary>
    public double Amount { get; init; }
}

public sealed record TakeMoneyMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "takeMoney";
    /// <summary>int, 1..1_000_000.</summary>
    public double Amount { get; init; }
}

public sealed record SetFlagMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "setFlag";
    public string Flag { get; init; } = "";
    /// <summary><c>boolean | number | string</c>.</summary>
    public JsonElement Value { get; init; }
}

public sealed record MessageMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "message";
    /// <summary>max length 500.</summary>
    public string Text { get; init; } = "";
}

public sealed record SetWeatherMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "setWeather";
    public string WeatherId { get; init; } = "";
}

public sealed record ModifyFriendshipMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "modifyFriendship";
    public string NpcId { get; init; } = "";
    /// <summary>int, -1000..1000.</summary>
    public double Delta { get; init; }
}

public sealed record GrantXpMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "grantXp";
    public string Skill { get; init; } = "";
    /// <summary>int, 1..10_000.</summary>
    public double Amount { get; init; }
}

public sealed record ModifyEnergyMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "modifyEnergy";
    /// <summary>int, -1000..1000.</summary>
    public double Delta { get; init; }
}

public sealed record StartQuestMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "startQuest";
    public string QuestId { get; init; } = "";
}

public sealed record WarpPlayerMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "warpPlayer";
    public string SceneId { get; init; } = "";
    /// <summary>int, min 0.</summary>
    public double X { get; init; }
    /// <summary>int, min 0.</summary>
    public double Y { get; init; }
}

public sealed record StartDialogueMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "startDialogue";
    public string NpcId { get; init; } = "";
    public string? DialogueId { get; init; }
}

public sealed record PlaySoundMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "playSound";
    public string SoundId { get; init; } = "";
}

public sealed record PerformActionMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "performAction";
    public string ActionId { get; init; } = "";
}

public sealed record StartMinigameMutation : PluginMutation
{
    [JsonIgnore]
    public override string Type => "startMinigame";
    public string MinigameId { get; init; } = "";
}

public static partial class PacksSchema
{
    /// <summary>Engine feature-set version packs declare compatibility against.</summary>
    public const string EngineVersion = "0.5.0";

    /// <summary>TS <c>PackManifestSchema.id</c> regex.</summary>
    public const string PackIdPattern = "^[a-z0-9][a-z0-9-]*$";

    // `\z`, not `$`: .NET `$` also matches before a trailing newline, JS `$` does not.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*\z")]
    private static partial Regex PackIdRegex();

    // JS `\d` is ASCII-only; .NET `\d` also matches other Unicode digits.
    [GeneratedRegex("^([0-9]+)\\.([0-9]+)\\.([0-9]+)")]
    private static partial Regex VersionRegex();

    /// <summary>Whether a pack id satisfies the manifest id rule.</summary>
    public static bool IsValidPackId(string id) => PackIdRegex().IsMatch(id);

    /// <summary>
    /// Validate raw pack JSON. Never throws; errors are actionable paths.
    /// Approximates zod's <c>safeParse</c>: System.Text.Json reports the first
    /// structural error only (not every issue), and the manifest id rule is
    /// checked explicitly.
    /// </summary>
    public static PackValidationResult ValidateContentPack(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object)
        {
            return new PackValidationResult { Ok = false, Pack = null, Errors = ["Pack data is not an object — expected { manifest, content }"] };
        }
        var errors = new List<string>();
        if (!raw.TryGetProperty("manifest", out var manifest) || manifest.ValueKind != JsonValueKind.Object)
        {
            errors.Add("manifest: Required");
        }
        else
        {
            foreach (var key in new[] { "id", "name", "version" })
            {
                if (!manifest.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
                {
                    errors.Add($"manifest.{key}: Required");
                }
            }
            if (manifest.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                && !IsValidPackId(idElement.GetString()!))
            {
                errors.Add("manifest.id: pack ids must be lowercase letters, digits and dashes");
            }
        }
        if (errors.Count > 0) return new PackValidationResult { Ok = false, Pack = null, Errors = errors };

        ContentPack? pack;
        try
        {
            pack = raw.Deserialize<ContentPack>(JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            var path = string.IsNullOrEmpty(ex.Path) ? "(root)" : ex.Path.TrimStart('$', '.');
            return new PackValidationResult { Ok = false, Pack = null, Errors = [$"{(path.Length == 0 ? "(root)" : path)}: {ex.Message}"] };
        }
        if (pack is null)
        {
            return new PackValidationResult { Ok = false, Pack = null, Errors = ["(root): Expected object, received null"] };
        }
        return new PackValidationResult { Ok = true, Pack = pack, Errors = [] };
    }

    private static double[]? ParseVersion(string v)
    {
        var match = VersionRegex().Match(v.Trim());
        if (!match.Success) return null;
        return
        [
            double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
        ];
    }

    /// <summary>
    /// Minimal semver-range check ('*', exact, '^x.y.z', '>=x.y.z') — enough for
    /// pack compatibility warnings without a dependency.
    /// </summary>
    public static bool IsEngineCompatible(string? range, string version = EngineVersion)
    {
        var trimmed = (range ?? "*").Trim();
        if (trimmed == "" || trimmed == "*") return true;
        var current = ParseVersion(version);
        if (current is null) return true;
        if (trimmed.StartsWith(">=", StringComparison.Ordinal))
        {
            var min = ParseVersion(trimmed[2..]);
            if (min is null) return false;
            double a = current[0], b = current[1], c = current[2], x = min[0], y = min[1], z = min[2];
            return a != x ? a > x : b != y ? b > y : c >= z;
        }
        if (trimmed.StartsWith('^'))
        {
            var @base = ParseVersion(trimmed[1..]);
            if (@base is null) return false;
            double a = current[0], b = current[1], c = current[2], x = @base[0], y = @base[1], z = @base[2];
            if (a != x) return false;
            return b != y ? b > y : c >= z;
        }
        var exact = ParseVersion(trimmed);
        if (exact is null) return false;
        return exact[0] == current[0] && exact[1] == current[1] && exact[2] == current[2];
    }
}
