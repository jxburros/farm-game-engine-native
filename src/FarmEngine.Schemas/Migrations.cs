using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FarmEngine.Json;

namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/migrations.ts.
//
// Versioned, pure project migrations.
//
// Every migration is a pure `(vN) => vN+1` function over plain JSON data.
// They never touch wall-clock time, RNG or the DOM, so the same input always
// yields the same output. Fixtures for every released version live in
// tests/FarmEngine.Core.Tests/Fixtures/ and are replayed through this pipeline
// in CI.
//
// C# notes: the TS works on `Record<string, any>`; here the raw data is a
// mutable System.Text.Json.Nodes DOM. The public entry points deep-clone
// their input once, so callers' nodes are never modified; the individual
// steps then take ownership of their argument and update it in place, which
// is observably the same as the TS object spreads (existing keys keep their
// position, new keys are appended in literal order).

/// <summary>TS <c>MigrationResult&lt;T&gt;</c>.</summary>
public sealed record MigrationResult<T>
{
    public bool Ok { get; init; }
    public T? Data { get; init; }
    public double FromVersion { get; init; }
    public bool Migrated { get; init; }
    public List<string> Errors { get; init; } = [];
}

public static class MigrationsSchema
{
    /// <summary>Default weather set every pre-v6 project receives (tunable afterwards).</summary>
    public static WeatherConfig DefaultWeatherConfig() => new()
    {
        Types =
        [
            new() { Id = "sun", Name = "Sunny", WatersOutdoorSoil = false, CropDamageChance = 0, NpcsStayInside = false, Overlay = null },
            new() { Id = "rain", Name = "Rain", WatersOutdoorSoil = true, CropDamageChance = 0, NpcsStayInside = false, Overlay = "rain" },
            new() { Id = "storm", Name = "Storm", WatersOutdoorSoil = true, CropDamageChance = 0.03, NpcsStayInside = true, Overlay = "rain" },
            new() { Id = "snow", Name = "Snow", WatersOutdoorSoil = false, CropDamageChance = 0, NpcsStayInside = false, Overlay = "snow" },
        ],
        Table = new OrderedDictionary<string, List<WeatherTableEntry>>
        {
            ["spring"] = [Entry("sun", 6), Entry("rain", 3), Entry("storm", 1)],
            ["summer"] = [Entry("sun", 7), Entry("rain", 1), Entry("storm", 2)],
            ["fall"] = [Entry("sun", 6), Entry("rain", 3), Entry("storm", 1)],
            ["winter"] = [Entry("sun", 5), Entry("snow", 5)],
        },
    };

    private static WeatherTableEntry Entry(string weatherId, double weight) => new() { WeatherId = weatherId, Weight = weight };
}

/// <summary>Port of the migration pipeline in <c>migrations.ts</c>.</summary>
public static class Migrations
{
    /// <summary>Classify a tile type into its render layer (duplicated from engine rules on purpose — migrations must be frozen in time, not track live code).</summary>
    private static string ClassifyTileTypeV2(JsonNode? type) => RawJson.AsString(type) switch
    {
        "path" => "overlay",
        "wall" or "door" => "object",
        _ => "background",
    };

    private static JsonNode? MigrateTileToLayers(JsonNode? tile)
    {
        if (RawJson.Get(RawJson.Require(tile), "background") is not null) return tile;
        var type = RawJson.Get(tile, "type");
        var layer = ClassifyTileTypeV2(type);
        var result = RawJson.Spread(tile);
        result["background"] = layer == "background" ? RawJson.Clone(type) : "grass";
        result["overlay"] = layer == "overlay" ? RawJson.Clone(type) : null;
        result["object"] = layer == "object" ? RawJson.Clone(type) : null;
        return result;
    }

    /// <summary>v1 → v2: introduce layered tiles (background/overlay/object).</summary>
    private static JsonObject MigrateV1ToV2(JsonObject project)
    {
        RawJson.Set(project, "scenes", RawJson.MapArray(RawJson.Get(project, "scenes"), scene =>
        {
            var result = RawJson.Spread(scene);
            RawJson.Set(result, "tiles", RawJson.MapArray(RawJson.Get(result, "tiles"), row => RawJson.MapArray(row, MigrateTileToLayers)));
            return result;
        }));
        return project;
    }

