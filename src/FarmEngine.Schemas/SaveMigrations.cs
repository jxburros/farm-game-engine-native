using System.Text.Json;
using System.Text.Json.Nodes;
using FarmEngine.Json;

namespace FarmEngine.Schemas;

// Port of the migration half of packages/engine-schemas/src/save.ts
// (SAVE_MIGRATIONS, migrateGameState). The GameState schemas, CURRENT_SAVE_VERSION
// and centerCoordinate live in Save.cs.
//
// Like project migrations these operate on the raw JSON DOM; the entry point
// deep-clones its input and each step updates its argument in place (the
// observable equivalent of the TS object spreads).

/// <summary>Port of <c>SAVE_MIGRATIONS</c> / <c>migrateGameState</c>.</summary>
public static class SaveMigrations
{
    /// <summary>
    /// <c>{ ...(state.meta ?? {}), saveVersion: n, packs: state.meta?.packs ?? [] }</c>.
    /// </summary>
    private static void StampMeta(JsonObject state, double saveVersion)
    {
        var meta = RawJson.Spread(RawJson.Get(state, "meta"));
        meta["saveVersion"] = RawJson.Num(saveVersion);
        RawJson.Default(meta, "packs", () => new JsonArray());
        RawJson.Set(state, "meta", meta);
    }

    private static JsonObject MigrateV1ToV2(JsonObject state)
    {
        StampMeta(state, 2);
        var clock = RawJson.Spread(RawJson.Get(state, "clock"));
        RawJson.Default(clock, "tick", () => RawJson.Num(0));
        RawJson.Default(clock, "timeMinutes", () => RawJson.Num(360));
        RawJson.Default(clock, "day", () => RawJson.Num(1));
        RawJson.Default(clock, "season", () => "spring");
        RawJson.Default(clock, "year", () => RawJson.Num(1));
        RawJson.Default(clock, "weatherId", () => "sun");
        RawJson.Set(state, "clock", clock);
        var player = RawJson.Spread(RawJson.Get(state, "player"));
        RawJson.Default(player, "energy", () => RawJson.Num(100));
        RawJson.Default(player, "maxEnergy", () => RawJson.Num(100));
        RawJson.Set(state, "player", player);
        RawJson.Default(state, "shop", () => null);
        RawJson.Default(state, "shopPurchasesToday", () => new JsonObject());
        return state;
    }

    private static JsonObject MigrateV2ToV3(JsonObject state)
    {
        StampMeta(state, 3);
        var clock = RawJson.Spread(RawJson.Get(state, "clock"));
        RawJson.Default(clock, "weatherId", () => "sun");
        RawJson.Set(state, "clock", clock);
        var player = RawJson.Spread(RawJson.Get(state, "player"));
        RawJson.Default(player, "skills", () => new JsonObject());
        RawJson.Set(state, "player", player);
        RawJson.Default(state, "social", () => new JsonObject());
        RawJson.Default(state, "animals", () => new JsonArray());
        RawJson.Default(state, "mine", () => new JsonObject { ["deepestFloor"] = 0.0, ["currentFloor"] = 0.0 });
        RawJson.Default(state, "quarantinedItems", () => new JsonArray());
        return state;
    }

    private static JsonObject MigrateV3ToV4(JsonObject state)
    {
        StampMeta(state, 4);
        var player = RawJson.Spread(RawJson.Get(state, "player"));
        // Grid saves stored the occupied tile; the free-movement position is
        // that tile's center. Fractional values pass through untouched.
        player["x"] = CenterCoordinate(RawJson.Get(player, "x") ?? RawJson.Num(0));
        player["y"] = CenterCoordinate(RawJson.Get(player, "y") ?? RawJson.Num(0));
        RawJson.Default(player, "moveIntent", () => new JsonObject { ["dx"] = 0.0, ["dy"] = 0.0 });
        RawJson.Set(state, "player", player);
        return state;
    }