    /// <summary>Backfill scene/tile fields older data may lack.</summary>
    private static JsonArray BackfillScenes(JsonNode? scenes) => RawJson.MapArray(scenes, scene =>
    {
        var result = RawJson.Spread(scene);
        RawJson.Default(result, "transitions", () => new JsonArray());
        RawJson.Default(result, "npcs", () => new JsonArray());
        RawJson.Default(result, "events", () => new JsonArray());
        RawJson.Set(result, "tiles", RawJson.MapArray(RawJson.Get(result, "tiles"), row => RawJson.MapArray(row, tile =>
        {
            var t = RawJson.Spread(RawJson.Require(tile));
            // collision: tile.collision ?? tile.object === 'wall'
            RawJson.Default(t, "collision", () => JsonValue.Create(RawJson.AsString(RawJson.Get(t, "object")) == "wall"));
            RawJson.Default(t, "soilMoisture", () => RawJson.Num(0));
            RawJson.Default(t, "soilFertility", () => RawJson.Num(0));
            return t;
        })));
        return result;
    });

    /// <summary>
    /// v2 → v3: make every optional-in-practice field concrete and stamp
    /// schemaVersion. Replaces the ad-hoc backfill effects from App.tsx.
    /// </summary>
    private static JsonObject MigrateV2ToV3(JsonObject project)
    {
        // Values the TS object literal reads from the ORIGINAL project after
        // earlier literal entries have replaced them in the spread copy.
        var firstSceneId = RawJson.Clone(RawJson.Get(RawJson.Index(RawJson.Get(project, "scenes"), 0), "id"));
        var originalCurrentTime = RawJson.Clone(RawJson.Get(project, "currentTime"));
        var player = RawJson.Spread(RawJson.Get(project, "player") ?? new JsonObject());

        RawJson.Set(project, "scenes", BackfillScenes(RawJson.Get(project, "scenes")));
        RawJson.Default(project, "id", () => "project-1");
        RawJson.Default(project, "name", () => "My Farming Game");
        RawJson.Default(project, "version", () => "2.0");
        RawJson.Default(project, "npcs", () => new JsonArray());
        RawJson.Default(project, "items", () => new JsonArray());
        RawJson.Default(project, "events", () => new JsonArray());
        RawJson.Default(project, "dialogues", () => new JsonArray());
        RawJson.Default(project, "quests", () => new JsonArray());
        RawJson.Default(project, "eventFlags", () => new JsonObject());
        RawJson.Default(project, "startSceneId", () => firstSceneId ?? "scene-farm");
        RawJson.Default(project, "mode", () => "play");
        RawJson.Default(project, "selectedTileType", () => "grass");
        RawJson.Default(project, "selectedNPCId", () => null);
        RawJson.Default(project, "selectedItemId", () => null);
        RawJson.Default(project, "currentTime", () => RawJson.Num(0));
        RawJson.Default(project, "customAssets", () => new JsonArray());
        RawJson.Default(project, "currentSeason", () => "spring");
        RawJson.Default(project, "currentDay", () => RawJson.Num(1));
        RawJson.Default(project, "gameStartTime", () => originalCurrentTime ?? RawJson.Num(0));

        RawJson.Default(player, "direction", () => "down");
        RawJson.Default(player, "inventory", () => new JsonArray());
        RawJson.Default(player, "maxInventorySize", () => RawJson.Num(20));
        RawJson.Default(player, "money", () => RawJson.Num(0));
        RawJson.Default(player, "activeQuests", () => new JsonArray());
        RawJson.Default(player, "completedQuests", () => new JsonArray());
        RawJson.Default(player, "pixelX", () => RawJson.Num(0));
        RawJson.Default(player, "pixelY", () => RawJson.Num(0));
        RawJson.Default(player, "targetX", () => RawJson.Num(0));
        RawJson.Default(player, "targetY", () => RawJson.Num(0));
        RawJson.Set(project, "player", player);
        return project;
    }

    /// <summary>
    /// Convert a legacy ms growth duration into whole in-game days.
    /// 5 real seconds ≈ 1 in-game day for prototype-era timings, clamped to [1, 28].
    /// </summary>
    private static double MsToGrowthDays(double growthTimeMs) => Math.Min(28, Math.Max(1, Js.Round(growthTimeMs / 5000)));