    /// <summary><see cref="SaveSchema.CenterCoordinate"/> over a raw value (non-numbers pass through, like <c>Number.isInteger</c>).</summary>
    private static JsonNode CenterCoordinate(JsonNode value) =>
        RawJson.IsNumber(value) ? RawJson.Num(SaveSchema.CenterCoordinate(RawJson.ToNumber(value))) : value.Parent is null ? value : value.DeepClone();

    /// <summary>
    /// TS <c>SAVE_MIGRATIONS</c>: <c>Registry[n]</c> upgrades a version-n GameState to n+1.
    /// Each step takes ownership of its argument and may update it in place.
    /// </summary>
    public static readonly IReadOnlyDictionary<double, Func<JsonObject, JsonObject>> Registry =
        new Dictionary<double, Func<JsonObject, JsonObject>>
        {
            [1] = MigrateV1ToV2,
            [2] = MigrateV2ToV3,
            [3] = MigrateV3ToV4,
        };

    /// <summary>
    /// Upgrade and validate serialized simulation state. This is deliberately
    /// independent from project migrations: authored content and a player's live
    /// runtime state evolve on different schedules. Never throws.
    /// </summary>
    public static SaveMigrationResult MigrateGameState(JsonNode? raw)
    {
        if (!RawJson.IsObjectLike(raw))
        {
            return Fail(0, false, "Save state is not an object");
        }
        var saveVersion = RawJson.Get(RawJson.Get(raw, "meta"), "saveVersion");
        var fromVersion = RawJson.IsNumber(saveVersion) ? RawJson.ToNumber(saveVersion) : 1;
        var current = SaveSchema.CurrentSaveVersion;
        if (fromVersion > current)
        {
            return Fail(fromVersion, false,
                $"Save version {Js.Num(fromVersion)} is newer than this engine supports ({Js.Num(current)}). Update the engine.");
        }

        var migrated = fromVersion < current;
        JsonObject state;
        try
        {
            state = RawJson.Spread(raw!.DeepClone());
            for (var version = fromVersion; version < current; version++)
            {
                if (!Registry.TryGetValue(version, out var migrate))
                {
                    return Fail(fromVersion, false, $"No save migration registered for version {Js.Num(version)}");
                }
                state = migrate(state);
            }
            var meta = RawJson.Spread(RawJson.Get(state, "meta"));
            meta["saveVersion"] = RawJson.Num(current);
            RawJson.Set(state, "meta", meta);
        }
        catch (Exception ex)
        {
            return Fail(fromVersion, migrated, $"Migration failed: {ex.Message}");
        }

        var (parsed, errors) = RawJson.ParseAndValidate<GameState>(state, SchemaValidation.ValidateGameState);
        if (parsed is null || errors.Count > 0)
        {
            return new SaveMigrationResult { Ok = false, Data = null, FromVersion = fromVersion, Migrated = migrated, Errors = errors };
        }
        return new SaveMigrationResult { Ok = true, Data = parsed, FromVersion = fromVersion, Migrated = migrated, Errors = [] };
    }

    /// <summary><see cref="MigrateGameState(JsonNode?)"/> over JSON text. Never throws.</summary>
    public static SaveMigrationResult MigrateGameState(string json) =>
        RawJson.TryParse(json, out var node, out var error)
            ? MigrateGameState(node)
            : Fail(0, false, $"Save state is not valid JSON: {error}");

    /// <summary>Re-validate (and migrate, if needed) an in-memory state. Never throws.</summary>
    public static SaveMigrationResult MigrateGameState(GameState state) =>
        MigrateGameState(JsonSerializer.SerializeToNode(state, JsonDefaults.Options));

    private static SaveMigrationResult Fail(double fromVersion, bool migrated, string error) =>
        new() { Ok = false, Data = null, FromVersion = fromVersion, Migrated = migrated, Errors = [error] };
}