    /// <summary>
    /// v3 → v4 (M2): day-based game time.
    /// - custom crop definitions gain growthDays/regrowthDays (converted from ms)
    /// - planted crops gain plantedOnDay/daysGrown (approximated from their
    ///   current wall-clock stage so in-progress farms keep their progress)
    /// - projects gain shops, nodeTypes, settings, currentTimeMinutes,
    ///   currentYear and player energy
    /// </summary>
    private static JsonObject MigrateV3ToV4(JsonObject project)
    {
        var currentDay = RawJson.Get(project, "currentDay")?.DeepClone() ?? RawJson.Num(1);
        var currentTime = RawJson.ToNumber(RawJson.Get(project, "currentTime") ?? RawJson.Num(0));

        // Snapshot of the ORIGINAL definitions (the TS lookup holds the
        // pre-backfill objects, not the ones the customCrops map produces).
        var cropDefLookup = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var crop in RawJson.Elements(RawJson.Get(project, "customCrops")))
        {
            cropDefLookup[RawJson.PropertyKey(RawJson.Require(crop), "id")] = RawJson.Clone(crop);
        }

        JsonNode? MigrateCrop(JsonNode? crop)
        {
            if (!RawJson.Truthy(crop) || RawJson.Has(crop, "plantedOnDay")) return crop;
            cropDefLookup.TryGetValue(RawJson.PropertyKey(crop, "type"), out var def);
            var growthTime = RawJson.ToNumber(RawJson.Get(def, "growthTime") ?? RawJson.Num(15000));
            var stages = RawJson.ToNumber(RawJson.Get(def, "stages") ?? RawJson.Num(4));
            var growthDays = RawJson.Get(def, "growthDays") is { } gd ? RawJson.ToNumber(gd) : MsToGrowthDays(growthTime);
            // Recover the legacy wall-clock stage, then express it as watered days.
            var plantedAt = RawJson.Get(crop, "plantedAt") is { } pa ? RawJson.ToNumber(pa) : currentTime;
            var elapsed = Math.Max(0, currentTime - plantedAt);
            var stageTime = growthTime / stages;
            var waterPenalty = RawJson.Truthy(RawJson.Get(crop, "watered")) ? 1 : 0.5;
            var legacyStage = Math.Min(Math.Floor((elapsed * waterPenalty) / stageTime), stages - 1);
            var daysGrown = Math.Min(growthDays, Math.Floor((legacyStage / Math.Max(1, stages - 1)) * growthDays));
            var result = RawJson.Spread(crop);
            result["plantedOnDay"] = currentDay.DeepClone();
            result["daysGrown"] = RawJson.Num(daysGrown);
            result["stage"] = RawJson.Num(legacyStage);
            return result;
        }

        RawJson.Set(project, "customCrops", RawJson.MapArray(RawJson.Get(project, "customCrops"), def =>
        {
            var result = RawJson.Spread(RawJson.Require(def));
            RawJson.Default(result, "growthDays", () => RawJson.Num(MsToGrowthDays(RawJson.ToNumber(RawJson.Get(result, "growthTime") ?? RawJson.Num(15000)))));
            if (RawJson.Get(result, "regrowthDays") is null)
            {
                if (RawJson.Truthy(RawJson.Get(result, "canRegrow")) && RawJson.Truthy(RawJson.Get(result, "regrowthTime")))
                {
                    result["regrowthDays"] = RawJson.Num(MsToGrowthDays(RawJson.ToNumber(RawJson.Get(result, "regrowthTime"))));
                }
                else
                {
                    // regrowthDays: undefined — dropped on serialization.
                    result.Remove("regrowthDays");
                }
            }
            return result;
        }));
        RawJson.Set(project, "scenes", RawJson.MapArray(RawJson.Get(project, "scenes"), scene =>
        {
            var result = RawJson.Spread(scene);
            RawJson.Set(result, "tiles", RawJson.MapArray(RawJson.Get(result, "tiles"), row => RawJson.MapArray(row, tile =>
            {
                var crop = RawJson.Get(RawJson.Require(tile), "crop");
                if (!RawJson.Truthy(crop)) return tile;
                var t = RawJson.Spread(tile);
                RawJson.Set(t, "crop", MigrateCrop(crop));
                return t;
            })));
            return result;
        }));
        RawJson.Default(project, "currentTimeMinutes", () => RawJson.Num(6 * 60));
        RawJson.Default(project, "currentYear", () => RawJson.Num(1));
        RawJson.Default(project, "shops", () => new JsonArray());
        RawJson.Default(project, "nodeTypes", () => new JsonArray());
        RawJson.Default(project, "settings", () => new JsonObject());
        // Exported games have no player object — only touch it when present.
        var player = RawJson.Get(project, "player");
        if (RawJson.Truthy(player))
        {
            var p = RawJson.Spread(player);
            RawJson.Default(p, "energy", () => RawJson.Num(100));
            RawJson.Default(p, "maxEnergy", () => RawJson.Num(100));
            RawJson.Set(project, "player", p);
        }
        return project;
    }

    /// <summary>
    /// v4 → v5 (M3): events gain trigger + conditions; legacy
    /// triggerType/triggerX/requiredItem/requiredFlag/triggerTime fields are
    /// converted. (The legacy event model was never evaluated at runtime, so
    /// this conversion is best-effort by declared intent.)
    /// </summary>
    private static JsonNode? MigrateEventV4(JsonNode? ev)
    {
        RawJson.Require(ev);
        if (RawJson.Has(ev, "trigger") && RawJson.Has(ev, "conditions")) return ev;
        var conditions = new JsonArray();
        var trigger = "enter";
        switch (RawJson.AsString(RawJson.Get(ev, "triggerType")))
        {
            case "location":
                trigger = "enter";
                if (RawJson.Has(ev, "triggerX") && RawJson.Has(ev, "triggerY"))
                {
                    conditions.Add(new JsonObject
                    {
                        ["type"] = "enterTile",
                        ["x"] = RawJson.Clone(RawJson.Get(ev, "triggerX")),
                        ["y"] = RawJson.Clone(RawJson.Get(ev, "triggerY")),
                    });
                }
                break;
            case "item":
                trigger = "tick";
                if (RawJson.Truthy(RawJson.Get(ev, "requiredItem")))
                {
                    conditions.Add(new JsonObject
                    {
                        ["type"] = "hasItem",
                        ["itemId"] = RawJson.Clone(RawJson.Get(ev, "requiredItem")),
                        ["quantity"] = RawJson.Num(1),
                    });
                }
                break;
            case "flag":
                trigger = "tick";
                if (RawJson.Truthy(RawJson.Get(ev, "requiredFlag")))
                {
                    conditions.Add(new JsonObject
                    {
                        ["type"] = "flag",
                        ["flag"] = RawJson.Clone(RawJson.Get(ev, "requiredFlag")),
                        ["value"] = true,
                    });
                }
                break;
            case "time":
                trigger = "tick";
                if (RawJson.Has(ev, "triggerTime"))
                {
                    var time = RawJson.Get(ev, "triggerTime");
                    conditions.Add(new JsonObject
                    {
                        ["type"] = "timeOfDay",
                        ["minMinute"] = RawJson.Clone(time),
                        ["maxMinute"] = RawJson.JsAdd(time, 60),
                    });
                }
                break;
            default:
                trigger = "enter";
                break;
        }
        var result = RawJson.Spread(ev);
        result["trigger"] = trigger;
        result["conditions"] = conditions;
        RawJson.Default(result, "outcomes", () => new JsonArray());
        RawJson.Default(result, "active", () => true);
        RawJson.Default(result, "repeatable", () => false);
        return result;
    }

    private static JsonObject MigrateV4ToV5(JsonObject project)
    {
        RawJson.Set(project, "events", RawJson.MapArray(RawJson.Get(project, "events"), MigrateEventV4));
        return project;
    }

    /// <summary>
    /// v5 → v6 (M4): recipes, machines, weather, animals, fishing, mining,
    /// skills — all backfilled with sensible empty/default content.
    /// </summary>
    private static JsonObject MigrateV5ToV6(JsonObject project)
    {
        RawJson.Default(project, "recipes", () => new JsonArray());
        RawJson.Default(project, "machineTypes", () => new JsonArray());
        RawJson.Default(project, "weather", () => JsonSerializer.SerializeToNode(MigrationsSchema.DefaultWeatherConfig(), JsonDefaults.Options));
        RawJson.Default(project, "animalSpecies", () => new JsonArray());
        RawJson.Default(project, "animals", () => new JsonArray());
        RawJson.Default(project, "fishTables", () => new JsonArray());
        RawJson.Default(project, "mine", () => new JsonObject { ["enabled"] = false });
        return project;
    }

    /// <summary>v6 → v7 (M5): installed content packs live in the project.</summary>
    private static JsonObject MigrateV6ToV7(JsonObject project)
    {
        RawJson.Default(project, "contentPacks", () => new JsonArray());
        return project;
    }

    /// <summary>v7 → v8: graphics settings (pixel-art rendering on by default).</summary>
    private static JsonObject MigrateV7ToV8(JsonObject project)
    {
        RawJson.Default(project, "graphics", () => new JsonObject { ["pixelArt"] = true });
        return project;
    }

    /// <summary>
    /// Registry of migrations (TS <c>MIGRATIONS</c>). <c>Registry[n]</c> upgrades a
    /// version-n project to version n+1. Each step takes ownership of its
    /// argument and may update it in place; pass a <c>DeepClone()</c> to keep
    /// the original.
    /// </summary>
    public static readonly IReadOnlyDictionary<double, Func<JsonObject, JsonObject>> Registry =
        new Dictionary<double, Func<JsonObject, JsonObject>>
        {
            [1] = MigrateV1ToV2,
            [2] = MigrateV2ToV3,
            [3] = MigrateV3ToV4,
            [4] = MigrateV4ToV5,
            [5] = MigrateV5ToV6,
            [6] = MigrateV6ToV7,
            [7] = MigrateV7ToV8,
        };

    /// <summary>Detect the schema version of a raw (possibly legacy) project object.</summary>
    public static double DetectProjectVersion(JsonNode? raw)
    {
        if (!RawJson.IsObjectLike(raw)) return ProjectSchema.CurrentProjectSchemaVersion;
        var schemaVersion = RawJson.Get(raw, "schemaVersion");
        if (RawJson.IsNumber(schemaVersion)) return RawJson.ToNumber(schemaVersion);
        var firstTile = RawJson.Index(RawJson.Index(RawJson.Get(RawJson.Index(RawJson.Get(raw, "scenes"), 0), "tiles"), 0), 0);
        if (RawJson.Truthy(firstTile) && RawJson.Get(firstTile, "background") is not null) return 2;
        return 1;
    }

    /// <summary>
    /// Migrate a raw project object (from storage or import) up to the current
    /// schema version and validate it. Never throws.
    /// </summary>
    public static MigrationResult<GameProject> MigrateProject(JsonNode? raw)
    {
        if (!RawJson.IsObjectLike(raw))
        {
            return Fail<GameProject>(0, false, "Project data is not an object");
        }

        var fromVersion = DetectProjectVersion(raw);
        var current = ProjectSchema.CurrentProjectSchemaVersion;
        if (fromVersion > current)
        {
            return Fail<GameProject>(fromVersion, false,
                $"Project schema version {Js.Num(fromVersion)} is newer than this engine supports ({Js.Num(current)}). Update the engine.");
        }

        var migrated = fromVersion < current;
        JsonObject project;
        try
        {
            project = RawJson.Spread(raw!.DeepClone());
            for (var v = fromVersion; v < current; v++)
            {
                if (!Registry.TryGetValue(v, out var step))
                {
                    return Fail<GameProject>(fromVersion, false, $"No migration registered for schema version {Js.Num(v)}");
                }
                project = step(project);
            }
            project["schemaVersion"] = RawJson.Num(current);
        }
        catch (Exception ex)
        {
            return Fail<GameProject>(fromVersion, migrated, $"Migration failed: {ex.Message}");
        }

        return Parse<GameProject>(project, fromVersion, migrated, SchemaValidation.ValidateProject);
    }

    /// <summary><see cref="MigrateProject(JsonNode?)"/> over JSON text. Never throws.</summary>
    public static MigrationResult<GameProject> MigrateProject(string json) =>
        RawJson.TryParse(json, out var node, out var error)
            ? MigrateProject(node)
            : Fail<GameProject>(0, false, $"Project data is not valid JSON: {error}");

    /// <summary>
    /// Migrate + validate an exported/shared game payload. Shares the tile-layer
    /// and backfill logic with project migrations.
    /// </summary>
    public static MigrationResult<ExportedGame> MigrateExportedGame(JsonNode? raw)
    {
        if (!RawJson.IsObjectLike(raw))
        {
            return Fail<ExportedGame>(0, false, "Game data is not an object");
        }

        var fromVersion = DetectProjectVersion(raw);
        var current = ProjectSchema.CurrentProjectSchemaVersion;
        if (fromVersion > current)
        {
            return Fail<ExportedGame>(fromVersion, false,
                $"Game schema version {Js.Num(fromVersion)} is newer than this engine supports ({Js.Num(current)}). Update the engine.");
        }

        var migrated = fromVersion < current;
        JsonObject game;
        try
        {
            // Exported games ride the SAME migration registry as projects — a
            // hand-maintained parallel if-chain here drifted from the registry and
            // guaranteed that every new version had to be added in two places.
            var original = RawJson.Spread(raw!.DeepClone());
            var originalKeys = original.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
            game = original;
            for (var v = fromVersion; v < current; v++)
            {
                if (!Registry.TryGetValue(v, out var step))
                {
                    return Fail<ExportedGame>(fromVersion, false, $"No migration registered for schema version {Js.Num(v)}");
                }
                game = step(game);
            }
            // Project migrations synthesize editor-only fields and a skeleton player;
            // an exported game must not gain fields its original never had (a
            // backfilled player without x/y would even fail validation below).
            if (!originalKeys.Contains("player")) game.Remove("player");
            foreach (var editorOnlyKey in EditorOnlyKeys)
            {
                if (!originalKeys.Contains(editorOnlyKey)) game.Remove(editorOnlyKey);
            }
            game["schemaVersion"] = RawJson.Num(current);
        }
        catch (Exception ex)
        {
            return Fail<ExportedGame>(fromVersion, migrated, $"Migration failed: {ex.Message}");
        }

        return Parse<ExportedGame>(game, fromVersion, migrated, SchemaValidation.ValidateExportedGame);
    }

    /// <summary><see cref="MigrateExportedGame(JsonNode?)"/> over JSON text. Never throws.</summary>
    public static MigrationResult<ExportedGame> MigrateExportedGame(string json) =>
        RawJson.TryParse(json, out var node, out var error)
            ? MigrateExportedGame(node)
            : Fail<ExportedGame>(0, false, $"Game data is not valid JSON: {error}");

    private static readonly string[] EditorOnlyKeys =
        ["id", "mode", "selectedTileType", "selectedNPCId", "selectedItemId", "currentTime", "eventFlags"];

    private static MigrationResult<T> Parse<T>(JsonObject data, double fromVersion, bool migrated, Func<T, IReadOnlyList<string>> validate)
        where T : class
    {
        var (parsed, errors) = RawJson.ParseAndValidate(data, validate);
        if (parsed is null || errors.Count > 0)
        {
            return new MigrationResult<T> { Ok = false, Data = null, FromVersion = fromVersion, Migrated = migrated, Errors = errors };
        }
        return new MigrationResult<T> { Ok = true, Data = parsed, FromVersion = fromVersion, Migrated = migrated, Errors = [] };
    }

    private static MigrationResult<T> Fail<T>(double fromVersion, bool migrated, string error) =>
        new() { Ok = false, Data = default, FromVersion = fromVersion, Migrated = migrated, Errors = [error] };
}

/// <summary>
/// JS-semantics helpers over the System.Text.Json.Nodes DOM for migrations,
/// which (like the TS) operate on raw <c>Record&lt;string, any&gt;</c> data.
/// A missing key and a JSON <c>null</c> are both "nullish" (<c>??</c>);
/// <see cref="Has"/> is the <c>!== undefined</c> / <c>in</c> test.
/// </summary>
internal static class RawJson
{
    /// <summary><c>raw &amp;&amp; typeof raw === 'object'</c> (arrays are objects in JS).</summary>
    public static bool IsObjectLike(JsonNode? node) => node is JsonObject or JsonArray;

    /// <summary>Optional-chaining property read (<c>obj?.key</c>): null for non-objects, missing keys and JSON null.</summary>
    public static JsonNode? Get(JsonNode? obj, string key) =>
        obj is JsonObject o && o.TryGetPropertyValue(key, out var value) ? value : null;

    /// <summary><c>obj.key !== undefined</c> (a JSON null counts as defined).</summary>
    public static bool Has(JsonNode? obj, string key) => obj is JsonObject o && o.ContainsKey(key);

    /// <summary><c>arr?.[i]</c>.</summary>
    public static JsonNode? Index(JsonNode? arr, int i) =>
        arr is JsonArray a && i >= 0 && i < a.Count ? a[i] : null;

    public static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    public static JsonValue Num(double value) => JsonValue.Create(value);

    /// <summary>Property access on <c>null</c>/<c>undefined</c> throws a TypeError in JS.</summary>
    public static JsonNode Require(JsonNode? node) =>
        node ?? throw new InvalidOperationException("Cannot read properties of null");

    public static bool IsNumber(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number;

    public static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? v.TryGetValue<string>(out var s) ? s : JsonSerializer.SerializeToElement(v).GetString()
            : null;

    /// <summary>JS truthiness: null, false, 0, NaN and "" are falsy; objects/arrays are truthy.</summary>
    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => AsString(v)!.Length > 0,
            JsonValueKind.Number => ToNumber(v) is var d && d != 0 && !double.IsNaN(d),
            _ => false,
        },
        _ => true,
    };

    /// <summary>JS <c>Number(x)</c> (approximate for objects).</summary>
    public static double ToNumber(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return 0;
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.Number:
                        return v.TryGetValue<double>(out var d) ? d : JsonSerializer.SerializeToElement(v).GetDouble();
                    case JsonValueKind.True:
                        return 1;
                    case JsonValueKind.String:
                        var s = AsString(v)!.Trim();
                        if (s.Length == 0) return 0;
                        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;
                    default:
                        return 0;
                }
            case JsonArray { Count: 0 }:
                return 0;
            case JsonArray { Count: 1 } a:
                return ToNumber(a[0]);
            default:
                return double.NaN;
        }
    }

    /// <summary>JS <c>x + n</c>: string concatenation for strings, numeric addition otherwise.</summary>
    public static JsonNode JsAdd(JsonNode? x, double n)
    {
        if (AsString(x) is { } s) return JsonValue.Create(s + Js.Num(n));
        return Num(ToNumber(x) + n);
    }

    /// <summary>The string a JS object key lookup <c>obj[x.key]</c> coerces <c>x.key</c> to.</summary>
    public static string PropertyKey(JsonNode? obj, string key)
    {
        if (!Has(obj, key)) return "undefined";
        return KeyString(Get(obj, key));
    }

    private static string KeyString(JsonNode? value) => value switch
    {
        null => "null",
        JsonArray a => string.Join(",", a.Select(e => e is null ? "" : KeyString(e))),
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => AsString(v)!,
            JsonValueKind.Number => Js.Num(ToNumber(v)),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "null",
        },
        _ => "[object Object]",
    };

    /// <summary>
    /// <c>{ ...node }</c>. An object is returned as-is (the caller owns it and
    /// updates it in place); arrays and strings spread to index keys; other
    /// primitives and null spread to <c>{}</c>.
    /// </summary>
    public static JsonObject Spread(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                return o;
            case JsonArray a:
            {
                var result = new JsonObject();
                for (var i = 0; i < a.Count; i++) result[i.ToString(CultureInfo.InvariantCulture)] = Clone(a[i]);
                return result;
            }
            default:
            {
                var result = new JsonObject();
                if (AsString(node) is { } s)
                {
                    for (var i = 0; i < s.Length; i++) result[i.ToString(CultureInfo.InvariantCulture)] = s[i].ToString();
                }
                return result;
            }
        }
    }

    /// <summary>
    /// <c>(node ?? []).map(f)</c>, reusing the array in place. Throws (like
    /// JS's "map is not a function") when the value is not an array.
    /// </summary>
    public static JsonArray MapArray(JsonNode? node, Func<JsonNode?, JsonNode?> map)
    {
        if (node is null) return [];
        if (node is not JsonArray array) throw new InvalidOperationException($"Expected an array at '{node.GetPath()}'");
        for (var i = 0; i < array.Count; i++)
        {
            var old = array[i];
            var mapped = map(old);
            if (!ReferenceEquals(old, mapped)) array[i] = mapped?.Parent is null ? mapped : mapped.DeepClone();
        }
        return array;
    }

    /// <summary><c>for (const x of node ?? [])</c>.</summary>
    public static IEnumerable<JsonNode?> Elements(JsonNode? node) => node switch
    {
        null => [],
        JsonArray a => a,
        _ => throw new InvalidOperationException($"Expected an array at '{node.GetPath()}'"),
    };

    /// <summary><c>obj[key] = value</c> unless it already holds that very node.</summary>
    public static void Set(JsonObject obj, string key, JsonNode? value)
    {
        if (value is not null && obj.TryGetPropertyValue(key, out var existing) && ReferenceEquals(existing, value)) return;
        if (value?.Parent is not null) value = value.DeepClone();
        obj[key] = value;
    }

    /// <summary>The spread idiom <c>{ ...obj, key: obj.key ?? fallback }</c>.</summary>
    public static void Default(JsonObject obj, string key, Func<JsonNode?> fallback)
    {
        if (Get(obj, key) is not null) return;
        Set(obj, key, fallback());
    }

    public static bool TryParse(string json, out JsonNode? node, out string error)
    {
        try
        {
            node = JsonNode.Parse(json);
            error = "";
            return true;
        }
        catch (JsonException ex)
        {
            node = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// zod <c>safeParse</c>: deserialize with <see cref="JsonDefaults.Options"/>
    /// (applies defaults, strips non-passthrough keys), then run the
    /// refinement checks. Type errors are reported zod-style
    /// (<c>player.money: Expected number, received string</c>). Returns at
    /// most 20 errors; never throws.
    /// </summary>
    public static (T? Parsed, List<string> Errors) ParseAndValidate<T>(JsonNode data, Func<T, IReadOnlyList<string>> validate)
        where T : class
    {
        T? parsed;
        try
        {
            parsed = data.Deserialize<T>(JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            string description;
            try
            {
                description = DescribeJsonException(ex, data);
            }
            catch (Exception)
            {
                description = $": {ex.Message}";
            }
            return (null, [description]);
        }
        catch (Exception ex)
        {
            return (null, [$": {ex.Message}"]);
        }
        if (parsed is null) return (null, [": Expected object, received null"]);
        try
        {
            return (parsed, validate(parsed).Take(20).ToList());
        }
        catch (Exception ex)
        {
            return (null, [$": {ex.Message}"]);
        }
    }

    private static readonly Regex PathSegment = new(@"\.([^.\[]+)|\[(\d+)\]|\['((?:[^'\\]|\\.)*)'\]", RegexOptions.CultureInvariant);

    /// <summary>JsonException path (<c>$.scenes[0].tiles[1][2].x</c>) → zod path segments.</summary>
    private static List<string> PathSegments(string? path)
    {
        var segments = new List<string>();
        if (string.IsNullOrEmpty(path)) return segments;
        foreach (Match m in PathSegment.Matches(path))
        {
            segments.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
        }
        return segments;
    }

    private static string DescribeJsonException(JsonException ex, JsonNode data)
    {
        var segments = PathSegments(ex.Path);
        var zodPath = string.Join(".", segments);
        JsonNode? node = data;
        var found = true;
        foreach (var segment in segments)
        {
            if (node is JsonObject o && o.TryGetPropertyValue(segment, out var child)) node = child;
            else if (node is JsonArray a && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < a.Count) node = a[i];
            else { found = false; break; }
        }
        var message = ex.Message;
        string? expected =
            message.Contains("System.Double", StringComparison.Ordinal) || message.Contains("System.UInt32", StringComparison.Ordinal) ? "number"
            : message.Contains("System.String", StringComparison.Ordinal) ? "string"
            : message.Contains("System.Boolean", StringComparison.Ordinal) ? "boolean"
            : message.Contains("List`1", StringComparison.Ordinal) || message.Contains("[]", StringComparison.Ordinal) ? "array"
            : message.Contains("could not be converted", StringComparison.Ordinal) ? "object"
            : null;
        if (expected is null || !found)
        {
            var first = message.Split(" Path:", 2)[0];
            return $"{zodPath}: {first}";
        }
        return $"{zodPath}: Expected {expected}, received {KindName(node)}";
    }

    private static string KindName(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => double.IsNaN(ToNumber(v)) ? "nan" : "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "null",
        },
        _ => "unknown",
    };
}
